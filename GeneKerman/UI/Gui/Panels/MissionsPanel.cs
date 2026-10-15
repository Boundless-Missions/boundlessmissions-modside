/*
 * UI/Gui/Panels/MissionsPanel.cs – This week's missions, in uGUI.
 *
 * Visual spec: WebUI/src/screens/Missions.tsx. Data: ClientState's cached
 * `missions` / `weekKey` / `missionsLocked`, filled by RefreshMissions().
 *
 * The difficulty thresholds are the ones both other front ends already use
 * (the classic window and Missions.tsx:16) — <=3 Easy, <=6 Medium, <=8 Hard,
 * else Extreme. They are duplicated here for the same reason they are duplicated
 * in the React app: the band is a rendering decision, and the alternative is a
 * bridge round-trip for four comparisons. If they ever move, all three move.
 *
 * Accept runs ClientState.DoSelectMission through its Request wrapper rather than
 * reissuing the API call, because that coroutine also injects the accepted mission
 * into KSP's stock contract system.
 *
 * No detail view, and so no master-detail widening: a mission *is* its card here,
 * exactly as in the web UI, where the card carries the description and the button.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class MissionsPanel : SidebarPanel
    {
        public override string Title => "Missions";

        private int lastCount = -1;
        private bool lastLoading;
        private bool lastLocked;
        private string lastWeek = "";
        private bool requested;

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.State;
            if (main == null) return;

            var missions = main.MissionList;
            lastCount = missions?.Count ?? -1;
            lastLoading = main.MissionsLoading;
            lastLocked = main.MissionsLocked;
            lastWeek = main.MissionWeekKey ?? "";

            var col = UIF.Box(Host, "Missions").Column(Theme.Space2).Flex(1f, 1f);

            UIF.PanelHeader(col, "Missions", () => { main.RequestMissionsRefresh(); MarkDirty(); });

            if (mod.Api == null || !mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.", null);
                return;
            }

#if GK_DEBUG_PANEL
            // Dev builds only: swap between the live board and the admin-rotated test
            // board. Drawn before the empty check, so a rotation that produced nothing
            // can still be switched back out of. The server decides whether the request
            // is honoured (WEEKLY_TEST_ACCOUNT_IDS); this only asks.
            var devRow = UIF.Box(col, "DevBoard").Row(Theme.Space2).H(26);
            UIF.Label(devRow, "Board", Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(devRow);
            UIF.Button(devRow,
                       main.TestBoardRequested ? "Test (dev)" : "Live",
                       () => { main.ToggleTestBoard(); MarkDirty(); },
                       main.TestBoardRequested ? BtnStyle.Destructive : BtnStyle.Secondary, 24);
#endif

            if (missions == null || missions.Count == 0)
            {
                // `!requested`: no fetch has happened yet, so this is the frame
                // before loading starts, not an empty week.
                bool pending = main.MissionsLoading || !requested;
                UIF.Notice(col, pending ? "Loading missions…" : "No missions available right now.", null);
                return;
            }

            // Week line: either the lock badge or the week key, mirroring the site.
            var meta = UIF.Box(col, "Meta").Row(Theme.Space2).H(22);
            if (!string.IsNullOrEmpty(lastWeek))
                UIF.Label(meta, "Week " + lastWeek, Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(meta);
#if GK_DEBUG_PANEL
            // A test mission still mints a real contract, so say which board this is
            // on every frame rather than trusting whoever toggled it to remember.
            // Gated: production can never be on the test board (it never asks), so
            // this renders nothing there and the literal need not ship.
            if (main.MissionBoardKind == "test")
                UIF.Badge(meta, "TEST BOARD", Theme.Status("danger"));
#endif
            if (main.MissionsLocked)
                UIF.Badge(meta, "Selection locked", Theme.Status("warning"));

#if GK_DEBUG_PANEL
            // Why the test board was asked for and not given. Only ever set when a
            // client asked, so this is silent for everybody else — and only a dev
            // build can ask.
            if (!string.IsNullOrEmpty(main.MissionTestRefusal))
                UIF.Notice(col, "Test board: " + main.MissionTestRefusal, null);
#endif

            DrawStatus(col);

            El list;
            UIF.ScrollView(col, out list, "missions").Flex(1f, 1f);

            foreach (var obj in missions)
            {
                var m = obj as Dictionary<string, object>;
                if (m != null) BuildCard(list, m);
            }
        }

        private void BuildCard(El parent, Dictionary<string, object> m)
        {
            int difficulty = MiniJSON.GetInt(m, "difficulty");
            string band;
            Color bandColor = BandFor(difficulty, out band);

            var card = UIF.Card(parent, "Mission").Column(Theme.Space2).Pad(Theme.Space3);

            var tags = UIF.Box(card, "Tags").Row(Theme.Space1).H(22);
            UIF.Badge(tags, band + " " + difficulty + "/10", bandColor);
            // "n" is the mission's number on the board (1..20); "id" is the claim key
            // the bot wants back on select, and since boards became install-specific
            // that id is a hash of the mission text rather than its position. Show the
            // number when the bot sent one, and fall back to the id for a board stored
            // before the two were split, where the id WAS the number.
            int boardNo = MiniJSON.GetInt(m, "n");
            if (boardNo <= 0) boardNo = MiniJSON.GetInt(m, "id");
            UIF.Label(tags, "#" + boardNo, Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(tags);

            UIF.Label(card, MiniJSON.GetString(m, "desc_en"), Theme.FontSm).Body();

            // Where it has to happen, when the mission says so.
            string body = MiniJSON.GetString(m, "required_body");
            string situation = MiniJSON.GetString(m, "required_situation");
            if (!string.IsNullOrEmpty(body) || !string.IsNullOrEmpty(situation))
            {
                var where = UIF.Box(card, "Where").Row(Theme.Space1).H(22);
                if (!string.IsNullOrEmpty(body))
                    UIF.Badge(where, body, Theme.MutedForeground, Theme.Secondary);
                if (!string.IsNullOrEmpty(situation))
                    UIF.Badge(where, Fmt.Situation(situation), Theme.MutedForeground, Theme.Secondary);
                UIF.Grow(where);
            }

            var rewards = UIF.Box(card, "Rewards").Row(Theme.Space3).H(18);
            UIF.Muted(rewards, "+" + MiniJSON.GetInt(m, "xp").ToString("N0") + " XP");
            UIF.Muted(rewards, "+" + MiniJSON.GetInt(m, "coins").ToString("N0") + " KCoins");
            UIF.Muted(rewards, "Fine " + MiniJSON.GetInt(m, "fine").ToString("N0"));
            UIF.Grow(rewards);

            // Accepting runs ClientState's DoSelectMission, which also injects the
            // contract into KSP's stock contract system — reissuing the API call
            // from here would accept the mission but never make it appear in the
            // in-game contracts screen.
            var main = GeneKermanMod.Instance?.State;
            if (main == null || main.MissionsLocked) return;

            int missionId = MiniJSON.GetInt(m, "id");
            UIF.Button(card, "Accept",
                       () => main.RequestSelectMission(missionId, BeginAction()),
                       BtnStyle.Primary, 28)
               .Interactable(!Busy);
        }

        /// <summary>The thresholds the classic window and Missions.tsx:16 both use.</summary>
        private static Color BandFor(int difficulty, out string label)
        {
            if (difficulty <= 3) { label = "Easy"; return Theme.Status("success"); }
            if (difficulty <= 6) { label = "Medium"; return Theme.Status("warning"); }
            if (difficulty <= 8) { label = "Hard"; return new Color(0.95f, 0.55f, 0.25f, 1f); }
            label = "Extreme";
            return Theme.Status("danger");
        }



        protected override void Poll()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.State;
            if (main == null) return;

            if (!requested && mod.Api != null && mod.Api.IsLinked)
            {
                // Set for either answer, not only when this panel does the fetching
                // — see the same guard in ContractsPanel.Poll. A week with no
                // missions is a non-null empty list somebody else already fetched,
                // and reading that as still-pending left the panel on
                // "Loading missions…" for as long as the week stayed empty.
                requested = true;
                if (main.MissionList == null && !main.MissionsLoading)
                {
                    main.RequestMissionsRefresh();
                    return;
                }

                // No fetch, so nothing below changes; repaint the already-drawn
                // "Loading missions…" frame by hand.
                MarkDirty();
            }

            if ((main.MissionList?.Count ?? -1) != lastCount ||
                main.MissionsLoading != lastLoading ||
                main.MissionsLocked != lastLocked ||
                (main.MissionWeekKey ?? "") != lastWeek)
            {
                MarkDirty();
            }
        }

        internal override void OnShown() => requested = false;
    }
}
