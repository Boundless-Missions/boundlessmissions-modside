/*
 * ToolActions.cs – The Tools-tab operations, factored so both the classic window and
 * the web bridge can drive them: import a flag from a URL, export a flag-encoded craft,
 * and quicksend a craft to another player.
 *
 * All three touch the filesystem or the network with values the caller supplies, so the
 * validation lives here rather than in whichever UI happened to call it.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class ToolActions
    {
        private const int MaxFlagBytes = 4 * 1024 * 1024;

        // ── Flag import ─────────────────────────────────────────────────────

        /// <summary>
        /// Downloads an image and installs it into the player's flag picker.
        ///
        /// The URL comes from the UI, and the mod fetches it — the same SSRF shape as
        /// the image proxy, but here an arbitrary host is the whole point of the
        /// feature, so a host allow-list is not an option. Instead: scheme check, no
        /// private or loopback addresses (nothing on the player's own machine or LAN),
        /// a size cap, and a magic-byte check so only a real image is ever written.
        ///
        /// The name becomes a filename, so it is stripped of anything path-like.
        /// </summary>
        public static IEnumerator ImportFlag(string url, string name, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            if (!IsPubliclyRoutableHttpUrl(url))
            {
                onDone(false, "That URL is not allowed. Use a public http(s) image link.");
                yield break;
            }

            byte[] data = null;
            bool ok = false;
            yield return mod.Api.DownloadFile(url, (o, bytes) => { ok = o; data = bytes; });

            if (!ok || data == null || data.Length == 0)
            {
                onDone(false, "Could not download the image. Check the URL.");
                yield break;
            }
            if (data.Length > MaxFlagBytes)
            {
                onDone(false, "That image is too large (max 4 MB).");
                yield break;
            }
            if (SniffImage(data) == null)
            {
                onDone(false, "That file is not a PNG or JPEG image.");
                yield break;
            }

            string safeName = SanitizeFileName(name);
            if (safeName.Length == 0) safeName = "Imported Flag";

            bool installed = FlagTransfer.InstallStandaloneFlag(safeName, data);
            onDone(true, installed
                ? "Flag added to your flag picker."
                : "Flag already present in your picker.");
        }

        // ── Craft export ────────────────────────────────────────────────────

        /// <summary>
        /// Writes the loaded craft with its flags, mod list and thumbnail baked in.
        /// Synchronous: local file IO only, no network.
        /// </summary>
        public static bool ExportFlagCraft(string craftPath, string craftName, out string message)
        {
            try
            {
                if (string.IsNullOrEmpty(craftPath) || !File.Exists(craftPath))
                {
                    message = "Save your craft first.";
                    return false;
                }

                byte[] craftBytes = File.ReadAllBytes(craftPath);
                craftBytes = FlagTransfer.EmbedFlagsInCraft(craftBytes);
                craftBytes = CkanGenerator.EmbedModsInCraft(craftBytes);
                craftBytes = CraftThumb.EmbedThumbForCurrentCraft(craftBytes);

                string dir = Path.Combine(GeneKermanMod.PluginDataPath, "ExportedCrafts");
                Directory.CreateDirectory(dir);

                // craftName comes from the loaded ship, but it reaches us through the UI
                // and lands in a path — sanitize regardless of who we think set it.
                string outPath = Path.Combine(dir, SanitizeFileName(craftName) + ".craft");
                File.WriteAllBytes(outPath, craftBytes);

                message = outPath;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Export flag-encoded craft failed: {ex.Message}");
                message = "Failed to export craft.";
                return false;
            }
        }

        // ── Quicksend ───────────────────────────────────────────────────────

        /// <summary>
        /// Sends the active vessel ("vessel") or the loaded editor craft ("craft") to
        /// another player. The payload is read from the game here — the browser has no
        /// access to craft files, which is the whole reason this is a /gk route.
        /// </summary>
        public static IEnumerator Quicksend(string recipientId, string recipientName, string kind,
                                            string editorCraftPath, string editorCraftName,
                                            Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            byte[] payload;
            string fileName, craftName;

            if (kind == "vessel")
            {
                string node = VesselTransfer.ExportActiveVessel(embedRoster: true);
                if (string.IsNullOrEmpty(node)) { onDone(false, "Could not read the active vessel."); yield break; }

                payload = Encoding.UTF8.GetBytes(node);
                fileName = "vessel.cfg";
                craftName = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "Vessel";
            }
            else
            {
                if (string.IsNullOrEmpty(editorCraftPath) || !File.Exists(editorCraftPath))
                {
                    onDone(false, "Save your craft first.");
                    yield break;
                }
                byte[] craftBytes = File.ReadAllBytes(editorCraftPath);
                payload = FlagTransfer.EmbedFlagsInCraft(craftBytes);
                payload = CkanGenerator.EmbedModsInCraft(payload);
                payload = CraftThumb.EmbedThumbForCurrentCraft(payload);
                fileName = SanitizeFileName(editorCraftName) + ".craft";
                craftName = editorCraftName;
            }

            string message = null;
            bool ok = false;
            yield return mod.Api.SendCraftToFriend(recipientId, kind, craftName, payload, fileName,
                (success, resp, _) =>
                {
                    if (success && !string.IsNullOrEmpty(resp))
                    {
                        var d = MiniJSON.DeserializeDict(resp);
                        ok = MiniJSON.GetBool(d, "success", false);
                        message = ok
                            ? $"Sent to {recipientName}. They'll get it at the Space Center."
                            : MiniJSON.GetString(d, "message", "Failed to send.");
                    }
                    else message = "Failed to send.";
                });

            onDone(ok, message ?? "Failed to send.");
        }

        // ── Guards ──────────────────────────────────────────────────────────

        /// <summary>
        /// http/https only, and never an address on this machine or a private network.
        /// A flag import is a blind fetch (the bytes are written to disk, not returned),
        /// but "blind" is not "harmless" — it would still let a caller probe the local
        /// network through the mod.
        /// </summary>
        private static bool IsPubliclyRoutableHttpUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            // Literal IPs are checked directly. Hostnames are left to DNS at fetch time:
            // resolving here and trusting it would be a TOCTOU race, and the size and
            // image checks below bound what a rebind could achieve anyway.
            if (IPAddress.TryParse(uri.Host, out IPAddress ip) && IsPrivate(ip)) return false;

            string h = uri.Host.ToLowerInvariant();
            return h != "localhost" && !h.EndsWith(".localhost") && !h.EndsWith(".local");
        }

        private static bool IsPrivate(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 10) return true;                                  // 10/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16/12
                if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16
                if (b[0] == 169 && b[1] == 254) return true;                  // link-local
                if (b[0] == 127) return true;
                if (b[0] == 0) return true;
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        }

        /// <summary>Reduces a display name to something safe to put in a path.</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (char c in name.Trim())
            {
                if (c == '/' || c == '\\' || c == ':' || c < ' ' || invalid.Contains(c)) continue;
                sb.Append(c);
            }
            // "." and ".." survive the loop above and are still path-meaningful.
            string s = sb.ToString().Trim(' ', '.');
            return s.Length > 64 ? s.Substring(0, 64) : s;
        }

        private static string SniffImage(byte[] d)
        {
            if (d.Length >= 4 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "image/png";
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
            return null;
        }
    }
}
