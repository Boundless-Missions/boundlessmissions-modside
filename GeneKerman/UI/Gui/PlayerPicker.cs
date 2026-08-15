/*
 * UI/Gui/PlayerPicker.cs – Pick another player: search, favourites, selection.
 *
 * A widget rather than part of a panel, for the reason WebUI/src/components/PlayerPicker.tsx
 * gives about itself: quicksend and contract creation both want "the same list", and that
 * is exactly the kind of thing that drifts — one grows a favourites filter, the other keeps
 * a bare list, and now starring somebody only works in one place.
 *
 * Favourites come from Favorites.cs (PluginData), the same store the browser UI writes
 * through /gk/favorites, so a star set in one front end is a star in the other.
 *
 * The list rebuilds itself, not the panel. Typing in the search box must filter as you
 * type, and a panel rebuild would destroy the box mid-keystroke — SidebarPanel.Tick even
 * refuses to do it while a field has focus. So the picker keeps a handle on its own list
 * container and refills only that.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class PlayerPicker
    {
        private const float ListHeight = 190f;
        private const int AvatarSize = 26;

        /// <summary>
        /// How often a batch of arrived avatars is allowed to redraw the list. One
        /// redraw per completed download would rebuild the rows fifty times while a
        /// roster loads.
        /// </summary>
        private const float AvatarRefreshSeconds = 0.3f;

        private List<object> corps;          // raw dicts from /api/v1/corps/list
        private bool loading;
        private bool requested;
        private string error;

        private string query = "";
        private bool favoritesOnly;

        private El listHost;
        private Action onSelectionChanged;

        /// <summary>
        /// Downloaded avatars, keyed by user id. A null value means "tried and
        /// failed", which is what stops a broken URL being re-fetched every redraw.
        /// The picker owns these textures — see Dispose.
        /// </summary>
        private readonly Dictionary<string, Texture2D> avatars = new Dictionary<string, Texture2D>();
        private readonly HashSet<string> avatarPending = new HashSet<string>();
        private bool avatarsArrived;
        private float nextAvatarRefresh;

        /// <summary>The chosen player, or null/empty when nothing is selected.</summary>
        public string SelectedId { get; private set; }
        public string SelectedName { get; private set; }

        public bool HasSelection => !string.IsNullOrEmpty(SelectedId);

        public void Attach(Action selectionChanged) => onSelectionChanged = selectionChanged;

        /// <summary>
        /// Fetch the roster once. Called from the owning panel's Poll, so a failed
        /// fetch is not retried every frame; OnShown resets the latch.
        /// </summary>
        public void EnsureLoaded()
        {
            if (requested || loading) return;

            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null || !mod.Api.IsLinked) return;

            requested = true;
            loading = true;
            error = null;

            mod.RunCoroutine(mod.Api.GetCorps((ok, data, err) =>
            {
                loading = false;
                if (ok && data != null)
                {
                    corps = MiniJSON.GetList(data, "corps");
                    error = null;
                }
                else
                {
                    corps = null;
                    error = err ?? "Failed to load players.";
                }
                RefreshList();
            }));
        }

        /// <summary>Forget the roster and the selection: a new visit starts clean.</summary>
        public void Reset()
        {
            requested = false;
            query = "";
            favoritesOnly = false;
            ClearSelection();
        }

        /// <summary>
        /// Pumped from the owning panel's Poll. Only job: fold a burst of arriving
        /// avatars into one redraw.
        /// </summary>
        public void Tick()
        {
            if (!avatarsArrived || Time.unscaledTime < nextAvatarRefresh) return;

            avatarsArrived = false;
            nextAvatarRefresh = Time.unscaledTime + AvatarRefreshSeconds;
            RefreshList();
        }

        /// <summary>
        /// Hand back every downloaded avatar. Unity does not reclaim a Texture2D on
        /// its own, and a scene load destroys them behind our back while the cache
        /// still claims to hold them — so this runs on hide and on scene change.
        /// </summary>
        public void Dispose()
        {
            foreach (var t in avatars.Values)
                if (t != null) UnityEngine.Object.Destroy(t);

            avatars.Clear();
            avatarPending.Clear();
            avatarsArrived = false;
        }

        public void ClearSelection()
        {
            SelectedId = null;
            SelectedName = null;
        }

        // ── Build ───────────────────────────────────────────────────────────

        public void Build(El parent, string emptyLabel = "No other players found.")
        {
            var box = UIF.Box(parent, "Picker").Column(Theme.Space2);

            var head = UIF.Box(box, "Search").Row(Theme.Space2).H(28);
            var field = UIF.TextField(head, query, "Search players…", 28);
            field.E.PrefW(0).Flex(1f);
            field.OnChanged(s => { query = s; RefreshList(); });

            // The filter toggle does invert: here the whole button is the state, so
            // the star is drawn in the fill's foreground colour rather than in
            // --primary, and stays visible on top of it.
            var filterIcon = favoritesOnly
                ? Sprites.Star(Theme.PrimaryForeground, 16)
                : Sprites.Star(Theme.Alpha(Theme.MutedForeground, 0f), 16, Theme.MutedForeground, 2);

            UIF.IconButton(head, filterIcon,
                           () => { favoritesOnly = !favoritesOnly; RefreshList(); },
                           favoritesOnly ? BtnStyle.Primary : BtnStyle.Ghost, 28, 16);

            listHost = UIF.Box(box, "List").Column(1).Pad(Theme.Space1).H(ListHeight)
                          .Bg(Theme.Alpha(Theme.Muted, 0.35f), Theme.RadiusSm, Theme.Border);
            emptyText = emptyLabel;
            RefreshList();
        }

        private string emptyText = "No other players found.";

        /// <summary>
        /// Refill the list in place. Safe to call when the host is gone — a rebuild
        /// or a scene change destroys it, and a fetch that lands afterwards would
        /// otherwise write into a destroyed hierarchy.
        /// </summary>
        private void RefreshList()
        {
            if (listHost == null || listHost.Go == null) return;

            listHost.ClearChildren();

            if (loading || (corps == null && error == null))
            {
                UIF.Muted(listHost, "Loading players…").Body();
                return;
            }
            if (error != null)
            {
                UIF.Label(listHost, error, Theme.FontXs, Theme.Destructive).Body();
                return;
            }

            var shown = Filter();
            if (shown.Count == 0)
            {
                UIF.Muted(listHost,
                    favoritesOnly ? "No favourites yet. Star someone to keep them here."
                    : !string.IsNullOrEmpty(query) ? "No player matches \"" + query + "\"."
                    : emptyText).Body();
                return;
            }

            El content;
            UIF.ScrollView(listHost, out content).Flex(1f, 1f);
            foreach (var c in shown) Row(content, c);
        }

        private List<Player> Filter()
        {
            var list = new List<Player>();
            if (corps == null) return list;

            string me = OwnUserId();
            string q = (query ?? "").Trim().ToLowerInvariant();

            foreach (var entry in corps)
            {
                var d = entry as Dictionary<string, object>;
                if (d == null) continue;

                var p = new Player
                {
                    Id = MiniJSON.GetString(d, "owner_id"),
                    Name = MiniJSON.GetString(d, "owner_name"),
                    Corp = MiniJSON.GetString(d, "corp_name"),
                    Level = MiniJSON.GetInt(d, "level"),
                    AvatarUrl = MiniJSON.GetString(d, "avatar_url"),
                };

                // No self-send and no self-contract; the classic windows filter the
                // same way. A missing profile means no id to compare, and showing
                // yourself is better than showing nobody.
                if (string.IsNullOrEmpty(p.Id) || p.Id == me) continue;
                if (favoritesOnly && !Favorites.IsFavorite(p.Id)) continue;

                if (q.Length > 0 &&
                    (p.Name ?? "").ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0 &&
                    (p.Corp ?? "").ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0)
                    continue;

                list.Add(p);
            }

            // Favourites first, then by name — the site's ordering.
            list.Sort((a, b) =>
            {
                int fa = Favorites.IsFavorite(a.Id) ? 0 : 1;
                int fb = Favorites.IsFavorite(b.Id) ? 0 : 1;
                if (fa != fb) return fa - fb;
                return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
            });

            return list;
        }

        private void Row(El parent, Player p)
        {
            bool selected = SelectedId == p.Id;
            bool fav = Favorites.IsFavorite(p.Id);

            // Clicking the selected player again clears it, so there is a way back to
            // "nobody chosen" without reloading the panel.
            // MinH, not H: a fixed height makes the Column below squeeze its two
            // labels, and a squeezed label using Ellipsis renders nothing at all.
            // That is what blanked every name in the first cut of this list.
            var row = UIF.ClickableRow(parent,
                         () => Select(selected ? null : p.Id, selected ? null : p.Name),
                         selected, Theme.RadiusSm)
                         .Row(Theme.Space2)
                         .Pad(Theme.Space2, Theme.Space1, Theme.Space1, Theme.Space1)
                         .ChildAlign(TextAnchor.MiddleLeft)
                         .MinH(36);

            Avatar(row, p);

            var text = UIF.Box(row, "Text").Column(0).PrefW(0).Flex(1f);
            UIF.Label(text, p.Name, Theme.FontSm).Ellipsis();
            if (!string.IsNullOrEmpty(p.Corp)) UIF.Muted(text, p.Corp).Ellipsis();

            if (p.Level > 0) UIF.Badge(row, "Lv " + p.Level, Theme.MutedForeground);

            // The star is its own button on top of the row's. A click on it toggles
            // the favourite and never reaches the row underneath, because a Button
            // consumes the click it handles.
            UIF.IconButton(row, StarIcon(fav, 14), () =>
            {
                Favorites.Set(p.Id, !fav);
                RefreshList();
            }, BtnStyle.Ghost, 24, 14);
        }

        /// <summary>
        /// Filled when starred, hollow when not.
        ///
        /// The shape carries the state and the button under it stays Ghost in both:
        /// a green star on the green fill of a Primary button is a green rectangle,
        /// which is what the first cut of this shipped as.
        /// </summary>
        private static SpriteKey StarIcon(bool filled, int size)
            => filled
               ? Sprites.Star(Theme.Primary, size)
               : Sprites.Star(Theme.Alpha(Theme.MutedForeground, 0f), size, Theme.MutedForeground, 2);

        /// <summary>
        /// The player's Discord avatar, or their initial on a tinted square until it
        /// arrives (and permanently, if the bot had no avatar URL for them).
        /// </summary>
        private void Avatar(El row, Player p)
        {
            var tex = AvatarTexture(p);
            if (tex != null)
            {
                UIF.Picture(row, tex, AvatarSize);
                return;
            }

            string initial = string.IsNullOrEmpty(p.Name) ? "?" : p.Name.Substring(0, 1).ToUpperInvariant();
            var box = UIF.Box(row, "Avatar").Row(0)
                         .ChildAlign(TextAnchor.MiddleCenter)
                         .Dot(Theme.Secondary, AvatarSize, Theme.Border);
            UIF.Label(box, initial, Theme.FontSm, Theme.MutedForeground);
        }

        private Texture2D AvatarTexture(Player p)
        {
            if (string.IsNullOrEmpty(p.Id)) return null;

            Texture2D tex;
            // A present key means the answer is already known, including "no".
            if (avatars.TryGetValue(p.Id, out tex)) return tex;
            if (string.IsNullOrEmpty(p.AvatarUrl) || avatarPending.Contains(p.Id)) return null;

            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) return null;

            avatarPending.Add(p.Id);
            string id = p.Id;
            mod.RunCoroutine(mod.Api.DownloadFile(p.AvatarUrl, (ok, bytes) =>
            {
                avatarPending.Remove(id);
                avatars[id] = Decode(ok, bytes);
                avatarsArrived = true;
            }));

            return null;
        }

        private static Texture2D Decode(bool ok, byte[] bytes)
        {
            if (!ok || bytes == null || bytes.Length == 0) return null;

            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }

            CircleCrop(tex);
            return tex;
        }

        /// <summary>
        /// Punch a circle out of the avatar, in its own pixels.
        ///
        /// The alternative is a uGUI Mask, which needs a stencil-capable material —
        /// the same reason ScrollView uses RectMask2D instead. Rounding the texture
        /// is deterministic, costs one pass over a 128px image, and antialiases at
        /// the *source* resolution, which is finer than the 26px it is drawn at.
        /// </summary>
        private static void CircleCrop(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            if (w < 2 || h < 2) return;

            var px = tex.GetPixels32();
            float cx = w * 0.5f, cy = h * 0.5f;
            float radius = Mathf.Min(cx, cy) - 0.5f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    // Same 1px analytic edge as Sprites: coverage, not a hard cut.
                    float coverage = Mathf.Clamp01(0.5f - (Mathf.Sqrt(dx * dx + dy * dy) - radius));
                    if (coverage >= 1f) continue;

                    int i = y * w + x;
                    var c = px[i];
                    c.a = (byte)(c.a * coverage);
                    px[i] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        private void Select(string id, string name)
        {
            SelectedId = id;
            SelectedName = name;
            RefreshList();
            onSelectionChanged?.Invoke();
        }

        private static string OwnUserId()
        {
            var profile = GeneKermanMod.Instance?.MainWindowRef?.ProfileData;
            return profile == null ? "" : MiniJSON.GetString(profile, "user_id");
        }

        private struct Player
        {
            public string Id;
            public string Name;
            public string Corp;
            public int Level;
            public string AvatarUrl;
        }
    }
}
