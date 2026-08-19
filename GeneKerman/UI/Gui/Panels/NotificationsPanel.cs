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
 *
 * Friend-quicksend offers render above the feed: the server posts a "Craft
 * Offered" notification into the same feed, so this is where the player comes
 * looking. The offer state and both decisions live in GiftInbox — this panel
 * only draws cards and downloads blueprint previews, which it owns (and must
 * destroy) like ContractsPanel owns its submission preview.
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
        private int lastGiftVersion = -1;

        // Blueprint previews downloaded for gift offers, keyed by import_id. Owned
        // here: destroyed on hide and on scene change, with the viewer closed first
        // because it only borrows them.
        private readonly Dictionary<string, Texture2D> giftBlueprints =
            new Dictionary<string, Texture2D>();
        private readonly HashSet<string> giftBlueprintLoading = new HashSet<string>();

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

            // Offers sit above the feed and outside its scroll view, so a pending
            // decision cannot be scrolled out of sight. Drawn before the loading /
            // empty early-outs — an empty feed does not mean no offers.
            BuildGiftOffers(col);
            DrawStatus(col);

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

            El list;
            UIF.ScrollView(col, out list, "feed").Flex(1f, 1f);

            foreach (var obj in feed)
            {
                var n = obj as Dictionary<string, object>;
                if (n == null) continue;
                BuildRow(list, n, main);
            }
        }

        // ── Gift offers ─────────────────────────────────────────────────────

        private void BuildGiftOffers(El col)
        {
            var offers = GiftInbox.Offers;
            if (GiftInbox.Loading && offers.Count == 0)
            {
                UIF.Muted(col, "Checking for craft offers…", Theme.FontXs);
                return;
            }
            if (offers.Count == 0) return;

            var box = UIF.Box(col, "Gifts").Column(Theme.Space2);
            UIF.Label(box, "CRAFT OFFERS", Theme.FontXs, Theme.MutedForeground);
            foreach (var offer in offers)
                BuildGiftCard(box, offer);
        }

        private void BuildGiftCard(El parent, Dictionary<string, object> offer)
        {
            string id = MiniJSON.GetString(offer, "import_id");
            string name = MiniJSON.GetString(offer, "craft_name", "Craft");
            string from = MiniJSON.GetString(offer, "owner_name", "Someone");
            string bpUrl = MiniJSON.GetString(offer, "blueprint_url", "");
            bool vessel = MiniJSON.GetString(offer, "source", "") == "gift_vessel";

            var card = UIF.Card(parent, "Gift").Column(Theme.Space1).Pad(Theme.Space3);

            var titleRow = UIF.Box(card, "Title").Row(Theme.Space2).H(18);
            UIF.Box(titleRow, "Dot").Dot(Theme.Primary, 8);
            UIF.Label(titleRow, name, Theme.FontSm).Bold();

            UIF.Label(card, from + " sent you " + (vessel
                          ? "a live vessel. Accepting spawns it into your save."
                          : "a craft blueprint. Accepting saves it to your Ships folder."),
                      Theme.FontSm, Theme.MutedForeground).Body();

            if (vessel && HighLogic.LoadedScene == GameScenes.EDITOR)
                UIF.Muted(card, "A live vessel can't spawn in the editor — accepting here " +
                                "delivers it on your next Space Center visit.", Theme.FontXs);

            if (string.IsNullOrEmpty(bpUrl))
                UIF.Muted(card, "No blueprint preview came with this one.", Theme.FontXs);

            var row = UIF.Box(card, "Actions").Row(Theme.Space2).H(24);

            if (!string.IsNullOrEmpty(bpUrl))
            {
                string label = giftBlueprintLoading.Contains(id) ? "Loading…" : "Blueprint";
                UIF.Button(row, label, () => ViewGiftBlueprint(id, bpUrl, name),
                           BtnStyle.Secondary, 24, Theme.Space2)
                   .Interactable(!Busy && !giftBlueprintLoading.Contains(id)).E.PrefW(88);
            }

            UIF.Button(row, "Accept", () =>
                GeneKermanMod.Instance.RunCoroutine(GiftInbox.Accept(offer, BeginAction())),
                BtnStyle.Primary, 24, Theme.Space2).Interactable(!Busy).E.PrefW(72);

            UIF.Grow(row);

            UIF.Button(row, "Decline", () =>
                GeneKermanMod.Instance.RunCoroutine(GiftInbox.Reject(offer, BeginAction())),
                BtnStyle.Ghost, 24, Theme.Space2).Interactable(!Busy).E.PrefW(76);
        }

        /// <summary>
        /// Open the offer's blueprint full screen, downloading it first if this is
        /// the first look. The texture is kept for the next click and destroyed with
        /// the panel's other previews.
        /// </summary>
        private void ViewGiftBlueprint(string id, string url, string caption)
        {
            Texture2D have;
            if (giftBlueprints.TryGetValue(id, out have) && have != null)
            {
                ShowImages(new List<Texture2D> { have }, new List<string> { caption }, 0, caption);
                return;
            }

            if (giftBlueprintLoading.Contains(id)) return;
            giftBlueprintLoading.Add(id);
            MarkDirty();

            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.DownloadFile(url,
                (ok, bytes) =>
                {
                    giftBlueprintLoading.Remove(id);
                    MarkDirty();

                    Texture2D tex = null;
                    if (ok && bytes != null)
                    {
                        tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                        if (!tex.LoadImage(bytes)) { Object.Destroy(tex); tex = null; }
                    }
                    if (tex == null) return;

                    giftBlueprints[id] = tex;
                    ShowImages(new List<Texture2D> { tex }, new List<string> { caption }, 0, caption);
                }));
        }

        private void ClearGiftBlueprints()
        {
            // The viewer borrows these; close it before they go.
            if (giftBlueprints.Count > 0) CloseImages();
            foreach (var t in giftBlueprints.Values)
                if (t != null) Object.Destroy(t);
            giftBlueprints.Clear();
            giftBlueprintLoading.Clear();
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

            // Fmt.Plain on both: the titles are written for Discord and open with an
            // emoji the borrowed font has no glyph for (see Fmt.Plain).
            UIF.Label(titleRow, Fmt.Plain(MiniJSON.GetString(n, "title")), Theme.FontSm,
                      Theme.Alpha(Theme.Foreground, dim)).Bold();

            UIF.Label(card, Fmt.Plain(MiniJSON.GetString(n, "message")), Theme.FontSm,
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
            lastGiftVersion = GiftInbox.Version;
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
                Unread(feed) != lastUnread ||
                GiftInbox.Version != lastGiftVersion)
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

        internal override void OnShown()
        {
            ClearStatus();
            // Opening the feed is the natural "anything for me?" moment — don't make
            // an offer wait for the next timer tick to appear.
            GiftInbox.Refresh();
        }

        internal override void OnHidden() => ClearGiftBlueprints();

        internal override void OnSceneChanged() => ClearGiftBlueprints();
    }
}
