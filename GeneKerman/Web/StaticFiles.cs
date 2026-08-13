/*
 * StaticFiles.cs – Serves the built web UI out of GameData/BoundlessMissions/WebUI/.
 *
 * The bundle is not secret (it ships in GameData and any player can read it), so these
 * routes need no session. What they do need is to be impossible to walk out of: the
 * same process holds session.token, the player's saves, and every other KSP file, and
 * this is the only place where a request path becomes a filesystem path.
 *
 * Hence three independent barriers, deliberately redundant:
 *   - reject suspicious characters and segments before touching the filesystem,
 *   - resolve with GetFullPath and re-verify the result is under the root,
 *   - only ever serve an allow-listed extension.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;

namespace GeneKerman.Web
{
    public sealed class StaticFiles
    {
        private readonly string root;
        private readonly string rootPrefix;

        /// <summary>
        /// Nothing outside this list is served, whatever is on disk. Keeps a stray
        /// .cfg, .log or .dll dropped into WebUI/ from ever being readable.
        /// </summary>
        private static readonly Dictionary<string, string> MimeTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".js",   "text/javascript; charset=utf-8" },
            { ".css",  "text/css; charset=utf-8" },
            { ".json", "application/json" },
            { ".svg",  "image/svg+xml" },
            { ".png",  "image/png" },
            { ".webp", "image/webp" },
            { ".ico",  "image/x-icon" },
            { ".woff2","font/woff2" },
            { ".map",  "application/json" },
        };

        public StaticFiles(string modPath)
        {
            root = Path.GetFullPath(Path.Combine(modPath, "WebUI"));
            rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        public bool BundleExists => File.Exists(Path.Combine(root, "index.html"));

        /// <summary>
        /// Confirms the bundle on disk was built for this DLL. CKAN installs, manual
        /// installs and hand-copied folders coexist, so a WebUI/ from a different mod
        /// version WILL happen — and a UI silently calling endpoints the DLL does not
        /// implement is far worse to debug than a refusal at startup.
        /// </summary>
        public bool VersionMatches(out string found)
        {
            found = null;
            try
            {
                string manifestPath = Path.Combine(root, "manifest.json");
                if (!File.Exists(manifestPath)) return false;

                var dict = MiniJSON.DeserializeDict(File.ReadAllText(manifestPath));
                if (dict == null) return false;

                found = MiniJSON.GetString(dict, "modVersion");
                return found == ModVersion.Current;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] WebUI manifest unreadable: " + e.Message);
                return false;
            }
        }

        /// <summary>Returns false if the request was not a static asset (caller 404s).</summary>
        public bool TryServe(HttpListenerContext ctx, string absolutePath)
        {
            string rel = ResolveSafe(absolutePath);
            if (rel == null)
            {
                // 404 rather than 403: a rejection that distinguishes "blocked" from
                // "absent" tells a prober which paths exist.
                Respond(ctx, 404, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"not_found\"}"), null);
                return true;
            }

            string full = Path.GetFullPath(Path.Combine(root, rel));

            // Barrier 2: whatever the string manipulation above produced, the resolved
            // path must still live under the root.
            if (!full.StartsWith(rootPrefix, StringComparison.Ordinal) && full != root)
            {
                Debug.LogWarning("[GeneKerman] WebUI path escaped root, refused.");
                Respond(ctx, 404, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"not_found\"}"), null);
                return true;
            }

            // SPA fallback: an extensionless path that is not a file is a client route,
            // so hand back the shell and let the app router deal with it.
            if (!File.Exists(full) && string.IsNullOrEmpty(Path.GetExtension(full)))
                full = Path.Combine(root, "index.html");

            if (!File.Exists(full))
            {
                Respond(ctx, 404, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"not_found\"}"), null);
                return true;
            }

            // Barrier 3.
            string ext = Path.GetExtension(full);
            if (!MimeTypes.TryGetValue(ext, out string mime))
            {
                Respond(ctx, 404, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"not_found\"}"), null);
                return true;
            }

            try
            {
                var info = new FileInfo(full);
                string etag = "\"" + info.LastWriteTimeUtc.Ticks.ToString("x") + "-" + info.Length.ToString("x") + "\"";

                if (ctx.Request.Headers["If-None-Match"] == etag)
                {
                    ctx.Response.StatusCode = 304;
                    LocalServer.ApplySecurityHeaders(ctx.Response, false);
                    ctx.Response.Headers["ETag"] = etag;
                    try { ctx.Response.Close(); } catch { }
                    return true;
                }

                Respond(ctx, 200, mime, File.ReadAllBytes(full), etag);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] WebUI read failed: " + e.Message);
                Respond(ctx, 500, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"read_failed\"}"), null);
            }
            return true;
        }

        /// <summary>
        /// Barrier 1. Returns a relative path, or null to refuse.
        ///
        /// The bundle is generated by our own build, so every legitimate filename is
        /// plain ASCII with no escaping — which lets us refuse percent-encoding outright
        /// rather than decode it and then try to out-think double-encoding tricks.
        /// </summary>
        private static string ResolveSafe(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            if (absolutePath == "/") return "index.html";

            if (absolutePath.IndexOf('%') >= 0) return null;   // no encoding needed, so none allowed
            if (absolutePath.IndexOf('\\') >= 0) return null;  // Windows separator
            if (absolutePath.IndexOf('\0') >= 0) return null;
            if (absolutePath.IndexOf(':') >= 0) return null;   // drive letters / ADS

            var parts = absolutePath.Split('/');
            var kept = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                if (p.Length == 0) continue;
                if (p == "." || p == "..") return null;
                kept.Add(p);
            }

            return kept.Count == 0 ? "index.html" : string.Join("/", kept.ToArray());
        }

        private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] body, string etag)
        {
            var res = ctx.Response;
            try
            {
                res.StatusCode = status;
                res.ContentType = contentType;
                LocalServer.ApplySecurityHeaders(res, contentType.StartsWith("text/html"));
                if (etag != null)
                {
                    res.Headers["ETag"] = etag;
                    // Revalidate every time. Over loopback a 304 costs nothing, and it
                    // means a rebuilt bundle is never served stale from the browser cache.
                    res.Headers["Cache-Control"] = "no-cache";
                }
                res.ContentLength64 = body.Length;
                res.OutputStream.Write(body, 0, body.Length);
            }
            catch (Exception) { /* client hung up */ }
            finally { try { res.Close(); } catch { } }
        }
    }
}
