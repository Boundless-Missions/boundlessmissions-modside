/*
 * ApiProxy.cs – Forwards same-origin /api/* calls from the page to the bot API,
 * attaching the session token in C# so it never reaches JavaScript.
 *
 * THE ALLOW-LIST IS THE SECURITY BOUNDARY. This class is a confused deputy by
 * construction: it holds a 30-day token and will authenticate whatever it forwards.
 * A wildcard "proxy anything under /api/v1/" would mean any XSS in the bundle — or any
 * hand-edited WebUI/ folder — inherits the player's full account. So routes are opted
 * in one at a time, per method, and the list grows only as each phase needs it.
 *
 * Permanently excluded, not merely "not yet":
 *   /api/v1/auth/link*   linking is an in-game act; the browser must never mint tokens
 *   /api/v1/attest/*     anti-tamper; proxying it would let a page answer for the DLL
 *   /api/v1/auth/logout_all  reachable only via the named /gk/actions/logout-all route,
 *                            so it cannot be hit by guessing a URL
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GeneKerman.Web
{
    public sealed class ApiProxy
    {
        /// <summary>Nothing the page sends is large — craft uploads never originate in the browser.</summary>
        private const int MaxRequestBytes = 1024 * 1024;

        private sealed class Rule
        {
            public Regex Path;
            public HashSet<string> Methods;
        }

        private static readonly List<Rule> AllowList = new List<Rule>
        {
            // ── Phase 1: Profile + notifications ────────────────────────────
            Allow(@"^/api/v1/user/profile$", "GET"),
            Allow(@"^/api/v1/user/notifications$", "GET"),
            Allow(@"^/api/v1/user/notifications/mark_read$", "POST"),
            Allow(@"^/api/v1/user/notifications/[A-Za-z0-9_-]{1,64}/mark_read$", "POST"),
            Allow(@"^/api/v1/user/notifications/[A-Za-z0-9_-]{1,64}$", "DELETE"),

            // ── Phase 2: Missions + Contracts, read-only ────────────────────
            Allow(@"^/api/v1/missions/weekly$", "GET"),
            Allow(@"^/api/v1/missions/select$", "POST"),
            Allow(@"^/api/v1/contracts/active$", "GET"),
            Allow(@"^/api/v1/contracts/incoming$", "GET"),
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/submission$", "GET"),

            // ── Phase 3: contract actions ───────────────────────────────────
            // The first state-changing verbs the browser can reach, so each is opted
            // in individually rather than by a `/contracts/{id}/*` wildcard — that
            // would silently enrol every future verb the API grows.
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/accept$", "POST"),
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/review$", "POST"),
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/dispute$", "POST"),
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/cancel$", "POST"),
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/give_up$", "POST"),
            Allow(@"^/api/v1/corps/list$", "GET"),

            // ── Phase 6a: the issuer's side of a dispute ────────────────────
            // Settle and more-time are requests the contractor makes and the issuer
            // answers. The answer used to exist only as a Discord DM, which stalled
            // the whole flow for anyone playing without Discord open. Neither takes a
            // date: the granted one is read from the request stored on the contract,
            // so a page cannot approve an extension nobody asked for.
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/settle_response$", "POST"),
            Allow(@"^/api/v1/contracts/[A-Za-z0-9_-]{1,64}/more_time_response$", "POST"),

            // Contract creation is NOT here: it goes through /gk/actions/create-contract,
            // because the mod has to derive the mod list and read the rescue vessel from
            // the running game. Craft download stays out too — the browser
            // has no business receiving a .craft file — installing one is a game
            // action, so it goes through /gk/actions/install-craft instead.
        };

        private static Rule Allow(string pattern, params string[] methods) => new Rule
        {
            Path = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant),
            Methods = new HashSet<string>(methods, StringComparer.Ordinal),
        };

        private readonly MainThreadQueue queue;

        public ApiProxy(MainThreadQueue queue) { this.queue = queue; }

        public void Handle(HttpListenerContext ctx, string path)
        {
            string method = ctx.Request.HttpMethod;

            // Not an upstream API call — a fetch-and-forward of an image the API told us
            // about. Handled separately because it has a different threat model (SSRF)
            // and must never carry the session token.
            if (path == "/api/img")
            {
                if (method != "GET") { LocalServer.Respond(ctx, 405, "application/json", "{\"error\":\"method_not_allowed\"}"); return; }
                HandleImage(ctx);
                return;
            }

            if (!IsAllowed(path, method))
            {
                // Logged because in development this almost always means "you forgot to
                // add the route", and silently 403ing costs an afternoon.
                Debug.LogWarning($"[GeneKerman] Bridge refused un-allow-listed {method} {path}");
                LocalServer.Respond(ctx, 403, "application/json", "{\"error\":\"route_not_allowed\"}");
                return;
            }

            string body;
            try
            {
                body = ReadBody(ctx.Request);
            }
            catch (InvalidDataException)
            {
                LocalServer.Respond(ctx, 413, "application/json", "{\"error\":\"body_too_large\"}");
                return;
            }

            // Blocks this ThreadPool thread until the main thread has run the coroutine.
            JobResult r = queue.Run(done =>
                GeneKermanMod.Instance.RunCoroutine(RelayRoutine(method, path, body, done)));

            LocalServer.Respond(ctx, r.Status, r.ContentType, r.Body);
        }

        // ── Image proxy ─────────────────────────────────────────────────────

        /// <summary>
        /// Blueprint and telemetry images come back from the API as absolute URLs on
        /// Firebase Storage. The page cannot fetch those itself — CSP is img-src 'self'
        /// and widening it would let a compromised bundle beacon out to any host — so
        /// they are fetched here and re-served same-origin.
        ///
        /// This is the classic SSRF shape: a URL supplied by the page, fetched by a
        /// privileged process. Four guards, in order:
        ///   1. scheme must be http/https (no file://, no gopher://),
        ///   2. host must be on the allow-list (this is the real boundary),
        ///   3. the response must actually be an image, and
        ///   4. it is capped before being handed to the browser.
        /// The request is sent with NO auth headers — the session token must never
        /// reach a third-party host.
        /// </summary>
        private const int MaxImageBytes = 5 * 1024 * 1024;

        private static readonly HashSet<string> ImageHostAllowList =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "firebasestorage.googleapis.com",
            "storage.googleapis.com",
            // Discord avatars, for the player picker. Both hosts serve only images and
            // are already trusted implicitly — the mod talks to Discord's API anyway —
            // and the magic-byte check below still applies to whatever comes back.
            "cdn.discordapp.com",
            "media.discordapp.net",
        };

        private void HandleImage(HttpListenerContext ctx)
        {
            string raw = ctx.Request.QueryString["u"];
            if (!IsImageUrlAllowed(raw))
            {
                Debug.LogWarning("[GeneKerman] Bridge refused an image URL outside the allow-list.");
                LocalServer.Respond(ctx, 403, "application/json", "{\"error\":\"host_not_allowed\"}");
                return;
            }

            JobResult r = queue.Run(done =>
                GeneKermanMod.Instance.RunCoroutine(ImageRoutine(raw, done)));

            if (r.Bytes != null)
                LocalServer.RespondBytes(ctx, r.Status, r.ContentType, r.Bytes, cacheable: true);
            else
                LocalServer.Respond(ctx, r.Status, r.ContentType, r.Body);
        }

        private static bool IsImageUrlAllowed(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            if (ImageHostAllowList.Contains(uri.Host)) return true;

            // The configured API host is also allowed — it serves some previews itself,
            // and in development it is localhost. Compared against whatever the mod is
            // actually pointed at, so switching servers does not need a code change.
            try
            {
                string serverHost = new Uri(GeneKermanMod.Instance.Api.ServerUrl).Host;
                return string.Equals(uri.Host, serverHost, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        private static IEnumerator ImageRoutine(string url, Action<JobResult> done)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { done(JobResult.Error(503, "Mod not ready.")); yield break; }

            byte[] data = null;
            bool ok = false;
            // DownloadFile deliberately sends no Authorization header.
            yield return mod.Api.DownloadFile(url, (success, bytes) => { ok = success; data = bytes; });

            if (!ok || data == null)
            {
                done(JobResult.Error(502, "Image unavailable."));
                yield break;
            }

            // Cap on the way out. UnityWebRequest has already buffered the whole body by
            // now, so this bounds what the browser receives rather than what we allocate
            // — acceptable because the only reachable hosts serve our own uploads, which
            // the API caps server-side (MAX_BLUEPRINT_BYTES).
            if (data.Length > MaxImageBytes)
            {
                done(JobResult.Error(413, "Image too large."));
                yield break;
            }

            string sniffed = SniffImageType(data);
            if (sniffed == null)
            {
                // Guard 3: an allow-listed host returning non-image bytes would otherwise
                // be re-served same-origin, which is how a stored file becomes stored XSS.
                done(JobResult.Error(415, "Not an image."));
                yield break;
            }

            done(new JobResult { Status = 200, ContentType = sniffed, Bytes = data });
        }

        /// <summary>
        /// Content type from magic bytes, not from the upstream header — the header is
        /// attacker-influenced, the file signature is not.
        /// </summary>
        private static string SniffImageType(byte[] d)
        {
            if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
                return "image/png";
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
                return "image/jpeg";
            if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
                               && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
                return "image/webp";
            return null;
        }

        private static bool IsAllowed(string path, string method)
        {
            foreach (var rule in AllowList)
                if (rule.Methods.Contains(method) && rule.Path.IsMatch(path))
                    return true;
            return false;
        }

        private static IEnumerator RelayRoutine(string method, string path, string body, Action<JobResult> done)
        {
            var mod = GeneKermanMod.Instance;
            if (mod == null || mod.Api == null)
            {
                done(JobResult.Error(503, "Mod not ready."));
                yield break;
            }

            yield return mod.Api.Relay(method, path, body, (status, contentType, respBody) =>
            {
                done(new JobResult
                {
                    // Relay reports 0 for a transport failure; surface that as a gateway
                    // error so the page can distinguish "server said no" from "no server".
                    Status = status == 0 ? 502 : (int)status,
                    ContentType = string.IsNullOrEmpty(contentType) ? "application/json" : contentType,
                    Body = respBody ?? "",
                });
            });
        }

        private static string ReadBody(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return null;

            if (req.ContentLength64 > MaxRequestBytes)
                throw new InvalidDataException("body too large");

            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192];
                int total = 0, read;
                while ((read = req.InputStream.Read(buf, 0, buf.Length)) > 0)
                {
                    total += read;
                    // Re-check while reading: ContentLength64 is client-supplied and a
                    // chunked request does not set it at all.
                    if (total > MaxRequestBytes) throw new InvalidDataException("body too large");
                    ms.Write(buf, 0, read);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
