/*
 * UI/Gui/Panels/FriendsPanel.cs – Who you may hand a craft to.
 *
 * Quicksend used to pick from the server roster: every corp in the guild. That made
 * the recipient of a hand-over — and `kind="vessel"` really is one, the ship and its
 * crew leave the sender's save the moment the server confirms — anybody at all in a
 * large Discord. The recipient list is now the friend list, and this panel is where
 * that list is built.
 *
 * Two ways in, because there are two kinds of player and neither can be found the
 * other's way. A **Boundless account** has no Discord name to search for, so it is
 * added by the username it had to claim before it could publish a name to anyone. A
 * **Discord player** is usually someone whose username you do not know but whose face
 * is in the server roster, so the roster picker adds them by account id. Both go to
 * the same endpoint and produce the same mutual friendship: nothing downstream can
 * tell which way a friend arrived, and nothing may.
 *
 * The panel owns its fetch rather than reading ClientState, for the same reason
 * PlayerPicker owns the roster: nothing else in the client needs this list, and a
 * cached copy that only one screen reads is a copy that goes stale unwatched. It
 * refetches on every open, which is one request and the only way an accept made on
 * the website shows up here.
 *
 * The server is the gate, not this screen: /api/v1/craft/send checks the friendship
 * itself, so nothing here can turn into a send to a stranger by drawing the wrong
 * list.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class FriendsPanel : SidebarPanel
    {
        public override string Title => "Friends";

        private readonly PlayerPicker roster = new PlayerPicker { From = PlayerPicker.Source.Roster };

        private List<object> friends;
        private List<object> incoming;
        private List<object> outgoing;

        private bool requested;
        private bool loading;
        private string error;

        private string addName = "";
        private bool showRoster;

        internal override void OnShown()
        {
            // The "Send request to X" button lives on the panel while the selection
            // lives in the picker, so a pick has to redraw the panel — the same wiring
            // ToolsPanel's quicksend button needs.
            roster.Attach(MarkDirty);

            // A fresh visit refetches. An accept can happen on the website or in
            // another session, and this list is the only thing that would still be
            // claiming otherwise.
            requested = false;
            addName = "";
            roster.Reset();
            ClearStatus();
        }

        internal override void OnHidden() => roster.Dispose();

        internal override void OnSceneChanged() => roster.Dispose();

        protected override void Poll()
        {
            Fetch();
            if (showRoster)
            {
                roster.EnsureLoaded();
                roster.Tick();
            }
        }

        /// <summary>
        /// One fetch per visit. The latch is set before the request rather than in
        /// the callback, or a slow server would be asked again every frame until it
        /// answered.
        /// </summary>
        private void Fetch(bool force = false)
        {
            if (loading || (requested && !force)) return;

            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null || !mod.Api.IsLinked) return;

            requested = true;
            loading = true;
            error = null;

            mod.RunCoroutine(mod.Api.GetFriends((ok, data, err) =>
            {
                loading = false;
                if (ok && data != null)
                {
                    friends = MiniJSON.GetList(data, "friends");
                    incoming = MiniJSON.GetList(data, "incoming");
                    outgoing = MiniJSON.GetList(data, "outgoing");
                    error = null;
                }
                else
                {
                    error = err ?? "Couldn't load your friends.";
                }
                MarkDirty();
            }));
        }

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) return;

            var col = UIF.Box(Host, "Friends").Column(Theme.Space2).Flex(1f, 1f);
            UIF.PanelHeader(col, "Friends", () => { Fetch(true); ClearStatus(); MarkDirty(); });

            if (!mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                           "Link this install from the toolbar button; your friends appear here after that.");
                return;
            }

            El body;
            UIF.ScrollView(col, out body, "friends").Flex(1f, 1f);

            UIF.Muted(body,
                "You can only quicksend craft to friends, both ways, once they accept. "
                + "A friendship is between two people, so it works across Discord servers "
                + "and with players who only have a Boundless account.").Body();

            BuildAdd(body, mod);

            if (error != null)
                UIF.Label(body, error, Theme.FontXs, Theme.Destructive).Body();
            else if (loading && friends == null)
                UIF.Muted(body, "Loading…").Body();

            // Requests first: they are the only rows here that are waiting on the
            // player. A friend list is a reference, an unanswered request is a task.
            BuildRequests(body, mod, incoming, true);
            BuildRequests(body, mod, outgoing, false);
            BuildFriends(body, mod);
        }

        // ── Add ─────────────────────────────────────────────────────────────

        private void BuildAdd(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Add").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Add a friend", Theme.FontSm).Bold();
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
                    Fetch(true);
                }
            }));
        }

        // ── Requests ────────────────────────────────────────────────────────

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
                    UIF.Button(row, "Accept", () => Act(mod, id, "accept"), BtnStyle.Primary, 24, Theme.Space2)
                       .Interactable(!Busy);
                    UIF.Button(row, "Decline", () => Act(mod, id, "decline"), BtnStyle.Ghost, 24, Theme.Space2)
                       .Interactable(!Busy);
                }
                else
                {
                    // "Cancel" rather than "Decline" for the same edit on the server:
                    // withdrawing your own request and turning down someone else's are
                    // one operation, and only the word differs.
                    UIF.Button(row, "Cancel", () => Act(mod, id, "decline"), BtnStyle.Ghost, 24, Theme.Space2)
                       .Interactable(!Busy);
                }
            }
        }

        // ── Friends ─────────────────────────────────────────────────────────

        private void BuildFriends(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "List").Column(Theme.Space2).Pad(Theme.Space3);
            int count = friends == null ? 0 : friends.Count;
            UIF.Label(card, "Friends (" + count + ")", Theme.FontSm).Bold();

            if (count == 0)
            {
                UIF.Muted(card, error != null ? "Couldn't load your friends."
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
                   .Interactable(!Busy);
            }
        }

        private static SpriteKey StarIcon(bool filled)
            => filled
               ? Sprites.Star(Theme.Primary, 14)
               : Sprites.Star(Theme.Alpha(Theme.MutedForeground, 0f), 14, Theme.MutedForeground, 2);

        /// <summary>
        /// Name, handle and level — the same three the picker draws, minus the
        /// avatar. No picture here on purpose: this panel is a list of people the
        /// player has already chosen, so a face adds nothing that the name does not,
        /// and downloading one per row would make opening the panel a burst of
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
                Fetch(true);
            }));
        }
    }
}
