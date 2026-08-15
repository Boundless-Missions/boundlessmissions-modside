/*
 * UI/Gui/DatePicker.cs – A month grid for the two dates the sidebar asks for.
 *
 * The browser UI gets this for free: <input type="date"> is a calendar in every
 * engine that renders it. In KSP there is no such thing, so both places that
 * wanted a date — the due date on a new contract, the new deadline on a "more
 * time" request — had grown their own ± stepper, which is fine for "a day or two
 * later" and hopeless for "the last Sunday of next month".
 *
 * One widget, both callers, for the reason PlayerPicker gives about itself: a
 * second copy is what drifts. The two differ in exactly one way, and it is a
 * parameter — the contract form also lets the date be typed (it always could),
 * while the dispute row is pick-only.
 *
 * The grid expands inline, below the row, rather than floating over the panel.
 * A popup would have to escape the panel's RectMask2D — which clips by rect, not
 * by hierarchy, so "draw it on top" means reparenting to the canvas root and
 * doing the anchoring arithmetic by hand — and then it would need a scrim to
 * catch the click that dismisses it. Expanding in place needs neither and cannot
 * end up half off-screen.
 *
 * Nothing here marks the panel dirty. This is the same rule PlayerPicker follows
 * and for a sharper reason: a panel rebuild constructs a new ScrollRect, which
 * starts at the top, so paging a month or picking a day would throw the player
 * back to the head of a long form. The widget owns two containers — the grid, and
 * the label or box showing the value — and updates only those. The panel finds
 * out the same way it already did: the callback writes the caller's draft, and
 * the Send button reads it at click time rather than at rebuild time.
 *
 * State (open, which month is shown) lives on this object rather than on the
 * GameObjects, because a panel rebuild destroys the hierarchy and would otherwise
 * close the calendar every time a notification arrived.
 */

using System;
using System.Globalization;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class DatePicker
    {
        /// <summary>What the server parses and what &lt;input type="date"&gt; emits.</summary>
        public const string Format = "yyyy-MM-dd";

        private const int CellHeight = 26;
        private const int Columns = 7;
        private const int Gap = 2;

        /// <summary>
        /// Monday first, as the rest of the community's week runs. Two letters
        /// because a 400px panel divided seven ways leaves ~50px a column.
        /// </summary>
        private static readonly string[] DayNames = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };

        private bool open;

        /// <summary>Any day inside the month on display.</summary>
        private DateTime view;

        // Live handles into the current hierarchy. All four are replaced by Build and
        // are stale — Unity-null — after a panel rebuild; see the guard in Refresh.
        private El gridHost;
        private Btn toggle;
        private Lbl valueLabel;
        private Fld field;

        // What Build was last called with. Kept so the widget can redraw itself
        // without asking the panel for anything.
        private string current = "";
        private DateTime min;
        private Action<string> onPick;

        internal bool IsOpen => open;

        /// <summary>Collapse the grid. Called when the thing being dated goes away.</summary>
        internal void Close() => open = false;

        /// <summary>
        /// A date out of whatever the caller is holding, falling back rather than
        /// failing: the contract form's box is free text, so it can legitimately be
        /// half-typed at the moment the calendar is opened.
        /// </summary>
        internal static DateTime Parse(string value, DateTime fallback)
        {
            DateTime d;
            string s = (value ?? "").Trim();

            if (DateTime.TryParseExact(s, Format, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out d)) return d.Date;

            // Anything else the player might type — "2026/09/01", "1 Sep 2026". The
            // picker writes it back in Format, so a stray form only survives until
            // the first pick.
            if (DateTime.TryParse(s, out d)) return d.Date;

            return fallback.Date;
        }

        /// <summary>Invariant, always: this string goes to the API, not to a reader.</summary>
        internal static string Print(DateTime d) => d.ToString(Format, CultureInfo.InvariantCulture);

        /// <summary>
        /// The row — optional caption, the value, and the toggle — plus the grid
        /// underneath it while it is open.
        /// </summary>
        /// <param name="min">Earliest selectable day. Days before it are drawn but dead.</param>
        /// <param name="onPick">A day was chosen; the value is in <see cref="Format"/>.</param>
        /// <param name="onTyped">
        /// Non-null makes the value an editable box, and is called per keystroke.
        /// Null renders it as a label — pick-only.
        /// </param>
        internal void Build(El parent, string value, DateTime min, Action<string> onPick,
                            Action<string> onTyped = null, string label = null)
        {
            this.min = min;
            this.onPick = onPick;
            current = string.IsNullOrEmpty(value) ? Print(min) : value;

            valueLabel = null;
            field = null;

            // Own wrapper column, so the grid lands directly under the row whatever
            // layout the caller happens to be building into.
            var wrap = UIF.Box(parent, "Date").Column(Theme.Space1);

            // MinH rather than H: this row can hold a label, and a column that cannot
            // fit its children squeezes them — see Lbl.Ellipsis.
            var row = UIF.Box(wrap, "DateRow").Row(Theme.Space2).MinH(30);
            if (!string.IsNullOrEmpty(label)) UIF.Muted(row, label);

            if (onTyped != null)
            {
                field = UIF.TextField(row, current, Format);
                field.E.PrefW(0).Flex(1f);
                // Two listeners' worth of work in one: the caller's draft, and this
                // widget's own idea of the value, which is what the calendar opens on.
                field.OnChanged(s => { current = s; onTyped(s); });
            }
            else
            {
                UIF.Grow(row);
                valueLabel = UIF.Label(row, Print(Parse(current, min)), Theme.FontSm).Align(TextAlign.Right);
                valueLabel.E.W(88);
            }

            // One style in both states. Swapping Ghost for Primary would mean
            // rebuilding the button's four state sprites to flip a caption, and the
            // caption already says which state it is in.
            toggle = UIF.Button(row, ToggleCaption(), Toggle, BtnStyle.Ghost, 30);
            toggle.E.W(52);

            // Empty until it is opened, and never removed: the grid is torn down and
            // rebuilt inside this container, so the widget has somewhere to draw into
            // that does not belong to the panel.
            gridHost = UIF.Box(wrap, "Grid").Column(Theme.Space1);
            Refresh();
        }

        private string ToggleCaption() => open ? "Done" : "Pick";

        private void Toggle()
        {
            open = !open;
            // Opening always lands on the month of the current value, so a calendar
            // left on March in one contract does not open on March in the next.
            if (open) view = FirstOfMonth(Parse(current, min));
            toggle?.Label.Set(ToggleCaption());
            Refresh();
        }

        /// <summary>
        /// Redraw the grid alone. The guard is not defensive coding: a panel rebuild
        /// destroys these GameObjects while this object survives, so a click that
        /// arrives in the same frame as a rebuild would otherwise write into a
        /// destroyed container.
        /// </summary>
        private void Refresh()
        {
            if (gridHost == null || gridHost.Go == null) return;

            gridHost.ClearChildren();
            if (open) BuildGrid(gridHost);
        }

        private void BuildGrid(El parent)
        {
            var card = UIF.Card(parent, "Calendar").Column(Theme.Space1).Pad(Theme.Space2);

            var head = UIF.Box(card, "Month").Row(Theme.Space1).MinH(24);
            UIF.Button(head, "<", () => Step(-1), BtnStyle.Ghost, 24, 0).E.W(26);
            UIF.Label(head, view.ToString("MMMM yyyy", CultureInfo.InvariantCulture), Theme.FontSm)
               .Bold().Align(TextAlign.Center).E.PrefW(0).Flex(1f);
            UIF.Button(head, ">", () => Step(1), BtnStyle.Ghost, 24, 0).E.W(26);

            var names = UIF.Box(card, "Weekdays").Row(Gap).MinH(16);
            foreach (string n in DayNames)
                UIF.Muted(names, n).Align(TextAlign.Center).E.PrefW(0).Flex(1f);

            DateTime selected = Parse(current, min);
            var first = FirstOfMonth(view);
            // DayOfWeek counts from Sunday; rotating by six moves it to Monday.
            int lead = ((int)first.DayOfWeek + 6) % 7;
            int days = DateTime.DaysInMonth(view.Year, view.Month);

            // Only the weeks the month actually occupies. A fixed six would leave a
            // blank strip under most months and make the panel jump as it is paged.
            int weeks = Mathf.CeilToInt((lead + days) / (float)Columns);
            var start = first.AddDays(-lead);

            for (int w = 0; w < weeks; w++)
            {
                var line = UIF.Box(card, "Week").Row(Gap).MinH(CellHeight);
                for (int c = 0; c < Columns; c++)
                    Cell(line, start.AddDays(w * Columns + c), selected);
            }
        }

        /// <summary>
        /// One day. Three shapes rather than one button with three colours: the
        /// selected day is a filled --primary button, a selectable day is a bare
        /// hover-highlight row (a Ghost button's outline on all 42 cells reads as a
        /// spreadsheet), and a day before the minimum is not a control at all — a
        /// hover on something that cannot be clicked is a promise the grid does not
        /// keep.
        /// </summary>
        private void Cell(El row, DateTime day, DateTime selected)
        {
            string text = day.Day.ToString(CultureInfo.InvariantCulture);
            bool inMonth = day.Month == view.Month && day.Year == view.Year;

            if (day < min.Date)
            {
                var dead = UIF.Box(row, "Day").Row(0).ChildAlign(TextAnchor.MiddleCenter)
                              .MinH(CellHeight).PrefW(0).Flex(1f);
                UIF.Label(dead, text, Theme.FontXs, Theme.Alpha(Theme.MutedForeground, 0.45f));
                return;
            }

            if (day == selected)
            {
                var b = UIF.Button(row, text, () => Pick(day), BtnStyle.Primary, CellHeight, 0);
                b.Label.Size(Theme.FontXs);
                b.E.PrefW(0).Flex(1f);
                return;
            }

            var cell = UIF.ClickableRow(row, () => Pick(day), false, Theme.RadiusSm)
                          .Row(0).ChildAlign(TextAnchor.MiddleCenter)
                          .MinH(CellHeight).PrefW(0).Flex(1f);

            // Today is accented rather than outlined, for the same reason the
            // unselected cells have no border.
            Color c = day == DateTime.Now.Date ? Theme.Primary
                    : inMonth ? Theme.Foreground
                    : Theme.MutedForeground;
            UIF.Label(cell, text, Theme.FontXs, c);
        }

        private void Step(int months)
        {
            view = view.AddMonths(months);
            Refresh();
        }

        private void Pick(DateTime day)
        {
            open = false;
            current = Print(day);

            // Straight into the live widgets rather than through a rebuild. Assigning
            // Fld.Text fires the field's own onValueChanged, which is what carries the
            // new date to the caller in the typed variant.
            if (field != null) field.Text = current;
            else valueLabel?.Set(current);

            onPick?.Invoke(current);

            toggle?.Label.Set(ToggleCaption());
            Refresh();
        }

        private static DateTime FirstOfMonth(DateTime d) => new DateTime(d.Year, d.Month, 1);
    }
}
