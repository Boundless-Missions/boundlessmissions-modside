/*
 * UI/Gui/Panels/ContractsPanel.cs – The contract inbox, in uGUI.
 *
 * Visual spec: WebUI/src/screens/Contracts.tsx. Data: MainWindow's cached
 * `contracts`, filled by RefreshContracts() — which also uploads the part
 * catalogue, so this panel deliberately does not fetch by any other route.
 *
 * Master-detail: the list on the left, and — once a row is clicked — the detail
 * beside it, with the sidebar widening to make room (SidebarPanel.WantsWide). The
 * whole row is the button; there is no separate "Open" affordance. Both follow the
 * web UI, which goes two-column above `xl` for the same reason: replacing the list
 * with the detail wastes half the width and loses your place in the inbox.
 *
 * The detail's action bar mirrors
 * MainWindow.DrawContractDetail's mapping — status crossed with is_outgoing —
 * button for button. Nothing here reimplements an action: every one routes into
 * the same coroutine the classic window runs (see MainWindow's Request*
 * wrappers), because those bodies carry side effects a second copy would drop.
 *
 * Status colours come from Theme.ContractStatus, which matches the web UI's
 * STATUS_STYLE rather than MainWindow's older IMGUI palette — visual parity with
 * the site is the whole point of this surface. See the note on Theme.ContractStatus.
 *
 * The list carries the classic window's mail furniture as well: week groups that
 * fold (ContractInbox.WeekKey, shared so both front ends file a contract under the
 * same heading), a local bin, and a selection mode for acting on several at once.
 * The bin is *local* — nothing is told to the server, the other party still sees the
 * contract — which is why it only takes finished ones, and why it lives in
 * ContractInbox next to the file it is stored in.
 *
 * Deliberately not here: answering a settle / more-time request as the issuer — that
 * is browser-UI and Discord only today, and the panel says so rather than pretending
 * otherwise.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class ContractsPanel : SidebarPanel
    {
        public override string Title => "Contracts";

        /// <summary>List column width while the detail pane is open (the web UI's 26rem).</summary>
        private const float ListWidth = 380f;

        /// <summary>
        /// Display height of a submission image. Fixed rather than fitted to the
        /// column, because UIF.Picture derives width from height — see the note there
        /// on why AspectRatioFitter is not usable inside a layout group.
        /// </summary>
        private const float PreviewHeight = 190f;

        /// <summary>
        /// Widest a thumbnail may be. Nothing clips a layout child, so a wide
        /// multi-view sheet at PreviewHeight would run out past the panel's edge and
        /// over the game; where that would happen the height comes down instead.
        /// </summary>
        private const float MaxPreviewWidth = 400f;

        /// <summary>0 = All, 1 = Incoming, 2 = Outgoing — the same modes the IMGUI tab has.</summary>
        private int filterMode;

        /// <summary>Showing the bin instead of the inbox. The bin is local — see ContractInbox.</summary>
        private bool showTrash;

        /// <summary>
        /// Selection mode. Off by default and off again on the way out: a row is a
        /// button that opens a contract, and a check box permanently in front of it
        /// would make the common action the fiddly one.
        /// </summary>
        private bool selecting;
        private readonly HashSet<string> selected = new HashSet<string>();

        /// <summary>Bulk cancel is destructive, so it arms before it fires — as the per-contract one does.</summary>
        private bool bulkCancelConfirm;

        /// <summary>Week headings the player has folded away, plus which view they were computed for.</summary>
        private readonly HashSet<string> collapsedWeeks = new HashSet<string>();
        private string autoCollapsedFor;

        /// <summary>
        /// Contracts past which older weeks start folded. The classic window has the
        /// same rule written down (MainWindow.AutoCollapseWeeks) but never calls it;
        /// here it runs, because a sidebar column shows fewer rows than a 550px window
        /// and an inbox that opens on last spring's contracts is not an inbox.
        /// </summary>
        private const int AutoCollapseAfter = 10;

        private int lastCount = -1;
        private bool lastLoading;
        private bool requested;

        /// <summary>Which contract the editor's part filter is enforcing, as last drawn.</summary>
        private string lastEnforcedId = "";

        /// <summary>Contract shown in the detail view, or null for the list.</summary>
        private string openId;

        /// <summary>Two-click confirms, held by contract id so they cannot leak between rows.</summary>
        private string confirmActionId;
        private string rescueAcceptConfirmId;

        /// <summary>Proposed date for a "More Time" request on a human-issued contract.</summary>
        private DateTime moreTimeDate = DateTime.Now.Date.AddDays(7);

        private readonly DatePicker moreTimePicker = new DatePicker();

        // Submission images for the open contract. Keyed by id and fetched at most
        // once per contract, because Rebuild runs on every data change and a fetch
        // per rebuild would download the blueprints over and over. The preview owns
        // Texture2Ds, so it is disposed whenever the selection moves or the panel
        // goes away — see ClearPreview.
        private SubmissionPreview preview;
        private string previewId;
        private bool previewLoading;

        /// <summary>
        /// Statuses that can have something to preview. Mirrors `hasSubmission` in
        /// WebUI/src/screens/Contracts.tsx: a round trip here goes through KSP's main
        /// thread, so the ones that cannot have a submission must not pay for one.
        /// </summary>
        private static bool HasSubmission(string status) =>
            status == "submitted" || status == "completed" ||
            status == "disputed" || status == "mod_review";

        /// <summary>Master-detail: widen the sidebar while a contract is selected.</summary>
        /// <summary>
        /// The create form takes the detail slot, exactly as it does in the web UI, so
        /// it widens the panel for the same reason a selected contract does.
        /// </summary>
        private bool creating;

        private readonly ContractForm form = new ContractForm();

        internal override bool WantsWide => creating || !string.IsNullOrEmpty(openId);

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            var contracts = main.ContractList;
            lastCount = contracts?.Count ?? -1;
            lastLoading = main.ContractsLoading;

            Dictionary<string, object> open = string.IsNullOrEmpty(openId) ? null : main.FindContract(openId);
            // The contract left the list (cancelled, completed and swept) — drop back
            // to the list rather than showing a detail pane with nothing in it.
            if (open == null) openId = null;

            // One detail slot, two possible occupants. The form wins: it is where the
            // player is typing, and a contract arriving in the list must not yank it away.
            if (creating) open = null;

            // One row, two columns. With nothing selected the list is the only child
            // and takes the whole width; selecting one adds the detail beside it and
            // the controller widens the panel to make room.
            var root = UIF.Box(Host, "Split").Row(Theme.Space3).Flex(1f, 1f);

            var col = UIF.Box(root, "List").Column(Theme.Space2).Flex(1f, 1f);
            if (open != null || creating) col.PrefW(ListWidth).Flex(0f, 1f);

            // Built inline rather than through UIF.PanelHeader: this is the only panel
            // with a second header action, and "+ New" belongs beside Refresh rather
            // than in the filter row, which does not exist on an empty inbox.
            var head = UIF.Box(col, "Head").Row(Theme.Space2).H(26);
            UIF.Label(head, "Contracts", Theme.FontBase).Bold();
            UIF.Grow(head);

            bool linked = mod.Api != null && mod.Api.IsLinked;
            if (linked)
            {
                UIF.Button(head, creating ? "Close" : "+ New", ToggleCreate,
                           creating ? BtnStyle.Ghost : BtnStyle.Primary, 24, Theme.Space2)
                   .E.PrefW(58);
            }

            UIF.Button(head, "Refresh", () => { main.RequestContractsRefresh(); MarkDirty(); },
                       BtnStyle.Ghost, 24, Theme.Space2).E.PrefW(70);

            if (!linked)
            {
                UIF.Notice(col, "Not linked to a Discord account.", null);
                return;
            }

            // No early return once the header is up: an empty inbox is exactly when a
            // player wants the create form, and returning here would take it away.
            if (contracts == null || contracts.Count == 0)
            {
                // `!requested`: Poll has not yet fired its one on-demand fetch, so
                // this is the frame before loading starts, not an empty inbox.
                bool pending = main.ContractsLoading || !requested;
                UIF.Notice(col,
                       pending ? "Loading contracts…" : "No contracts.",
                       pending ? null : "Offers from other players and weekly work appear here.");
            }
            else
            {
                BuildList(col, contracts);
            }

            if (creating)
            {
                var pane = UIF.Box(root, "New").Column(Theme.Space2).Flex(1f, 1f);
                var formHead = UIF.Box(pane, "FormHead").Row(Theme.Space2).H(26);
                UIF.Label(formHead, "New contract", Theme.FontBase).Bold();
                UIF.Grow(formHead);
                UIF.Button(formHead, "X", ToggleCreate, BtnStyle.Ghost, 24).E.W(30);

                DrawStatus(pane);
                form.Build(pane, Busy);
            }
            else if (open != null)
            {
                var detail = UIF.Box(root, "Detail").Column(Theme.Space2).Flex(1f, 1f);
                BuildDetail(detail, mod, main, open);
            }
        }

        private void BuildList(El col, IList<object> contracts)
        {
            // Filter row. Purely local — it re-renders the cached list, it does not refetch.
            var filters = UIF.Box(col, "Filters").Row(Theme.Space1).H(24);
            FilterButton(filters, "All", 0);
            FilterButton(filters, "Incoming", 1);
            FilterButton(filters, "Outgoing", 2);

            var shown = Filtered(contracts);

            // "N waiting on you", from the same predicate that decides whether a row
            // gets its dot — so the count and the dots can never disagree. Never in the
            // bin: nothing in there is waiting on anybody.
            int waiting = 0;
            if (!showTrash) foreach (var c in shown) if (NeedsAction(c)) waiting++;
            UIF.Grow(filters);
            if (waiting > 0)
                UIF.Muted(filters, waiting + " waiting on you");

            BuildViewRow(col, contracts);
            if (selecting) BuildSelectionBar(col, shown);

            if (shown.Count == 0)
            {
                UIF.Notice(col, showTrash ? "The bin is empty." : "No contracts match the filter.", null);
                return;
            }

            EnsureAutoCollapse(shown);

            El list;
            UIF.ScrollView(col, out list, "contracts.inbox").Flex(1f, 1f);

            BuildWeeks(list, shown);
        }

        /// <summary>
        /// Inbox / bin, and the way into selection mode. A second row rather than more
        /// buttons on the filter row: these two switch what the list *is*, where the
        /// filters only narrow it, and mixing the two reads as six equal choices.
        /// </summary>
        private void BuildViewRow(El col, IList<object> contracts)
        {
            var row = UIF.Box(col, "Views").Row(Theme.Space1).H(24);

            int binned = 0;
            foreach (var obj in contracts)
            {
                var c = obj as Dictionary<string, object>;
                if (c != null && ContractInbox.IsTrashed(MiniJSON.GetString(c, "contract_id"))) binned++;
            }

            ViewButton(row, "Inbox", false);
            ViewButton(row, binned > 0 ? "Bin (" + binned + ")" : "Bin", true);

            UIF.Grow(row);

            UIF.Button(row, selecting ? "Done" : "Select", () =>
            {
                selecting = !selecting;
                selected.Clear();
                bulkCancelConfirm = false;
                ClearStatus();
                MarkDirty();
            }, selecting ? BtnStyle.Secondary : BtnStyle.Ghost, 24, Theme.Space2)
               .E.PrefW(62);
        }

        private void ViewButton(El parent, string label, bool trash)
        {
            var b = UIF.Button(parent, label, () =>
            {
                if (showTrash == trash) return;
                showTrash = trash;
                // The selection belongs to the view it was made in: "cancel these" and
                // "restore these" are different lists, and carrying one over would act
                // on rows that are no longer on screen.
                selected.Clear();
                bulkCancelConfirm = false;
                openId = null;
                ClearStatus();
                MarkDirty();
            }, showTrash == trash ? BtnStyle.Secondary : BtnStyle.Ghost, 24, Theme.Space2);
            b.Label.Size(Theme.FontXs);
            b.E.PrefW(72);
        }

        /// <summary>
        /// What can be done to the ticked rows. Cancel is the classic window's bulk
        /// action and keeps its rule — pending only, since that is the one status where
        /// cancelling is a withdrawal rather than a forfeit — and the bin actions are
        /// the per-row ones applied to the whole selection.
        /// </summary>
        private void BuildSelectionBar(El col, List<Dictionary<string, object>> shown)
        {
            var card = UIF.Card(col, "Selection").Column(Theme.Space2).Pad(Theme.Space2);

            var head = UIF.Box(card, "SelHead").Row(Theme.Space2).H(24);
            bool allSelected = shown.Count > 0 && selected.Count >= shown.Count;
            UIF.Checkbox(head, allSelected, on =>
            {
                selected.Clear();
                if (on) foreach (var c in shown) selected.Add(MiniJSON.GetString(c, "contract_id"));
                bulkCancelConfirm = false;
                MarkDirty();
            });
            UIF.Label(head, allSelected ? "All selected" : "Select all", Theme.FontXs);
            UIF.Grow(head);
            UIF.Muted(head, selected.Count + " selected");

            DrawStatus(card);

            if (selected.Count == 0)
            {
                UIF.Muted(card, "Tick the contracts you want to act on.").Body();
                return;
            }

            int cancellable = CountSelected(shown, c => MiniJSON.GetString(c, "status") == "pending");
            int binnable = CountSelected(shown, c => ContractInbox.IsFinished(MiniJSON.GetString(c, "status")));

            var actions = UIF.Box(card, "SelActions").Row(Theme.Space2).H(28);

            if (showTrash)
            {
                UIF.Button(actions, "Restore " + selected.Count, () => BulkTrash(shown, false),
                           BtnStyle.Secondary, 28).Interactable(!Busy).E.Flex(1f);
            }
            else
            {
                UIF.Button(actions, binnable > 0 ? "Bin " + binnable : "Bin",
                           () => BulkTrash(shown, true), BtnStyle.Ghost, 28)
                   .Interactable(!Busy && binnable > 0).E.Flex(1f);

                if (bulkCancelConfirm)
                {
                    UIF.Button(actions, "Confirm", () => { bulkCancelConfirm = false; BulkCancel(shown); },
                               BtnStyle.Destructive, 28).Interactable(!Busy).E.Flex(1f);
                    UIF.Button(actions, "Keep", () => { bulkCancelConfirm = false; MarkDirty(); },
                               BtnStyle.Ghost, 28).E.Flex(1f);
                }
                else
                {
                    UIF.Button(actions, cancellable > 0 ? "Cancel " + cancellable : "Cancel",
                               () => { bulkCancelConfirm = true; MarkDirty(); },
                               BtnStyle.Destructive, 28)
                       .Interactable(!Busy && cancellable > 0).E.Flex(1f);
                }
            }

            if (!showTrash && binnable < selected.Count)
                UIF.Muted(card, "Only finished contracts can go in the bin; live ones stay put.").Body();
        }

        private int CountSelected(List<Dictionary<string, object>> shown,
                                  Func<Dictionary<string, object>, bool> predicate)
        {
            int n = 0;
            foreach (var c in shown)
                if (selected.Contains(MiniJSON.GetString(c, "contract_id")) && predicate(c)) n++;
            return n;
        }

        /// <summary>Bin or restore every ticked row the rule allows. Purely local — see ContractInbox.</summary>
        private void BulkTrash(List<Dictionary<string, object>> shown, bool trash)
        {
            int moved = 0;
            foreach (var c in shown)
            {
                string cid = MiniJSON.GetString(c, "contract_id");
                if (!selected.Contains(cid)) continue;
                // Restoring has no status rule: whatever is in the bin got there by
                // passing the rule on the way in.
                if (trash && !ContractInbox.IsFinished(MiniJSON.GetString(c, "status"))) continue;

                ContractInbox.SetTrashed(cid, trash);
                moved++;
            }

            selected.Clear();
            var done = BeginAction();
            done(moved > 0, moved == 0
                ? "Nothing to move."
                : moved + (trash ? " moved to the bin." : " restored."));
        }

        /// <summary>
        /// Cancel every ticked pending contract, one request each, through the same
        /// coroutine a single Withdraw runs — it stops the editor's part filter if it
        /// was gating parts for that contract, and re-reads the list afterwards.
        ///
        /// The completion fires once, when the last reply lands: a status line that
        /// rewrote itself per contract would settle on whichever request happened to
        /// finish last rather than on what happened.
        /// </summary>
        private void BulkCancel(List<Dictionary<string, object>> shown)
        {
            var main = GeneKermanMod.Instance?.MainWindowRef;
            if (main == null) return;

            var ids = new List<string>();
            foreach (var c in shown)
            {
                string cid = MiniJSON.GetString(c, "contract_id");
                if (selected.Contains(cid) && MiniJSON.GetString(c, "status") == "pending")
                    ids.Add(cid);
            }

            if (ids.Count == 0) return;

            selected.Clear();
            var done = BeginAction();

            int outstanding = ids.Count;
            int failed = 0;
            foreach (string cid in ids)
            {
                main.RequestCancelContract(cid, (ok, msg) =>
                {
                    if (!ok) failed++;
                    if (--outstanding > 0) return;

                    int total = ids.Count;
                    done(failed == 0, failed == 0
                        ? total + " cancelled."
                        : (total - failed) + " of " + total + " cancelled.");
                });
            }
        }

        /// <summary>
        /// Fold away everything older than the newest <see cref="AutoCollapseAfter"/>
        /// contracts, once per view. Once, and not on every rebuild: a refresh landing
        /// must not re-fold a week the player has just opened.
        /// </summary>
        private void EnsureAutoCollapse(List<Dictionary<string, object>> shown)
        {
            if (shown.Count == 0) return;

            string view = (showTrash ? "bin" : "inbox") + ":" + filterMode;
            if (autoCollapsedFor == view) return;
            autoCollapsedFor = view;

            collapsedWeeks.Clear();

            // Counted per week first, then folded week by week: the test is how many
            // contracts came *before* a week, so a week that starts inside the first
            // ten stays open even if it runs past them. Deciding row by row would fold
            // it the moment its eleventh contract went by.
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var c in shown)
            {
                string week = ContractInbox.WeekKey(MiniJSON.GetString(c, "created_at"));
                if (!counts.ContainsKey(week)) { counts[week] = 0; order.Add(week); }
                counts[week]++;
            }

            int before = 0;
            foreach (string week in order)
            {
                if (before >= AutoCollapseAfter) collapsedWeeks.Add(week);
                before += counts[week];
            }
        }

        /// <summary>
        /// The list, in week groups. Grouping is what the classic window's mail list
        /// does and it survives the move for the same reason: contracts arrive in
        /// bursts, and an ungrouped column of forty is a wall.
        /// </summary>
        private void BuildWeeks(El list, List<Dictionary<string, object>> shown)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<Dictionary<string, object>>>();

            // shown is already newest-first, so first-seen order is newest-week-first.
            foreach (var c in shown)
            {
                string week = ContractInbox.WeekKey(MiniJSON.GetString(c, "created_at"));
                if (!groups.ContainsKey(week))
                {
                    groups[week] = new List<Dictionary<string, object>>();
                    order.Add(week);
                }
                groups[week].Add(c);
            }

            foreach (string week in order)
            {
                var items = groups[week];
                bool collapsed = collapsedWeeks.Contains(week);

                string key = week;
                var head = UIF.ClickableRow(list, () =>
                {
                    if (!collapsedWeeks.Remove(key)) collapsedWeeks.Add(key);
                    MarkDirty();
                }, false, Theme.RadiusSm)
                    .Row(Theme.Space2)
                    .Pad(Theme.Space3, Theme.Space3, Theme.Space1, Theme.Space1)
                    .ChildAlign(TextAnchor.MiddleLeft)
                    .H(24);

                UIF.Label(head, week, Theme.FontXs, Theme.MutedForeground).Bold();
                UIF.Grow(head);
                // No caret glyph: the font is KSP's and arrows are not a safe bet in it
                // (see Theme.Font), so the state is said in words instead.
                UIF.Muted(head, collapsed ? items.Count + " hidden" : items.Count.ToString());

                if (collapsed) continue;

                var card = UIF.Card(list, "Inbox").Column(0f);
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) UIF.Divider(card);
                    BuildRow(card, items[i]);
                }
            }
        }

        /// <summary>
        /// Newest first. The API returns storage order, which interleaves finished
        /// contracts with live ones; sorting makes it read as an inbox rather than a
        /// dump. Mirrors `sortKey` in WebUI/src/screens/Contracts.tsx, including the
        /// fall back to the due date when created_at is absent.
        /// </summary>
        private List<Dictionary<string, object>> Filtered(IList<object> contracts)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var obj in contracts)
            {
                var c = obj as Dictionary<string, object>;
                if (c == null) continue;

                // The bin is a view, not a filter on top of one: a binned contract is
                // absent from the inbox entirely, exactly as in the classic window.
                if (ContractInbox.IsTrashed(MiniJSON.GetString(c, "contract_id")) != showTrash) continue;

                bool isIncoming = !MiniJSON.GetBool(c, "is_outgoing");
                if (filterMode == 1 && !isIncoming) continue;
                if (filterMode == 2 && isIncoming) continue;

                result.Add(c);
            }

            result.Sort((a, b) => SortKey(b).CompareTo(SortKey(a)));
            return result;
        }

        private static long SortKey(Dictionary<string, object> c)
        {
            DateTime d;
            string created = MiniJSON.GetString(c, "created_at", "");
            if (DateTime.TryParse(created, out d)) return d.Ticks;
            string due = MiniJSON.GetString(c, "due_date", "");
            if (DateTime.TryParse(due, out d)) return d.Ticks;
            return 0;
        }

        /// <summary>
        /// Whether this contract is waiting on *you*. Ported from `needsAction` in
        /// WebUI/src/screens/Contracts.tsx, and used for both the row dot and the
        /// header count for the same reason it is shared there: a "needs you" marker
        /// on a contract with no action available is worse than no marker at all.
        /// </summary>
        private static bool NeedsAction(Dictionary<string, object> c)
        {
            bool isOutgoing = MiniJSON.GetBool(c, "is_outgoing");
            switch (MiniJSON.GetString(c, "status"))
            {
                case "pending":   return true;        // issuer withdraws, contractor accepts or declines
                case "active":    return !isOutgoing;
                case "submitted": return isOutgoing;  // the issuer reviews
                case "disputed":  return !isOutgoing; // the contractor resolves
                default:          return false;
            }
        }

        private void FilterButton(El parent, string label, int mode)
        {
            var b = UIF.Button(parent, label, () =>
            {
                filterMode = mode;
                MarkDirty();
            }, filterMode == mode ? BtnStyle.Secondary : BtnStyle.Ghost, 24, Theme.Space2);
            b.Label.Size(Theme.FontXs);
            b.E.PrefW(64);
        }

        /// <summary>
        /// One inbox row. The whole row is the button — there is no "Open" affordance,
        /// matching the web UI, where each list item is a full-width button. The two
        /// controls that sit inside it (the tick box, the bin button) are Buttons of
        /// their own, and Unity gives a click to the innermost handler, so pressing
        /// either does not also open the contract.
        /// </summary>
        private void BuildRow(El parent, Dictionary<string, object> c)
        {
            string cid = MiniJSON.GetString(c, "contract_id");
            string status = MiniJSON.GetString(c, "status");
            bool isOutgoing = MiniJSON.GetBool(c, "is_outgoing");

            var row = UIF.ClickableRow(parent, () => Select(cid), openId == cid)
                         .Row(Theme.Space2)
                         .Pad(Theme.Space3, Theme.Space3, Theme.Space2, Theme.Space2)
                         .ChildAlign(TextAnchor.UpperLeft);

            if (selecting)
            {
                bool ticked = selected.Contains(cid);
                UIF.Checkbox(row, ticked, on =>
                {
                    if (on) selected.Add(cid); else selected.Remove(cid);
                    bulkCancelConfirm = false;
                    MarkDirty();
                });
            }
            else
            {
                // The "waiting on you" pip. Always present so the text column starts at
                // the same x whether or not it is lit — an indented row reads as broken.
                UIF.Box(row, "Pip")
                   .Dot(NeedsAction(c) ? Theme.Primary : Theme.Alpha(Theme.Primary, 0f), 8);
            }

            var text = UIF.Box(row, "Text").Column(Theme.Space1).Flex(1f, 0f);

            var top = UIF.Box(text, "Top").Row(Theme.Space2).H(20);
            UIF.Badge(top, Fmt.Status(status), Theme.ContractStatus(status));

            // Parts badge: this contract restricts what may be built. It goes red-hot
            // while the editor is actually enforcing *this* contract's list, which is
            // the classic window's [R] marker and the only on-screen sign that the part
            // picker is short because of a contract rather than because of a bug.
            if (IsRestricted(c))
            {
                bool enforcing = IsEnforcing(cid);
                UIF.Badge(top, enforcing ? "Parts: enforced" : "Parts",
                          enforcing ? Theme.Primary : Theme.Status("warning"));
            }

            UIF.Label(top, isOutgoing
                        ? "to " + MiniJSON.GetString(c, "contractor_name")
                        : "from " + MiniJSON.GetString(c, "issuer_name"),
                      Theme.FontXs, Theme.MutedForeground);
            UIF.Grow(top);

            string mission = MiniJSON.GetString(c, "mission");
            UIF.Label(text, string.IsNullOrEmpty(mission) ? "(no description)" : mission,
                      Theme.FontSm).Body();

            var foot = UIF.Box(text, "Foot").Row(Theme.Space3).H(16);
            UIF.Muted(foot, MiniJSON.GetInt(c, "payment").ToString("N0") + " KCoins");
            string due = MiniJSON.GetString(c, "due_date");
            if (!string.IsNullOrEmpty(due)) UIF.Muted(foot, "due " + due);
            UIF.Grow(foot);

            // Per-row bin / restore, for the common case of tidying one line without
            // going into selection mode. Only where the classic window offers it: a
            // contract still in play cannot be hidden.
            if (selecting || !(showTrash || ContractInbox.IsFinished(status))) return;

            bool trashed = showTrash;
            UIF.Button(row, trashed ? "Restore" : "Bin", () =>
            {
                ContractInbox.SetTrashed(cid, !trashed);
                if (openId == cid) openId = null;
                MarkDirty();
            }, BtnStyle.Ghost, 22, Theme.Space2).E.PrefW(trashed ? 66 : 44);
        }

        /// <summary>Does this contract restrict what the contractor may build?</summary>
        private static bool IsRestricted(Dictionary<string, object> c)
        {
            if (!string.IsNullOrEmpty(MiniJSON.GetString(c, "modlist", ""))) return true;
            return !ContractConstraints.Parse(MiniJSON.GetDict(c, "constraints")).IsEmpty;
        }

        /// <summary>Is the editor's part filter currently gating parts for this contract?</summary>
        private static bool IsEnforcing(string cid) =>
            EditorPartEnforcer.Instance != null &&
            EditorPartEnforcer.Instance.IsEnforcing() &&
            EditorPartEnforcer.Instance.ActiveContractId == cid;

        /// <summary>Open a contract in the detail pane, resetting anything half-pressed.</summary>
        /// <summary>
        /// Open or close the create form. Opening it clears the selection, because the
        /// two share the detail slot and a half-written contract is worth more than a
        /// detail pane the player can get back with one click.
        /// </summary>
        private void ToggleCreate()
        {
            creating = !creating;
            ClearStatus();

            if (creating)
            {
                openId = null;
                form.Reset();
            }
            else
            {
                // The avatars the picker downloaded belong to a form that is gone.
                form.Dispose();
            }

            MarkDirty();
        }

        private void Select(string cid)
        {
            creating = false;
            openId = cid;
            ClearStatus();
            confirmActionId = null;
            rescueAcceptConfirmId = null;
            moreTimeDate = DateTime.Now.Date.AddDays(7);
            moreTimePicker.Close();
            MarkDirty();
        }

        /// <summary>
        /// Open a contract from outside this panel — a notification about one, via
        /// SidebarController.ShowContract, which is what calls this. Switches to whichever view the contract is
        /// actually in, since a binned contract opened from the inbox would show a
        /// detail pane beside a list that does not contain it; and refetches, because
        /// the id may name a contract that arrived after the last poll.
        /// </summary>
        internal void ShowContract(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return;

            showTrash = ContractInbox.IsTrashed(cid);
            selecting = false;
            selected.Clear();
            Select(cid);

            GeneKermanMod.Instance?.MainWindowRef?.RequestContractsRefresh();
        }

        // ── Detail view ─────────────────────────────────────────────────────
        //
        // The action bar mirrors MainWindow.DrawContractDetail's mapping exactly —
        // status crossed with is_outgoing — and every button routes into the same
        // coroutine the classic window runs. See the note on MainWindow's
        // Request* wrappers for why that matters rather than being tidiness.

        private void BuildDetail(El col, GeneKermanMod mod, MainWindow main, Dictionary<string, object> c)
        {
            string cid = MiniJSON.GetString(c, "contract_id");
            string status = MiniJSON.GetString(c, "status");
            bool isOutgoing = MiniJSON.GetBool(c, "is_outgoing");
            string mType = MiniJSON.GetString(c, "mission_type", "active_vessel");

            // Back closes the detail, which also narrows the panel again. The web UI
            // hides this above `xl` because the list is still on screen; here the list
            // is always beside it, but the panel is wide and a way out is cheap.
            var head = UIF.Box(col, "DetailHead").Row(Theme.Space2).H(26);
            UIF.Button(head, "< Back", () => { openId = null; ClearStatus(); MarkDirty(); },
                       BtnStyle.Ghost, 24).E.PrefW(62);
            UIF.Grow(head);

            El body;
            // Keyed by contract, not just "the detail view": every action on this
            // screen rebuilds the panel, so the offset has to survive that — but
            // opening a *different* contract is a different document and starts at
            // the top, which a shared key would not do.
            UIF.ScrollView(col, out body, "contracts.detail:" + openId).Flex(1f, 1f);

            var card = UIF.Card(body, "Detail").Column(Theme.Space3).Pad(Theme.Space4);

            // Badge row, in the web UI's order: status, then what kind of work it is.
            var badges = UIF.Box(card, "Badges").Row(Theme.Space2).H(22);
            UIF.Badge(badges, Fmt.Status(status), Theme.ContractStatus(status));
            if (MiniJSON.GetBool(c, "is_bot_issued"))
                UIF.Badge(badges, "Weekly mission", Theme.MutedForeground, Theme.Secondary);
            if (mType == "craft_build")
                UIF.Badge(badges, "Craft build", Theme.MutedForeground, Theme.Secondary);
            else if (mType == "rescue")
                UIF.Badge(badges, "Rescue", Theme.MutedForeground, Theme.Secondary);
            else if (mType == "flag_design")
                UIF.Badge(badges, "Flag design", Theme.MutedForeground, Theme.Secondary);
            UIF.Grow(badges);

            UIF.Label(card, MiniJSON.GetString(c, "mission"), Theme.FontSm).Body();

            UIF.Divider(card);

            // The web UI's <dl>: a two-column grid of uppercase label over value.
            var grid = UIF.Box(card, "Fields").Column(Theme.Space3);
            var fields = new List<KeyValuePair<string, string>>();
            Add(fields, "Issuer", MiniJSON.GetString(c, "issuer_name"));
            Add(fields, "Contractor", MiniJSON.GetString(c, "contractor_name"));
            Add(fields, "Payment", MiniJSON.GetInt(c, "payment").ToString("N0") + " KCoins");
            Add(fields, "Fine", MiniJSON.GetInt(c, "fine").ToString("N0"));
            Add(fields, "Due", MiniJSON.GetString(c, "due_date"));
            Add(fields, "Body", MiniJSON.GetString(c, "required_body"));
            Add(fields, "Situation", Fmt.Situation(MiniJSON.GetString(c, "required_situation")));
            BuildFieldGrid(grid, fields);

            var kerbals = MiniJSON.GetList(c, "rescue_kerbals");
            if (kerbals != null && kerbals.Count > 0)
            {
                UIF.Divider(card);
                var names = new List<string>();
                foreach (var k in kerbals) if (k != null) names.Add(k.ToString());
                Section(card, "Kerbals to recover", string.Join(", ", names.ToArray()));

                // Crew-only or salvage, and any Δv the crew must be left with — the two
                // things that decide whether a submission will be accepted.
                var rtSpec = MiniJSON.GetDict(c, "rescue_target");
                if (rtSpec != null)
                {
                    // Where they have to be delivered, and — when the issuer named one —
                    // which orbit that is. Ap/Pe say how high, never which plane, and a
                    // plane requirement changes what the job costs.
                    string rBody = MiniJSON.GetString(rtSpec, "body", "?");
                    if (MiniJSON.GetString(rtSpec, "mode", "orbit") == "surface")
                        Section(card, "Deliver to",
                                $"{rBody} surface at {MiniJSON.GetDouble(rtSpec, "lat"):F1}°, " +
                                $"{MiniJSON.GetDouble(rtSpec, "lon"):F1}°");
                    else
                    {
                        Section(card, "Deliver to",
                                $"{rBody} orbit, Ap {MiniJSON.GetDouble(rtSpec, "ap") / 1000:F0} km / " +
                                $"Pe {MiniJSON.GetDouble(rtSpec, "pe") / 1000:F0} km");
                        string orbitReq = RescueTargetSpec.FromDict(rtSpec).DescribeOrbitRequirement();
                        if (!string.IsNullOrEmpty(orbitReq))
                            Section(card, "Required orbit", orbitReq);
                    }

                    Section(card, "Recover",
                            MiniJSON.GetString(rtSpec, "recovery", "crew") == "vessel"
                                ? "The crew and the stranded vessel itself"
                                : "The crew (the wreck may be left behind)");
                    double minDv = MiniJSON.GetDouble(rtSpec, "min_dv", 0);
                    if (minDv > 0)
                        Section(card, "Δv on arrival", $"≥{minDv:F0} m/s left on the delivering craft");
                }
            }

            string modlist = MiniJSON.GetString(c, "modlist", "");
            if (!string.IsNullOrEmpty(modlist))
            {
                UIF.Divider(card);
                Section(card, "Mods allowed", modlist);
            }

            BuildPartRules(card, c, cid, status, modlist);

            BuildSubmission(body, cid, status);

            // A dispute is the one state nothing else ends, so the server closes it on
            // a timer and collects the fine. Both parties are told when.
            string autoFine = MiniJSON.GetString(c, "auto_fine_at", "");
            if (status == "disputed" && !string.IsNullOrEmpty(autoFine))
                UIF.Notice(body, "Unresolved disputes settle themselves.",
                           "The " + MiniJSON.GetInt(c, "fine").ToString("N0") +
                           " KCoin fine is collected automatically on " + autoFine + ".");

            DrawStatus(col);
            BuildActions(col, mod, main, c, cid, status, isOutgoing, mType);
        }

        /// <summary>
        /// What this contract lets the contractor build, and — in the editor — the
        /// switch that makes KSP's part picker obey it.
        ///
        /// The limits line is shown wherever the contract is read, because "no ion
        /// engines" is something to know before accepting, not only while building.
        /// The switch is the editor's alone: EditorPartEnforcer is an
        /// [KSPAddon(EditorAny)] MonoBehaviour, so outside the VAB/SPH there is no
        /// instance to talk to and nothing it could filter.
        ///
        /// Enforcement is one contract at a time by construction — the enforcer holds a
        /// single ActiveContractId — so turning it on here takes it off whatever it was
        /// on. That is the enforcer's rule, not this panel's, and the classic window's
        /// toggle behaves the same way.
        /// </summary>
        private void BuildPartRules(El card, Dictionary<string, object> c, string cid,
                                    string status, string modlist)
        {
            var limits = ContractConstraints.Parse(MiniJSON.GetDict(c, "constraints"));
            bool inEditor = HighLogic.LoadedSceneIsEditor && status == "active";

            if (limits.IsEmpty && !inEditor) return;

            UIF.Divider(card);

            if (!limits.IsEmpty)
                Section(card, "Mission limits", limits.Describe());

            if (!inEditor) return;

            if (string.IsNullOrEmpty(modlist) && limits.IsEmpty)
            {
                Section(card, "Parts", "No restriction: every part in your install is allowed.");
                return;
            }

            lastEnforcedId = EnforcedId();
            bool enforcing = IsEnforcing(cid);

            UIF.Switch(card, "Enforce in the editor",
                       "Hides every part this contract does not allow from the VAB/SPH picker.",
                       enforcing, on =>
                       {
                           if (on) EditorPartEnforcer.Instance?.EnforceModlist(cid, modlist, limits);
                           else EditorPartEnforcer.Instance?.StopEnforcing();
                           MarkDirty();
                       });

            // Enforcement is global, so a player who armed it on another contract needs
            // to be told why this switch is off while the picker is still short.
            string other = EnforcedId();
            if (!enforcing && !string.IsNullOrEmpty(other) && other != cid)
                UIF.Muted(card, "Another contract's part filter is active; turning this on replaces it.")
                   .Body();
        }

        /// <summary>The contract the editor's part filter is gating for, or "".</summary>
        private static string EnforcedId()
        {
            var e = EditorPartEnforcer.Instance;
            return (e != null && e.IsEnforcing()) ? (e.ActiveContractId ?? "") : "";
        }

        /// <summary>
        /// The SUBMISSION card: the blueprint renders and telemetry diagrams the
        /// contractor sent. Fetched lazily and cached per contract; see `preview`.
        /// </summary>
        private void BuildSubmission(El parent, string cid, string status)
        {
            if (!HasSubmission(status)) return;

            var card = UIF.Card(parent, "Submission").Column(Theme.Space2).Pad(Theme.Space4);
            UIF.Label(card, "SUBMISSION", Theme.FontXs, Theme.MutedForeground);

            if (previewLoading || previewId != cid)
            {
                UIF.Muted(card, "Loading submission…", Theme.FontSm);
                return;
            }

            if (preview == null || !preview.HasImages)
            {
                UIF.Muted(card, preview?.Error ?? "No images were submitted.", Theme.FontSm).Body();
                return;
            }

            if (!string.IsNullOrEmpty(preview.VesselName))
                UIF.Label(card, preview.VesselName, Theme.FontSm).Bold();

            // One flat list across both kinds, so the viewer's arrows step through
            // the whole submission rather than through two separate sets.
            var shots = new List<Texture2D>();
            var captions = new List<string>();
            string vessel = string.IsNullOrEmpty(preview.VesselName) ? "Submission" : preview.VesselName;

            foreach (var tex in preview.Blueprints)
            {
                if (tex == null) continue;
                shots.Add(tex);
                captions.Add(vessel + " - blueprint");
            }
            foreach (var tex in preview.Telemetry)
            {
                if (tex == null) continue;
                shots.Add(tex);
                captions.Add(vessel + " - orbit & telemetry");
            }

            // Stacked rather than tiled: the renders are wide multi-view sheets, and
            // uGUI has no wrapping layout to build a real grid out of.
            for (int i = 0; i < shots.Count; i++)
                PictureRow(card, shots, captions, i, vessel);

            if (shots.Count > 0)
                UIF.Muted(card, "Click an image to open it full screen.", Theme.FontXs);
        }

        /// <summary>
        /// One thumbnail row. The whole set is handed to the viewer along with this
        /// row's index, so opening any image and then stepping through them lands on
        /// the same list the card shows, in the same order.
        /// </summary>
        private void PictureRow(El parent, List<Texture2D> shots, List<string> captions, int i, string vessel)
        {
            var tex = shots[i];
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            float height = Mathf.Min(PreviewHeight, MaxPreviewWidth / Mathf.Max(aspect, 0.01f));

            var row = UIF.Box(parent, "PicRow").Row(0f);
            int at = i;
            UIF.PictureButton(row, tex, height, () => ShowImages(shots, captions, at, vessel));
            UIF.Grow(row);
        }

        /// <summary>Fetch the open contract's submission, once. Called from Poll.</summary>
        private void EnsurePreview(string cid, string status)
        {
            if (!HasSubmission(status)) { ClearPreview(); return; }
            if (previewLoading || previewId == cid) return;

            ClearPreview();
            previewId = cid;
            previewLoading = true;

            string wanted = cid;
            GeneKermanMod.Instance.RunCoroutine(SubmissionPreviewLoader.Fetch(cid, result =>
            {
                previewLoading = false;

                // The selection may have moved while this was in flight. Drop the
                // late arrival rather than showing one contract's blueprints under
                // another's heading — and destroy it, since nothing else will.
                if (previewId != wanted) { result.Dispose(); return; }

                preview = result;
                MarkDirty();
            }));
        }

        private void ClearPreview()
        {
            // The viewer borrows these textures. Close it before they are destroyed
            // rather than leaving it to notice the nulls a frame later.
            if (preview != null) CloseImages();

            preview?.Dispose();
            preview = null;
            previewId = null;
        }

        private static void Add(List<KeyValuePair<string, string>> into, string label, string value)
        {
            if (!string.IsNullOrEmpty(value))
                into.Add(new KeyValuePair<string, string>(label, value));
        }

        /// <summary>
        /// Two per row, label over value. uGUI has GridLayoutGroup, but it insists on
        /// a fixed cell size, which would either clip a long name or leave a gap after
        /// a short one — so the "grid" is rows of two flexible halves.
        /// </summary>
        private static void BuildFieldGrid(El parent, List<KeyValuePair<string, string>> fields)
        {
            for (int i = 0; i < fields.Count; i += 2)
            {
                var row = UIF.Box(parent, "FieldRow").Row(Theme.Space4);
                Field(row, fields[i].Key, fields[i].Value);
                if (i + 1 < fields.Count) Field(row, fields[i + 1].Key, fields[i + 1].Value);
                else UIF.Grow(row);
            }
        }

        private static void Field(El parent, string label, string value)
        {
            var cell = UIF.Box(parent, "Field").Column(2).Flex(1f, 0f);
            UIF.Label(cell, label.ToUpperInvariant(), Theme.FontXs, Theme.MutedForeground);
            UIF.Label(cell, value, Theme.FontSm).Body();
        }

        private static void Section(El parent, string label, string value)
        {
            var box = UIF.Box(parent, "Section").Column(2);
            UIF.Label(box, label.ToUpperInvariant(), Theme.FontXs, Theme.MutedForeground);
            UIF.Label(box, value, Theme.FontSm, Theme.MutedForeground).Body();
        }


        private void BuildActions(El col, GeneKermanMod mod, MainWindow main,
                                  Dictionary<string, object> c, string cid, string status,
                                  bool isOutgoing, string mType)
        {
            var bar = UIF.Box(col, "Actions").Column(Theme.Space2);

            switch (status)
            {
                case "pending":
                    if (isOutgoing)
                    {
                        // The issuer is not the recipient, so their only move is to
                        // withdraw the offer — never accept it on the other's behalf.
                        Confirmable(bar, "Withdraw offer", cid,
                                    () => main.RequestCancelContract(cid, Done()));
                    }
                    else
                    {
                        bool isRescue = mType == "rescue";
                        bool moddedTarget = MiniJSON.GetBool(c, "is_modded_target");

                        // A rescue aimed at a modded body needs that planet pack
                        // installed; accepting blind strands the contract.
                        if (isRescue && moddedTarget && rescueAcceptConfirmId != cid)
                        {
                            var rt = MiniJSON.GetDict(c, "rescue_target");
                            string rbody = rt != null ? MiniJSON.GetString(rt, "body", "this planet") : "this planet";
                            UIF.Label(bar, "Targets " + rbody + " (modded). You need its planet pack installed.",
                                      Theme.FontXs, Theme.Status("warning")).Body();
                            UIF.Button(bar, "Accept anyway", () => { rescueAcceptConfirmId = cid; MarkDirty(); },
                                       BtnStyle.Primary, 28).Interactable(!Busy);
                        }
                        else
                        {
                            UIF.Button(bar, "Accept", () =>
                            {
                                rescueAcceptConfirmId = null;
                                main.RequestAcceptContract(cid, MiniJSON.GetString(c, "issuer_name", ""), Done());
                            }, BtnStyle.Primary, 28).Interactable(!Busy);
                        }

                        UIF.Button(bar, "Decline", () =>
                        {
                            rescueAcceptConfirmId = null;
                            main.RequestCancelContract(cid, Done());
                        }, BtnStyle.Ghost, 28).Interactable(!Busy);
                    }
                    break;

                case "active":
                    // A rescue's wreck does not arrive with the contract: accepting
                    // only agrees to the job, and the stranded vessel is spawned into
                    // this save on demand. That is a separate step so a failure is
                    // retryable and so it never lands in a scene that cannot take a
                    // vessel — and it is why this has to be reachable from here.
                    if (mType == "rescue" && !isOutgoing) BuildRescueWreck(bar, main, c, cid);

                    // Submission is inherently an in-game act — it needs live
                    // telemetry and a screenshot with the HUD hidden — so this
                    // raises the IMGUI SubmitWindow rather than reimplementing it.
                    UIF.Button(bar, "Submit in KSP", () =>
                    {
                        var done = BeginAction();
                        mod.RunCoroutine(Web.GkRoutes.OpenSubmitRoutine(cid, (ok, msg) =>
                        {
                            done(ok, ok ? "Submit window opened in KSP. Switch to the game." : msg);
                            if (ok) openId = null;
                        }));
                    }, BtnStyle.Primary, 28).Interactable(!Busy);

                    // Giving up costs the agreed fine, so it gets a confirm. The
                    // issuer side has nothing to give up — they would cancel.
                    if (!isOutgoing)
                    {
                        int giveUpFine = MiniJSON.GetInt(c, "fine");
                        Confirmable(bar,
                                    giveUpFine > 0 ? "Give up (fine " + giveUpFine.ToString("N0") + ")" : "Give up",
                                    cid, () => main.RequestGiveUpContract(cid, Done()));
                    }
                    break;

                case "submitted":
                    if (isOutgoing)
                    {
                        UIF.Button(bar, "Approve", () => main.RequestReviewContract(cid, true, Done()),
                                   BtnStyle.Primary, 28).Interactable(!Busy);
                        UIF.Button(bar, "Refuse", () => main.RequestReviewContract(cid, false, Done()),
                                   BtnStyle.Destructive, 28).Interactable(!Busy);
                    }
                    else
                    {
                        UIF.Notice(bar, "Waiting for the issuer to review your submission.");
                    }
                    break;

                case "disputed":
                    BuildDisputeActions(bar, main, c, cid, isOutgoing);
                    break;

                case "completed":
                    BuildCompletedActions(bar, main, c, cid, isOutgoing, mType);
                    break;
            }
        }

        private void BuildDisputeActions(El bar, MainWindow main, Dictionary<string, object> c,
                                         string cid, bool isOutgoing)
        {
            var pendingReq = MiniJSON.GetDict(c, "pending_request");
            string reqKind = pendingReq != null ? MiniJSON.GetString(pendingReq, "kind") : "";

            if (isOutgoing)
            {
                // An open settle / more-time request puts the ball in the issuer's
                // court, and answering one is browser-UI and Discord only — so say
                // who is being waited on rather than implying the contractor stalls.
                if (reqKind == "settle")
                    UIF.Notice(bar, "They asked to settle.", "Answer in the browser UI or on Discord.");
                else if (reqKind == "more_time")
                    UIF.Notice(bar, "They asked for more time (" +
                               MiniJSON.GetString(pendingReq, "new_date") + ").",
                               "Answer in the browser UI or on Discord.");
                else
                    UIF.Notice(bar, "Waiting for the contractor to resolve this.");

                // Changing your mind about a refusal is the one exit from a dispute
                // that favours the contractor, so it is offered whatever is pending.
                UIF.Button(bar, "Accept after all", () => main.RequestReviewContract(cid, true, Done()),
                           BtnStyle.Primary, 28).Interactable(!Busy);
                return;
            }

            bool botIssued = MiniJSON.GetBool(c, "is_bot_issued");
            // One extension per dispute — the server refuses a second, so offering
            // the button again would only produce an error.
            bool moreTimeUsed = MiniJSON.GetBool(c, "more_time_used");

            if (!moreTimeUsed)
            {
                // A human-issued contract must name the date it is asking for. Pick
                // only, unlike the contract form: this is a single date on a card
                // that is already dense, and there is nothing to type it against.
                // Tomorrow at the earliest — "more time" that has already elapsed is
                // meaningless, and the API refuses it anyway.
                if (!botIssued)
                {
                    moreTimePicker.Build(bar, DatePicker.Print(moreTimeDate),
                                         DateTime.Now.Date.AddDays(1),
                                         picked => moreTimeDate = DatePicker.Parse(picked, moreTimeDate),
                                         null, "New date");
                }

                UIF.Button(bar, "More time", () => main.RequestDispute(
                               cid, "more_time",
                               botIssued ? null : DatePicker.Print(moreTimeDate), Done()),
                           BtnStyle.Secondary, 28).Interactable(!Busy);
            }

            UIF.Button(bar, "Pay fine", () => main.RequestDispute(cid, "pay_fine", null, Done()),
                       BtnStyle.Destructive, 28).Interactable(!Busy);

            // Sue sends the blueprint and details to the moderators — the recourse
            // when the AI reviewer (or the issuer) refused wrongly.
            UIF.Button(bar, "Sue", () => main.RequestDispute(cid, "sue", null, Done()),
                       BtnStyle.Primary, 28).Interactable(!Busy);

            // Settling needs a human counterparty; an AI contract has none.
            if (!botIssued)
                UIF.Button(bar, "Settle", () => main.RequestDispute(cid, "settle", null, Done()),
                           BtnStyle.Secondary, 28).Interactable(!Busy);
        }

        private void BuildCompletedActions(El bar, MainWindow main, Dictionary<string, object> c,
                                           string cid, bool isOutgoing, string mType)
        {
            // A flag has no craft to fetch — it is installed into the issuer's flag
            // picker by the craft-import queue at the Space Center.
            if (mType == "flag_design")
            {
                UIF.Notice(bar, isOutgoing
                    ? "Flag added to your flag picker (visit the Space Center)."
                    : "Flag delivered to the issuer.");
                return;
            }

            // Bot-contract crafts go to the builder's corp channel as a blueprint,
            // never into the save as a live vessel — that would allow duplication.
            if (MiniJSON.GetBool(c, "is_bot_issued"))
            {
                UIF.Notice(bar, "Delivered to your corp.");
                return;
            }

            if (GKContractScenario.Instance != null && GKContractScenario.Instance.HasImportedVessel(cid))
            {
                UIF.Notice(bar, "Vessel imported.");
                return;
            }

            // The deliverable belongs to whoever built it, so its crew are tagged
            // with the contractor's name on import.
            UIF.Button(bar, "Import / download", () => main.RequestDownloadCraft(
                           cid, MiniJSON.GetString(c, "contractor_name", ""), Done()),
                       BtnStyle.Primary, 28).Interactable(!Busy);
        }

        /// <summary>
        /// The stranded vessel of an accepted rescue: spawn it, or — once it is in this
        /// save — say what state its crew are in.
        ///
        /// Spawning runs MainWindow's coroutine rather than a copy: it carries the
        /// per-save dedup (GKContractScenario.HasImportedVessel), the in-flight guard,
        /// the orbit-epoch freeze that puts the wreck where the issuer left it, and the
        /// emergency-freeze registration. A second implementation would drop some of
        /// those silently, and the ones it dropped would only show up as a wreck in the
        /// wrong place or a crew that starves on thaw.
        /// </summary>
        private void BuildRescueWreck(El parent, MainWindow main, Dictionary<string, object> c, string cid)
        {
            var box = UIF.Box(parent, "Wreck").Column(Theme.Space2);
            UIF.Label(box, "Stranded vessel", Theme.FontSm).Body();

            bool spawned = GKContractScenario.Instance != null
                           && GKContractScenario.Instance.HasImportedVessel(cid);

            if (!spawned)
            {
                // Every scene that can register a vessel. Outside them the spawn would
                // fail on the far side of a download, so the button says so instead.
                bool sceneOk = HighLogic.LoadedSceneIsFlight
                               || HighLogic.LoadedScene == GameScenes.SPACECENTER
                               || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
                if (!sceneOk)
                {
                    UIF.Muted(box, "Enter Flight, the Space Center or the Tracking Station " +
                                   "to spawn the stranded vessel.").Body();
                    return;
                }

                UIF.Muted(box, "It spawns where the issuer left it, not at the delivery " +
                               "target; finding it is the mission.").Body();
                // BeginAction, not Done: the contract stays open so the freeze status
                // below replaces the button in place.
                UIF.Button(box, "🛟 Spawn stranded vessel",
                           () => main.RequestSpawnRescueWreck(c, BeginAction()),
                           BtnStyle.Primary, 28).Interactable(!Busy);
                return;
            }

            UIF.Muted(box, "Already spawned into this save.").Body();

            // Stranded crew ride out the wait in emergency freeze — lifted out of the
            // simulation and released by every installed LS mod, so nothing consumes
            // them. They thaw within 10 km; this forces it.
            var freeze = RescueImmunityGuardian.GetRecord(cid);
            if (freeze == null) return;

            UIF.Muted(box, "🧊 " + freeze.Crew.Count + " kerbal(s) in emergency freeze. " +
                           "They thaw when you get close.").Body();

            // Only worth saying when the two installs actually differ: that is the case
            // where the wreck's own supplies are useless here.
            if (LsFreeze.IsCrossMod(freeze.BuiltWithLs))
                UIF.Label(box,
                    "Built for " + LsFreeze.DisplayNameFor(freeze.BuiltWithLs) + "; you run " +
                    LsFreeze.DisplayNameFor(LsFreeze.LocalLsKey) + ". Emergency rations were " +
                    "stowed aboard so the crew survive the thaw.",
                    Theme.FontXs, Theme.Status("warning")).Body();

            UIF.Button(box, "🧊 Thaw the crew now", () =>
            {
                RescueImmunityGuardian.ReviveContract(cid);
                MarkDirty();
            }, BtnStyle.Ghost, 28).Interactable(!Busy);
        }

        /// <summary>
        /// A destructive button that arms on the first click and fires on the second,
        /// matching the classic window. Keyed by contract id so an armed confirm
        /// cannot carry over to a different contract.
        /// </summary>
        private void Confirmable(El parent, string label, string cid, Action fire)
        {
            if (confirmActionId == cid)
            {
                var row = UIF.Box(parent, "ConfirmRow").Row(Theme.Space2).H(28);
                UIF.Button(row, "Confirm", () => { confirmActionId = null; fire(); },
                           BtnStyle.Destructive, 28).Interactable(!Busy).E.Flex(1f);
                UIF.Button(row, "Cancel", () => { confirmActionId = null; MarkDirty(); },
                           BtnStyle.Ghost, 28).E.Flex(1f);
            }
            else
            {
                string id = cid;
                UIF.Button(parent, label, () => { confirmActionId = id; MarkDirty(); },
                           BtnStyle.Destructive, 28).Interactable(!Busy);
            }
        }

        /// <summary>Completion that also returns to the list — the contract's state just changed.</summary>
        private Action<bool, string> Done()
        {
            var inner = BeginAction();
            return (ok, msg) =>
            {
                inner(ok, msg);
                if (ok) openId = null;
            };
        }


        protected override void Poll()
        {
            var mod = GeneKermanMod.Instance;
            var main = mod?.MainWindowRef;
            if (main == null) return;

            // The form owns a player picker, which fetches a roster and downloads
            // avatars; both need pumping, and only while it is on screen.
            if (creating) form.Poll();

            if (!requested && main.ContractList == null && !main.ContractsLoading &&
                mod.Api != null && mod.Api.IsLinked)
            {
                requested = true;
                main.RequestContractsRefresh();
                return;
            }

            if ((main.ContractList?.Count ?? -1) != lastCount ||
                main.ContractsLoading != lastLoading)
            {
                MarkDirty();
            }

            // The classic window and the submit gate arm and drop the editor's part
            // filter too, and the row badge plus the detail's switch both draw its
            // state — so notice when it moves under us.
            string enforced = EnforcedId();
            if (enforced != lastEnforcedId)
            {
                lastEnforcedId = enforced;
                MarkDirty();
            }

            // Kick the submission fetch here rather than in Rebuild: Rebuild runs on
            // every data change, and starting a download from it would re-download
            // the blueprints each time the list refreshed underneath.
            if (string.IsNullOrEmpty(openId))
            {
                ClearPreview();
            }
            else
            {
                var open = main.FindContract(openId);
                if (open != null) EnsurePreview(openId, MiniJSON.GetString(open, "status"));
            }
        }

        internal override void OnShown()
        {
            requested = false;

            // Selection is a mode, and a mode left armed is one the player has to
            // notice and leave before the list behaves normally again. The folded
            // weeks are recomputed with it (autoCollapsedFor), so a session-old
            // fold does not hide this morning's contracts.
            selecting = false;
            selected.Clear();
            bulkCancelConfirm = false;
            autoCollapsedFor = null;

            form.Attach(MarkDirty, Done, () =>
            {
                // Sent. Close the form and refetch, so the new contract shows up in
                // the list the player is looking at rather than on the next poll.
                creating = false;
                form.Dispose();
                GeneKermanMod.Instance?.MainWindowRef?.RequestContractsRefresh();
            });
        }

        internal override void OnHidden()
        {
            // Textures are not garbage: hold them and every contract ever opened
            // stays resident until the scene changes.
            ClearPreview();
            form.Dispose();
        }

        internal override void OnSceneChanged()
        {
            form.Dispose();
            // The textures are already gone; what matters is dropping `previewId`,
            // because it is what stops Poll re-fetching. Without this the submission
            // card stays blank for the rest of the session.
            ClearPreview();
        }
    }
}
