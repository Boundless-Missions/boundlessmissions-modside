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
            // The rule travels with the request: `IsPubliclyRoutableHttpUrl` is re-run
            // against wherever the chain actually lands, so a public host cannot 302
            // the fetch onto 127.0.0.1 or the LAN — which is what made this a
            // port scanner with the three outcomes below as its oracle.
            yield return mod.Api.DownloadFile(url, (o, bytes) => { ok = o; data = bytes; },
                                              u => IsPubliclyRoutableHttpUrl(u.AbsoluteUri));

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
            // FlagTransfer refuses this too — it now judges every image before writing
            // it into GameData, where KSP's own loader decodes it on every launch. Asked
            // here as well so the refusal has a sentence: the installer's "no" is
            // indistinguishable from "you already have this flag".
            string sizeRefusal;
            if (!ImageIsSafeToDecode(data, out sizeRefusal))
            {
                onDone(false, "That image cannot be used as a flag (" + sizeRefusal + ").");
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
                string sentRefusal = mod.PendingRemovalRefusal(FlightGlobals.ActiveVessel);
                if (sentRefusal != null) { onDone(false, sentRefusal); yield break; }

                // Before the snapshot is read, let alone uploaded: a hand-over we cannot
                // complete now is one that may never complete at all (see
                // GeneKermanMod.LiveHandoverRefusal), and the failure mode is the server
                // offering a ship the sender still has. Refusing here costs the player a
                // sentence; refusing after the upload would cost them a duplicate.
                //
                // Vessel branch only, and that is the point: a "craft" send is a
                // blueprint, removes nothing from this save, and is sendable in any
                // flight state — gating it would break sending from the editor, which
                // was never at risk.
                string handoverRefusal = GeneKermanMod.LiveHandoverRefusal();
                if (handoverRefusal != null) { onDone(false, handoverRefusal); yield break; }

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
                                ? $"Sent to {recipientName}. {craftName} and its crew leave " +
                                  "your save. It comes back if they decline."
                                : $"Sent to {recipientName}. They'll be asked in-game to accept it.")
                            : MiniJSON.GetString(d, "message", "Failed to send.");
                    }
                    else message = "Failed to send.";
                });

            // Only once the server holds the snapshot — losing the vessel on a failed
            // send would destroy the ship and deliver nothing. Same rule and same
            // machinery as issuing a rescue: the queue defers while the player is
            // still flying it, and returnToSpaceCenter closes that deferral by taking
            // them home so the removal runs now, not at some later visit a decline
            // could race.
            if (ok && kind == "vessel" && returnable && !string.IsNullOrEmpty(vesselPid))
            {
                // Write the hand-over down BEFORE queueing the removal, and outside the
                // save. The scenario's queue is rolled back by a quickload — which is
                // exactly the case the server's later "craft_gift_accepted" echo exists
                // to repair — so the echo needs a record that a quickload cannot erase,
                // and one that says this client really did hand this pid over, to this
                // server, out of this save. Without it the handler acted on any pid a
                // server named, destroying the vessel and striking its crew off the
                // roster for good. See QuicksendLedger and MaybeHandleGiftAccepted.
                QuicksendLedger.Record(vesselPid, craftName, mod.Api.ServerUrl);
                mod.QueueRescueVesselRemoval(vesselPid, craftName,
                    VesselTransfer.CrewFate.LeavesWithCraft, vesselCrew,
                    returnToSpaceCenter: true);
            }

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

            /// <summary>Why the active vessel cannot be handed over right now, or empty
            /// when it can — <see cref="GeneKermanMod.LiveHandoverRefusal"/>, read here
            /// so the front ends can disable Send and say why instead of letting the
            /// press fail. Only ever set while a vessel is being flown: a blueprint
            /// takes nothing out of the save and is sendable in any flight state.</summary>
            public string VesselSendBlock;

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

            /// <summary>Whether Send may be offered at all. Deliberately separate from
            /// <see cref="SendKind"/>, which still answers *what* would be sent: a
            /// blocked live vessel is a vessel send that cannot run right now, not a
            /// craft send, and collapsing the two would silently offer the blueprint of
            /// a ship the player asked to hand over.</summary>
            public bool CanSend =>
                SendKind == "craft" ||
                (SendKind == "vessel" && string.IsNullOrEmpty(VesselSendBlock));
        }

        public static CraftState ReadCraftState()
        {
            var state = new CraftState
            {
                EditorCraft = "",
                EditorType = "",
                EditorPath = "",
                ActiveVessel = "",
                VesselSendBlock = "",
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
                var active = FlightGlobals.ActiveVessel;
                state.ActiveVessel = active != null ? active.vesselName : "";
                // Only while something is being flown, and on the silent overload: this
                // is polled roughly once a second per open Tools screen, and KSP's
                // logging overload writes a "Cannot save" line on every call — which
                // would bury the one that actually matters, at the moment of a send, in
                // the KSP.log a bug report ships.
                if (active != null)
                    state.VesselSendBlock = GeneKermanMod.LiveHandoverRefusal() ?? "";
            }
            catch (Exception) { }

            return state;
        }

        /// <summary>The .craft file to send for whatever is open in the editor: a snapshot
        /// of the LIVE ship, written to our own PluginData.
        ///
        /// Not <see cref="CraftState.EditorPath"/>, which is only ever "a file of that name
        /// exists in this save's Ships folder" — and a craft's name is not an identity.
        /// KSP's default name is "Untitled Space Craft", so a player who has ever saved one
        /// has a file that answers to every later one, and every editor path here would send
        /// THAT: the blueprint and the stats are read from the live ship while the craft file,
        /// its part list and its mod tags come off last week's design. It listed one ship and
        /// delivered another, twice in a row, before the mismatch was noticed at all.
        ///
        /// Snapshotting rather than calling ShipConstruction.SaveShip (which is what the
        /// submission path does) because saving is the player's decision: writing into their
        /// Ships folder to satisfy an upload would silently overwrite the very file this bug
        /// is about. The snapshot is ours, overwritten each time, and never cleaned up — it
        /// is one small file and the alternative is deleting it out from under a coroutine
        /// that has not read it yet.
        ///
        /// The self-check on the first line is not paranoia about the format: ConfigNode
        /// writes a NAMED node wrapped in braces, which KSP's craft loader rejects (the same
        /// trap TextureTransfer and FlagTransfer avoid by never reparsing a craft). A wrapped
        /// or empty result therefore falls back to the file on disk rather than shipping a
        /// craft nobody can open.</summary>
        public static string EditorCraftSource(CraftState state)
        {
            try
            {
                var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                if (ship != null && ship.parts != null && ship.parts.Count > 0)
                {
                    ConfigNode node = ship.SaveShip();
                    if (node != null)
                    {
                        string dir = Path.Combine(GeneKermanMod.PluginDataPath, "EditorSnapshot");
                        Directory.CreateDirectory(dir);
                        string name = SanitizeFileName(state.EditorCraft ?? "");
                        if (name.Length == 0) name = "Untitled";
                        string path = Path.Combine(dir, name + ".craft");
                        node.Save(path);

                        if (File.Exists(path))
                        {
                            string head = "";
                            using (var sr = new StreamReader(path)) head = sr.ReadLine() ?? "";
                            if (head.TrimStart().StartsWith("ship", StringComparison.OrdinalIgnoreCase))
                                return path;
                            Debug.LogWarning("[GeneKerman] EditorCraftSource: snapshot is not a craft file " +
                                             $"(first line '{head}'), using the saved file instead.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] EditorCraftSource: snapshot failed: {ex.Message}");
            }
            return state.EditorPath;
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
            return ExportFlagCraft(EditorCraftSource(state), state.EditorCraft, out message);
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
                path = EditorCraftSource(state);
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
                onDone(false, "The mod isn't allowed to send anything; check the data-sharing " +
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
                onDone(false, "Summarise the bug in one line first, at least " +
                              MinBugSummary + " characters.");
                yield break;
            }
            if (details.Length < MinBugDetails)
            {
                onDone(false, "Add a few words on what you did and what happened, at least " +
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
                    message = "You've filed several reports already; try again later.";
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
                return "Couldn't reach the server; check the address in Settings and " +
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
                   (string.IsNullOrEmpty(detail) ? "." : ": " + detail);
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
                craftBytes = File.ReadAllBytes(EditorCraftSource(state));
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
                // Reforged Materials Redux is the same blind spot again — a TU addon that
                // adds no parts and stores its paint in its own module, so neither the part
                // walk nor TU's texture-set scan sees it. Only a craft that was actually
                // painted contributes, since its module rides on every part of every craft
                // saved on a Reforged install.
                foreach (var f in ReforgedTransfer.PaintFoldersForCraft(craftBytes))
                    if (!modFolders.Contains(f)) modFolders.Add(f);
                // And say so as a flag of its own, so the listing can be *tagged* as
                // painted rather than leaving a buyer to spot a recolour pack among a long
                // mod row. Not derived from the folders above: a set the sender can't
                // resolve either resolves to nothing while the paint job is still there.
                craftCustomTextures = TextureTransfer.CraftHasCustomTextures(craftBytes)
                                      || ReforgedTransfer.CraftHasPaint(craftBytes);
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

                        // The complexity bonus just moved this player's balance, and
                        // nothing else would notice: the profile is fetched on link,
                        // on opening the panel and from the Profile tab's own refresh
                        // button — there is no timer. Without this the toast says
                        // "+300 KCoins" while every balance on screen keeps the old
                        // number until the panel is reopened.
                        if (ok) GeneKermanMod.Instance?.State?.RequestProfileRefresh();
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
            // An IPv4-mapped IPv6 literal (::ffff:127.0.0.1) parses as IPv6 and would
            // otherwise skip the whole v4 table below.
            if (ip.IsIPv4MappedToIPv6)
            {
                try { ip = ip.MapToIPv4(); b = ip.GetAddressBytes(); }
                catch { return true; }   // unreadable: refuse rather than allow
            }
            if (b.Length == 4)
            {
                if (b[0] == 10) return true;                                  // 10/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16/12
                if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16
                if (b[0] == 169 && b[1] == 254) return true;                  // link-local
                if (b[0] == 127) return true;
                if (b[0] == 0) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;    // 100.64/10 CGNAT
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

        public static string SniffImage(byte[] d)
        {
            if (d == null) return null;
            if (d.Length >= 4 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "image/png";
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
            return null;
        }

        // ── Image decode guard (every LoadImage site) ────────────────────────
        //
        // Texture2D.LoadImage allocates from the *declared* dimensions, so a few
        // kilobytes of PNG whose IHDR says 60000×60000 asks Unity for ~14 GB and takes
        // the game down before anything can catch it. Every image that reaches a
        // LoadImage here came off the wire — a peer's flag, a peer's blueprint, an
        // avatar, a submission screenshot — so the header is read and judged first.
        // The byte cap alone cannot do this job: compression is exactly what makes a
        // decompression bomb small.

        // The first cut of these caps was set from what an image "ought" to weigh rather
        // than from what this mod actually produces, and landed under its own output:
        // 8 MB / 16 MP against a blueprint sheet that is 4096×2200 = 9.01 MP and up to
        // 3.28 MB, and a checkpoint screenshot that is ScreenCapture.CaptureScreenshot at
        // native resolution with no downscale — about 7.8 MB from a 4K submitter. Worse,
        // they sat *below the server's*, which accepts MAX_BLUEPRINT_BYTES (~13.4 MB) and
        // 30 MP: an image could upload cleanly and then be undecodable at the other end,
        // so a reviewer judged a submission blind and NotificationsPanel drew a blank
        // panel with nothing said. And 16 MP was only 1.8× the blueprint's own 9.01 MP,
        // so raising BLUEPRINT_SCALE to 3 would have made the mod refuse its own render.
        //
        // So both ceilings are now the server's, expressed the same way it expresses
        // them. The point of the guard is unchanged and is carried by the *pixel* cap,
        // not the byte one: 30 MP × 4 B is 120 MB, which a machine running KSP survives,
        // where the 60000×60000 PNG this exists to stop asks for ~14 GB.

        /// <summary>Hard ceiling on the compressed bytes of any image we decode. Mirrors
        /// api_server.MAX_BLUEPRINT_BYTES — 512 KB of headers plus a 1.5 B/px budget over
        /// the blueprint sheet's own pixels — and is derived from the same expression, so
        /// raising VesselRenderer.SCALE moves the mod's ceiling and the server's together
        /// (settings.BLUEPRINT_SCALE is the other half of that pair).</summary>
        public const int MaxImageBytes =
            512 * 1024 + (int)(2048 * 1100 * VesselRenderer.SCALE * VesselRenderer.SCALE * 1.5);
        /// <summary>Per-side ceiling. Generous on purpose — the pixel cap below is what
        /// bounds the allocation, and this only catches a degenerate strip that slips
        /// under it.</summary>
        public const int MaxImageDimension = 16384;
        /// <summary>Total pixels, matching settings.MAX_IMAGE_PIXELS on the server: an
        /// image the server accepted is one this client can decode. 30 MP is 120 MB as
        /// RGBA32, against 9 MP for the largest blueprint and 8 MP for a 4K screenshot.</summary>
        public const int MaxImagePixels = 30000000;

        /// <summary>
        /// True when these bytes are a PNG/JPEG this machine can afford to decode.
        /// Unparseable dimensions are refused rather than waved through: LoadImage
        /// would fail on them anyway, and "I could not tell how big it is" is not a
        /// reason to hand it to the decoder.
        /// </summary>
        public static bool ImageIsSafeToDecode(byte[] d, out string reason)
        {
            reason = null;
            if (d == null || d.Length == 0) { reason = "empty"; return false; }
            if (d.Length > MaxImageBytes)
            {
                reason = $"{d.Length} bytes, over the {MaxImageBytes / (1024 * 1024)} MB cap";
                return false;
            }

            string mime = SniffImage(d);
            if (mime == null) { reason = "not a PNG or JPEG"; return false; }

            int w, h;
            if (!TryReadImageSize(d, mime, out w, out h))
            {
                reason = "dimensions could not be read";
                return false;
            }
            if (w <= 0 || h <= 0 || w > MaxImageDimension || h > MaxImageDimension ||
                (long)w * h > MaxImagePixels)
            {
                reason = $"declares {w}x{h}";
                return false;
            }
            return true;
        }

        /// <summary>Convenience wrapper: log the refusal and answer yes/no.</summary>
        public static bool ImageIsSafeToDecode(byte[] d, string what)
        {
            string why;
            if (ImageIsSafeToDecode(d, out why)) return true;
            Debug.LogWarning($"[GeneKerman] Refused to decode {what}: {why}.");
            return false;
        }

        /// <summary>Read width/height out of a PNG IHDR or a JPEG SOF segment without
        /// decoding anything. Both walks are bounded by the buffer length.</summary>
        private static bool TryReadImageSize(byte[] d, string mime, out int w, out int h)
        {
            w = h = 0;
            try
            {
                if (mime == "image/png")
                {
                    // 8-byte signature, then the IHDR chunk: length, "IHDR", w, h — all
                    // big-endian, so width sits at 16 and height at 20.
                    if (d.Length < 24) return false;
                    if (d[12] != 'I' || d[13] != 'H' || d[14] != 'D' || d[15] != 'R') return false;
                    w = BeInt32(d, 16);
                    h = BeInt32(d, 20);
                    return true;
                }

                // JPEG: walk the marker segments to the first SOFn, which carries
                // precision, height, width after its 2-byte length.
                int i = 2;
                while (i + 3 < d.Length)
                {
                    if (d[i] != 0xFF) { i++; continue; }        // fill byte / resync
                    byte marker = d[i + 1];
                    if (marker == 0xFF) { i++; continue; }       // padding run
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    { i += 2; continue; }                        // standalone, no payload
                    if (marker == 0xD9 || marker == 0xDA) return false;  // EOI / scan: no SOF
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (len < 2) return false;
                    bool isSof = (marker >= 0xC0 && marker <= 0xC3) ||
                                 (marker >= 0xC5 && marker <= 0xC7) ||
                                 (marker >= 0xC9 && marker <= 0xCB) ||
                                 (marker >= 0xCD && marker <= 0xCF);
                    if (isSof)
                    {
                        if (i + 9 >= d.Length) return false;
                        h = (d[i + 5] << 8) | d[i + 6];
                        w = (d[i + 7] << 8) | d[i + 8];
                        return true;
                    }
                    i += 2 + len;
                }
                return false;
            }
            catch { return false; }
        }

        private static int BeInt32(byte[] d, int o)
        {
            // Clamp rather than wrap: a chunk length with the high bit set is not a
            // negative size, it is a number we must refuse.
            long v = ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3];
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }
    }
}
