/*
 * UI/Gui/Panels/MarketPanel.cs – Listing a craft for sale, in uGUI.
 *
 * The marketplace is two halves and only one of them is in the game. Browsing,
 * filtering and buying live on the website, which has the screen space and the
 * search the storefront needs; what a browser cannot do is read the ship open in
 * the VAB, render its blueprint and upload the file. So this panel is the selling
 * half, and points at the site for the rest — the same split the classic window's
 * Market tab makes.
 *
 * ToolActions.SellCurrentCraft is the implementation and is called unchanged: the
 * flags, mod list and thumbnail baked into the file, the blueprint render, the
 * listing thumbnail, the life-support flag and the price validation all live there,
 * so this panel and MainWindow.DoSellCraft cannot list two different things.
 *
 * The craft is polled rather than read per frame, exactly as ToolsPanel does, and
 * for the same reason: nothing raises an event when a craft is loaded or saved, and
 * the read touches the filesystem. Mass and cost come from a second, heavier read
 * (it walks every part and asks its modules what they cost), so it only runs when
 * the craft on screen has actually changed.
 */

using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class MarketPanel : SidebarPanel
    {
        public override string Title => "Market";

        private const float CraftPollSeconds = 1.5f;

        private ToolActions.CraftState craft;
        private LifeSupportInfo ls;
        private float mass, cost;
        private float nextCraftRead;
        private string craftSignature = "";

        private string priceText = "100";

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) return;

            var col = UIF.Box(Host, "Market").Column(Theme.Space2).Flex(1f, 1f);
            UIF.PanelHeader(col, "Marketplace", () =>
            {
                ReadCraft(force: true);
                ClearStatus();
                MarkDirty();
            });

            if (!mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                           "Link this install from the classic window to sell crafts.");
                return;
            }

            El body;
            UIF.ScrollView(col, out body, "market").Flex(1f, 1f);

            BuildSell(body, mod);
            BuildBrowse(body, mod);
        }

        // ── Sell ────────────────────────────────────────────────────────────

        private void BuildSell(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Sell").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Sell a craft", Theme.FontSm).Bold();

            if (!HighLogic.LoadedSceneIsEditor)
            {
                UIF.Muted(card, "Open a craft in the VAB or SPH to put it up for sale.").Body();
                DrawStatus(card);
                return;
            }

            if (string.IsNullOrEmpty(craft.EditorCraft))
            {
                UIF.Muted(card, "No craft loaded. Start one, or load a saved one.").Body();
                DrawStatus(card);
                return;
            }

            UIF.Label(card, craft.EditorCraft, Theme.FontSm).Ellipsis();

            var facts = UIF.Box(card, "Facts").Row(Theme.Space1).H(22);
            UIF.Badge(facts, craft.EditorType, Theme.MutedForeground, Theme.Secondary);
            UIF.Badge(facts, craft.EditorParts + " parts", Theme.MutedForeground, Theme.Secondary);
            UIF.Badge(facts, mass.ToString("F1") + " t", Theme.MutedForeground, Theme.Secondary);
            UIF.Badge(facts, "√" + cost.ToString("N0"), Theme.MutedForeground, Theme.Secondary);
            UIF.Grow(facts);

            // What the listing will advertise about life support. Buyers filter on it,
            // and it is read from the ship rather than typed, so showing it here is the
            // only chance to notice that a "week-long station" scans as stock.
            if (ls.HasLifeSupport)
            {
                UIF.Muted(card, "Life support: " + LsFreeze.DisplayNameFor(ls.ModKey) +
                                (ls.EnduranceDaysPerKerbal > 0
                                    ? ", " + ls.EnduranceDaysPerKerbal.ToString("F0") + " days per kerbal"
                                    : "")).Body();
            }

            if (!craft.EditorSaved)
            {
                UIF.Muted(card, "Save '" + craft.EditorCraft +
                                "' in KSP first: a listing uploads the saved file.").Body();
                DrawStatus(card);
                return;
            }

            UIF.Muted(card, "PRICE (KCOINS)");
            var field = UIF.TextField(card, priceText, "100");
            field.OnChanged(s => priceText = s);
            field.Interactable(!Busy);

            UIF.Button(card, Busy ? "Listing…" : "List for sale", () =>
            {
                var done = BeginAction();
                mod.RunCoroutine(ToolActions.SellCurrentCraft(priceText, null, done));
            }, BtnStyle.Primary, 30).Interactable(!Busy);

            UIF.Muted(card, "The craft file, a rendered blueprint and its mod list go with the " +
                            "listing; your custom flags are baked in.").Body();

            DrawStatus(card);
        }

        // ── Browse ──────────────────────────────────────────────────────────

        private static void BuildBrowse(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Browse").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Browse and buy", Theme.FontSm).Bold();
            UIF.Muted(card, "Browsing, filtering and buying crafts, and managing what you have " +
                            "listed, are on the website. Bought crafts arrive in KSP at the " +
                            "Space Center.").Body();
            UIF.Button(card, "Open the marketplace",
                       () => Application.OpenURL(mod.Api.MarketplaceUrl), BtnStyle.Secondary, 30);
        }

        // ── Plumbing ────────────────────────────────────────────────────────

        private void ReadCraft(bool force = false)
        {
            craft = ToolActions.ReadCraftState();
            nextCraftRead = Time.unscaledTime + CraftPollSeconds;

            // Only the parts that are on screen. Comparing whole states would redraw
            // for a part count that changes with every click in the VAB.
            string signature = craft.EditorCraft + "|" + craft.EditorType + "|" +
                               craft.EditorParts + "|" + craft.EditorSaved;
            if (!force && signature == craftSignature) return;

            craftSignature = signature;
            ReadValue();
            MarkDirty();
        }

        /// <summary>The heavy half of the read — see the note in the file header.</summary>
        private void ReadValue()
        {
            if (string.IsNullOrEmpty(craft.EditorCraft))
            {
                mass = 0f;
                cost = 0f;
                ls = LifeSupportInfo.None;
                return;
            }

            ToolActions.ReadEditorValue(out mass, out cost);
            ls = LifeSupportScan.FromEditor();
        }

        protected override void Poll()
        {
            if (Time.unscaledTime < nextCraftRead) return;
            ReadCraft();
        }

        internal override void OnShown()
        {
            ClearStatus();
            nextCraftRead = 0f;
            craftSignature = "";
            ReadCraft(force: true);
        }
    }
}
