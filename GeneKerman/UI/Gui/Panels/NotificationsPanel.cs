/*
 * UI/Gui/Panels/NotificationsPanel.cs – The notification feed, in uGUI.
 *
 * This mirrors MainWindow.DrawNotificationsTab (MainWindow.cs:1659-1747) and
 * reads the *same* list object — MainWindow owns the fetch, the local-notification
 * merge, the de-dup and the unread count, and none of that is forked here. The
 * whole panel is display.
 *
 * That is deliberate and is why this is the first slice: if the sidebar turns
 * out not to be worth it, deleting this file costs nothing, because no logic
 * lives in it.
 *
 * Read-only for now. Mark-read and dismiss are mutations against the server and
 * belong with the rest of the actions, once the shell has proven itself.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class NotificationsPanel : SidebarPanel
    {
        public override string Title => "Feed";

        // What the last Rebuild drew, so Poll can notice a fetch landing without
        // MainWindow having to call back into the sidebar.
        private int lastCount = -1;
        private string lastTopId = "";
        private bool lastLoading;
        private bool lastLinked;

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            var feed = main.NotificationFeed;
            bool linked = mod.Api != null && mod.Api.IsLinked;

            lastCount = feed?.Count ?? -1;
            lastTopId = TopId(feed);
            lastLoading = main.NotificationsLoading;
            lastLinked = linked;

            var col = UIF.Box(Host, "Feed").Column(Theme.Space2).Flex(1f, 1f);

            UIF.PanelHeader(col, "Notifications",
                            () => { main.RequestNotificationRefresh(); MarkDirty(); });

            if (!linked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                     "Open the classic window and link with a 6-digit code to see your feed here.");
                return;
            }

            if (main.NotificationsLoading)
            {
                UIF.Notice(col, "Loading notifications…", null);
                return;
            }

            if (feed == null || feed.Count == 0)
            {
                UIF.Notice(col, "No notifications.", "Contract offers, approvals and payouts land here.");
                return;
            }

            El list;
            UIF.ScrollView(col, out list).Flex(1f, 1f);

            foreach (var obj in feed)
            {
                var n = obj as Dictionary<string, object>;
                if (n == null) continue;
                BuildRow(list, n);
            }
        }

        private static void BuildRow(El parent, Dictionary<string, object> n)
        {
            bool read = MiniJSON.GetBool(n, "read");

            // Read rows are dimmed rather than hidden, matching the IMGUI tab's
            // 0.55 alpha. Applied per-colour instead of via a parent tint, because
            // CanvasRenderer does not propagate alpha the way GUI.color does.
            float dim = read ? 0.55f : 1f;

            var card = UIF.Card(parent, "Notif")
                .Column(Theme.Space1)
                .Pad(Theme.Space3);

            var titleRow = UIF.Box(card, "Title").Row(Theme.Space2).H(18);
            // The unread marker, standing in for the IMGUI tab's "● " prefix.
            if (!read)
                UIF.Box(titleRow, "Dot").Dot(Theme.Primary, 8);

            UIF.Label(titleRow, MiniJSON.GetString(n, "title"), Theme.FontSm,
                      Theme.Alpha(Theme.Foreground, dim)).Bold();

            UIF.Label(card, MiniJSON.GetString(n, "message"), Theme.FontSm,
                      Theme.Alpha(Theme.MutedForeground, dim)).Body();

            string ts = MiniJSON.GetString(n, "timestamp");
            if (!string.IsNullOrEmpty(ts))
                UIF.Muted(card, ts, Theme.FontXs).Color(Theme.Alpha(Theme.MutedForeground, dim * 0.8f));
        }


        /// <summary>
        /// A fetch completing, a toast arriving, or a login does not notify the
        /// sidebar, so notice the change here. Comparing the count and the newest
        /// id catches both "list replaced" and "one prepended" — the only two ways
        /// MainWindow mutates this list.
        /// </summary>
        protected override void Poll()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            var feed = main.NotificationFeed;
            bool linked = mod.Api != null && mod.Api.IsLinked;

            if ((feed?.Count ?? -1) != lastCount ||
                TopId(feed) != lastTopId ||
                main.NotificationsLoading != lastLoading ||
                linked != lastLinked)
            {
                MarkDirty();
            }
        }

        private static string TopId(IList<object> feed)
        {
            if (feed == null || feed.Count == 0) return "";
            var first = feed[0] as Dictionary<string, object>;
            return first == null ? "" : MiniJSON.GetString(first, "id");
        }
    }
}
