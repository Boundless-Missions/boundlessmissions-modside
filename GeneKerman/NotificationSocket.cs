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

        private volatile bool connected;
        private volatile bool connecting;
        private volatile bool pendingRetry; // set by OnClose (bg thread), consumed by Tick (main)
        private volatile bool sendDead;     // set by a failed keepalive (bg thread), consumed by Tick
        private volatile bool justConnected; // set by OnOpen (bg thread), consumed by ConsumeJustConnected
        private volatile bool versionPoke;    // set by a "version" frame (bg thread), consumed by ConsumeVersionPoke
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
                // KSP ships its own (old) certificate store; for wss accept whatever
                // the configured server presents rather than failing the handshake.
                if (url.StartsWith("wss://"))
                    ws.SslConfiguration.ServerCertificateValidationCallback = (s, c, ch, e) => true;

                ws.OnOpen += (s, e) =>
                {
                    connected = true;
                    connecting = false;
                    justConnected = true;
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
