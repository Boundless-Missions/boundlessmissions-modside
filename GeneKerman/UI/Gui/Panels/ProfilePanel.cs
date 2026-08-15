/*
 * UI/Gui/Panels/ProfilePanel.cs – Account summary, in uGUI.
 *
 * The React `ProfileCard` (WebUI/src/screens/ProfileCard.tsx) is the visual
 * specification; MainWindow.DrawProfileTab is the data specification. Neither is
 * re-implemented: the numbers come from MainWindow's cached `profile` blob, which
 * is the same dictionary both other front ends render.
 *
 * Read-only, deliberately. Unlink, log-out-all and the privacy/terms links are
 * *actions*, and this phase does pure-data screens first — they land with the
 * rest of the action bar. Until then they remain one click away in the classic
 * window, which the sidebar does not replace.
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
        private bool requested;
        private bool logoutConfirm;

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            var profile = main.ProfileData;
            Snapshot(main, profile);

            var col = UIF.Box(Host, "Profile").Column(Theme.Space2).Flex(1f, 1f);

            UIF.PanelHeader(col, "Profile", () => { main.RequestProfileRefresh(); MarkDirty(); });

            if (mod.Api == null || !mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                       "Link this install from the classic window to see your account here.");
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
            UIF.ScrollView(col, out body).Flex(1f, 1f);

            // Identity + the four stats the site shows, in the site's order.
            var card = UIF.Card(body, "Account").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, MiniJSON.GetString(profile, "username"), Theme.FontLg).Bold();

            string currency = MiniJSON.GetString(profile, "currency_name", "KCoins");
            var stats = UIF.Box(card, "Stats").Column(Theme.Space1);
            Stat(stats, currency, MiniJSON.GetInt(profile, "balance"));
            Stat(stats, "Level", MiniJSON.GetInt(profile, "level"));
            Stat(stats, "XP", MiniJSON.GetInt(profile, "xp"));
            Stat(stats, "Messages", MiniJSON.GetInt(profile, "messages"));

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

            BuildAccountActions(body, mod);
        }

        private void BuildAccountActions(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Account actions").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Account", Theme.FontSm).Bold();

            DrawStatus(card);

            // Unlink is local and instant — it drops this install's token and raises
            // the IMGUI link window, because re-linking is an in-game act (a 6-digit
            // code plus a Discord approval) that the sidebar cannot carry out.
            UIF.Button(card, "Unlink this install", () =>
            {
                mod.Api.ClearToken();
                mod.ShowMainWindow = false;
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
                    mod.MainWindowRef?.RequestLogoutAllDevices();
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

        private void Snapshot(MainWindow main, Dictionary<string, object> profile)
        {
            lastLoading = main.ProfileLoading;
            lastHadProfile = profile != null;
            lastBalance = profile == null ? -1 : MiniJSON.GetInt(profile, "balance");
            lastXp = profile == null ? -1 : MiniJSON.GetInt(profile, "xp");
        }

        protected override void Poll()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            // MainWindow only fetches when *it* is opened, so a player who never
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
                                     MiniJSON.GetInt(profile, "xp") != lastXp)))
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
            ClearStatus();
        }
    }
}
