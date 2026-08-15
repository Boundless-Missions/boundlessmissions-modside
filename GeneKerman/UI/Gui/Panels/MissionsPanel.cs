/*
 * UI/Gui/Panels/MissionsPanel.cs – This week's missions, in uGUI.
 *
 * Visual spec: WebUI/src/screens/Missions.tsx. Data: MainWindow's cached
 * `missions` / `weekKey` / `missionsLocked`, filled by RefreshMissions().
 *
 * The difficulty thresholds are the ones both other front ends already use
 * (MainWindow.cs:689 and Missions.tsx:16) — <=3 Easy, <=6 Medium, <=8 Hard,
 * else Extreme. They are duplicated here for the same reason they are duplicated
 * in the React app: the band is a rendering decision, and the alternative is a
 * bridge round-trip for four comparisons. If they ever move, all three move.
 *
 * Accept runs MainWindow.DoSelectMission through its Request wrapper rather than
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
            var main = mod?.MainWindowRef;
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

            if (missions == null || missions.Count == 0)
            {
                // `!requested`: Poll has not yet fired its one on-demand fetch, so
                // this is the frame before loading starts, not an empty week.
                bool pending = main.MissionsLoading || !requested;
                UIF.Notice(col, pending ? "Loading missions…" : "No missions available right now.", null);
                return;
            }

            // Week line: either the lock badge or the week key, mirroring the site.
            var meta = UIF.Box(col, "Meta").Row(Theme.Space2).H(22);
            if (!string.IsNullOrEmpty(lastWeek))
                UIF.Label(meta, "Week " + lastWeek, Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(meta);
            if (main.MissionsLocked)
                UIF.Badge(meta, "Selection locked", Theme.Status("warning"));

            DrawStatus(col);

            El list;
            UIF.ScrollView(col, out list).Flex(1f, 1f);

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
            UIF.Label(tags, "#" + MiniJSON.GetInt(m, "id"), Theme.FontXs, Theme.MutedForeground);
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

            // Accepting runs MainWindow's DoSelectMission, which also injects the
            // contract into KSP's stock contract system — reissuing the API call
            // from here would accept the mission but never make it appear in the
            // in-game contracts screen.
            var main = GeneKermanMod.Instance?.MainWindowRef;
            if (main == null || main.MissionsLocked) return;

            int missionId = MiniJSON.GetInt(m, "id");
            UIF.Button(card, "Accept",
                       () => main.RequestSelectMission(missionId, BeginAction()),
                       BtnStyle.Primary, 28)
               .Interactable(!Busy);
        }

        /// <summary>The thresholds MainWindow.cs:689 and Missions.tsx:16 both use.</summary>
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
            var main = mod?.MainWindowRef;
            if (main == null) return;

            if (!requested && main.MissionList == null && !main.MissionsLoading &&
                mod.Api != null && mod.Api.IsLinked)
            {
                requested = true;
                main.RequestMissionsRefresh();
                return;
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
