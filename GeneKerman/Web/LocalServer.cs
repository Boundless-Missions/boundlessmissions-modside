/*
 * LocalServer.cs – Loopback HTTP bridge between the game and the browser-hosted UI.
 *
 * Why a local server at all: KSP runs Unity 2019.4, which has no webview, and shipping
 * an embedded browser would mean 50-300 MB of per-platform native binaries against a
 * mod that is under a megabyte. So the UI runs in the player's own browser, and this
 * server is what makes that safe and same-origin:
 *
 *   GET  /            → the built React bundle in GameData/BoundlessMissions/WebUI/
 *   *    /api/v1/...  → proxied upstream with the session token attached in C#
 *   *    /gk/...      → game state and actions only this process can perform
 *   GET  /gk/events   → SSE push, tee'd from the notification socket
 *
 * Because the page, its data and its game bridge share one origin there is no CORS, no
 * mixed content, and the session token never enters JavaScript.
 *
 * Threading: the accept loop and every handler run off the main thread. Anything that
 * touches KSP goes through MainThreadQueue, drained by GeneKermanMod.Update().
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace GeneKerman.Web
{
    public sealed class LocalServer
    {
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }

        /// <summary>Origin only — safe to log and to show in the in-game window.</summary>
        public string Url => IsRunning ? $"http://127.0.0.1:{Port}" : null;

        public readonly MainThreadQueue Queue = new MainThreadQueue();
        public readonly EventStream Events = new EventStream();
        public readonly JobRegistry Jobs = new JobRegistry();

        private readonly BridgeAuth auth = new BridgeAuth();
        private StaticFiles staticFiles;
        private ApiProxy apiProxy;
        private GkRoutes gkRoutes;

        private HttpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        /// <summary>
        /// The Host header we require, exactly. An attacker who points a domain at
        /// 127.0.0.1 (DNS rebinding) arrives with Host: evil.com:PORT and is rejected.
        ///
        /// Measured, not assumed: Mono's own prefix matcher rejects "localhost:PORT",
        /// "evil.com:PORT" and "[::1]:PORT" with a 400 before we see them — but it lets
        /// "127.0.0.1" (no port) and "127.0.0.1:PORT.evil.com" through to us. This check
        /// is what stops those two. Do not delete it as redundant.
        /// </summary>
        private string expectedHost;

        // ── Lifecycle ───────────────────────────────────────────────────────

        /// <summary>
        /// Starts the bridge and returns the one-time launch URL to hand to the browser,
        /// or null on failure.
        ///
        /// The returned URL contains a single-use nonce and MUST NOT be logged: players
        /// routinely upload KSP.log to Discord, and DeviceId.GetKspLog() uploads it
        /// automatically for device reports. Log <see cref="Url"/> instead.
        /// </summary>
        public string Start()
        {
            if (IsRunning) return null;

            staticFiles = new StaticFiles(GeneKermanMod.ModPath);

            if (!staticFiles.BundleExists)
            {
                Debug.LogError("[GeneKerman] Web UI bundle missing (GameData/BoundlessMissions/WebUI/). " +
                               "Staying on the classic UI.");
                return null;
            }

            // A WebUI/ built for a different DLL would call endpoints this build does not
            // implement, and the failure would look like a dozen unrelated bugs. Refuse.
            if (!staticFiles.VersionMatches(out string found))
            {
                Debug.LogError($"[GeneKerman] Web UI bundle is version '{found ?? "unknown"}' but this " +
                               $"build is '{ModVersion.Current}'. Reinstall the mod. Staying on the classic UI.");
                return null;
            }

            try
            {
                Port = PickFreeLoopbackPort();
                expectedHost = "127.0.0.1:" + Port;

                listener = new HttpListener();
                // Trailing slash is mandatory in a prefix. Never "+" or "*" (those bind
                // every interface) and never "localhost" (also resolves ::1).
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                listener.Start();

                apiProxy = new ApiProxy(Queue);
                gkRoutes = new GkRoutes(this, Queue, auth);
                string nonce = auth.BeginSession();

                running = true;
                IsRunning = true;

                acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true, // must not keep the process alive on quit
                    Name = "GK-LocalServer",
                };
                acceptThread.Start();

                Debug.Log($"[GeneKerman] Web bridge listening on {Url}");
                return $"{Url}/?k={nonce}";
            }
            catch (Exception e)
            {
                Debug.LogError("[GeneKerman] Web bridge failed to start: " + e);
                Stop();
                return null;
            }
        }

        public void Stop()
        {
            if (!IsRunning && listener == null) return;

            running = false;
            IsRunning = false;

            auth.EndSession();
            try { Events.CloseAll(); } catch { }
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
            listener = null;

            // Release anything still parked in Queue.Run so no request thread waits out
            // the full 30s timeout after the server is gone.
            Queue.DrainAndFail();

            acceptThread = null;
            Debug.Log("[GeneKerman] Web bridge stopped.");
        }

        /// <summary>Main thread, from GeneKermanMod.Update(). Never from OnGUI().</summary>
        public void Pump()
        {
            if (!IsRunning) return;
            Queue.Pump();
        }

        /// <summary>
        /// A fresh single-use launch URL for "Reopen in browser". Same caveat as
        /// <see cref="Start"/>: contains a nonce, so never log it.
        /// </summary>
        public string NewLaunchUrl() => IsRunning ? $"{Url}/?k={auth.IssueNonce()}" : null;

        /// <summary>Tee of an incoming notification to any connected page.</summary>
        public void Broadcast(string eventName, string jsonPayload)
        {
            if (!IsRunning) return;
            Events.Broadcast(eventName, jsonPayload);
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
                    if (running) Debug.LogWarning("[GeneKerman] Web bridge accept error: " + e.Message);
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

                if (path.StartsWith("/gk/", StringComparison.Ordinal))
                    gkRoutes.Dispatch(ctx, path);
                else if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    // /api/img is loaded by <img src>, which — like EventSource — cannot
                    // set a custom header, so it authenticates on the SameSite=Strict
                    // cookie alone. Safe for the same reasons: the cookie is never sent
                    // from another site, ValidateOrigin has already enforced Host and
                    // Sec-Fetch-Site, and we emit no CORS headers, so a cross-origin
                    // embed both fails auth and could not read the pixels anyway.
                    // It also never carries the session token upstream.
                    bool ok = path == "/api/img"
                        ? auth.IsAuthorizedCookieOnly(ctx.Request)
                        : auth.IsAuthorized(ctx.Request);

                    if (!ok)
                        Respond(ctx, 401, "application/json", "{\"error\":\"unauthorized\"}");
                    else
                        apiProxy.Handle(ctx, path);
                }
                else
                    staticFiles.TryServe(ctx, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Web bridge request failed: " + e.Message);
                try { Respond(ctx, 500, "application/json", "{\"error\":\"internal\"}"); } catch { }
            }
        }

        /// <summary>
        /// Host / Origin / Sec-Fetch-Site gate, applied before routing so a rebinding or
        /// cross-origin attempt never reaches a handler.
        /// </summary>
        private bool ValidateOrigin(HttpListenerContext ctx)
        {
            var req = ctx.Request;

            // We emit no Access-Control-* headers, ever, and answer every preflight with
            // 405. That is what makes the X-GK-CSRF header un-forgeable from another
            // origin: a cross-origin request carrying a custom header must preflight first.
            if (req.HttpMethod == "OPTIONS")
            {
                Respond(ctx, 405, "text/plain", "");
                return false;
            }

            if (!string.Equals(req.UserHostName, expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[GeneKerman] Web bridge rejected Host '{req.UserHostName}'.");
                Respond(ctx, 421, "text/plain", "Misdirected Request");
                return false;
            }

            string origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) &&
                !string.Equals(origin, "http://" + expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                Respond(ctx, 403, "text/plain", "Forbidden");
                return false;
            }

            // "none" is a top-level navigation — the player opening the page.
            string site = req.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(site) && site != "same-origin" && site != "none")
            {
                Respond(ctx, 403, "text/plain", "Forbidden");
                return false;
            }

            return true;
        }

        // ── Response helpers ────────────────────────────────────────────────

        /// <summary>Applied to every response, including errors.</summary>
        public static void ApplySecurityHeaders(HttpListenerResponse res, bool isHtml)
        {
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Cache-Control"] = "no-store";
            if (isHtml)
            {
                // No 'unsafe-inline': the bundle ships real files, so everything is
                // 'self'. connect-src 'self' also means a compromised page cannot
                // exfiltrate anything it reads to an outside host.
                res.Headers["Content-Security-Policy"] =
                    "default-src 'self'; img-src 'self' data:; connect-src 'self'; " +
                    "style-src 'self'; script-src 'self'; " +
                    "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            }
        }

        /// <summary>Binary response (the image proxy). Cached: these URLs are content-addressed.</summary>
        public static void RespondBytes(HttpListenerContext ctx, int status, string contentType,
                                        byte[] body, bool cacheable)
        {
            var res = ctx.Response;
            try
            {
                res.StatusCode = status;
                res.ContentType = contentType;
                ApplySecurityHeaders(res, false);
                if (cacheable)
                    res.Headers["Cache-Control"] = "private, max-age=3600";
                res.ContentLength64 = body.Length;
                res.OutputStream.Write(body, 0, body.Length);
            }
            catch (Exception) { /* client hung up mid-write */ }
            finally { try { res.Close(); } catch { } }
        }

        public static void Respond(HttpListenerContext ctx, int status, string contentType, string body)
        {
            var res = ctx.Response;
            try
            {
                byte[] buf = Encoding.UTF8.GetBytes(body ?? "");
                res.StatusCode = status;
                res.ContentType = contentType;
                ApplySecurityHeaders(res, contentType != null && contentType.StartsWith("text/html"));
                res.ContentLength64 = buf.Length;
                res.OutputStream.Write(buf, 0, buf.Length);
            }
            catch (Exception) { /* client hung up mid-write */ }
            finally
            {
                try { res.Close(); } catch { /* Mono throws on a broken pipe */ }
            }
        }

        // ── Port selection ──────────────────────────────────────────────────

        /// <summary>
        /// Ask the OS for a free loopback port by binding port 0, reading what we got and
        /// releasing it. A fresh port every session — defence in depth only, since a
        /// local process can scan every port in about a second.
        /// </summary>
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
                    Debug.LogWarning($"[GeneKerman] Port probe {attempt + 1}/5 failed: {e.Message}");
                }
                finally
                {
                    try { probe?.Stop(); } catch { }
                }
            }
            throw new InvalidOperationException("Could not obtain a free loopback port.");
        }
    }
}
