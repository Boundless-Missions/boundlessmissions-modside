#if GK_DEBUG_PANEL
/*
 * DebugBridge.cs — an agent-drivable loopback bridge for testing the mod IN THE GAME.
 *
 * COMPILED OUT of production builds. The whole file is wrapped in #if GK_DEBUG_PANEL,
 * which GeneKerman.csproj defines only off the 'production' channel. A shipped DLL
 * contains none of this — not the routes, not the strings, not the listener.
 * `tools/assert_production_clean.sh` asserts exactly that against the built DLL.
 *
 * Why it exists: the crew-transfer path has been repaired four times, each round
 * compiling and passing its tests, and three of those rounds corrupted saves. The
 * existing DebugTestPanel tables cover pure string and set logic against a stubbed
 * roster; every one of those bugs lived in ProtoVessel.Load, rosterStatus,
 * PurgeBorrowedGhostCrew and CrewedNames() — state a human can only inspect by
 * squinting at the Astronaut Complex. This makes that state machine-readable, so a
 * test asserts on it.
 *
 * Why a SECOND listener rather than routes on LocalServer, which already has all this
 * machinery. Three reasons, in order of weight:
 *
 *   1. LocalServer refuses to start without a version-matched WebUI bundle in
 *      GameData/BoundlessMissions/WebUI/, and only runs at all when the player has
 *      switched `enableWebUi` on. Both are correct for the browser UI and both would
 *      make a test bridge unavailable in the ordinary case — testing the *sidebar*
 *      build, with no bundle installed.
 *   2. ApiProxy.cs:5-9 states the browser bridge is a confused deputy by construction
 *      and that its allow-list is the boundary. A channel that spawns vessels and
 *      edits rosters widens precisely that boundary. Keeping it on its own listener,
 *      its own port, its own auth and its own file means the production surface is not
 *      touched — there is no code path from a browser session to any of this.
 *   3. The audit's LB2 is still open: the `gk` cookie is host-scoped, not port-scoped,
 *      so another loopback service can replay cookie-only routes. So nothing here
 *      authenticates on a cookie. The only credential is a bearer token in a custom
 *      header, which a browser cannot attach cross-origin without a preflight we
 *      answer 405 (see ValidateOrigin), and which is never sent by the browser
 *      automatically the way a cookie is.
 *
 * What IS reused, verbatim: MainThreadQueue (the hard part — KSP state is main-thread
 * only), JobRegistry, EventStream, JobResult and LocalServer's static response
 * helpers. None of those are coupled to the browser listener.
 *
 * Threat model, stated plainly: the token sits in a file readable by the user account
 * running KSP, and so does PluginData/session.token. A hostile process on that account
 * has already won. This is not a security boundary against a local attacker; it is a
 * boundary against everything ELSE on loopback — another mod, a stray page, a service
 * on a neighbouring port — reaching a channel that can delete vessels.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace GeneKerman.Web
{
    public sealed class DebugBridge
    {
        /// <summary>
        /// Where the driver looks. One per KSP install, which is what lets a two-instance
        /// test (KR-KSP issuing, KR2-KSP rescuing) address each side unambiguously — the
        /// port is ephemeral and the token rotates, so there is nothing else to key on.
        /// </summary>
        public const string HandshakeFileName = "debug_bridge.json";

        /// <summary>The one credential. A header, never a cookie — see the file header.</summary>
        public const string TokenHeader = "X-GK-Debug-Token";

        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public string Url => IsRunning ? "http://127.0.0.1:" + Port : null;

        public readonly MainThreadQueue Queue = new MainThreadQueue();
        public readonly EventStream Events = new EventStream();
        public readonly JobRegistry Jobs = new JobRegistry();

        private HttpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private string token;
        private string expectedHost;
        private string handshakePath;
        private DebugRoutes routes;

        /// <summary>
        /// Bumped whenever the wire format changes in a way a driver must notice. The
        /// driver refuses to run against a mismatch rather than misread a field that
        /// moved — a test harness reporting a false PASS is worse than one that will
        /// not start.
        /// </summary>
        public const int Protocol = 1;

        // ── Lifecycle ───────────────────────────────────────────────────────

        /// <summary>
        /// Binds loopback, mints a token and writes the handshake file. Returns false and
        /// logs on any failure; the game is never blocked by this not coming up.
        /// </summary>
        public bool Start()
        {
            if (IsRunning) return true;

            try
            {
                Port = PickFreeLoopbackPort();
                expectedHost = "127.0.0.1:" + Port;
                token = RandomToken(32);

                listener = new HttpListener();
                // Never "+" or "*" (every interface) and never "localhost" (also ::1).
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                listener.Start();

                routes = new DebugRoutes(this, Queue);

                running = true;
                IsRunning = true;

                acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true, // must not keep the process alive on quit
                    Name = "GK-DebugBridge",
                };
                acceptThread.Start();

                WriteHandshake();
                // The token is deliberately NOT logged: KSP.log is attached to bug and
                // device reports, and players upload it to Discord. The port alone is
                // useless without it.
                Debug.Log("[GeneKerman] DEV debug bridge listening on " + Url +
                          " (handshake: " + handshakePath + ")");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[GeneKerman] Debug bridge failed to start: " + e);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning && listener == null) return;

            running = false;
            IsRunning = false;
            token = null;

            try { Events.CloseAll(); } catch { }
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
            listener = null;

            // Release anything parked in Queue.Run so no request thread waits out the
            // full 30s timeout after the listener is gone.
            Queue.DrainAndFail();

            // Remove the handshake before anything else can read a port that is no
            // longer listening — a driver that connects to a dead port and times out is
            // a far more confusing failure than one that says "KSP is not running".
            DeleteHandshake();

            acceptThread = null;
            Debug.Log("[GeneKerman] Debug bridge stopped.");
        }

        /// <summary>Main thread, from GeneKermanMod.Update(). Never from OnGUI().</summary>
        public void Pump()
        {
            if (!IsRunning) return;
            Queue.Pump();
        }

        public void Broadcast(string eventName, string jsonPayload)
        {
            if (!IsRunning) return;
            Events.Broadcast(eventName, jsonPayload);
        }

        // ── Handshake file ──────────────────────────────────────────────────

        /// <summary>
        /// Port, token and enough identity for a driver to tell two running instances
        /// apart. The install path is the discriminator: two KSP copies differ by where
        /// they live long before they differ by anything in the save.
        /// </summary>
        private void WriteHandshake()
        {
            try
            {
                string dir = GeneKermanMod.PluginDataPath;
                Directory.CreateDirectory(dir);
                handshakePath = Path.Combine(dir, HandshakeFileName);

                var sb = new StringBuilder();
                sb.Append("{\"protocol\":").Append(Protocol)
                  .Append(",\"port\":").Append(Port)
                  .Append(",\"token\":").Append(JobResult.Quote(token))
                  .Append(",\"host\":").Append(JobResult.Quote(expectedHost))
                  .Append(",\"tokenHeader\":").Append(JobResult.Quote(TokenHeader))
                  .Append(",\"modVersion\":").Append(JobResult.Quote(ModVersion.Current))
                  .Append(",\"install\":").Append(JobResult.Quote(SafeRoot()))
                  .Append(",\"pid\":").Append(SafeProcessId())
                  .Append('}');

                // Written whole, then moved into place: a driver polling for the file
                // must never read a half-written one and parse a truncated token.
                string tmp = handshakePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                // Restrict BEFORE the move, so the file is never world-readable at its
                // final name — a driver polling for that name would otherwise be racing
                // the chmod. This file carries the bridge's only credential, and the
                // auth comment reasons about "a hostile local user on the same account";
                // at the default umask the reader did not need the same account.
                SecureFile.RestrictToOwner(tmp);
                if (File.Exists(handshakePath)) File.Delete(handshakePath);
                File.Move(tmp, handshakePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not write the debug handshake file: " + e.Message);
                handshakePath = null;
            }
        }

        private void DeleteHandshake()
        {
            try { if (handshakePath != null && File.Exists(handshakePath)) File.Delete(handshakePath); }
            catch (Exception) { /* a stale handshake is caught by the connect failing */ }
            handshakePath = null;
        }

        private static string SafeRoot()
        {
            try { return KSPUtil.ApplicationRootPath ?? ""; }
            catch (Exception) { return ""; }
        }

        private static int SafeProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception) { return 0; }
        }

        // ── Accept loop ─────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext(); // blocks; throws when Stop() is called
                }
                catch (HttpListenerException) { break; }   // expected on Stop()
                catch (ObjectDisposedException) { break; } // expected on Close()
                catch (Exception e)
                {
                    if (running) Debug.LogWarning("[GeneKerman] Debug bridge accept error: " + e.Message);
                    break;
                }

                ThreadPool.QueueUserWorkItem(_ => SafeHandle(ctx));
            }
        }

        private void SafeHandle(HttpListenerContext ctx)
        {
            try
            {
                if (!ValidateOrigin(ctx)) return;

                string path = ctx.Request.Url.AbsolutePath;

                // The one unauthenticated route, and it answers nothing but "a bridge of
                // this protocol is here". A driver needs it to tell a live instance from
                // a stale handshake file without spending its token on a dead port.
                if (path == "/gk/debug/ping")
                {
                    LocalServer.Respond(ctx, 200, "application/json",
                        "{\"ok\":true,\"protocol\":" + Protocol + "}");
                    return;
                }

                if (!IsAuthorized(ctx.Request))
                {
                    LocalServer.Respond(ctx, 401, "application/json", "{\"error\":\"unauthorized\"}");
                    return;
                }

                if (path.StartsWith("/gk/debug/", StringComparison.Ordinal))
                    routes.Dispatch(ctx, path);
                else
                    LocalServer.Respond(ctx, 404, "application/json", "{\"error\":\"not_found\"}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Debug bridge request failed: " + e.Message);
                try { LocalServer.Respond(ctx, 500, "application/json", "{\"error\":\"internal\"}"); } catch { }
            }
        }

        private bool IsAuthorized(HttpListenerRequest req)
        {
            if (token == null) return false;
            return FixedTimeEquals(req.Headers[TokenHeader], token);
        }

        /// <summary>
        /// Same Host / Origin / Sec-Fetch-Site gate as LocalServer, for the same reasons:
        /// an exact Host match is what stops DNS rebinding (Mono's own prefix matcher
        /// lets "127.0.0.1" with no port and "127.0.0.1:PORT.evil.com" through), and
        /// answering every preflight 405 with no Access-Control-* header ever is what
        /// makes the token header un-forgeable from another origin.
        /// </summary>
        private bool ValidateOrigin(HttpListenerContext ctx)
        {
            var req = ctx.Request;

            if (req.HttpMethod == "OPTIONS")
            {
                LocalServer.Respond(ctx, 405, "text/plain", "");
                return false;
            }

            if (!string.Equals(req.UserHostName, expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                LocalServer.Respond(ctx, 421, "text/plain", "Misdirected Request");
                return false;
            }

            string origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) &&
                !string.Equals(origin, "http://" + expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                LocalServer.Respond(ctx, 403, "text/plain", "Forbidden");
                return false;
            }

            string site = req.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(site) && site != "same-origin" && site != "none")
            {
                LocalServer.Respond(ctx, 403, "text/plain", "Forbidden");
                return false;
            }

            return true;
        }

        // ── Primitives ──────────────────────────────────────────────────────

        private static int PickFreeLoopbackPort()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                TcpListener probe = null;
                try
                {
                    probe = new TcpListener(IPAddress.Loopback, 0);
                    probe.Start();
                    return ((IPEndPoint)probe.LocalEndpoint).Port;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[GeneKerman] Debug port probe " + (attempt + 1) + "/5 failed: " + e.Message);
                }
                finally { try { probe?.Stop(); } catch { } }
            }
            throw new InvalidOperationException("Could not obtain a free loopback port.");
        }

        private static string RandomToken(int bytes)
        {
            var buf = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(buf);
            return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
#endif
