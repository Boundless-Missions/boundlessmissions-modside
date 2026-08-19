/*
 * UI/Gui/Panels/SubmitPanel.cs – Submitting a contract, in a draggable window.
 *
 * The port of the old IMGUI UI/SubmitWindow.cs. Everything that decides anything
 * moved to SubmissionSession; what is left here is the reading of it, so this
 * file contains no validation, no capture and no upload — a rule worth keeping,
 * because the last copy of this screen had all three inlined between GUILayout
 * calls and no second front end could reach any of it.
 *
 * It is a window rather than a sidebar tab for one reason: submitting is an act
 * *about* the scene behind it. In the VAB the player is checking the craft on the
 * stage against a part restriction; in flight they are reading a situation
 * mismatch off the navball. A centred panel that owns the middle of the screen is
 * the wrong shape for both, so this one can be dragged out of the way and stays
 * put — see FloatWindow.
 *
 * The screen is a poll-free rebuild: SubmissionSession raises Changed for every
 * state move it makes (a capture finishing, PRE settling, an upload failing) and
 * the only thing Poll does is ask the session to re-check whether the renders
 * still show the craft, which is throttled inside it.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class SubmitPanel : WindowPanel
    {
        public override string Title => "Submit contract";

        private readonly SubmissionSession session = new SubmissionSession();

        /// <summary>The scene the session was last validated for. See WatchScene.</summary>
        private GameScenes lastScene = GameScenes.LOADING;

        internal SubmitPanel()
        {
            session.Changed += MarkDirty;
            // The session decides when the flow is over — an approved submission and
            // one filed for review both end it — and the window it is being shown in
            // is what has to go away.
            session.Closed += CloseWindow;
        }

        /// <summary>Open the window on a contract. Called through
        /// GeneKermanMod.OpenSubmitWindow, which every front end routes to.</summary>
        internal void Open(string contractId, string mission,
            string missionType, string requiredSituation, string requiredBody, string requiredModlist,
            RescueTargetSpec rescueTarget, List<string> rescueKerbals, ContractConstraints constraints)
        {
            Window?.Show();
            lastScene = HighLogic.LoadedScene;
            session.Open(contractId, mission, missionType, requiredSituation, requiredBody,
                         requiredModlist, rescueTarget, rescueKerbals, constraints);
            MarkDirty();
        }

        // ── Build ───────────────────────────────────────────────────────────

        protected override void Rebuild()
        {
            var col = UIF.Box(Host, "Submit").Column(Theme.Space2).Flex(1f, 1f);

            BuildTags(col);
            UIF.Divider(col);

            El list;
            UIF.ScrollView(col, out list, "submit").Flex(1f, 1f);

            // The mission text scrolls with everything else rather than sitting above
            // the list: contracts are written by players and some of them are three
            // paragraphs long, which as a fixed header would leave the part that has
            // the buttons on it no height at all.
            UIF.Label(list, Fmt.Plain(session.ContractMission), Theme.FontSm, Theme.MutedForeground).Body();

            if (session.PhysicsStabilizing)
            {
                UIF.Notice(list, "Stabilizing physics range…",
                           "Pausing Physics Range Extender and letting distant craft unload.");
            }
            else if (!session.SceneValid)
            {
                BuildWrongScene(list);
            }
            else if (session.MissionType == "craft_build")
            {
                BuildEditorMode(list);
                BuildRenderSection(list);
            }
            else
            {
                BuildFlightMode(list);
                BuildRenderSection(list);
            }

            BuildFooter(col);
        }

        /// <summary>What this contract is and where it has to happen, in one line —
        /// the only thing pinned above the scroll, so it is still on screen when the
        /// player has scrolled down to the buttons.</summary>
        private void BuildTags(El parent)
        {
            var tags = UIF.Box(parent, "Tags").Row(Theme.Space1).H(22);

            if (session.IsRescue)
                UIF.Badge(tags, "Rescue", Theme.Status("warning"));
            else if (session.MissionType == "craft_build")
                UIF.Badge(tags, "Craft build", Theme.Status("warning"));
            else
                UIF.Badge(tags, "Active vessel", Theme.Primary);

            if (!string.IsNullOrEmpty(session.RequiredBody))
                UIF.Badge(tags, session.RequiredBody, Theme.MutedForeground, Theme.Secondary);
            if (!string.IsNullOrEmpty(session.RequiredSituation))
                UIF.Badge(tags, Fmt.Situation(session.RequiredSituation), Theme.MutedForeground, Theme.Secondary);

            UIF.Grow(tags);
        }

        // ── Wrong scene ─────────────────────────────────────────────────────

        private void BuildWrongScene(El parent)
        {
            // No message means the scene is mid-change: LeavingScene has dropped what
            // belonged to the old one and WatchScene has not yet been able to ask the
            // new one anything. Saying "wrong location" there would be a guess.
            if (string.IsNullOrEmpty(session.ValidationMsg))
            {
                UIF.Notice(parent, "Loading…", "Waiting for the scene to finish loading.");
                return;
            }

            var card = UIF.Card(parent, "WrongScene").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Wrong location", Theme.FontSm, Theme.Status("warning")).Bold();
            UIF.Label(card, session.ValidationMsg, Theme.FontXs, Theme.MutedForeground).Body();

            string help;
            if (session.MissionType == "craft_build")
            {
                help = "Craft build missions need the craft open in the VAB or SPH. " +
                       "The loaded craft is what gets submitted, and its renders are " +
                       "captured from the build stage.";
            }
            else
            {
                help = "Active vessel missions are submitted from flight" +
                       (string.IsNullOrEmpty(session.RequiredBody) ? "" : ", at " + session.RequiredBody) +
                       (string.IsNullOrEmpty(session.RequiredSituation)
                           ? "" : ", " + Fmt.Situation(session.RequiredSituation)) +
                       ". Telemetry is captured with the submission.";
            }
            UIF.Muted(card, help).Body();

            // The window survives scene loads and revalidates itself on each one, so
            // this is a statement of what to do next rather than a dead end.
            UIF.Muted(card, "This window stays open — it rechecks when you get there.").Body();
        }

        // ── Editor mode (craft_build) ───────────────────────────────────────

        private void BuildEditorMode(El parent)
        {
            if (string.IsNullOrEmpty(session.EditorCraftName))
            {
                UIF.Notice(parent, "No craft loaded in the editor.",
                           "Open or build a craft in the VAB or SPH first.");
                UIF.Button(parent, "Refresh craft data", () => session.CaptureEditorCraft(),
                           BtnStyle.Secondary, 28);
                return;
            }

            var card = UIF.Card(parent, "Craft").Column(Theme.Space1).Pad(Theme.Space3);

            var top = UIF.Box(card, "Top").Row(Theme.Space2).H(22);
            UIF.Label(top, session.EditorCraftName, Theme.FontSm).Bold().Ellipsis().E.PrefW(0).Flex(1f);
            UIF.Badge(top, session.EditorCraftType, Theme.MutedForeground, Theme.Secondary);

            UIF.Muted(card, $"{session.EditorPartCount} parts · {session.EditorCraftMass:F1} t · " +
                            $"{session.EditorCraftCost:N0} funds");

            if (!string.IsNullOrEmpty(session.EditorCraftPath))
                UIF.Label(card, "Craft file ready.", Theme.FontXs, Theme.Primary);
            else
                UIF.Label(card, "Save your craft first.", Theme.FontXs, Theme.Destructive).Body();

            // Explain a greyed-out Submit: list the parts that break the restriction.
            if (!session.VesselValid && !string.IsNullOrEmpty(session.ValidationMsg))
                BuildProblem(parent, "Craft not accepted", session.ValidationMsg);

            UIF.Button(parent, "Refresh craft data", () => session.CaptureEditorCraft(),
                       BtnStyle.Secondary, 28);
        }

        // ── Flight mode (active_vessel / rescue) ────────────────────────────

        private void BuildFlightMode(El parent)
        {
            var v = session.ActiveVessel;
            if (v == null)
            {
                UIF.Notice(parent, "No active vessel detected.", null);
                UIF.Button(parent, "Refresh data", () => session.CaptureFlightData(), BtnStyle.Secondary, 28);
                return;
            }

            if (!session.VesselValid && !string.IsNullOrEmpty(session.ValidationMsg))
                BuildProblem(parent, "Vessel state mismatch", session.ValidationMsg);
            else if (session.VesselValid)
                UIF.Label(parent, "Vessel state matches the contract.", Theme.FontXs, Theme.Primary).Body();

            // The orbit this contract asks for, drawn whether or not the craft is in it.
            // A requirement only ever printed as a failure is one the player meets by
            // accident or not at all — and the vessel readout below prints the live
            // inclination and eccentricity, so the two can be compared.
            string orbitReq = session.DescribeOrbitRequirement();
            if (!string.IsNullOrEmpty(orbitReq))
                UIF.Muted(parent, "Required orbit: " + orbitReq).Body();

            var card = UIF.Card(parent, "Vessel").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(card, v.vesselName, Theme.FontSm).Bold().Ellipsis();
            UIF.Muted(card, v.body + " · " + Fmt.Situation(v.situation));
            UIF.Muted(card, $"Alt {v.altitude:N0} m · {v.partCount} parts · {v.totalMass:F1} t" +
                            (v.crewCount > 0 ? $" · {v.crewCount} crew" : ""));
            if (v.sma > 0)
                UIF.Muted(card, $"SMA {v.sma:N0} m · e {v.eccentricity:F3} · i {v.inclination:F1}°");

            UIF.Button(parent, "Refresh data", () => session.CaptureFlightData(), BtnStyle.Secondary, 28);

            // Multi-craft sending is for ordinary active-vessel contracts only.
            if (!session.IsRescue) BuildNearbySection(parent);
        }

        // ── Extra crafts (multi-vessel submission) ──────────────────────────

        private void BuildNearbySection(El parent)
        {
            UIF.Label(parent, "Extra crafts in range", Theme.FontSm).Bold();

            if (session.PreDisabledByUs)
                UIF.Muted(parent, "Physics Range Extender is paused; this is the stock range.").Body();

            var nearby = session.Nearby;
            if (nearby == null || nearby.Count == 0)
            {
                UIF.Notice(parent, "No other vessels in physics range.", null);
                UIF.Button(parent, "Rescan range", () => session.CaptureFlightData(), BtnStyle.Ghost, 26);
                return;
            }

            UIF.Muted(parent, $"{session.SelectedExtras} of {nearby.Count} selected — packed and sent " +
                              "with this submission.").Body();

            var batch = UIF.Box(parent, "Batch").Row(Theme.Space2).H(26);
            UIF.Button(batch, "Select all", () => session.SetAllSelected(true), BtnStyle.Ghost, 26).E.Flex(1f);
            UIF.Button(batch, "Select none", () => session.SetAllSelected(false), BtnStyle.Ghost, 26).E.Flex(1f);

            var range = UIF.Box(parent, "Range").Row(Theme.Space2).H(28);
            UIF.Muted(range, "Within").E.W(42);
            UIF.TextField(range, session.RangeFilterKm, "2.5", 28)
               .OnChanged(t => session.RangeFilterKm = t)
               .E.W(56);
            UIF.Muted(range, "km").E.W(20);
            UIF.Button(range, "Apply", () => session.SelectWithinRange(), BtnStyle.Secondary, 28).E.Flex(1f);

            var listCard = UIF.Card(parent, "Nearby").Column(Theme.Space1).Pad(Theme.Space2);
            foreach (var e in nearby)
            {
                var entry = e;
                var row = UIF.Box(listCard, "Craft").Row(Theme.Space2).MinH(30);
                UIF.Checkbox(row, entry.Selected, on => session.SetSelected(entry, on));

                var text = UIF.Box(row, "Text").Column(1).PrefW(0).Flex(1f);
                UIF.Label(text, entry.Snap.vesselName + "  ·  " + FormatDistance(entry.Distance),
                          Theme.FontXs).Ellipsis();
                UIF.Muted(text, $"{entry.Snap.vesselType} · {entry.Snap.body} " +
                                $"{Fmt.Situation(entry.Snap.situation)} · {entry.Snap.partCount} parts · " +
                                $"{entry.Snap.crewCount} crew").Ellipsis();
            }

            UIF.Button(parent, "Rescan range", () => session.CaptureFlightData(), BtnStyle.Ghost, 26);
        }

        private static string FormatDistance(double metres)
            => metres >= 1000.0 ? $"{metres / 1000.0:F2} km" : $"{metres:F0} m";

        // ── Renders ─────────────────────────────────────────────────────────

        private void BuildRenderSection(El parent)
        {
            var card = UIF.Card(parent, "Renders").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Vessel renders", Theme.FontSm).Bold();

            int extras = session.SelectedExtras;
            UIF.Muted(card, extras > 0
                ? $"Renders the contract craft plus {extras} selected extra" + (extras == 1 ? "." : "s.")
                : "An orthographic blueprint of the craft, captured here.").Body();

            if (session.ScreenshotTaken && session.RenderStale)
            {
                UIF.Label(card, "The craft changed after these renders were taken.",
                          Theme.FontXs, Theme.Destructive).Body();
                UIF.Muted(card, HighLogic.LoadedSceneIsEditor
                    ? "Renders must show the craft you submit. Retake them to continue."
                    : "The vessel is no longer the one in these renders. Retake them to continue.").Body();
                UIF.Button(card, "Retake renders", () => session.TakeRenders(), BtnStyle.Primary, 30);
            }
            else if (session.ScreenshotTaken)
            {
                UIF.Label(card, $"Captured {session.RenderCount} render" +
                                (session.RenderCount == 1 ? "." : "s."), Theme.FontXs, Theme.Primary);
                UIF.Button(card, "Retake renders", () => session.TakeRenders(), BtnStyle.Secondary, 28);
            }
            else
            {
                UIF.Button(card, "Capture vessel renders", () => session.TakeRenders(), BtnStyle.Primary, 30);
            }
        }

        // ── Footer ──────────────────────────────────────────────────────────

        /// <summary>A failure the player has to act on: the mismatch list, or the parts
        /// a restricted contract will not take. Its own card so it cannot be read as
        /// part of the vessel readout above it.</summary>
        private void BuildProblem(El parent, string title, string body)
        {
            var card = UIF.Card(parent, "Problem").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(card, title, Theme.FontSm, Theme.Destructive).Bold();
            UIF.Label(card, body, Theme.FontXs, Theme.MutedForeground).Body();
        }

        private void BuildFooter(El parent)
        {
            if (!string.IsNullOrEmpty(session.StatusMsg))
                UIF.Label(parent, session.StatusMsg, Theme.FontXs,
                          session.StatusIsError ? Theme.Destructive : Theme.Primary).Body();

            var bar = UIF.Box(parent, "Actions").Row(Theme.Space2).H(32);
            UIF.Button(bar, "Cancel", () => session.Close(), BtnStyle.Ghost, 32).E.W(88);
            UIF.Grow(bar);

            if (!session.SceneValid) return;

            UIF.Button(bar, session.IsSubmitting ? "Submitting…" : "Submit",
                       () => session.Submit(), BtnStyle.Primary, 32)
               .Interactable(!session.IsSubmitting && session.CanSubmit())
               .E.W(150);
        }

        // ── Frame ───────────────────────────────────────────────────────────

        protected override void Poll()
        {
            // Throttled inside the session, and the only thing here that is not
            // event-driven: nothing tells us a part was pulled off the craft.
            session.TickRenderStale();
            WatchScene();
        }

        /// <summary>
        /// Notice that the player has arrived somewhere else and revalidate there.
        ///
        /// A poll, and it has to be, for the same reason SidebarController.UpdateAssets
        /// is one: the scene event fires when a load is *requested*, while the old scene
        /// is still the loaded one, so validating from it would only ever re-answer the
        /// question the player is leaving. The extra condition is the other half of the
        /// same trap — a scene is "loaded" some seconds before it can be asked anything,
        /// and capturing a flight scene at that moment reads a vessel that is not there
        /// yet.
        /// </summary>
        private void WatchScene()
        {
            var scene = HighLogic.LoadedScene;
            if (scene == lastScene) return;

            if (scene == GameScenes.FLIGHT &&
                !(FlightGlobals.ready && FlightGlobals.ActiveVessel != null)) return;
            if (scene == GameScenes.EDITOR && EditorLogic.fetch == null) return;

            lastScene = scene;
            session.Revalidate();
        }

        internal override void OnWindowClosed() => session.Close();

        internal override void OnSceneChanged()
        {
            // The load has been requested, not finished: all this does is let go of what
            // belonged to the scene being left (PRE, the renders, the vessel readout).
            // Validating for where the player has arrived is WatchScene's, a few frames
            // later. Only while the window is up — a closed window has no session in
            // progress and nothing to hand back.
            if (Window != null && Window.IsOpen) session.LeavingScene();
        }
    }
}
