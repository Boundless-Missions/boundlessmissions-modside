/*
 * GiftInbox.cs – Receiver side of the friend quicksend.
 *
 * A quicksent craft used to ride the auto-import queue: it landed in the save,
 * unasked, whenever the recipient next stood at the Space Center. Now the server
 * holds it as an OFFER (status "offered", invisible to the auto-import poll) and
 * this class is the client's view of those offers: the poll, the accept (which
 * moves the entry into the normal import queue and, where the scene allows,
 * delivers it on the spot) and the decline (which deletes it server-side and
 * notifies the sender).
 *
 * The state is static and UI-free for the same reason ToolActions is: the sidebar
 * renders it, but the decision logic must not live in a panel. Panels notice
 * changes by comparing <see cref="Version"/> — the retained-mode contract.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public static class GiftInbox
    {
        /// <summary>Offers awaiting a decision, oldest first (server order). Plain
        /// parsed JSON dicts — no textures, so they survive scene changes.</summary>
        public static readonly List<Dictionary<string, object>> Offers =
            new List<Dictionary<string, object>>();

        /// <summary>True only while the FIRST fetch is in flight — refreshes of a
        /// list already on screen stay silent instead of flashing a spinner.</summary>
        public static bool Loading { get; private set; }

        /// <summary>Bumped on every change to <see cref="Offers"/>, so retained-mode
        /// panels can notice a change from their Poll without a callback.</summary>
        public static int Version { get; private set; }

        private const float PollInterval = 60f;
        private static float lastPoll = -PollInterval;
        private static bool pollInFlight;
        private static bool fetchedOnce;

        /// <summary>
        /// Called from GeneKermanMod.Update (after the consent/data-sharing gates —
        /// this talks to the server). Polls in every scene the sidebar is usable in,
        /// so an offer can be decided in the VAB or in flight, not just at the KSC.
        /// </summary>
        public static void Tick()
        {
            var scene = HighLogic.LoadedScene;
            if (scene != GameScenes.SPACECENTER && scene != GameScenes.EDITOR &&
                scene != GameScenes.FLIGHT && scene != GameScenes.TRACKSTATION)
                return;
            if (Time.realtimeSinceStartup - lastPoll < PollInterval) return;
            lastPoll = Time.realtimeSinceStartup;
            Refresh();
        }

        public static void Refresh()
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null || !mod.Api.IsLinked || pollInFlight) return;

            pollInFlight = true;
            Loading = !fetchedOnce;
            if (Loading) Version++;

            mod.RunCoroutine(mod.Api.Get("/api/v1/craft/gifts/pending", (ok, resp, status) =>
            {
                pollInFlight = false;
                bool wasLoading = Loading;
                Loading = false;
                if (!ok || string.IsNullOrEmpty(resp))
                {
                    // Leave whatever we last knew on screen; a transient network
                    // failure must not clear a list the player is looking at.
                    if (wasLoading) Version++;
                    return;
                }

                fetchedOnce = true;
                var data = MiniJSON.DeserializeDict(resp);
                var fresh = new List<Dictionary<string, object>>();
                foreach (var obj in MiniJSON.GetList(data, "gifts"))
                {
                    var e = obj as Dictionary<string, object>;
                    if (e != null && !string.IsNullOrEmpty(MiniJSON.GetString(e, "import_id", "")))
                        fresh.Add(e);
                }

                Offers.Clear();
                Offers.AddRange(fresh);
                Version++;
            }));
        }

        /// <summary>
        /// Whether an import entry of this <paramref name="source"/> could be
        /// delivered in the current scene. Blueprint installs and flags are file
        /// writes — safe anywhere a save is loaded. A live vessel spawn mirrors
        /// VesselTransfer.CanImport: never in the editor.
        /// </summary>
        public static bool CanDeliverHere(string source)
        {
            if (HighLogic.CurrentGame == null) return false;
            var scene = HighLogic.LoadedScene;

            if (source == "rescue_delivery" || source == "gift_vessel")
                return scene == GameScenes.FLIGHT ||
                       scene == GameScenes.SPACECENTER ||
                       scene == GameScenes.TRACKSTATION;

            return scene == GameScenes.SPACECENTER || scene == GameScenes.EDITOR ||
                   scene == GameScenes.FLIGHT || scene == GameScenes.TRACKSTATION;
        }

        /// <summary>
        /// Accept an offer. On success the server has moved it into the normal
        /// import queue; if this scene can deliver it, one immediate queue poll
        /// imports it on the spot through the exact machinery the Space Center
        /// timer runs — otherwise it arrives on the next visit to a scene that can.
        /// </summary>
        public static IEnumerator Accept(Dictionary<string, object> offer, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            string id = MiniJSON.GetString(offer, "import_id", "");
            string source = MiniJSON.GetString(offer, "source", "");

            bool ok = false;
            string msg = null;
            yield return mod.Api.Post($"/api/v1/craft/gifts/{id}/accept", "{}", (s, resp, status) =>
            {
                if (s && !string.IsNullOrEmpty(resp))
                {
                    var d = MiniJSON.DeserializeDict(resp);
                    ok = MiniJSON.GetBool(d, "success", false);
                    msg = MiniJSON.GetString(d, "message", null);
                }
            });

            if (!ok)
            {
                // "No longer there" usually means decided from another install —
                // refresh so the stale card goes away rather than failing again.
                Refresh();
                onDone(false, msg ?? "Could not accept the offer.");
                yield break;
            }

            RemoveLocal(id);

            if (CanDeliverHere(source))
            {
                mod.MainWindowRef?.PollCraftImports();
                onDone(true, source == "gift_vessel"
                    ? "Accepted — spawning it into your save."
                    : "Accepted — installing to your Ships folder.");
            }
            else
            {
                onDone(true, "Accepted. It will arrive next time you're at the Space Center.");
            }
        }

        /// <summary>Decline an offer. The server deletes it and tells the sender.</summary>
        public static IEnumerator Reject(Dictionary<string, object> offer, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            string id = MiniJSON.GetString(offer, "import_id", "");

            bool ok = false;
            string msg = null;
            yield return mod.Api.Post($"/api/v1/craft/gifts/{id}/reject", "{}", (s, resp, status) =>
            {
                if (s && !string.IsNullOrEmpty(resp))
                {
                    var d = MiniJSON.DeserializeDict(resp);
                    ok = MiniJSON.GetBool(d, "success", false);
                    msg = MiniJSON.GetString(d, "message", null);
                }
            });

            if (ok) RemoveLocal(id);
            else Refresh();
            onDone(ok, ok ? "Declined — the sender has been told." : (msg ?? "Could not decline the offer."));
        }

        private static void RemoveLocal(string importId)
        {
            for (int i = Offers.Count - 1; i >= 0; i--)
                if (MiniJSON.GetString(Offers[i], "import_id", "") == importId)
                    Offers.RemoveAt(i);
            Version++;
        }
    }
}
