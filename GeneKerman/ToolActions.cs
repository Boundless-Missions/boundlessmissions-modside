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
                craftBytes = RealFuelsTransfer.EmbedInCraft(craftBytes);
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
        ///
        /// A "vessel" send is a hand-over, not a copy: once the server confirms, the
        /// vessel and its crew are queued out of this save exactly like the issuer
        /// side of a rescue (the active vessel can't die under the player, so the
        /// removal lands when they leave it). The server keeps the snapshot — a
        /// decline re-queues it to us as a normal import, so the ship comes home.
        /// </summary>
        public static IEnumerator Quicksend(string recipientId, string recipientName, string kind,
                                            string editorCraftPath, string editorCraftName,
                                            Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            byte[] payload;
            string fileName, craftName;
            string vesselPid = null;
            List<string> vesselCrew = null;

            if (kind == "vessel")
            {
                string node = VesselTransfer.ExportActiveVessel(embedRoster: true);
                if (string.IsNullOrEmpty(node)) { onDone(false, "Could not read the active vessel."); yield break; }

                payload = Encoding.UTF8.GetBytes(node);
                fileName = "vessel.cfg";
                var v = FlightGlobals.ActiveVessel;
                craftName = v != null ? v.vesselName : "Vessel";
                // Captured at the same instant as the snapshot: the pid addresses the
                // removal below (and the server's echo of the decision), and the crew
                // names make it exploit-proof — a kerbal who EVAs off between now and
                // the removal still leaves by name, like a rescue's stranded crew.
                if (v != null)
                {
                    vesselPid = v.id.ToString();
                    vesselCrew = new List<string>();
                    foreach (var pcm in v.GetVesselCrew())
                        if (pcm != null) vesselCrew.Add(pcm.name);
                }
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
                payload = RealFuelsTransfer.EmbedInCraft(payload);
                payload = CkanGenerator.EmbedModsInCraft(payload);
                payload = CraftThumb.EmbedThumbForCurrentCraft(payload);
                fileName = SanitizeFileName(editorCraftName) + ".craft";
                craftName = editorCraftName;
            }

            // Rendered blueprint — what the recipient sees before deciding to accept.
            // Same renderer as a marketplace listing; works on the editor ship and the
            // active vessel alike. Optional: a failed render still sends, just blind.
            byte[] blueprintBytes = null;
            try
            {
                string bpPath = VesselRenderer.CaptureVessel();
                if (!string.IsNullOrEmpty(bpPath) && File.Exists(bpPath))
                    blueprintBytes = File.ReadAllBytes(bpPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Quicksend blueprint render failed: {ex.Message}");
            }

            string message = null;
            bool ok = false;
            bool returnable = false;
            yield return mod.Api.SendCraftToFriend(recipientId, kind, craftName, payload, fileName,
                blueprintBytes, vesselPid,
                (success, resp, _) =>
                {
                    if (success && !string.IsNullOrEmpty(resp))
                    {
                        var d = MiniJSON.DeserializeDict(resp);
                        ok = MiniJSON.GetBool(d, "success", false);
                        // The server's promise that a decline gives the vessel back.
                        // An older server never makes it, and without it the send
                        // stays a copy — removing the ship on our own say-so would
                        // mean a decline deletes it with nothing to return.
                        returnable = MiniJSON.GetBool(d, "vessel_returnable", false);
                        message = ok
                            ? (kind == "vessel" && returnable
                                ? $"Sent to {recipientName} — {craftName} and its crew leave " +
                                  "your save. It comes back if they decline."
                                : $"Sent to {recipientName}. They'll be asked in-game to accept it.")
                            : MiniJSON.GetString(d, "message", "Failed to send.");
                    }
                    else message = "Failed to send.";
                });

            // Only once the server holds the snapshot — losing the vessel on a failed
            // send would destroy the ship and deliver nothing. Same rule and same
            // machinery as issuing a rescue: the queue defers while the player is
            // still flying it, and QueueRescueVesselRemoval says so out loud.
            if (ok && kind == "vessel" && returnable && !string.IsNullOrEmpty(vesselPid))
                mod.QueueRescueVesselRemoval(vesselPid, craftName,
                    VesselTransfer.CrewFate.LeavesWithCraft, vesselCrew);

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

        /// <summary>Mirrors the classic window's CaptureEditorCraft lookup: save folder first, then stock.</summary>
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

        /// <summary>Floors, ours rather than the server's — that only insists on a
        /// non-empty summary. A one-word report costs a maintainer a channel and a
        /// round trip to ask what actually happened, so the bar is here. Public
        /// because the Tools panel gates its Send button on exactly these numbers:
        /// a second copy would drift, and the button would go back to promising a
        /// send that the check below refuses.</summary>
        public const int MinBugSummary = 5;
        public const int MinBugDetails = 15;

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
            // Both messages name the number. "Summarise the bug in one line first"
            // in front of a box that already has a line in it reads as a malfunction,
            // not as a rule — the player has no way to guess that "test" is four
            // characters short of being one.
            if (summary.Length < MinBugSummary)
            {
                onDone(false, "Summarise the bug in one line first — at least " +
                              MinBugSummary + " characters.");
                yield break;
            }
            if (details.Length < MinBugDetails)
            {
                onDone(false, "Add a few words on what you did and what happened — at least " +
                              MinBugDetails + " characters.");
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
                else message = BugFailureMessage(status, resp);
            });

            onDone(ok, message ?? "Could not file the report.");
        }

        /// <summary>
        /// Why the report didn't land, in the player's words plus the one number a
        /// maintainer needs.
        ///
        /// This is the channel bug reports arrive through, so a bug in *it* is
        /// reported by nobody: the player sees one flat sentence and gives up. A
        /// transport failure (nothing reached the server) and a refusal by the server
        /// are opposite problems with opposite fixes, and only the status separates
        /// them — so it is shown rather than logged where the player will not look.
        /// </summary>
        private static string BugFailureMessage(long status, string body)
        {
            if (status == 0)
                return "Couldn't reach the server — check the address in Settings and " +
                       "that you're online.";

            // FastAPI puts the reason in `detail`, but only as a string when it was
            // raised deliberately; a validation failure makes it a list of objects,
            // which is noise to a player and would ToString() as a type name anyway.
            string detail = null;
            if (!string.IsNullOrEmpty(body))
            {
                var d = MiniJSON.DeserializeDict(body);
                if (d != null && d.TryGetValue("detail", out object v) && v is string s)
                    detail = s;
                if (string.IsNullOrEmpty(detail))
                    detail = MiniJSON.GetString(d, "message", null);
            }
            return "The server refused the report (HTTP " + status + ")" +
                   (string.IsNullOrEmpty(detail) ? "." : " — " + detail);
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
            bool craftCustomTextures;
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
                // And say so as a flag of its own, so the listing can be *tagged* as
                // painted rather than leaving a buyer to spot a recolour pack among a long
                // mod row. Not derived from the folders above: a set the sender can't
                // resolve either resolves to nothing while the paint job is still there.
                craftCustomTextures = TextureTransfer.CraftHasCustomTextures(craftBytes);
                // RealFuels/RO add no parts either: union the fuel-config folders in so
                // an RO craft is tagged (and filterable) as one instead of as stock.
                foreach (var f in RealFuelsTransfer.FuelConfigFoldersForCraft(craftBytes))
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
                craftBytes = RealFuelsTransfer.EmbedInCraft(craftBytes);
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
                craftCustomTextures,
                (success, resp, status) =>
                {
                    if (success && !string.IsNullOrEmpty(resp))
                    {
                        var data = MiniJSON.DeserializeDict(resp);
                        ok = MiniJSON.GetBool(data, "success", false);
                        // The success line is built here, not taken from the server, so the
                        // complexity bonus rides in its own field — "reward_note" says either
                        // what was just paid or when the next payout opens. An older server
                        // sends neither and the line reads exactly as it always did.
                        string note = MiniJSON.GetString(data, "reward_note", "");
                        message = ok
                            ? $"{state.EditorCraft} is listed for {price:N0} KCoins."
                              + (string.IsNullOrEmpty(note) ? "" : " " + note)
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
