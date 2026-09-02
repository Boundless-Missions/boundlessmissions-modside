/*
 * UI/Gui/Panels/MoreTimePanel.cs – Asking the issuer for a later deadline, in a
 * draggable window.
 *
 * This was an inline form on the dispute action bar: a pick-only DatePicker
 * squeezed onto a card that already carried Pay fine, Sue, Settle and a restore
 * button, with the send button underneath it. Two things were wrong with that and
 * only one of them was the width. A month grid opened *inside* a list row pushes
 * every control below it down the moment it expands, so the button the player is
 * aiming at moves while they read the calendar; and the ask was drawn without the
 * one number it is about — the deadline being moved — because the card had no room
 * to print it twice.
 *
 * So it is a window, the same class of surface as the submission screen and for a
 * related reason: it is a decision read *against* something else (there, the craft
 * on the stage; here, the current due date and how much extra time is actually
 * being asked for), and a form that owns its own rectangle can show both dates at
 * once and keep its Send button in one place.
 *
 * The grid is opened by MoreTimePanel rather than left behind DatePicker's "Pick"
 * disclosure: a window whose entire purpose is one date should not make the player
 * press a button to reveal the thing they opened it for. It still expands *in
 * place* inside this window — the reasoning in DatePicker's header about
 * RectMask2D is unchanged, this window just has the room the card did not.
 *
 * Nothing here decides anything about the request. The send is
 * ClientState.RequestDispute(cid, "more_time", date, …) — the same call the card
 * made — and the caller's own completion (ContractsPanel.Done) still runs, so the
 * inbox behaves exactly as it did: busy while in flight, back to the list on
 * success. What this file adds is where the player reads and presses it.
 */

using System;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class MoreTimePanel : WindowPanel
    {
        /// <summary>Also keys the remembered window position, so it must not carry the
        /// contract's name — see FloatWindow.Key.</summary>
        public override string Title => "Request more time";

        private readonly DatePicker picker = new DatePicker();

        // Which contract this window is currently about. Read at click time rather
        // than captured into the Send lambda: Open() can retarget the window a frame
        // before Rebuild replaces the buttons, and a stale capture would send B's
        // request under A's id.
        private string contractId;
        private string mission = "";
        private string issuer = "";
        private string currentDue = "";

        /// <summary>The current deadline, parsed, or null when the contract carries
        /// none (or one this client cannot read).</summary>
        private DateTime? dueDate;

        private DateTime proposed = DateTime.Now.Date.AddDays(7);

        /// <summary>Hands back the caller's completion at the moment Send is pressed —
        /// a factory rather than a callback because ContractsPanel.Done() marks the
        /// inbox busy when it is *created*, and creating it at Open would grey the
        /// card out for as long as the calendar is on screen.</summary>
        private Func<Action<bool, string>> begin;

        /// <summary>Told when this window goes away by any route, so the card that
        /// opened it can stop claiming a request is being written.</summary>
        private Action onClosed;

        /// <summary>Earliest date the request may name. "More time" that has already
        /// elapsed is meaningless and the API refuses it.</summary>
        private static DateTime Floor => DateTime.Now.Date.AddDays(1);

        /// <summary>
        /// Point the window at a contract and show it. Called through
        /// GeneKermanMod.OpenMoreTimeWindow, which is the one entry point.
        ///
        /// Re-opening on the contract it is already showing only brings it forward:
        /// re-seeding would throw away a date the player has already chosen, which is
        /// exactly what a "bring it back to the front" click must not do.
        /// </summary>
        internal void Open(string cid, string missionText, string issuerName, string due,
                           Func<Action<bool, string>> onSend, Action closed)
        {
            if (string.IsNullOrEmpty(cid)) return;

            bool sameContract = Window != null && Window.IsOpen && cid == contractId;

            // The tracker belongs to whoever opened it last, whether or not anything
            // else about the window changed.
            begin = onSend;
            onClosed = closed;

            if (sameContract)
            {
                Window.Show();   // clamps and raises; the panel keeps its date
                MarkDirty();
                return;
            }

            contractId = cid;
            mission = missionText ?? "";
            issuer = issuerName ?? "";
            currentDue = due ?? "";

            DateTime parsed;
            dueDate = DateTime.TryParse(currentDue, out parsed) ? (DateTime?)parsed.Date : null;

            // A week past whichever is later: the deadline being moved (so the default
            // is genuinely *more* time, not a date already behind the contract) or
            // today, for a deadline already in the past.
            DateTime seed = dueDate.HasValue && dueDate.Value > DateTime.Now.Date
                          ? dueDate.Value.AddDays(7)
                          : DateTime.Now.Date.AddDays(7);
            proposed = seed < Floor ? Floor : seed;

            // Opened rather than left behind the "Pick" disclosure — this window is
            // the date. On the month the seed falls in, not on today's.
            picker.Expand(proposed);

            Window?.Show();
            MarkDirty();
        }

        /// <summary>
        /// The window closed — its X, Escape, a finished send, or teardown. The card
        /// that opened it is told here and nowhere else, so the two can never disagree
        /// about whether a request is being written; and the contract is dropped, so a
        /// window reopened later cannot inherit the last one's target.
        /// </summary>
        internal override void OnWindowClosed()
        {
            var notify = onClosed;

            onClosed = null;
            begin = null;
            contractId = null;
            mission = "";
            issuer = "";
            currentDue = "";
            dueDate = null;
            picker.Close();

            if (notify != null) notify();
        }

        // ── Build ───────────────────────────────────────────────────────────

        protected override void Rebuild()
        {
            var col = UIF.Box(Host, "MoreTime").Column(Theme.Space2).Flex(1f, 1f);

            if (string.IsNullOrEmpty(contractId))
            {
                UIF.Notice(col, "No contract selected.", null);
                return;
            }

            El body;
            UIF.ScrollView(col, out body, "moretime").Flex(1f, 1f);

            BuildContext(body);
            BuildDates(body);

            UIF.Muted(body, "One ask per dispute; the issuer approves or refuses it. " +
                            "Nothing else about the contract changes: the payment, the fine " +
                            "and the work are all as they were.").Body();

            BuildFooter(col);
        }

        /// <summary>Which contract this is. The card that opened the window may well be
        /// behind it, or the sidebar closed entirely.</summary>
        private void BuildContext(El parent)
        {
            var card = UIF.Card(parent, "Contract").Column(Theme.Space1).Pad(Theme.Space3);

            var top = UIF.Box(card, "Top").Row(Theme.Space2).H(20);
            UIF.Badge(top, "Disputed", Theme.ContractStatus("disputed"));
            if (!string.IsNullOrEmpty(issuer))
                UIF.Label(top, "from " + issuer, Theme.FontXs, Theme.MutedForeground)
                   .Ellipsis().E.PrefW(0).Flex(1f);
            else UIF.Grow(top);

            UIF.Label(card, string.IsNullOrEmpty(mission) ? "(no description)" : mission,
                      Theme.FontSm).Body();
        }

        /// <summary>
        /// The two dates side by side, then the grid. Printing the current deadline
        /// next to the proposed one is the whole reason this is a window: the ask is
        /// "how much longer", and a single date answers that only if you already
        /// remember the other one.
        /// </summary>
        private void BuildDates(El parent)
        {
            var card = UIF.Card(parent, "Dates").Column(Theme.Space2).Pad(Theme.Space3);

            var row = UIF.Box(card, "Now").Row(Theme.Space2).MinH(20);
            UIF.Muted(row, "Current deadline");
            UIF.Grow(row);
            UIF.Label(row, string.IsNullOrEmpty(currentDue) ? "none set" : currentDue,
                      Theme.FontSm).Align(TextAlign.Right);

            UIF.Divider(card);

            // The picker writes straight into `proposed`; no MarkDirty, per DatePicker's
            // header — a rebuild would construct a new ScrollRect and throw the player
            // back to the top of this window mid-pick. The delta line below is the one
            // thing that has to follow the value, and it is redrawn from the pick.
            picker.Build(card, DatePicker.Print(proposed), Floor,
                         picked =>
                         {
                             proposed = DatePicker.Parse(picked, proposed);
                             MarkDirty();   // the footer button carries the date
                         },
                         null, "New deadline");

            string delta = DescribeDelta();
            if (!string.IsNullOrEmpty(delta))
            {
                bool backwards = dueDate.HasValue && proposed <= dueDate.Value;
                UIF.Label(card, delta, Theme.FontXs,
                          backwards ? Theme.Status("warning") : Theme.Primary).Body();
            }
        }

        /// <summary>How much extra time is actually being asked for — the number the
        /// card could not print, and the one the issuer is being asked to agree to.</summary>
        private string DescribeDelta()
        {
            if (!dueDate.HasValue)
                return "This contract has no deadline on record; the request sets one.";

            int days = (int)(proposed - dueDate.Value).TotalDays;

            if (days > 0) return days == 1 ? "One day later than the current deadline."
                                           : days + " days later than the current deadline.";
            if (days == 0) return "That is the current deadline, so the issuer would be agreeing to nothing.";
            return "That is earlier than the current deadline. Pick a later date.";
        }

        private void BuildFooter(El parent)
        {
            UIF.Divider(parent);
            DrawStatus(parent);

            var row = UIF.Box(parent, "Actions").Row(Theme.Space2).H(30);

            UIF.Button(row, "Request until " + DatePicker.Print(proposed), Send, BtnStyle.Primary, 30)
               .Interactable(!Busy && proposed >= Floor)
               .E.PrefW(0).Flex(1f);

            UIF.Button(row, "Cancel", CloseWindow, BtnStyle.Ghost, 30)
               .Interactable(!Busy)
               .E.W(78);
        }

        // ── Send ────────────────────────────────────────────────────────────

        private void Send()
        {
            var main = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.State : null;
            string cid = contractId;   // the live target, never a captured one

            if (main == null || string.IsNullOrEmpty(cid) || Busy) return;
            if (proposed < Floor) return;

            // The caller's completion is created here, not at Open: it is what marks
            // the inbox busy and returns it to the list on success.
            Action<bool, string> outer = begin != null ? begin() : null;
            Action<bool, string> inner = BeginAction();

            main.RequestDispute(cid, "more_time", DatePicker.Print(proposed), (ok, msg) =>
            {
                inner(ok, msg);
                if (outer != null) outer(ok, msg);
                // Only on success: a refusal ("already pending", a date the server
                // would not take) has to stay on screen next to the date that caused
                // it, or the player is left with a closed window and no reason.
                if (ok) CloseWindow();
            });
        }
    }
}
