/*
 * UI/Gui/Panels/ProfilePanel.cs – Account summary, in uGUI.
 *
 * The React `ProfileCard` (WebUI/src/screens/ProfileCard.tsx) is the visual
 * specification; the classic window's profile tab is the data specification. Neither is
 * re-implemented: the numbers come from ClientState's cached `profile` blob, which
 * is the same dictionary both other front ends render.
 *
 * The panel also owns the **friend list**, which used to be a tab of its own. It
 * belongs here: a friendship is an account-level fact, not a tool — it is between
 * two people rather than between two people in a server, it survives every scene
 * and every craft, and the only other thing on this screen (unlink, log out
 * everywhere) is account-level in exactly the same way. It sits *above* those,
 * because the account actions are the destructive end of the panel and nothing
 * routine should be reached past them.
 *
 * Two ways to add a friend, because there are two kinds of player and neither can
 * be found the other's way. A **Boundless account** has no Discord name to search
 * for, so it is added by the username it had to claim before it could publish a
 * name to anyone. A **Discord player** is usually someone whose username you do not
 * know but whose face is in the server roster, so the roster picker adds them by
 * account id. Both go to the same endpoint and produce the same mutual friendship:
 * nothing downstream can tell which way a friend arrived, and nothing may.
 *
 * The friend list is fetched by this panel rather than read off ClientState, for
 * the same reason PlayerPicker owns the roster: nothing else in the client needs
 * the list, and a cached copy that only one screen reads is a copy that goes stale
 * unwatched. It refetches on every open, which is one request and the only way an
 * accept made on the website shows up here.
 *
 * The server is the gate, not this screen: /api/v1/craft/send checks the friendship
 * itself, so nothing here can turn into a send to a stranger by drawing the wrong
 * list.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class ProfilePanel : SidebarPanel
    {
        public override string Title => "Profile";

        private bool lastLoading;
        private bool lastHadProfile;
        private int lastBalance;
        private int lastXp;
        private int lastDebt;
        private bool requested;
        private bool logoutConfirm;

        // ── Friends state ───────────────────────────────────────────────────

        private readonly PlayerPicker roster = new PlayerPicker { From = PlayerPicker.Source.Roster };

        private List<object> friends;
        private List<object> incoming;
        private List<object> outgoing;

        private bool friendsRequested;
        private bool friendsLoading;
        private string friendsError;

        private string addName = "";
        private bool showRoster;

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.State;
            if (main == null) return;

            var profile = main.ProfileData;
            Snapshot(main, profile);

            var col = UIF.Box(Host, "Profile").Column(Theme.Space2).Flex(1f, 1f);

            UIF.PanelHeader(col, "Profile", () =>
            {
                main.RequestProfileRefresh();
                FetchFriends(true);
                ClearStatus();
                MarkDirty();
            });

            if (mod.Api == null || !mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                       "Use the toolbar button to link it; your balance, XP and friends appear here after that.");
                return;
            }

            if (profile == null)
            {
                // `!requested` means Poll has not yet fired its one on-demand fetch,
                // so this is the frame before loading starts — not a failure. Without
                // that distinction the panel flashes "unavailable" every time it opens.
                bool pending = main.ProfileLoading || !requested;
                UIF.Notice(col, pending ? "Loading profile…" : "Profile unavailable.",
                       pending ? null : "The server could not be reached. Try Refresh.");
                return;
            }

            El body;
            UIF.ScrollView(col, out body, "profile").Flex(1f, 1f);

            // Identity + the stats the site shows, in the site's order.
            var card = UIF.Card(body, "Account").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, MiniJSON.GetString(profile, "username"), Theme.FontLg).Bold();

            string currency = MiniJSON.GetString(profile, "currency_name", "KCoins");
            var stats = UIF.Box(card, "Stats").Column(Theme.Space1);
            Stat(stats, currency, MiniJSON.GetInt(profile, "balance"));
            Stat(stats, "Level", MiniJSON.GetInt(profile, "level"));
            Stat(stats, "XP", MiniJSON.GetInt(profile, "xp"));
            // No message count: XP is no longer earned by talking, so nothing
            // increments it. The server still sends the field so an older client
            // keeps showing its own historical number rather than a sudden zero.

            // Unpaid contract fines. Drawn only when there are any — but when there
            // are, it has to be said here: a share of every payout is going to them,
            // and rewards that arrive smaller with nothing explaining why read as the
            // mod being broken and arrive as a bug report rather than as an appeal.
            int debt = MiniJSON.GetInt(profile, "debt");
            if (debt > 0)
            {
                int pct = MiniJSON.GetInt(profile, "debt_garnish_percent");
                UIF.Notice(body, "Unpaid fines: " + debt + " " + currency,
                       pct > 0
                           ? pct + "% of what you earn goes towards them until they are "
                             + "paid off. Nothing else is restricted."
                           : "Repaid out of a share of what you earn.");
            }

            // Unlocked KSP achievement levels, as pips rather than the IMGUI's
            // emoji run — a borrowed font is no place to bet on emoji coverage.
            var levels = MiniJSON.GetList(profile, "unlocked_levels");
            if (levels != null && levels.Count > 0)
            {
                var ach = UIF.Card(body, "Achievements").Column(Theme.Space2).Pad(Theme.Space3);
                UIF.Label(ach, "KSP achievements", Theme.FontSm).Bold();

                var pips = UIF.Box(ach, "Pips").Row(Theme.Space1);
                foreach (var l in levels)
                    UIF.Badge(pips, "L" + ToInt(l), Theme.AccentForeground, Theme.Accent);
                UIF.Grow(pips);
            }

            var server = UIF.Card(body, "Server").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(server, "Server", Theme.FontSm).Bold();
            UIF.Muted(server, mod.Api.ServerUrl, Theme.FontXs).Body();

            BuildFriendsSection(body, mod);

            BuildAccountActions(body, mod);
        }

        // ── Friends ─────────────────────────────────────────────────────────

        private void BuildFriendsSection(El parent, GeneKermanMod mod)
        {
            BuildAdd(parent, mod);

            if (friendsError != null)
                UIF.Label(parent, friendsError, Theme.FontXs, Theme.Destructive).Body();
            else if (friendsLoading && friends == null)
                UIF.Muted(parent, "Loading friends…").Body();

            // Requests first: they are the only rows here that are waiting on the
            // player. A friend list is a reference, an unanswered request is a task.
            BuildRequests(parent, mod, incoming, true);
            BuildRequests(parent, mod, outgoing, false);
            BuildFriends(parent, mod);
        }

        private void BuildAdd(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Add").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Add a friend", Theme.FontSm).Bold();
            UIF.Muted(card,
                "You can only quicksend craft to friends, both ways, once they accept. "
                + "A friendship is between two people, so it works across Discord servers "
                + "and with players who only have a Boundless account.").Body();
            UIF.Muted(card, "Their Boundless username: the permanent one, not a nickname.").Body();

            var row = UIF.Box(card, "AddRow").Row(Theme.Space2).H(30);
            var field = UIF.TextField(row, addName, "username", 30);
            field.E.PrefW(0).Flex(1f);
            field.OnChanged(s => addName = s);

            UIF.Button(row, "Send", () => Request(mod, addName.Trim(), null),
                       BtnStyle.Primary, 30)
               .Interactable(!Busy && !string.IsNullOrEmpty((addName ?? "").Trim()))
               .E.W(72);

            // The other half: someone in your Discord server whose username you do
            // not know. Collapsed by default — it is a second network request and a
            // whole list, and the common case is that a friend told you their name.
            UIF.Button(card, showRoster ? "Hide server list" : "Or pick from your Discord server",
                       () => { showRoster = !showRoster; MarkDirty(); },
                       BtnStyle.Ghost, 26);

            if (showRoster)
            {
                roster.Build(card, "Nobody else has linked KSP in this server yet.");
                UIF.Button(card,
                    roster.HasSelection ? "Send request to " + roster.SelectedName : "Pick a player",
                    () =>
                    {
                        if (roster.HasSelection) Request(mod, null, roster.SelectedId);
                    }, BtnStyle.Secondary, 28)
                   .Interactable(!Busy && roster.HasSelection);
            }

            // The panel's one status line, drawn here rather than next to the account
            // actions: friend requests are the only thing on this screen that reports
            // a result, and Status is shared, so a second DrawStatus would print the
            // same sentence twice.
            DrawStatus(card);
        }

        private void Request(GeneKermanMod mod, string username, string userId)
        {
            var done = BeginAction();
            mod.RunCoroutine(mod.Api.SendFriendRequest(username, userId, (ok, data, err) =>
            {
                // A 200 can still be a refusal ("You're already friends."), so the
                // success flag comes out of the body rather than off the transport.
                bool accepted = ok && MiniJSON.GetBool(data, "success");
                string message = ok
                    ? MiniJSON.GetString(data, "message", accepted ? "Request sent." : "Not sent.")
                    : (err ?? "Couldn't send that request.");
                done(accepted, message);
                if (accepted)
                {
                    addName = "";
                    roster.ClearSelection();
                    FetchFriends(true);
                }
            }));
        }

        private void BuildRequests(El parent, GeneKermanMod mod, List<object> rows, bool inbound)
        {
            if (rows == null || rows.Count == 0) return;

            var card = UIF.Card(parent, inbound ? "Incoming" : "Outgoing")
                          .Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, inbound ? "Requests for you (" + rows.Count + ")"
                                    : "Waiting on them (" + rows.Count + ")",
                      Theme.FontSm).Bold();

            foreach (var entry in rows)
            {
                var d = entry as Dictionary<string, object>;
                if (d == null) continue;
                string id = MiniJSON.GetString(d, "user_id");
                if (string.IsNullOrEmpty(id)) continue;

                var row = PersonRow(card, d);
                if (inbound)
                {
                    // PrefW on every one of these, and it is not decoration. A
                    // Button's caption is a child *stretched* over its rect rather
                    // than a layout child, so the button has no preferred width of
                    // its own — in a Row (childForceExpandWidth = false) it collapses
                    // to nothing and the caption, which overflows by design, spills
                    // sideways across the level badge next to it. The widths match
                    // the same captions elsewhere (NotificationsPanel's gift offer,
                    // ContractsPanel's select bar) so the friend rows and the rest of
                    // the sidebar keep one button size. A Column force-expands its
                    // children, which is why a full-width button needs none of this.
                    UIF.Button(row, "Accept", () => Act(mod, id, "accept"), BtnStyle.Primary, 24, Theme.Space2)
                       .Interactable(!Busy).E.PrefW(72);
                    UIF.Button(row, "Decline", () => Act(mod, id, "decline"), BtnStyle.Ghost, 24, Theme.Space2)
                       .Interactable(!Busy).E.PrefW(76);
                }
                else
                {
                    // "Cancel" rather than "Decline" for the same edit on the server:
                    // withdrawing your own request and turning down someone else's are
                    // one operation, and only the word differs.
                    UIF.Button(row, "Cancel", () => Act(mod, id, "decline"), BtnStyle.Ghost, 24, Theme.Space2)
                       .Interactable(!Busy).E.PrefW(60);
                }
            }
        }

        private void BuildFriends(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "List").Column(Theme.Space2).Pad(Theme.Space3);
            int count = friends == null ? 0 : friends.Count;
            UIF.Label(card, "Friends (" + count + ")", Theme.FontSm).Bold();

            if (count == 0)
            {
                UIF.Muted(card, friendsError != null
                                    ? "Couldn't load your friends."
                                    : "No friends yet. Add one above, and they "
                                      + "can send you craft as soon as they accept.").Body();
                return;
            }

            // Rows straight into the card, not a scroll view of their own: the whole
            // panel is already inside one, and a nested scroll swallows the wheel
            // wherever the pointer happens to be over the inner list.
            foreach (var entry in friends)
            {
                var d = entry as Dictionary<string, object>;
                if (d == null) continue;
                string id = MiniJSON.GetString(d, "user_id");
                if (string.IsNullOrEmpty(id)) continue;

                var row = PersonRow(card, d);
                bool fav = Favorites.IsFavorite(id);
                UIF.IconButton(row, StarIcon(fav), () =>
                {
                    Favorites.Set(id, !fav);
                    MarkDirty();
                }, BtnStyle.Ghost, 24, 14);
                UIF.Button(row, "Remove", () => Act(mod, id, "remove"), BtnStyle.Ghost, 24, Theme.Space2)
                   .Interactable(!Busy).E.PrefW(68);
            }
        }

        private static SpriteKey StarIcon(bool filled)
            => filled
               ? Sprites.Star(Theme.Primary, 14)
               : Sprites.Star(Theme.Alpha(Theme.MutedForeground, 0f), 14, Theme.MutedForeground, 2);

        /// <summary>
        /// Name, handle and level — the same three the picker draws, minus the
        /// avatar. No picture here on purpose: this list is people the player has
        /// already chosen, so a face adds nothing that the name does not, and
        /// downloading one per row would make opening the panel a burst of
        /// requests. Streamer mode is respected all the same: the handle is a detail
        /// about somebody else.
        /// </summary>
        private static El PersonRow(El parent, Dictionary<string, object> d)
        {
            var row = UIF.Box(parent, "Person").Row(Theme.Space2)
                         .Pad(Theme.Space2, Theme.Space1, Theme.Space1, Theme.Space1)
                         .ChildAlign(TextAnchor.MiddleLeft)
                         .MinH(32);

            var text = UIF.Box(row, "Text").Column(0).PrefW(0).Flex(1f);
            UIF.Label(text, MiniJSON.GetString(d, "name"), Theme.FontSm).Ellipsis();

            string uname = MiniJSON.GetString(d, "username");
            if (!StreamerMode.HideDetails && !string.IsNullOrEmpty(uname))
                UIF.Muted(text, "@" + uname).Ellipsis();

            int level = MiniJSON.GetInt(d, "level");
            if (level > 0) UIF.Badge(row, "Lv " + level, Theme.MutedForeground);

            return row;
        }

        private void Act(GeneKermanMod mod, string id, string action)
        {
            var done = BeginAction();
            mod.RunCoroutine(mod.Api.FriendAction(id, action, (ok, data, err) =>
            {
                bool good = ok && MiniJSON.GetBool(data, "success");
                string message = ok
                    ? MiniJSON.GetString(data, "message", good ? "Done." : "That didn't work.")
                    : (err ?? "Couldn't reach the server.");
                done(good, message);
                // Refetch either way: a refusal is usually "that is no longer there",
                // which means this list is the thing that is wrong.
                FetchFriends(true);
            }));
        }

        /// <summary>
        /// One fetch per visit. The latch is set before the request rather than in
        /// the callback, or a slow server would be asked again every frame until it
        /// answered.
        /// </summary>
        private void FetchFriends(bool force = false)
        {
            if (friendsLoading || (friendsRequested && !force)) return;

            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null || !mod.Api.IsLinked) return;

            friendsRequested = true;
            friendsLoading = true;
            friendsError = null;

            mod.RunCoroutine(mod.Api.GetFriends((ok, data, err) =>
            {
                friendsLoading = false;
                if (ok && data != null)
                {
                    friends = MiniJSON.GetList(data, "friends");
                    incoming = MiniJSON.GetList(data, "incoming");
                    outgoing = MiniJSON.GetList(data, "outgoing");
                    friendsError = null;
                }
                else
                {
                    friendsError = err ?? "Couldn't load your friends.";
                }
                MarkDirty();
            }));
        }

        // ── Account ─────────────────────────────────────────────────────────

        private void BuildAccountActions(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Account actions").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Account", Theme.FontSm).Bold();

            // Unlink is local and instant — it drops this install's token and raises
            // the IMGUI link window, because re-linking is an in-game act (a 6-digit
            // code plus a Discord approval) that the sidebar cannot carry out.
            UIF.Button(card, "Unlink this install", () =>
            {
                mod.Api.ClearToken();
                mod.ShowLinkWindow = true;
                MarkDirty();
            }, BtnStyle.Secondary, 28);

            // Log out everywhere invalidates every token on the account including
            // this one, so it gets the same two-click confirm the classic window
            // uses. One click here is a locked-out player on every machine.
            if (!logoutConfirm)
            {
                UIF.Button(card, "Log out all devices", () => { logoutConfirm = true; MarkDirty(); },
                           BtnStyle.Ghost, 28);
            }
            else
            {
                UIF.Muted(card, "This logs out every device, including this one.").Body();
                var row = UIF.Box(card, "ConfirmRow").Row(Theme.Space2).H(28);
                UIF.Button(row, "Confirm", () =>
                {
                    logoutConfirm = false;
                    mod.State?.RequestLogoutAllDevices();
                    MarkDirty();
                }, BtnStyle.Destructive, 28).E.Flex(1f);
                UIF.Button(row, "Cancel", () => { logoutConfirm = false; MarkDirty(); },
                           BtnStyle.Ghost, 28).E.Flex(1f);
            }

            // Opened via the mod so KSP drives the OS browser, exactly as the
            // consent gate and the browser UI already do.
            var links = UIF.Box(card, "Links").Row(Theme.Space2).H(26);
            UIF.Button(links, "Privacy", () => Application.OpenURL(ApiClient.PrivacyPolicyUrl),
                       BtnStyle.Ghost, 26).E.Flex(1f);
            UIF.Button(links, "Terms", () => Application.OpenURL(ApiClient.TermsOfServiceUrl),
                       BtnStyle.Ghost, 26).E.Flex(1f);
        }

        /// <summary>One "12,340 / KCoins" pair, the sidebar's take on the site's stat tile.</summary>
        private static void Stat(El parent, string label, int value)
        {
            var row = UIF.Box(parent, "Stat").Row(Theme.Space2).H(22);
            UIF.Label(row, label.ToUpperInvariant(), Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(row);
            UIF.Label(row, value.ToString("N0"), Theme.FontBase, Theme.Primary).Align(TextAlign.Right);
        }


        private static int ToInt(object o)
        {
            if (o is long l) return (int)l;
            if (o is double d) return (int)d;
            int parsed;
            return int.TryParse(o?.ToString() ?? "", out parsed) ? parsed : 0;
        }

        private void Snapshot(ClientState main, Dictionary<string, object> profile)
        {
            lastLoading = main.ProfileLoading;
            lastHadProfile = profile != null;
            lastBalance = profile == null ? -1 : MiniJSON.GetInt(profile, "balance");
            lastXp = profile == null ? -1 : MiniJSON.GetInt(profile, "xp");
            lastDebt = profile == null ? -1 : MiniJSON.GetInt(profile, "debt");
        }

        protected override void Poll()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.State;
            if (main == null) return;

            FetchFriends();
            if (showRoster)
            {
                roster.EnsureLoaded();
                roster.Tick();
            }

            // the classic window only fetched when it was opened, so a player who never
            // opens the classic window would otherwise see an empty panel forever.
            // Once per becoming-visible, not once per frame — a failed fetch leaves
            // profile null, and retrying on every frame would hammer the server.
            if (!requested && main.ProfileData == null && !main.ProfileLoading &&
                mod.Api != null && mod.Api.IsLinked)
            {
                requested = true;
                main.RequestProfileRefresh();
                return;
            }

            var profile = main.ProfileData;
            if (main.ProfileLoading != lastLoading ||
                (profile != null) != lastHadProfile ||
                (profile != null && (MiniJSON.GetInt(profile, "balance") != lastBalance ||
                                     MiniJSON.GetInt(profile, "xp") != lastXp ||
                                     MiniJSON.GetInt(profile, "debt") != lastDebt)))
            {
                MarkDirty();
            }
        }

        internal override void OnShown()
        {
            // Allow exactly one automatic fetch attempt per time the panel is opened.
            requested = false;
            // A confirm left half-pressed must not survive being tabbed away from,
            // or the next visit shows a primed destructive button.
            logoutConfirm = false;

            // The "Send request to X" button lives on the panel while the selection
            // lives in the picker, so a pick has to redraw the panel — the same wiring
            // ToolsPanel's quicksend button needs.
            roster.Attach(MarkDirty);

            // A fresh visit refetches the friend list. An accept can happen on the
            // website or in another session, and this list is the only thing that
            // would still be claiming otherwise.
            friendsRequested = false;
            addName = "";
            roster.Reset();

            ClearStatus();
        }

        internal override void OnHidden() => roster.Dispose();

        internal override void OnSceneChanged() => roster.Dispose();
    }
}
