/*
 * UI/Gui/Panels/FinancePanel.cs – Where the money went, and sending some of it away.
 *
 * The wallet was a single number on the Profile tab, which answers "how much have
 * I got" and nothing else. Every question a player actually asks about it —
 * why did that drop, what does contracting earn me against what the marketplace
 * does, did the fine come out yet — needs the *history*, which is what
 * data/store.py's ledger records and /api/v1/finance serves.
 *
 * Three things share the screen, and the widening (SidebarPanel.WantsWide) is what
 * makes that work rather than making them take turns:
 *
 *   • The summary + graph + list are the panel's resting state, in one column.
 *   • "Where it goes" (the lifetime per-category breakdown) and "Send coins" each
 *     take the detail slot beside it, exactly as ContractsPanel's detail and create
 *     form do. Both are things you read *against* the history — a breakdown is
 *     meaningless without the list it summarises, and choosing what to send is
 *     done while looking at what you have — so replacing the list with either
 *     would be the wrong trade.
 *
 * The graph is a diverging bar chart: money in above the axis, money out below it,
 * one column per day. That shape is chosen for a reason beyond legibility — it puts
 * *position* on the in/out distinction, so the two series are told apart without
 * relying on the green/red pair, which is the one colour pairing a colourblind
 * player is least able to separate. (Theme's own pair happens to survive a
 * deuteranopia check comfortably, because its green is light and its red is dark;
 * the position encoding means the chart does not depend on that holding.)
 *
 * Nothing here recomputes a total. The server sends `totals` separately from
 * `entries` precisely because the ledger is a ring buffer — summing the visible
 * rows would produce a lifetime figure that silently shrinks as old entries roll
 * off the end — so this file renders what it is given and never adds anything up.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class FinancePanel : SidebarPanel
    {
        public override string Title => "Finance";

        /// <summary>History column width while a detail pane is open (ContractsPanel's).</summary>
        private const float ListWidth = 380f;

        /// <summary>Total height of the graph, split evenly above and below the axis.</summary>
        private const float GraphHeight = 96f;

        /// <summary>Shortest bar drawn for a non-zero day, so a small day is visible
        /// rather than rounding away to nothing and reading as a day with no activity.</summary>
        private const float MinBar = 2f;

        // ── Detail slot ─────────────────────────────────────────────────────
        //
        // Two possible occupants, at most one at a time. Kept as two bools rather
        // than an enum because each is toggled by its own header button and "both
        // off" is the resting state.
        private bool sending;
        private bool breakdown;

        // ── Send form ───────────────────────────────────────────────────────
        private readonly PlayerPicker picker = new PlayerPicker();
        private string amountText = "";
        private string noteText = "";
        private bool confirming;

        // ── List paging / filtering ─────────────────────────────────────────
        private int page;
        private string filter = "";

        private bool requested;
        private bool lastLoading;
        private bool lastHadData;
        private int lastBalance = -1;

        /// <summary>Widen while either detail pane is open — nothing to make room for otherwise.</summary>
        internal override bool WantsWide => sending || breakdown;

        // ─────────────────────────────────────────────────────────────────────

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.State;
            if (main == null) return;

            var data = main.FinanceData;
            Snapshot(main, data);

            var root = UIF.Box(Host, "Split").Row(Theme.Space3).Flex(1f, 1f);
            var col = UIF.Box(root, "History").Column(Theme.Space2).Flex(1f, 1f);
            bool wide = sending || breakdown;
            if (wide) col.PrefW(ListWidth).Flex(0f, 1f);

            BuildHeader(col, main, data != null);

            if (mod.Api == null || !mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                           "Link this install from the toolbar to see your wallet history.");
                return;
            }

            if (data == null)
            {
                // Same distinction ProfilePanel draws: the frame before Poll fires
                // its one on-demand fetch is not a failure, and calling it one makes
                // the panel flash "unavailable" every time it is opened.
                bool pending = main.FinanceLoading || !requested;
                UIF.Notice(col, pending ? "Loading your wallet…" : "History unavailable.",
                           pending ? null : "The server could not be reached. Try Refresh.");
                return;
            }

            string currency = MiniJSON.GetString(data, "currency_name", "KCoins");

            El body;
            UIF.ScrollView(col, out body, "finance").Flex(1f, 1f);

            BuildSummary(body, data, currency);
            BuildGraph(body, data, currency);
            BuildEntries(body, main, data, currency);

            if (breakdown) BuildBreakdown(root, data, currency);
            else if (sending) BuildSendForm(root, main, data, currency);
        }

        private void BuildHeader(El col, ClientState main, bool haveData)
        {
            // Title + Refresh only, exactly like UIF.PanelHeader. The two detail
            // toggles get their own row below, because a HorizontalLayoutGroup
            // squeezes children past their preferred width rather than wrapping —
            // so a title and three buttons in one 368px row do not shrink to fit,
            // they overlap, which is what shipped.
            var head = UIF.Box(col, "Head").Row(Theme.Space2).H(26);
            UIF.Label(head, "Finance", Theme.FontBase).Bold();
            UIF.Grow(head);
            // W is not optional for a button inside a Row. UIF.Button stretches its
            // caption over the button by anchors rather than parenting it as a layout
            // child, so the button contributes NO preferred width — in a Column that
            // is invisible (children force-expand), but in a Row it collapses to zero
            // and the caption draws outside the rect, over whatever is beside it.
            // Every UIF.Button in a Row in this file therefore carries a W or a Flex.
            UIF.Button(head, main.FinanceLoading ? "…" : "Refresh", () =>
            {
                main.RequestFinanceRefresh(page * ClientState.FinancePageSize, filter);
                MarkDirty();
            }, BtnStyle.Ghost, 26).E.W(72);

            if (!haveData) return;

            // Two equal shares of the width, so each caption gets ~180px and neither
            // is truncated. Each toggle closes the other: there is one detail slot,
            // and leaving both set would silently drop whichever lost the race in
            // Rebuild.
            var tabs = UIF.Box(col, "DetailTabs").Row(Theme.Space2).H(26);
            UIF.Button(tabs, breakdown ? "Hide totals" : "Where it goes", () =>
            {
                breakdown = !breakdown;
                if (breakdown) { sending = false; confirming = false; }
                MarkDirty();
            }, breakdown ? BtnStyle.Secondary : BtnStyle.Ghost, 26).E.Flex(1f);

            UIF.Button(tabs, sending ? "Close" : "Send coins", () =>
            {
                sending = !sending;
                if (sending) { breakdown = false; picker.EnsureLoaded(); }
                else ResetSendForm();
                MarkDirty();
            }, sending ? BtnStyle.Secondary : BtnStyle.Primary, 26).E.Flex(1f);
        }

        // ── Summary ─────────────────────────────────────────────────────────

        private void BuildSummary(El body, Dictionary<string, object> data, string currency)
        {
            var card = UIF.Card(body, "Balance").Column(Theme.Space2).Pad(Theme.Space3);

            UIF.Label(card, currency.ToUpperInvariant(), Theme.FontXs, Theme.MutedForeground);
            UIF.Label(card, MiniJSON.GetInt(data, "balance").ToString("N0"),
                      Theme.FontXl, Theme.Primary).Bold();

            BuildEscrow(card, data);

            // Lifetime in and out. Labelled with the arrow as well as the colour, so
            // the pair reads without it — the same rule the graph follows.
            var totals = UIF.Box(card, "Totals").Row(Theme.Space4).MinH(34);
            Money(totals, "Earned", MiniJSON.GetInt(data, "total_in"), Theme.Primary);
            Money(totals, "Spent", MiniJSON.GetInt(data, "total_out"), Theme.Destructive);
            UIF.Grow(totals);

            // A balance with nothing behind it is the normal state for anyone whose
            // wallet predates the ledger, and left unexplained it reads as the tab
            // being broken — the screenshot that prompted this said "Earned 0,
            // Spent 0" beside 295 coins. Said once, and only while it is true.
            if (MiniJSON.GetInt(data, "total_in") == 0 &&
                MiniJSON.GetInt(data, "total_out") == 0 &&
                MiniJSON.GetInt(data, "balance") > 0)
            {
                UIF.Muted(card, "Your balance is from before this tab existed. "
                                + "Tracking starts now, so earnings and spending "
                                + "will fill in from here.", Theme.FontXs).Body();
            }

            int debt = MiniJSON.GetInt(data, "debt");
            if (debt > 0)
            {
                int pct = MiniJSON.GetInt(data, "debt_garnish_percent");
                UIF.Notice(card, "Unpaid fines: " + debt.ToString("N0") + " " + currency,
                           pct > 0
                               ? pct + "% of what you earn goes towards them until they are "
                                 + "paid off. Those deductions appear below as debt repayments."
                               : "Repaid out of a share of what you earn.");
            }
        }

        /// <summary>
        /// What the player has issued but not yet settled — the answer to "my balance
        /// dropped and I did not spend anything".
        ///
        /// Escrow is neither earned nor spent, so it is deliberately *not* drawn in
        /// the vocabulary of the two below it: no swatch, and a colour that is not
        /// the graph's green or red. Those two mean "into the wallet" and "out of
        /// the wallet" everywhere on this screen, and money that is coming back —
        /// or going to somebody who has not delivered yet — is neither.
        ///
        /// Shown only while there is some, like the debt notice underneath. A
        /// permanent "In escrow 0" is a line of furniture on a dense card, and the
        /// player who needs the explanation is by definition the one who has just
        /// issued something.
        /// </summary>
        private static void BuildEscrow(El card, Dictionary<string, object> data)
        {
            int escrow = MiniJSON.GetInt(data, "escrow");
            if (escrow <= 0) return;

            int contracts = MiniJSON.GetInt(data, "escrow_contracts");
            int auctions = MiniJSON.GetInt(data, "escrow_auctions");

            var row = UIF.Box(card, "Escrow").Row(Theme.Space2).MinH(18);
            UIF.Label(row, "In escrow", Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(row);
            UIF.Label(row, escrow.ToString("N0"), Theme.FontSm, Theme.Status("info"))
               .Bold().Align(TextAlign.Right);

            // Says what is holding it as well as how much, because "where did my
            // money go" is answered by the count and not by the number.
            UIF.Muted(card, EscrowSentence(contracts, auctions), Theme.FontXs).Body();
        }

        /// <summary>
        /// "Locked in 2 contracts you issued. …" — built rather than formatted so the
        /// singular reads properly and neither half appears when it is empty.
        /// </summary>
        private static string EscrowSentence(int contracts, int auctions)
        {
            string what;
            if (contracts > 0 && auctions > 0)
                what = Count(contracts, "contract") + " you issued and "
                     + Count(auctions, "auction") + " still open";
            else if (auctions > 0)
                what = Count(auctions, "auction") + " still open";
            else if (contracts > 0)
                what = Count(contracts, "contract") + " you issued";
            else
                // The total is non-zero but neither count is: an older server that
                // sends `escrow` without the breakdown. Say the true half.
                what = "work you have issued";

            return "Locked in " + what + ". It comes back if they are cancelled, or "
                 + "goes to the contractor when the work is delivered.";
        }

        private static string Count(int n, string noun)
            => n + " " + noun + (n == 1 ? "" : "s");

        /// <summary>
        /// One "Earned / 1,240" pair, marked by the same drawn swatch the graph's
        /// legend uses.
        ///
        /// A swatch rather than an arrow glyph, and the reason generalises: the TMP
        /// font is *borrowed from KSP*, so its coverage is not ours to assume — ▲ and
        /// ▼ are not in it and rendered as empty boxes. Only ° — … ● Δ have shown
        /// themselves across the other panels. A drawn sprite cannot fail that way,
        /// and reusing the legend's mark makes the summary and the chart agree about
        /// what green and red mean.
        /// </summary>
        private static void Money(El row, string label, int value, Color color)
        {
            var box = UIF.Box(row, label).Column(0f);
            var key = UIF.Box(box, "Key").Row(4f).MinH(14);
            UIF.Box(key, "Sw").Size(8f, 8f).Bg(color, 2);
            UIF.Label(key, label, Theme.FontXs, Theme.MutedForeground);
            UIF.Label(box, value.ToString("N0"), Theme.FontSm, color).Bold();
        }

        /// <summary>A number preceded by a colour swatch — see Money on why not a glyph.</summary>
        private static void Swatched(El row, string text, Color color)
        {
            var box = UIF.Box(row, "N").Row(4f).MinH(16);
            UIF.Box(box, "Sw").Size(8f, 8f).Bg(color, 2);
            UIF.Label(box, text, Theme.FontXs, color);
        }

        // ── Graph ───────────────────────────────────────────────────────────

        private void BuildGraph(El body, Dictionary<string, object> data, string currency)
        {
            var series = MiniJSON.GetList(data, "series");
            if (series == null || series.Count == 0) return;

            var card = UIF.Card(body, "Graph").Column(Theme.Space2).Pad(Theme.Space3);

            var head = UIF.Box(card, "GraphHead").Row(Theme.Space2).H(18);
            UIF.Label(head, "Last " + series.Count + " days", Theme.FontSm).Bold();
            UIF.Grow(head);
            // The legend is always present: two series, so identity must never rest
            // on colour alone. The swatches repeat the axis convention in words.
            Legend(head, "in", Theme.Primary);
            Legend(head, "out", Theme.Destructive);

            // One scale for both halves. Scaling in and out independently would draw
            // a day that earned 10 and spent 1000 as two bars of the same length —
            // the single most misleading thing this chart could do.
            int peak = 0;
            for (int i = 0; i < series.Count; i++)
            {
                var d = series[i] as Dictionary<string, object>;
                if (d == null) continue;
                peak = Mathf.Max(peak, Mathf.Max(MiniJSON.GetInt(d, "incoming"),
                                                 MiniJSON.GetInt(d, "outgoing")));
            }

            if (peak <= 0)
            {
                UIF.Muted(card, "No money has moved in this period.", Theme.FontXs).Body();
                return;
            }

            float half = (GraphHeight - 1f) / 2f;
            var plot = UIF.Box(card, "Plot").Row(2f).H(GraphHeight);

            for (int i = 0; i < series.Count; i++)
            {
                var d = series[i] as Dictionary<string, object>;
                int inc = d == null ? 0 : MiniJSON.GetInt(d, "incoming");
                int outg = d == null ? 0 : MiniJSON.GetInt(d, "outgoing");

                // Equal share of whatever width the panel has: the graph has to look
                // right at both the narrow and the widened width, and a fixed bar
                // width would either overflow one or leave a gap in the other.
                var day = UIF.Box(plot, "D" + i).Column(0f).Flex(1f, 0f);

                var up = UIF.Box(day, "In").Column(0f).H(half)
                            .ChildAlign(TextAnchor.LowerCenter);
                if (inc > 0)
                    UIF.Box(up, "Bar").H(BarHeight(inc, peak, half)).Bg(Theme.Primary, 2);

                // The axis runs the full width even on an empty day, so a quiet day
                // reads as "nothing happened" rather than as a gap in the data.
                UIF.Box(day, "Axis").H(1f).Fill(Theme.Border);

                var dn = UIF.Box(day, "Out").Column(0f).H(half)
                            .ChildAlign(TextAnchor.UpperCenter);
                if (outg > 0)
                    UIF.Box(dn, "Bar").H(BarHeight(outg, peak, half)).Bg(Theme.Destructive, 2);
            }

            // Endpoints only. A label under all fourteen columns does not fit at the
            // narrow width and is not what the axis is for — the list underneath is
            // where an exact date is read.
            var axis = UIF.Box(card, "Axis").Row(Theme.Space2).H(14);
            UIF.Label(axis, DayLabel(series[0]), Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(axis);
            UIF.Label(axis, "peak " + peak.ToString("N0"), Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(axis);
            UIF.Label(axis, DayLabel(series[series.Count - 1]), Theme.FontXs,
                      Theme.MutedForeground).Align(TextAlign.Right);
        }

        private static void Legend(El row, string label, Color color)
        {
            var box = UIF.Box(row, "Key" + label).Row(4f).H(14);
            UIF.Box(box, "Sw").Size(8f, 8f).Bg(color, 2);
            UIF.Label(box, label, Theme.FontXs, Theme.MutedForeground);
        }

        private static float BarHeight(int value, int peak, float half)
            => Mathf.Max(MinBar, Mathf.Round(half * value / (float)peak));

        private static string DayLabel(object entry)
        {
            var d = entry as Dictionary<string, object>;
            string day = d == null ? "" : MiniJSON.GetString(d, "day");
            // "2026-08-29" → "08-29": the year is the same for every bar on screen.
            return day.Length == 10 ? day.Substring(5) : day;
        }

        // ── The list ────────────────────────────────────────────────────────

        private void BuildEntries(El body, ClientState main,
                                  Dictionary<string, object> data, string currency)
        {
            var entries = MiniJSON.GetList(data, "entries");
            int count = MiniJSON.GetInt(data, "entry_count");
            int capacity = MiniJSON.GetInt(data, "ledger_capacity");

            var card = UIF.Card(body, "Entries").Column(Theme.Space2).Pad(Theme.Space3);

            var head = UIF.Box(card, "EntriesHead").Row(Theme.Space2).H(20);
            UIF.Label(head, string.IsNullOrEmpty(filter) ? "Recent activity" : "Filtered",
                      Theme.FontSm).Bold();
            UIF.Grow(head);
            if (!string.IsNullOrEmpty(filter))
            {
                UIF.Button(head, "Clear filter", () =>
                {
                    filter = "";
                    page = 0;
                    main.RequestFinanceRefresh(0, "");
                    MarkDirty();
                }, BtnStyle.Ghost, 20).E.W(84);
            }

            if (entries == null || entries.Count == 0)
            {
                UIF.Muted(card, string.IsNullOrEmpty(filter)
                              ? "Nothing here yet. Contracts, sales and rewards all show up here."
                              : "No movements in this category.", Theme.FontXs).Body();
                return;
            }

            foreach (var o in entries)
            {
                var e = o as Dictionary<string, object>;
                if (e == null) continue;
                BuildEntryRow(card, e, currency);
            }

            BuildPager(card, main, entries.Count, count, capacity);
        }

        private void BuildEntryRow(El card, Dictionary<string, object> e, string currency)
        {
            int amount = MiniJSON.GetInt(e, "amount");
            bool incoming = amount > 0;

            // MinH, not H: this row holds Ellipsis labels, and a fixed height squeezes
            // a child whose preferred height does not fit — at which point TMP's
            // Ellipsis renders nothing at all rather than clipping (see UIF.Ellipsis).
            var row = UIF.Box(card, "Tx").Row(Theme.Space2).MinH(38);

            // The sign is spelled out as well as coloured, for the same reason the
            // graph puts income above the axis: this must not be colour-only.
            var text = UIF.Box(row, "Text").Column(0f).Flex(1f, 0f);
            string label = MiniJSON.GetString(e, "category_label");
            if (string.IsNullOrEmpty(label)) label = MiniJSON.GetString(e, "category", "Other");
            UIF.Label(text, label, Theme.FontSm).Ellipsis();

            // The other party leads, then whatever the movement was about: for a
            // transfer that is "Bob — thanks for the lift", for a contract
            // "Bob — Deliver a rover to the Mun". The name is never part of the
            // stored detail (see the note in api_server.finance_send), so this is
            // the only place the two are joined.
            string detail = MiniJSON.GetString(e, "detail");
            string who = MiniJSON.GetString(e, "counterparty_name");
            string sub = string.IsNullOrEmpty(who) ? detail
                       : string.IsNullOrEmpty(detail) ? who
                       : who + ": " + detail;
            if (!string.IsNullOrEmpty(sub))
                UIF.Muted(text, sub, Theme.FontXs).Ellipsis();

            var right = UIF.Box(row, "Right").Column(0f);
            UIF.Label(right, (incoming ? "+" : "-") + Mathf.Abs(amount).ToString("N0"),
                      Theme.FontSm, incoming ? Theme.Primary : Theme.Destructive)
               .Bold().Align(TextAlign.Right);
            UIF.Muted(right, Ago(MiniJSON.GetDouble(e, "ts")), Theme.FontXs)
               .Align(TextAlign.Right);
        }

        private void BuildPager(El card, ClientState main, int shown, int count, int capacity)
        {
            int size = ClientState.FinancePageSize;
            bool hasPrev = page > 0;
            bool hasNext = (page * size) + shown < count;
            if (!hasPrev && !hasNext)
            {
                if (count >= capacity && capacity > 0)
                    UIF.Muted(card, "Showing the last " + capacity.ToString("N0")
                                    + " movements. Older ones are counted in the totals "
                                    + "but no longer listed individually.", Theme.FontXs)
                       .Body();
                return;
            }

            var nav = UIF.Box(card, "Pager").Row(Theme.Space2).H(26);
            if (hasPrev)
                UIF.Button(nav, "Newer", () => GoTo(main, page - 1), BtnStyle.Ghost, 26).E.W(64);
            UIF.Grow(nav);
            UIF.Label(nav, ((page * size) + 1) + "-" + ((page * size) + shown)
                           + " of " + count, Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(nav);
            if (hasNext)
                UIF.Button(nav, "Older", () => GoTo(main, page + 1), BtnStyle.Ghost, 26).E.W(64);
        }

        private void GoTo(ClientState main, int target)
        {
            page = Mathf.Max(0, target);
            main.RequestFinanceRefresh(page * ClientState.FinancePageSize, filter);
            MarkDirty();
        }

        private static string Ago(double ts)
        {
            if (ts <= 0) return "";
            // The server sends a unix timestamp; the client's clock is the only one
            // available to compare it against, so a skewed clock skews this label —
            // acceptable for "2h ago", which is why no exact time is printed here.
            var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts);
            var span = DateTime.UtcNow - when;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + "m ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + "h ago";
            if (span.TotalDays < 30) return (int)span.TotalDays + "d ago";
            return when.ToString("yyyy-MM-dd");
        }

        // ── Breakdown (detail slot) ─────────────────────────────────────────

        private void BuildBreakdown(El root, Dictionary<string, object> data, string currency)
        {
            var pane = UIF.Box(root, "Breakdown").Column(Theme.Space2).Flex(1f, 1f);

            var head = UIF.Box(pane, "BreakHead").Row(Theme.Space2).H(26);
            UIF.Label(head, "Where it goes", Theme.FontBase).Bold();
            UIF.Grow(head);
            UIF.Button(head, "Close", () => { breakdown = false; MarkDirty(); },
                       BtnStyle.Ghost, 26).E.W(60);

            UIF.Muted(pane, "Lifetime totals, across your whole history, not just the "
                            + "movements listed on the left.", Theme.FontXs).Body();

            var totals = MiniJSON.GetList(data, "totals");
            if (totals == null || totals.Count == 0)
            {
                UIF.Notice(pane, "Nothing to break down yet.");
                return;
            }

            // One shared scale, so the bars compare against each other rather than
            // each filling its own row.
            int peak = 0;
            foreach (var o in totals)
            {
                var t = o as Dictionary<string, object>;
                if (t == null) continue;
                peak = Mathf.Max(peak, MiniJSON.GetInt(t, "incoming") + MiniJSON.GetInt(t, "outgoing"));
            }

            El content;
            UIF.ScrollView(pane, out content, "breakdown").Flex(1f, 1f);

            foreach (var o in totals)
            {
                var t = o as Dictionary<string, object>;
                if (t == null) continue;
                BuildBreakdownRow(content, t, peak, currency);
            }
        }

        private void BuildBreakdownRow(El content, Dictionary<string, object> t,
                                       int peak, string currency)
        {
            string cat = MiniJSON.GetString(t, "category");
            string label = MiniJSON.GetString(t, "label");
            if (string.IsNullOrEmpty(label)) label = cat;
            int inc = MiniJSON.GetInt(t, "incoming");
            int outg = MiniJSON.GetInt(t, "outgoing");

            var card = UIF.Card(content, "B_" + cat).Column(Theme.Space1).Pad(Theme.Space3);

            var head = UIF.Box(card, "Head").Row(Theme.Space2).MinH(20);   // Ellipsis: see above
            UIF.Label(head, label, Theme.FontSm).Bold().Ellipsis();
            UIF.Grow(head);
            UIF.Muted(head, MiniJSON.GetInt(t, "count") + "x", Theme.FontXs);

            // A stacked bar: earned then spent, with a 2px gap so the two segments
            // read as two rather than as one bar changing colour mid-way.
            if (peak > 0 && (inc > 0 || outg > 0))
            {
                // Widths are shares of the row: each segment takes its own fraction
                // of the largest category, and a spacer soaks up the remainder so a
                // small category's bar stays short instead of filling the width.
                var bar = UIF.Box(card, "Bar").Row(2f).H(6);
                if (inc > 0)
                    UIF.Box(bar, "In").Flex(inc / (float)peak, 0f).H(6).Bg(Theme.Primary, 2);
                if (outg > 0)
                    UIF.Box(bar, "Out").Flex(outg / (float)peak, 0f).H(6).Bg(Theme.Destructive, 2);
                UIF.Box(bar, "Rest").Flex(Mathf.Max(0f, 1f - (inc + outg) / (float)peak), 0f).H(6);
            }

            var nums = UIF.Box(card, "Nums").Row(Theme.Space3).MinH(18);
            if (inc > 0) Swatched(nums, inc.ToString("N0"), Theme.Primary);
            if (outg > 0) Swatched(nums, outg.ToString("N0"), Theme.Destructive);
            UIF.Grow(nums);

            // Filtering the list is the point of the breakdown: it answers "how much"
            // and this is the way through to "on what".
            bool active = filter == cat;
            UIF.Button(nums, active ? "Filtering" : "Show these", () =>
            {
                filter = active ? "" : cat;
                page = 0;
                GeneKermanMod.Instance?.State?.RequestFinanceRefresh(0, filter);
                MarkDirty();
            }, active ? BtnStyle.Secondary : BtnStyle.Ghost, 20).E.W(92);
        }

        // ── Send form (detail slot) ─────────────────────────────────────────

        private void BuildSendForm(El root, ClientState main,
                                   Dictionary<string, object> data, string currency)
        {
            var pane = UIF.Box(root, "Send").Column(Theme.Space2).Flex(1f, 1f);

            var head = UIF.Box(pane, "SendHead").Row(Theme.Space2).H(26);
            UIF.Label(head, "Send coins", Theme.FontBase).Bold();
            UIF.Grow(head);
            UIF.Button(head, "Close", () => { sending = false; ResetSendForm(); MarkDirty(); },
                       BtnStyle.Ghost, 26).E.W(60);

            El content;
            UIF.ScrollView(pane, out content, "sendform").Flex(1f, 1f);

            int balance = MiniJSON.GetInt(data, "balance");
            int minimum = Mathf.Max(1, MiniJSON.GetInt(data, "min_transfer", 1));

            var card = UIF.Card(content, "SendCard").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Muted(card, "You have " + balance.ToString("N0") + " " + currency + ".",
                      Theme.FontXs).Body();

            picker.Build(card, "No other players found to send to.");

            // Updated in place as the player types rather than by rebuilding: a
            // rebuild destroys the field being typed into (SidebarPanel.Tick defers
            // one for exactly that reason), so a form that only re-evaluated on
            // rebuild would leave its button dead and its hint stale for the whole
            // time it takes to type an amount. Same pattern as ToolsPanel.
            Btn review = null;
            Lbl hint = null;
            Action rearm = () =>
            {
                if (review != null && review.Button != null)
                    review.Interactable(SendReady(balance, minimum));
                // Lbl.Set goes through a Unity null check, so this is safe on a build
                // that has already been torn down under a late callback.
                if (hint != null) hint.Set(SendHint(balance, minimum));
            };

            var amountField = UIF.TextField(card, amountText, "Amount");
            amountField.OnChanged(s =>
            {
                amountText = s;
                // An edit invalidates a primed confirm — the number it named is no
                // longer the number in the box. That one does need a rebuild, since
                // the confirm is a different set of controls.
                if (confirming) { confirming = false; MarkDirty(); return; }
                rearm();
            });

            var noteField = UIF.TextField(card, noteText, "Note (optional)");
            noteField.OnChanged(s => noteText = s);

            DrawStatus(card);

            int amount;
            bool parsed = int.TryParse((amountText ?? "").Trim(), out amount);
            bool ready = SendReady(balance, minimum);

            // Says which condition is unmet rather than leaving a dead button: the
            // four reasons are not interchangeable and only one of them is fixed by
            // typing a different number.
            hint = UIF.Muted(card, SendHint(balance, minimum), Theme.FontXs);
            hint.Body();

            if (!confirming)
            {
                review = UIF.Button(card, Busy ? "Sending…" : "Review transfer", () =>
                {
                    if (!SendReady(balance, minimum)) return;
                    confirming = true;
                    MarkDirty();
                }, BtnStyle.Primary, 30);
                review.Interactable(ready);
                return;
            }

            // A transfer cannot be undone by either party, so it gets the same
            // two-click confirm as logging out everywhere — and the confirm restates
            // the amount and the name, because those are exactly what a misclick
            // gets wrong.
            UIF.Notice(card, "Send " + amount.ToString("N0") + " " + currency
                             + " to " + picker.SelectedName + "?",
                       "This cannot be undone. If they owe unpaid fines, part of it "
                       + "goes to those and they receive the rest.");

            var confirmRow = UIF.Box(card, "Confirm").Row(Theme.Space2).H(30);
            UIF.Button(confirmRow, "Send", () =>
            {
                if (!ready) return;
                confirming = false;
                var done = BeginAction();
                main.RequestSendMoney(picker.SelectedId, amount, noteText, (ok, message) =>
                {
                    if (ok)
                    {
                        // Clear the form but stay on it: sending twice in a row is
                        // normal, and re-opening the pane to do it is friction. The
                        // recipient stays selected for the same reason.
                        amountText = "";
                        noteText = "";
                        page = 0;
                    }
                    done(ok, message);
                });
            }, BtnStyle.Primary, 30).E.Flex(1f);
            UIF.Button(confirmRow, "Cancel", () => { confirming = false; MarkDirty(); },
                       BtnStyle.Ghost, 30).E.Flex(1f);
        }

        /// <summary>The amount as typed, or -1 when the box does not hold a number.</summary>
        private int TypedAmount()
        {
            int amount;
            return int.TryParse((amountText ?? "").Trim(), out amount) ? amount : -1;
        }

        private bool SendReady(int balance, int minimum)
        {
            int amount = TypedAmount();
            return picker.HasSelection && amount >= minimum && amount <= balance && !Busy;
        }

        /// <summary>
        /// Why the transfer cannot be sent yet, or "" when it can. One sentence,
        /// naming the single condition that is unmet — a form that says only
        /// "invalid" leaves the player guessing which of four things to change.
        /// </summary>
        private string SendHint(int balance, int minimum)
        {
            if (Busy) return "";
            if (!picker.HasSelection) return "Choose who to send to.";
            int amount = TypedAmount();
            if (amount <= 0) return "Enter an amount.";
            if (amount < minimum) return "The smallest transfer is " + minimum.ToString("N0") + ".";
            if (amount > balance) return "You only have " + balance.ToString("N0") + ".";
            return "";
        }

        private void ResetSendForm()
        {
            confirming = false;
            amountText = "";
            noteText = "";
            picker.ClearSelection();
            ClearStatus();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void Snapshot(ClientState main, Dictionary<string, object> data)
        {
            lastLoading = main.FinanceLoading;
            lastHadData = data != null;
            lastBalance = data == null ? -1 : MiniJSON.GetInt(data, "balance");
        }

        protected override void Poll()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.State;
            if (main == null) return;

            // One automatic fetch per time the panel is opened — *every* time, not
            // only the first. The blob lives on ClientState and survives the panel
            // being torn down, so a `FinanceData == null` guard here (which is what
            // shipped) meant the tab was fetched once per KSP session and never
            // again: issue a contract, open Finance, and the escrow that had just
            // left the wallet was missing from both the balance and the list,
            // because the screen was still showing the blob from before it. Nothing
            // else refreshes it — RefreshAll deliberately leaves Finance out, since
            // a player who never opens the tab should not pay for its history.
            //
            // `requested` is what keeps this to one fetch rather than one per frame:
            // a failed fetch leaves FinanceData at whatever it was, so the condition
            // cannot be "have we got data" in either direction.
            if (!requested && !main.FinanceLoading &&
                mod.Api != null && mod.Api.IsLinked)
            {
                requested = true;
                main.RequestFinanceRefresh(page * ClientState.FinancePageSize, filter);
                return;
            }

            if (sending)
            {
                picker.EnsureLoaded();
                picker.Tick();
            }

            var data = main.FinanceData;
            if (main.FinanceLoading != lastLoading ||
                (data != null) != lastHadData ||
                (data != null && MiniJSON.GetInt(data, "balance") != lastBalance))
            {
                MarkDirty();
            }
        }

        internal override void OnShown()
        {
            requested = false;
            // Back to the top. The fetch Poll is about to make is by offset, and
            // whatever has happened since the tab was last open has pushed the old
            // page 3 somewhere else — re-opening onto a stale offset would show a
            // slice of history nobody asked for. The category filter is kept: that
            // one was a deliberate choice and the header says it is on.
            page = 0;
            picker.Attach(MarkDirty);
            picker.Reset();
            // A confirm left primed must not survive being tabbed away from, and a
            // detail pane left open would widen the sidebar the moment this panel is
            // selected, before the player has asked for anything.
            sending = false;
            breakdown = false;
            ResetSendForm();
        }

        internal override void OnHidden() => picker.Dispose();

        internal override void OnSceneChanged() => picker.Dispose();
    }
}
