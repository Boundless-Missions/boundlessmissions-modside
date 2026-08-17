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
                // Bake the scale FIRST: the file on disk carries raw TweakScale data, and
                // a blueprint has no import-side scale step to fix it later.
                craftBytes = ScaleBridge.BakeEditorCraft(craftBytes);
                craftBytes = FlagTransfer.EmbedFlagsInCraft(craftBytes);
                craftBytes = TweakScaleGuard.EmbedVersionInCraft(craftBytes);
                craftBytes = TextureTransfer.EmbedInCraft(craftBytes);
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
                // Bake the scale FIRST — see ScaleBridge.BakeEditorCraft. Without this a
                // quicksent craft arrives with raw TweakScale data and no way to repair it.
                payload = ScaleBridge.BakeEditorCraft(craftBytes);
                payload = FlagTransfer.EmbedFlagsInCraft(payload);
                payload = TweakScaleGuard.EmbedVersionInCraft(payload);
                payload = TextureTransfer.EmbedInCraft(payload);
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

        // ── Craft state ─────────────────────────────────────────────────────

        /// <summary>
        /// What the Tools screens need to know about the running game: which craft is
        /// open in the editor, whether it has ever been saved (nothing can be sent or
        /// exported until it has — there is no file to read), and what is being flown.
        ///
        /// Read on the main thread only. Every caller already is one: the bridge goes
        /// through MainThreadQueue, the sidebar runs in Update.
        /// </summary>
        public struct CraftState
        {
            public string EditorCraft;
            public string EditorType;
            public int EditorParts;
            /// <summary>The saved .craft on disk. Empty when the craft is unsaved.</summary>
            public string EditorPath;
            public string ActiveVessel;

            public bool EditorSaved => !string.IsNullOrEmpty(EditorPath);

            /// <summary>
            /// "vessel", "craft", or null when there is nothing to send. A flying
            /// vessel goes as a live vessel, crew and all; otherwise a saved editor
            /// craft goes as a blueprint. All three front ends apply this same rule.
            /// </summary>
            public string SendKind =>
                !string.IsNullOrEmpty(ActiveVessel) ? "vessel"
                : (!string.IsNullOrEmpty(EditorCraft) && EditorSaved) ? "craft"
                : null;
        }

        public static CraftState ReadCraftState()
        {
            var state = new CraftState
            {
                EditorCraft = "",
                EditorType = "",
                EditorPath = "",
                ActiveVessel = "",
            };

            try
            {
                var ship = EditorLogic.fetch?.ship;
                if (ship != null)
                {
                    state.EditorCraft = ship.shipName ?? "Untitled";
                    state.EditorParts = ship.parts?.Count ?? 0;
                    state.EditorType = EditorDriver.editorFacility == EditorFacility.VAB ? "VAB" : "SPH";
                    state.EditorPath = FindSavedCraftPath(state.EditorCraft, state.EditorType);
                }
            }
            catch (Exception) { /* not in the editor */ }

            try
            {
                state.ActiveVessel = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName : "";
            }
            catch (Exception) { }

            return state;
        }

        /// <summary>Mirrors MainWindow.CaptureEditorCraft's lookup: save folder first, then stock.</summary>
        public static string FindSavedCraftPath(string name, string type)
        {
            try
            {
                string saveFolder = HighLogic.SaveFolder ?? "default";
                string p = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder,
                                        "Ships", type, name + ".craft");
                if (File.Exists(p)) return p;

                p = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", type, name + ".craft");
                if (File.Exists(p)) return p;
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>
        /// Export whatever is open in the editor. The path is resolved here rather
        /// than passed in: a filesystem path must never arrive from a UI, least of
        /// all from a page served over HTTP.
        /// </summary>
        public static bool ExportCurrentCraft(out string message)
        {
            var state = ReadCraftState();
            if (string.IsNullOrEmpty(state.EditorCraft))
            {
                message = "Open a craft in the VAB or SPH first.";
                return false;
            }
            return ExportFlagCraft(state.EditorPath, state.EditorCraft, out message);
        }

        /// <summary>
        /// Quicksend the current vessel or editor craft. Same reasoning as
        /// <see cref="ExportCurrentCraft"/>: the caller names the recipient and the
        /// kind, never a path.
        /// </summary>
        public static IEnumerator QuicksendCurrent(string recipientId, string recipientName,
                                                   string kind, Action<bool, string> onDone)
        {
            string path = "", name = "";
            if (kind == "craft")
            {
                var state = ReadCraftState();
                if (string.IsNullOrEmpty(state.EditorCraft))
                {
                    onDone(false, "Open a craft in the VAB or SPH first.");
                    yield break;
                }
                path = state.EditorPath;
                name = state.EditorCraft;
            }

            yield return Quicksend(recipientId, recipientName, kind, path, name, onDone);
        }

        // ── Bug report ──────────────────────────────────────────────────────

        /// <summary>Caps mirroring the server's, so an over-long report is trimmed
        /// where the player can still see what they wrote rather than silently on
        /// arrival.</summary>
        public const int MaxBugSummary = 200;
        public const int MaxBugDetails = 1500;

        /// <summary>
        /// File a bug report against the mod. The server turns it into a private
        /// Discord ticket the reporter can be replied to in.
        ///
        /// <paramref name="attachLog"/> reflects the switch in the Tools tab: KSP.log
        /// is the one artefact that makes most KSP bugs diagnosable, and also the one
        /// that carries the player's mod list, install paths and machine specs — so it
        /// is their call, made every time, never a default that hides.
        /// </summary>
        public static IEnumerator SubmitBugReport(string summary, string details, bool attachLog,
                                                  Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }
            if (!mod.Api.IsLinked) { onDone(false, "Link this install to Discord first."); yield break; }
            // Named rather than left to fail as a network error: a blocked send looks
            // identical to an unreachable server from the callback, and this one the
            // player can actually fix.
            if (mod.Api.TransmissionBlocked)
            {
                onDone(false, "The mod isn't allowed to send anything — check the data-sharing " +
                              "switch in Settings.");
                yield break;
            }

            summary = (summary ?? "").Trim();
            details = (details ?? "").Trim();
            if (summary.Length < 5)
            {
                onDone(false, "Summarise the bug in one line first.");
                yield break;
            }
            if (details.Length < 15)
            {
                onDone(false, "Add a few words on what you did and what happened.");
                yield break;
            }
            if (summary.Length > MaxBugSummary) summary = summary.Substring(0, MaxBugSummary);
            if (details.Length > MaxBugDetails) details = details.Substring(0, MaxBugDetails);

            bool ok = false;
            string message = null;
            yield return mod.Api.SubmitBugReport(summary, details, attachLog, (success, resp, status) =>
            {
                if (success && !string.IsNullOrEmpty(resp))
                {
                    var d = MiniJSON.DeserializeDict(resp);
                    ok = MiniJSON.GetBool(d, "success", false);
                    message = MiniJSON.GetString(d, "message",
                        ok ? "Bug reported. Thank you." : "Could not file the report.");
                }
                else if (status == 429)
                {
                    // The one failure the player can act on, so it is named rather
                    // than folded into "could not send".
                    message = "You've filed several reports already — try again later.";
                }
                else message = "Could not reach the server to file the report.";
            });

            onDone(ok, message ?? "Could not file the report.");
        }

        // ── Marketplace ─────────────────────────────────────────────────────

        /// <summary>
        /// Mass (t) and full funds cost of the craft open in the editor.
        ///
        /// Separate from <see cref="ReadCraftState"/> because it walks every part and
        /// asks each one's modules what they cost — fine when a screen wants to show
        /// the figures, wasteful in the once-a-second poll that only needs to know
        /// whether a craft is loaded at all.
        /// </summary>
        public static void ReadEditorValue(out float mass, out float cost)
        {
            mass = 0f;
            cost = 0f;
            try
            {
                var ship = EditorLogic.fetch?.ship;
                if (ship?.parts == null) return;

                foreach (var part in ship.parts)
                {
                    mass += part.mass + part.GetResourceMass();
                    cost += VesselDataCollector.GetPartCost(part);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not value the editor craft: {ex.Message}");
            }
        }

        /// <summary>
        /// List the craft open in the editor on the marketplace.
        ///
        /// The whole payload is assembled here — the .craft with its flags, mod list
        /// and thumbnail baked in, a rendered blueprint, a square listing thumbnail,
        /// and the life-support flag the listing is filtered by. None of that is
        /// reachable from a browser tab, which is why this is an action rather than
        /// something a front end does for itself; and the price is parsed here so all
        /// three front ends reject the same inputs.
        ///
        /// <paramref name="onProgress"/> carries the two long steps (the blueprint
        /// render, then the upload) to whichever status line the caller has.
        /// </summary>
        public static IEnumerator SellCurrentCraft(string priceText, Action<string> onProgress,
                                                   Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            var state = ReadCraftState();
            if (string.IsNullOrEmpty(state.EditorCraft))
            {
                onDone(false, "Open a craft in the VAB or SPH first.");
                yield break;
            }
            if (!state.EditorSaved)
            {
                onDone(false, "Save your craft first: there is no file to list yet.");
                yield break;
            }
            if (!int.TryParse((priceText ?? "").Trim(), out int price) || price <= 0)
            {
                onDone(false, "Enter a price above zero.");
                yield break;
            }

            byte[] craftBytes;
            string craftMods;
            string craftParts;
            try
            {
                craftBytes = File.ReadAllBytes(state.EditorPath);
                // Bake the scale before anything reads these bytes — see
                // ScaleBridge.BakeEditorCraft. A listing is bought by strangers on unknown
                // installs, which is exactly the case an unbaked craft breaks on. Baking
                // rewrites MODULE nodes and positions but never a part name, so the tags
                // below are the same either way.
                craftBytes = ScaleBridge.BakeEditorCraft(craftBytes);
                // Tag the listing with the craft's mods (from the ORIGINAL bytes, before
                // any GKMODS/flag/thumb blocks are appended) so the website can filter by mod.
                // A recolour pack adds no parts, so the part walk above cannot see it and
                // a Textures Unlimited craft would tag as stock. Resolve the paint job's
                // packs separately and union them in, or the website's mod filter quietly
                // lies about what this craft needs to look the way the screenshot does.
                var modFolders = CkanGenerator.ModFoldersForCraft(craftBytes);
                foreach (var f in TextureTransfer.TexturePackFoldersForCraft(craftBytes))
                    if (!modFolders.Contains(f)) modFolders.Add(f);
                craftMods = string.Join(",", modFolders.ToArray());
                // And with its exact part names, so the server can tell a buyer which parts
                // they're missing before they spend anything — a mod-folder tag can't, since
                // owning the mod is no guarantee of owning the part (a DLC part vs its
                // ReStock+ stand-in, say). Same ORIGINAL bytes, same reason.
                craftParts = string.Join(",", CkanGenerator.PartNamesForCraft(craftBytes).ToArray());
                craftBytes = FlagTransfer.EmbedFlagsInCraft(craftBytes);
                craftBytes = TweakScaleGuard.EmbedVersionInCraft(craftBytes);
                craftBytes = TextureTransfer.EmbedInCraft(craftBytes);
                craftBytes = CkanGenerator.EmbedModsInCraft(craftBytes);
                craftBytes = CraftThumb.EmbedThumbForCurrentCraft(craftBytes); // NW thumbnail (last)
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Failed to read craft: {ex.Message}");
                onDone(false, "Could not read the craft file.");
                yield break;
            }

            ReadEditorValue(out float mass, out float cost);

            onProgress?.Invoke("Rendering blueprint...");

            // Rendered blueprint — shown publicly on the listing.
            byte[] blueprintBytes = null;
            try
            {
                string bpPath = VesselRenderer.CaptureVessel();
                if (!string.IsNullOrEmpty(bpPath) && File.Exists(bpPath))
                    blueprintBytes = File.ReadAllBytes(bpPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Blueprint render failed: {ex.Message}");
            }

            // Square NW-view thumbnail — the website shows this on the listing card
            // (the full blueprint is reserved for the detail view).
            byte[] thumbnailBytes = null;
            try
            {
                thumbnailBytes = VesselRenderer.CaptureNWThumbnail();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Thumbnail render failed: {ex.Message}");
            }

            // Life-support flag: which LS mod the craft is provisioned for and how long
            // it lasts, read from the live editor ship (tags as "none" if stock).
            LifeSupportInfo ls = LifeSupportScan.FromEditor();

            onProgress?.Invoke("Listing craft...");

            bool ok = false;
            string message = null;
            yield return mod.Api.ListCraftForSale(
                craftBytes, SanitizeFileName(state.EditorCraft) + ".craft",
                state.EditorCraft, state.EditorType, state.EditorParts, mass, cost, price,
                blueprintBytes, thumbnailBytes, craftMods, craftParts,
                ls.ModKey, ls.EnduranceDaysPerKerbal, ls.CrewCapacity,
                (success, resp, status) =>
                {
                    if (success && !string.IsNullOrEmpty(resp))
                    {
                        var data = MiniJSON.DeserializeDict(resp);
                        ok = MiniJSON.GetBool(data, "success", false);
                        message = ok
                            ? $"{state.EditorCraft} is listed for {price:N0} KCoins."
                            : MiniJSON.GetString(data, "message", "Failed to list.");
                    }
                    else message = "Failed to list craft.";
                });

            onDone(ok, message ?? "Failed to list craft.");
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
