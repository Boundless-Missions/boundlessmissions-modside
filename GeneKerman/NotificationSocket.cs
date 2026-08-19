/*
 * NotificationSocket.cs – Live notification push over WebSocket.
 *
 * KSP's Mono runtime has no usable System.Net.WebSockets.ClientWebSocket, so we
 * use the bundled websocket-sharp library. Its events fire on background threads,
 * so received notifications are parsed and pushed onto a thread-safe queue; the
 * main thread drains them via TryDequeue() (see GeneKermanMod.Update).
 *
 * Connection lifecycle (connect / retry) is driven from the main thread by Tick()
 * to keep all reconnection logic single-threaded and simple.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp;

namespace GeneKerman
{
    public class NotificationSocket
    {
        private readonly ApiClient api;
        private WebSocket ws;

        private readonly ConcurrentQueue<Dictionary<string, object>> incoming =
            new ConcurrentQueue<Dictionary<string, object>>();

        /// <summary>A command frame plus when it arrived, so a stale one can be dropped.</summary>
        private class PendingCommand
        {
            public Dictionary<string, object> payload;
            public int receivedTick;   // Environment.TickCount — Unity's Time is main-thread only
        }

        private readonly ConcurrentQueue<PendingCommand> commands =
            new ConcurrentQueue<PendingCommand>();

        // Commands are the opposite of notifications: a notification the client missed
        // is worth recovering, a command is not. If the player closed their laptop after
        // pressing a website button, a window opening an hour later during a reentry is
        // a bug, not a feature. Hence a small cap and a short life.
        private const int COMMAND_CAPACITY = 4;
        private const int COMMAND_TTL_MS = 30000;

        private volatile bool connected;
        private volatile bool connecting;
        private volatile bool pendingRetry; // set by OnClose (bg thread), consumed by Tick (main)
        private volatile bool sendDead;     // set by a failed keepalive (bg thread), consumed by Tick
        private volatile bool justConnected; // set by OnOpen (bg thread), consumed by ConsumeJustConnected
        private volatile bool versionPoke;    // set by a "version" frame (bg thread), consumed by ConsumeVersionPoke
        private volatile bool policyPoke;     // set by a "policy" frame (bg thread), consumed by ConsumePolicyPoke
        private bool enabled;               // set by Connect()/Disconnect(), read on main thread
        private float nextRetryTime;
        private float lastKeepalive;        // main-thread keepalive timer
        private float retryDelay = 2f;      // backoff: 2s → 30s
        private const float MAX_RETRY = 30f;
        private const float KEEPALIVE_INTERVAL = 25f; // ping cadence (keeps NAT open, surfaces dead sockets)

        public bool IsConnected => connected;
        public bool IsEnabled => enabled;

        /// <summary>
        /// Optional: host-supplied way to obtain a short-lived WebSocket ticket
        /// (it runs ApiClient.GetWsTicket on a coroutine and invokes the callback
        /// with the ticket, or null on failure). When set, the socket authenticates
        /// the handshake with a single-use ticket instead of the long-lived token —
        /// keeping the token out of the WS URL and server logs. Falls back to the
        /// token URL when unset or when the ticket request fails (old server).
        /// </summary>
        public Action<Action<string>> TicketProvider;

        /// <summary>
        /// Returns true exactly once after each successful (re)connect. The caller
        /// uses this to run a catch-up notification poll: pushes the server sent
        /// while the socket was dead are lost (they hit a discarded connection), so
        /// only a fresh fetch recovers them — and re-syncs the contract list.
        /// </summary>
        public bool ConsumeJustConnected()
        {
            if (!justConnected) return false;
            justConnected = false;
            return true;
        }

        /// <summary>
        /// Returns true once after the server broadcast a "version" frame (a new mod
        /// version was published). The caller re-runs its version check in response.
        /// </summary>
        public bool ConsumeVersionPoke()
        {
            if (!versionPoke) return false;
            versionPoke = false;
            return true;
        }

        /// <summary>
        /// Returns true once after the server broadcast a "policy" frame (the Privacy
        /// Policy / Terms version was bumped). The caller re-fetches the policy version
        /// in response and raises the re-consent gate if the client is now behind.
        /// </summary>
        public bool ConsumePolicyPoke()
        {
            if (!policyPoke) return false;
            policyPoke = false;
            return true;
        }

        public NotificationSocket(ApiClient api)
        {
            this.api = api;
        }

        /// <summary>Enable the socket. Actual connection is established by Tick().</summary>
        public void Connect()
        {
            enabled = true;
            nextRetryTime = 0f; // connect on next tick
        }

        /// <summary>Disable the socket and close any open connection.</summary>
        public void Disconnect()
        {
            enabled = false;
            CloseSocket();
        }

        /// <summary>Drain one queued notification, if any. Call on the main thread.</summary>
        public bool TryDequeue(out Dictionary<string, object> notif)
        {
            return incoming.TryDequeue(out notif);
        }

        /// <summary>
        /// The oldest live command, without removing it — expired ones are dropped on
        /// the way. Peek rather than dequeue because the caller may not be able to act
        /// yet (a time-critical prompt could be on screen), and a command it declined
        /// to show this frame must stay queued until it is shown or expires.
        /// Call on the main thread; DropCommand() removes the one you handled.
        /// </summary>
        public bool TryPeekCommand(out Dictionary<string, object> cmd)
        {
            cmd = null;
            PendingCommand head;
            while (commands.TryPeek(out head))
            {
                // unchecked so the comparison still works across TickCount's ~24.9-day wrap.
                if (unchecked(Environment.TickCount - head.receivedTick) <= COMMAND_TTL_MS)
                {
                    cmd = head.payload;
                    return true;
                }
                commands.TryDequeue(out head);
            }
            return false;
        }

        /// <summary>Remove the command returned by the last TryPeekCommand. Main thread.</summary>
        public void DropCommand()
        {
            PendingCommand ignored;
            commands.TryDequeue(out ignored);
        }

        /// <summary>Drives (re)connection. Call once per frame on the main thread.</summary>
        public void Tick()
        {
            if (!enabled) return;

            // A background-thread OnClose asked for a retry; schedule the backoff here
            // (Time is a main-thread-only Unity API).
            if (pendingRetry)
            {
                pendingRetry = false;
                ScheduleRetry();
                return;
            }

            if (connected)
            {
                // A keepalive send failed (broken pipe on a silently dropped
                // connection). Without this the socket would stay "connected"
                // forever — never reconnecting and, worse, suppressing the
                // fallback poll in GeneKermanMod — so no notifications (and no
                // contract refresh) would ever arrive again until a manual action.
                if (sendDead)
                {
                    Debug.LogWarning("[GeneKerman] Notification socket dead (keepalive failed); reconnecting.");
                    CloseSocket();
                    ScheduleRetry();
                    return;
                }

                // Periodic keepalive: keeps NAT mappings alive and turns a silently
                // dropped TCP connection into a send failure within one cycle.
                if (Time.realtimeSinceStartup - lastKeepalive >= KEEPALIVE_INTERVAL)
                {
                    lastKeepalive = Time.realtimeSinceStartup;
                    SendKeepalive();
                }
                return;
            }

            if (connecting) return;
            if (!api.IsLinked) return;
            if (Time.realtimeSinceStartup < nextRetryTime) return;

            OpenSocket();
        }

        // Fire-and-forget keepalive. SendAsync avoids blocking the Unity main
        // thread; the completed callback (bg thread) flags a dead socket so Tick
        // can drop and reconnect it. The server discards these frames.
        private void SendKeepalive()
        {
            var sock = ws;
            if (sock == null) return;
            try
            {
                sock.SendAsync("{\"type\":\"ping\"}", ok => { if (!ok) sendDead = true; });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] Keepalive send threw: " + ex.Message);
                sendDead = true;
            }
        }

        private void OpenSocket()
        {
            // Mark connecting up front so Tick() doesn't fire a second attempt while
            // the (possibly multi-frame) ticket request is in flight.
            connecting = true;
            sendDead = false;
            lastKeepalive = Time.realtimeSinceStartup;

            if (TicketProvider != null)
            {
                // Get a short-lived single-use ticket first, so the long-lived token
                // never appears in the WS URL. On failure (e.g. an old server) fall
                // back to the deprecated token URL so the socket still connects.
                TicketProvider(ticket =>
                {
                    string url = string.IsNullOrEmpty(ticket)
                        ? api.NotificationsWebSocketUrl
                        : api.WebSocketUrlForTicket(ticket);
                    OpenSocketWithUrl(url);
                });
            }
            else
            {
                OpenSocketWithUrl(api.NotificationsWebSocketUrl);
            }
        }

        private void OpenSocketWithUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                // Nothing to connect to (e.g. unlinked) — release the guard and retry.
                connecting = false;
                ScheduleRetry();
                return;
            }
            try
            {
                CloseSocket();

                ws = new WebSocket(url);
                // TLS validation. websocket-sharp's default callback accepts ANY
                // certificate, so leaving it unset is the same as trusting a MITM. We
                // set it explicitly:
                //
                //   • Public host (the official server, or any custom wss:// on a
                //     routable address): enforce a real chain — accept only when the
                //     cert validates with NO policy errors (a valid chain AND a
                //     matching hostname). A self-signed / mis-issued cert from a
                //     man-in-the-middle is refused, so the socket simply stays down and
                //     the client falls back to HTTP polling (see GeneKermanMod: the
                //     10-min notification poll and the 30-s import poll both run
                //     whenever the socket is down). The WS is an accelerant, never the
                //     only channel — so failing closed here degrades gracefully instead
                //     of trusting an attacker. We deliberately do NOT pin the leaf: the
                //     official server uses a short-lived (Let's Encrypt) cert that
                //     rotates, and a leaf pin would break every client on renewal.
                //
                //   • Loopback / private-LAN dev server (localhost, 127/8, ::1, 10/8,
                //     192.168/16, 172.16-31/12): a developer testing over a self-signed
                //     cert. There is no MITM to defend against on your own machine/LAN,
                //     and requiring a CA-signed cert for local dev is pointless friction,
                //     so accept whatever it presents.
                if (url.StartsWith("wss://"))
                {
                    if (IsLocalDevHost(HostOf(url)))
                        ws.SslConfiguration.ServerCertificateValidationCallback =
                            (s, c, ch, e) => true;
                    else
                        ws.SslConfiguration.ServerCertificateValidationCallback =
                            ValidatePublicServerCertificate;
                }

                ws.OnOpen += (s, e) =>
                {
                    connected = true;
                    connecting = false;
                    justConnected = true;
                    // Anything queued before this connection is from before the gap and
                    // is no longer what the player is looking at. Notifications catch up
                    // after a reconnect; commands must do the opposite.
                    PendingCommand stale;
                    while (commands.TryDequeue(out stale)) { }
                    retryDelay = 2f;
                    Debug.Log("[GeneKerman] Notification socket connected.");
                };
                ws.OnMessage += (s, e) =>
                {
                    if (!e.IsText || string.IsNullOrEmpty(e.Data)) return;
                    try
                    {
                        var msg = MiniJSON.DeserializeDict(e.Data);
                        // A "version" poke means a new mod build was published — flag
                        // it so the main thread re-runs the version check (and gates
                        // this client if it's now outdated).
                        if (MiniJSON.GetString(msg, "type") == "version")
                        {
                            versionPoke = true;
                            return;
                        }
                        // A "policy" poke means the Privacy/Terms version was bumped —
                        // flag it so the main thread re-fetches the policy version and
                        // re-prompts consent if this client accepted an older one.
                        if (MiniJSON.GetString(msg, "type") == "policy")
                        {
                            policyPoke = true;
                            return;
                        }
                        // A "command" frame is the website asking this game to raise a
                        // window. Bounded, because nothing consumes these while the
                        // player is at the main menu or mid-scene-load, and an unbounded
                        // queue would turn a stuck client into a burst of prompts.
                        if (MiniJSON.GetString(msg, "type") == "command")
                        {
                            PendingCommand evicted;
                            while (commands.Count >= COMMAND_CAPACITY &&
                                   commands.TryDequeue(out evicted)) { }
                            commands.Enqueue(new PendingCommand
                            {
                                payload = msg,
                                receivedTick = Environment.TickCount
                            });
                            return;
                        }
                        var notif = MiniJSON.GetDict(msg, "notification");
                        if (notif != null) incoming.Enqueue(notif);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[GeneKerman] Bad notification frame: " + ex.Message);
                    }
                };
                ws.OnError += (s, e) =>
                {
                    Debug.LogWarning("[GeneKerman] Notification socket error: " + e.Message);
                };
                ws.OnClose += (s, e) =>
                {
                    connected = false;
                    connecting = false;
                    pendingRetry = true; // Tick() schedules the backoff on the main thread
                };

                ws.ConnectAsync();
            }
            catch (Exception ex)
            {
                connecting = false;
                Debug.LogWarning("[GeneKerman] Notification socket connect failed: " + ex.Message);
                ScheduleRetry();
            }
        }

        private void ScheduleRetry()
        {
            nextRetryTime = Time.realtimeSinceStartup + retryDelay;
            retryDelay = Math.Min(retryDelay * 2f, MAX_RETRY);
        }

        // ── TLS certificate validation (wss://) ──────────────────────────────

        /// <summary>Certificate callback for a public wss:// server. Accepts the
        /// connection only when the presented chain has no policy errors — i.e. it
        /// validates against the trust store AND the hostname matches. Anything else
        /// (self-signed, wrong host, expired, mis-issued — what a man-in-the-middle
        /// would present) is refused; the socket then stays down and the client falls
        /// back to HTTP polling. Runs on a websocket-sharp background thread, so it
        /// only touches Debug.Log, never Unity API.</summary>
        private static bool ValidatePublicServerCertificate(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            System.Security.Cryptography.X509Certificates.X509Chain chain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                return true;
            Debug.LogWarning("[GeneKerman] Notification socket: refusing server " +
                "certificate (" + sslPolicyErrors + ") — falling back to HTTP polling. " +
                "This is expected on a MITM'd or misconfigured connection.");
            return false;
        }

        /// <summary>The host component of a ws(s):// URL, or "" if unparseable.</summary>
        private static string HostOf(string url)
        {
            try { return new Uri(url).Host; }
            catch { return ""; }
        }

        /// <summary>True for a loopback or private-LAN host — a local dev server, where
        /// there is no man-in-the-middle to defend against and a self-signed cert is
        /// normal. Everything else is treated as public and gets full validation.</summary>
        private static bool IsLocalDevHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim().Trim('[', ']').ToLowerInvariant();  // strip IPv6 brackets
            if (host == "localhost" || host == "::1") return true;

            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(host, out ip)) return false;  // a real DNS name → public
            if (System.Net.IPAddress.IsLoopback(ip)) return true;            // 127.0.0.0/8, ::1

            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 10) return true;                          // 10.0.0.0/8
                if (b[0] == 192 && b[1] == 168) return true;          // 192.168.0.0/16
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;  // 172.16.0.0/12
                if (b[0] == 169 && b[1] == 254) return true;          // 169.254.0.0/16 link-local
            }
            return false;
        }

        private void CloseSocket()
        {
            if (ws == null) return;
            try { ws.Close(); } catch { /* ignore */ }
            ws = null;
            connected = false;
            sendDead = false;
        }
    }
}
