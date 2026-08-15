/*
 * UI/Gui/Panels/GalleryPanel.cs – Dev-only widget gallery.
 *
 * Every primitive UIF can build, in one column, so it can be held up against
 * the live website side by side. This comparison is the actual deliverable of
 * the phase: if uGUI cannot get close enough to the site here, nothing built on
 * top of it will either, and the sidebar stops at ~1,150 new lines with no IMGUI
 * touched.
 *
 * Never shown to players. Gated on `enableGuiGallery = true` in
 * PluginData/settings.cfg, which has no toggle in any UI.
 */

using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class GalleryPanel : SidebarPanel
    {
        public override string Title => "Gallery";

        protected override void Rebuild()
        {
            var col = UIF.Box(Host, "Gallery").Column(Theme.Space2).Flex(1f, 1f);

            El list;
            UIF.ScrollView(col, out list).Flex(1f, 1f);

            Section(list, "Type scale");
            var type = Card(list);
            UIF.Label(type, "text-xl · 20 · Bold", Theme.FontXl).Bold();
            UIF.Label(type, "text-lg · 17", Theme.FontLg);
            UIF.Label(type, "text-base · 15", Theme.FontBase);
            UIF.Label(type, "text-sm · 13", Theme.FontSm);
            UIF.Muted(type, "text-xs · 11 · muted-foreground");

            Section(list, "Buttons");
            var btns = Card(list);
            UIF.Button(btns, "Primary", null, BtnStyle.Primary);
            UIF.Button(btns, "Secondary", null);
            UIF.Button(btns, "Ghost", null, BtnStyle.Ghost);
            UIF.Button(btns, "Destructive", null, BtnStyle.Destructive);
            UIF.Button(btns, "Disabled", null).Interactable(false);

            Section(list, "Button row");
            var row = RowCard(list);
            UIF.Button(row, "Accept", null, BtnStyle.Primary).E.Flex(1f);
            UIF.Button(row, "Decline", null, BtnStyle.Ghost).E.Flex(1f);

            Section(list, "Badges");
            var badges = RowCard(list);
            UIF.Badge(badges, "Easy", Theme.Status("success"));
            UIF.Badge(badges, "Medium", Theme.Status("warning"));
            UIF.Badge(badges, "Hard", Theme.Status("danger"));
            UIF.Badge(badges, "Info", Theme.Status("info"));

            Section(list, "Surfaces");
            var surfaces = Card(list);
            Swatch(surfaces, "card", Theme.Card);
            Swatch(surfaces, "secondary", Theme.Secondary);
            Swatch(surfaces, "muted", Theme.Muted);
            Swatch(surfaces, "accent", Theme.Accent);
            Swatch(surfaces, "primary", Theme.Primary);
            Swatch(surfaces, "destructive", Theme.Destructive);

            Section(list, "Radii");
            var radii = RowCard(list);
            RadiusChip(radii, "sm 6", Theme.RadiusSm);
            RadiusChip(radii, "md 11", Theme.Radius);
            RadiusChip(radii, "pill 14", Theme.RadiusPill);

            Section(list, "Card with body copy");
            var sample = Card(list);
            UIF.Label(sample, "Contract approved", Theme.FontSm).Bold();
            UIF.Label(sample, "Your submission for \"Return a crew from low Mun orbit\" was approved. " +
                              "1,450 credits have been paid into your balance.",
                      Theme.FontSm, Theme.MutedForeground).Body();
            UIF.Muted(sample, "2 minutes ago");

            Section(list, "Dots");
            var dots = RowCard(list);
            UIF.Box(dots, "D1").Dot(Theme.Primary, 8);
            UIF.Box(dots, "D2").Dot(Theme.Status("warning"), 10);
            UIF.Box(dots, "D3").Dot(Theme.Destructive, 12);
            UIF.Box(dots, "D4").Dot(Theme.Muted, 16, Theme.Border);
            UIF.Grow(dots);

            Section(list, "Divider");
            var div = Card(list);
            UIF.Label(div, "Above", Theme.FontSm);
            UIF.Divider(div);
            UIF.Label(div, "Below", Theme.FontSm);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void Section(El parent, string title)
        {
            UIF.Muted(parent, title.ToUpperInvariant(), Theme.FontXs)
               .Color(Theme.Alpha(Theme.MutedForeground, 0.8f));
        }

        /// <summary>A card whose children stack vertically.</summary>
        private static El Card(El parent)
        {
            return UIF.Card(parent, "GalleryCard")
                      .Column(Theme.Space2)
                      .Pad(Theme.Space3);
        }

        /// <summary>
        /// A card whose children sit in a row. Separate from Card() because a
        /// GameObject may carry only one layout group — adding a Row on top of a
        /// Column is a Unity error, not a last-writer-wins.
        /// </summary>
        private static El RowCard(El parent)
        {
            return UIF.Card(parent, "GalleryRowCard")
                      .Row(Theme.Space2)
                      .Pad(Theme.Space3);
        }

        private static void Swatch(El parent, string name, Color color)
        {
            var row = UIF.Box(parent, "Swatch").Row(Theme.Space2).H(22);
            UIF.Box(row, "Chip").Bg(color, Theme.RadiusSm, Theme.Border).Size(34f, 18f);
            UIF.Label(row, name, Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(row);
            UIF.Label(row, Hex(color), Theme.FontXs, Theme.MutedForeground);
        }

        private static void RadiusChip(El parent, string label, int radius)
        {
            var chip = UIF.Box(parent, "RadiusChip")
                .Row(0)
                .ChildAlign(TextAnchor.MiddleCenter)
                .Bg(Theme.Secondary, radius, Theme.Border)
                .Size(96f, 34f);
            UIF.Label(chip, label, Theme.FontXs, Theme.MutedForeground);
        }

        private static string Hex(Color c) =>
            "#" + ColorUtility.ToHtmlStringRGB(c).ToLowerInvariant();
    }
}
