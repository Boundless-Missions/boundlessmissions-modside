/*
 * UI/Gui/Panels/NotificationsPanel.cs – The notification feed, in uGUI.
 *
 * This mirrors MainWindow.DrawNotificationsTab and reads the *same* list object —
 * MainWindow owns the fetch, the local-notification merge, the de-dup and the
 * unread count, and none of that is forked here.
 *
 * The mutations are the same story: mark-read, dismiss and mark-all-read all run
 * MainWindow's Request* wrappers rather than calling the API from here. Each of
 * them has to flip a flag on the feed object, recount the unread badge and talk to
 * the server together, and a local notification (id "local-…", no server record)
 * has to skip the call entirely — a second copy of that is how a badge ends up
 * disagreeing with the list under it.
 *
 * "Open" hands the contract to the inbox panel through the controller
 * (SidebarPanel.OpenContract), which is the sidebar's equivalent of the classic
 * window switching to the Contracts tab.
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
        private int lastUnread = -1;

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            var feed = main.NotificationFeed;
            bool linked = mod.Api != null && mod.Api.IsLinked;

            Snapshot(main, feed, linked);

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

            // Mark all read sits above the list rather than under it (where the classic
            // window puts it): the feed scrolls, and a button below a scroll view is one
            // the player has to reach the bottom of the feed to press.
            int unread = Unread(feed);
            if (unread > 0)
            {
                var bar = UIF.Box(col, "Bulk").Row(Theme.Space2).H(26);
                UIF.Muted(bar, unread + " unread");
                UIF.Grow(bar);
                UIF.Button(bar, "Mark all read",
                           () => main.RequestMarkAllNotificationsRead(BeginAction()),
                           BtnStyle.Ghost, 26, Theme.Space2)
                   .Interactable(!Busy).E.PrefW(112);
            }

            DrawStatus(col);

            El list;
            UIF.ScrollView(col, out list).Flex(1f, 1f);

            foreach (var obj in feed)
            {
                var n = obj as Dictionary<string, object>;
                if (n == null) continue;
                BuildRow(list, n, main);
            }
        }

        private void BuildRow(El parent, Dictionary<string, object> n, MainWindow main)
        {
            bool read = MiniJSON.GetBool(n, "read");
            string id = MiniJSON.GetString(n, "id");

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

            BuildRowActions(card, n, main, id, read);
        }

        private void BuildRowActions(El card, Dictionary<string, object> n, MainWindow main,
                                     string id, bool read)
        {
            if (string.IsNullOrEmpty(id)) return;

            var data = MiniJSON.GetDict(n, "data");
            string contractId = data == null ? "" : MiniJSON.GetString(data, "contract_id");

            var row = UIF.Box(card, "Actions").Row(Theme.Space2).H(24);

            if (!string.IsNullOrEmpty(contractId))
            {
                string cid = contractId;
                UIF.Button(row, "Open", () =>
                {
                    // Marking it read on the way is what the player means by opening it,
                    // and it saves a second click on the row they are leaving. Unread
                    // only: a read one would be a pointless request.
                    if (!read) main.RequestMarkNotificationRead(id, (ok, msg) => MarkDirty());
                    OpenContract(cid);
                }, BtnStyle.Secondary, 24, Theme.Space2).Interactable(!Busy).E.PrefW(62);
            }

            // A fix this install can carry out itself (see LocalNotifActions) — rendered
            // from the key, so the panel never learns what any of them do. Primary
            // styling because it is the point of the notification, not an aside; pressing
            // it marks the row read, since acting on a warning is having seen it.
            string action = LocalNotifActions.Of(n);
            if (!string.IsNullOrEmpty(action))
            {
                UIF.Button(row, LocalNotifActions.LabelFor(action), () =>
                {
                    LocalNotifActions.Run(n);
                    if (!read) main.RequestMarkNotificationRead(id, (ok, msg) => MarkDirty());
                    MarkDirty();
                }, BtnStyle.Primary, 24, Theme.Space2).Interactable(!Busy).E.PrefW(132);
            }

            UIF.Grow(row);

            if (!read)
            {
                UIF.Button(row, "Mark read",
                           () => main.RequestMarkNotificationRead(id, BeginAction()),
                           BtnStyle.Ghost, 24, Theme.Space2)
                   .Interactable(!Busy).E.PrefW(88);
            }

            // No confirm, as in the classic window: dismissing hides one line of a feed
            // the server can send again, so an accidental press costs nothing.
            UIF.Button(row, "Dismiss",
                       () => main.RequestDismissNotification(id, BeginAction()),
                       BtnStyle.Ghost, 24, Theme.Space2)
               .Interactable(!Busy).E.PrefW(74);
        }

        private static int Unread(IList<object> feed)
        {
            int unread = 0;
            if (feed != null)
                foreach (var o in feed)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && !MiniJSON.GetBool(d, "read")) unread++;
                }
            return unread;
        }

        private void Snapshot(MainWindow main, IList<object> feed, bool linked)
        {
            lastCount = feed?.Count ?? -1;
            lastTopId = TopId(feed);
            lastLoading = main.NotificationsLoading;
            lastLinked = linked;
            lastUnread = Unread(feed);
        }

        /// <summary>
        /// A fetch completing, a toast arriving, or a login does not notify the
        /// sidebar, so notice the change here. Comparing the count and the newest
        /// id catches both "list replaced" and "one prepended"; the unread count
        /// catches the third case, which the other two cannot see — a mark-read
        /// leaves the list exactly as long, in exactly the same order.
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
                linked != lastLinked ||
                Unread(feed) != lastUnread)
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

        internal override void OnShown() => ClearStatus();
    }
}
