/*
 * UI/Gui/Panels/ToolsPanel.cs – Quicksend, craft export, flag import and bug
 * reports, in uGUI.
 *
 * WebUI/src/screens/Tools.tsx is the visual specification; ToolActions.cs is the
 * implementation, unchanged and shared — this panel calls exactly what /gk/actions/*
 * calls, so a fix to the flag guards or the send payload lands in all three front ends
 * at once. The craft-state read moved into ToolActions with this slice for the same
 * reason: it used to be private to the bridge's routes.
 *
 * The one structural difference from the web screen: several cards, one status line.
 * SidebarPanel carries a single Status because only one action can be in flight at a
 * time anyway; `statusOwner` decides which card it is drawn under, so the result still
 * appears where the button that caused it is.
 *
 * The bug-report card is the exception to "ToolActions is shared with the web screen":
 * it exists only here, because filing one needs KSP.log off this machine.
 */

using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class ToolsPanel : SidebarPanel
    {
        public override string Title => "Tools";

        /// <summary>The local half of this panel is the reason limited mode exists:
        /// exporting a craft, importing a flag and repairing the roster all happen on
        /// this PC. The two cards that need the server are hidden there instead.</summary>
        internal override bool WorksOffline => true;

        private const int CardSend = 0;
        private const int CardExport = 1;
        private const int CardFlag = 2;
        private const int CardBug = 3;
        private const int CardRoster = 4;

        /// <summary>
        /// How often the craft situation is re-read. It changes when the player loads
        /// a craft or launches — neither of which raises an event we can hook — so it
        /// has to be polled, but it also touches the filesystem (does a .craft with
        /// this name exist?) and must not do that every frame.
        /// </summary>
        private const float CraftPollSeconds = 1.5f;

        private readonly PlayerPicker picker = new PlayerPicker();

        private ToolActions.CraftState craft;
        private float nextCraftRead;
        private string craftSignature = "";

        private int statusOwner = -1;

        private string flagUrl = "";
        private string flagName = "";

        private string bugSummary = "";
        private string bugDetails = "";
        private bool bugAttachLog = true;

        // How many kerbals have a profession no installed mod defines, as of the last
        // time this panel was shown. -1 == not counted yet.
        private int brokenCrew = -1;

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) return;

            var col = UIF.Box(Host, "Tools").Column(Theme.Space2).Flex(1f, 1f);
            UIF.PanelHeader(col, "Tools", () =>
            {
                ReadCraft();
                picker.Reset();
                ClearStatus();
                MarkDirty();
            });

            // Above the link gate on purpose, and the only card there: every other tool
            // talks to the server, while a broken roster is a local save problem that
            // being unlinked neither causes nor excuses. It is also the second way in —
            // the warning notification carries the same fix, and that one can be
            // dismissed, leaving a save nobody can open the Astronaut Complex in.
            if (brokenCrew > 0)
                BuildRosterRepair(col);

            // Two ways to have no server: not linked yet, and the version gate (limited
            // mode, where the sidebar narrows to this panel and Settings). Neither is a
            // reason to withhold the local tools — exporting a craft and importing a
            // flag happen entirely on this PC — so the notice sits above them instead
            // of replacing them.
            bool offline = !mod.Api.IsLinked || mod.UpdateRequired;
            if (!mod.Api.IsLinked)
                UIF.Notice(col, "Not linked to a Discord account.",
                           "Use the toolbar button to link this install; the tools below " +
                           "work either way.");

            El body;
            UIF.ScrollView(col, out body, "tools").Flex(1f, 1f);

            if (!offline) BuildQuicksend(body, mod);
            BuildExport(body);
            BuildImportFlag(body, mod);
            // Filing a bug is an upload like any other: under the gate it would come
            // back 426, and with no account there is nobody to open the ticket for.
            if (!offline) BuildBugReport(body, mod);
        }

        // ── Quicksend ───────────────────────────────────────────────────────

        private void BuildQuicksend(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "Quicksend").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Quicksend to a friend", Theme.FontSm).Bold();

            string kind = craft.SendKind;
            if (kind == "vessel")
            {
                UIF.Label(card, craft.ActiveVessel + ", sent as a live vessel, crew included.",
                          Theme.FontXs).Body();
                // A live vessel send is a hand-over, and the player must read that
                // BEFORE pressing Send — the same reason the rescue form carries an
                // explicit permanence line.
                UIF.Muted(card, "This hands the vessel over: it and its crew leave your " +
                                "save once sent (the ship you're flying goes when you " +
                                "leave it). If your friend declines, it comes back.",
                          Theme.FontXs);
            }
            else if (kind == "craft")
            {
                UIF.Label(card, craft.EditorCraft + " (" + craft.EditorType + ", " +
                                craft.EditorParts + " parts), sent as a blueprint.",
                          Theme.FontXs).Body();
            }
            else if (!string.IsNullOrEmpty(craft.EditorCraft))
            {
                // Saved-but-not-yet is the common case and deserves the specific
                // sentence: the craft is right there on screen, so "nothing to send"
                // would read as a bug.
                UIF.Muted(card, "Save '" + craft.EditorCraft +
                                "' in KSP first. There's no file to send yet.").Body();
            }
            else
            {
                UIF.Muted(card, "Fly a vessel, or open a saved craft in the editor, to send it.").Body();
            }

            picker.Build(card, "No other players found to send to.");

            bool ready = kind != null && picker.HasSelection && !Busy;
            string label = Busy ? "Sending…"
                         : picker.HasSelection ? "Send to " + picker.SelectedName
                         : "Pick a player";

            UIF.Button(card, label, () =>
            {
                string k = craft.SendKind;
                if (k == null || !picker.HasSelection) return;

                var done = Begin(CardSend);
                mod.RunCoroutine(ToolActions.QuicksendCurrent(
                    picker.SelectedId, picker.SelectedName, k, done));
            }, BtnStyle.Primary, 30).Interactable(ready);

            if (statusOwner == CardSend) DrawStatus(card);
        }

        // ── Export ──────────────────────────────────────────────────────────

        // ── Roster repair ───────────────────────────────────────────────────

        /// <summary>Only drawn when the roster actually holds a kerbal whose profession
        /// nothing installed defines — a card that says "nothing is wrong" is a card that
        /// teaches the player to ignore this one.</summary>
        private void BuildRosterRepair(El parent)
        {
            var card = UIF.Card(parent, "RosterRepair").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, brokenCrew + " kerbal(s) with an unknown profession",
                      Theme.FontSm).Bold();
            UIF.Muted(card,
                "A mod that added their profession is no longer installed. KSP throws while " +
                "drawing any crew list they appear in, so the Astronaut Complex and crew " +
                "assignment stay broken until this is dealt with. Fixing gives them a local " +
                "profession and remembers the original — reinstall the mod and they get it back.")
               .Body();

            UIF.Button(card, "Fix professions", () =>
            {
                var done = Begin(CardRoster);
                string message = TraitRepair.Repair();
                ReadBrokenCrew();
                done(brokenCrew == 0, message);
                MarkDirty();
            }, BtnStyle.Primary, 30).Interactable(!Busy);

            if (statusOwner == CardRoster) DrawStatus(card);
        }

        private void BuildExport(El parent)
        {
            var card = UIF.Card(parent, "Export").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Export flag-encoded craft", Theme.FontSm).Bold();
            UIF.Muted(card,
                "Writes the loaded craft with its custom flags, mod list and thumbnail baked " +
                "in, so they survive when you share the file.").Body();

            bool ready = craft.EditorSaved && !string.IsNullOrEmpty(craft.EditorCraft) && !Busy;

            UIF.Button(card, ready ? "Export " + craft.EditorCraft : "Open a saved craft in KSP", () =>
            {
                // Synchronous: local file IO only, so the completion runs in the same
                // frame and Busy never actually reads true here.
                var done = Begin(CardExport);
                bool ok = ToolActions.ExportCurrentCraft(out string message);
                done(ok, ok ? "Saved to " + message : message);
            }, BtnStyle.Secondary, 30).Interactable(ready);

            if (statusOwner == CardExport) DrawStatus(card);
        }

        // ── Flag import ─────────────────────────────────────────────────────

        private void BuildImportFlag(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "ImportFlag").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Import a flag", Theme.FontSm).Bold();
            UIF.Muted(card, "Paste a public PNG or JPEG link to add it to your in-game flag picker.")
               .Body();

            Btn import = null;
            System.Action rearm = () => Rearm(import, FlagReady());

            var urlField = UIF.TextField(card, flagUrl, "https://example.com/flag.png");
            urlField.OnChanged(s => { flagUrl = s; rearm(); });

            var nameField = UIF.TextField(card, flagName, "Name (optional)");
            nameField.OnChanged(s => flagName = s);

            import = UIF.Button(card, Busy ? "Importing…" : "Import flag", () =>
            {
                string url = (flagUrl ?? "").Trim();
                if (url.Length == 0) return;

                var done = Begin(CardFlag);
                mod.RunCoroutine(ToolActions.ImportFlag(url, (flagName ?? "").Trim(), (ok, message) =>
                {
                    // Clear on success only: a rejected URL is the one the player
                    // needs in front of them to fix.
                    if (ok) { flagUrl = ""; flagName = ""; }
                    done(ok, message);
                }));
            }, BtnStyle.Secondary, 30).Interactable(FlagReady());

            if (statusOwner == CardFlag) DrawStatus(card);
        }

        private bool FlagReady() => (flagUrl ?? "").Trim().Length > 0 && !Busy;

        // ── Bug report ──────────────────────────────────────────────────────

        /// <summary>
        /// Report a bug without leaving the game, with KSP.log attached.
        ///
        /// It lives here rather than as a Discord command for one reason: the log.
        /// Nearly every KSP bug is undiagnosable without it and nobody is going to
        /// find, trim and upload a 200 MB file by hand — but only the mod can read it,
        /// and only from inside the running game. The attach switch stays visible and
        /// per-report because that file describes the player's machine, not just the
        /// bug.
        /// </summary>
        private void BuildBugReport(El parent, GeneKermanMod mod)
        {
            var card = UIF.Card(parent, "BugReport").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Report a bug", Theme.FontSm).Bold();
            UIF.Muted(card, "Opens a private ticket in Discord. Replies come back to you there.")
               .Body();

            Btn send = null;
            Lbl hint = null;
            System.Action rearm = () =>
            {
                Rearm(send, BugReady());
                // Safe on a torn-down build: Lbl.Set goes through a Unity null check
                // on the text component, which a destroyed label fails.
                hint?.Set(BugHint());
            };

            var summaryField = UIF.TextField(card, bugSummary, "One line: what went wrong");
            summaryField.OnChanged(s => { bugSummary = s; rearm(); });

            var detailsField = UIF.TextField(card, bugDetails,
                "What you did, what happened, what you expected…", 88, true);
            detailsField.OnChanged(s => { bugDetails = s; rearm(); });

            UIF.Switch(card, "Attach KSP.log",
                       "Your game log: mod list, install paths and system specs. Almost every " +
                       "bug needs it to be fixable.",
                       bugAttachLog,
                       // MarkDirty is not optional here: UIF.Switch paints its state at
                       // build time and hands its handler a fixed !on, so without a
                       // rebuild the knob never moves and a second click resends the
                       // first click's value.
                       v => { bugAttachLog = v; MarkDirty(); });

            // Why Send is dark, said before it is pressed rather than after. Silent
            // while the form is untouched: a disabled button on an empty form explains
            // itself, and a requirement stated before anything is typed reads as a
            // complaint about not having typed yet.
            hint = UIF.Muted(card, BugHint(), Theme.FontXs).Body();

            send = UIF.Button(card, Busy ? "Sending…" : "Send report", () =>
            {
                var done = Begin(CardBug);
                mod.RunCoroutine(ToolActions.SubmitBugReport(bugSummary, bugDetails, bugAttachLog,
                    (ok, message) =>
                    {
                        // Same rule as the flag URL: clear on success only. A report the
                        // server rejected is the text the player needs back to fix it.
                        if (ok) { bugSummary = ""; bugDetails = ""; }
                        done(ok, message);
                    }));
            }, BtnStyle.Secondary, 30);
            send.Interactable(BugReady());

            // Under the button, not above it: this is a consequence of pressing Send,
            // not another rule about filling the form in, and it has to be the last
            // thing read before the press rather than one more line to scroll past.
            // Red because it is the only text on this card that is about the player's
            // account rather than about the bug — the report channel is anonymous
            // enough to feel free, and it is not.
            UIF.Label(card,
                      "Misuse may result in a temporary suspension from Boundless Missions services.",
                      Theme.FontXs, Theme.Lighten(Theme.Status("danger"), 0.35f))
               .Body();

            if (statusOwner == CardBug) DrawStatus(card);
        }

        private bool BugReady() => (bugSummary ?? "").Trim().Length >= ToolActions.MinBugSummary
                                && (bugDetails ?? "").Trim().Length >= ToolActions.MinBugDetails
                                && !Busy;

        /// <summary>
        /// What is still short of ToolActions' floors, or "" when the report will be
        /// taken. Empty on an untouched form as well: the button is dark there for the
        /// obvious reason, and a rule quoted at someone who has not typed anything yet
        /// is noise.
        /// </summary>
        private string BugHint()
        {
            int s = (bugSummary ?? "").Trim().Length;
            int d = (bugDetails ?? "").Trim().Length;
            if (s == 0 && d == 0) return "";

            bool shortSummary = s < ToolActions.MinBugSummary;
            bool shortDetails = d < ToolActions.MinBugDetails;
            if (shortSummary && shortDetails)
                return "Summary needs " + ToolActions.MinBugSummary + "+ characters, details " +
                       ToolActions.MinBugDetails + "+.";
            if (shortSummary)
                return "Summary needs " + ToolActions.MinBugSummary + "+ characters.";
            if (shortDetails)
                return "Details need " + ToolActions.MinBugDetails + "+ characters.";
            return "";
        }

        /// <summary>
        /// Re-arm a button whose enabled state depends on what has been *typed*.
        ///
        /// A keystroke deliberately does not MarkDirty — a rebuild destroys the box
        /// being typed into, caret and all, which is why SidebarPanel.Tick defers one
        /// until the player stops. So a `ready` evaluated while building the card stays
        /// frozen at what it was when the card was drawn: both boxes empty, hence a dead
        /// button. Anything else marking the panel dirty (the attach switch, a craft
        /// change in the editor) would silently "fix" it on the next rebuild, which is
        /// exactly how this presented — Send only came alive after toggling the switch.
        /// </summary>
        private static void Rearm(Btn btn, bool ready)
        {
            // The button belongs to a build that may already have been torn down; its
            // fields go with it, so this normally can't fire, but a click handler
            // running one frame late must not touch a destroyed component.
            if (btn == null || btn.Button == null) return;
            btn.Interactable(ready);
        }

        // ── Plumbing ────────────────────────────────────────────────────────

        /// <summary>BeginAction, plus a note of which card the result belongs under.</summary>
        private System.Action<bool, string> Begin(int owner)
        {
            statusOwner = owner;
            return BeginAction();
        }

        private void ReadCraft()
        {
            craft = ToolActions.ReadCraftState();
            nextCraftRead = Time.unscaledTime + CraftPollSeconds;
        }

        protected override void Poll()
        {
            picker.EnsureLoaded();
            picker.Tick();

            if (Time.unscaledTime < nextCraftRead) return;
            ReadCraft();

            // Only the parts that are on screen. Comparing whole states would redraw
            // for a part count that changes with every click in the VAB.
            string signature = craft.ActiveVessel + "|" + craft.EditorCraft + "|" +
                               craft.EditorType + "|" + craft.EditorParts + "|" + craft.EditorSaved;
            if (signature == craftSignature) return;

            craftSignature = signature;
            MarkDirty();
        }

        internal override void OnShown()
        {
            picker.Attach(MarkDirty);
            picker.Reset();
            ClearStatus();
            statusOwner = -1;
            nextCraftRead = 0f;
            ReadCraft();
            ReadBrokenCrew();
        }

        /// <summary>Count the kerbals with an unresolvable profession, once per showing
        /// rather than per rebuild. A roster only breaks between sessions (a mod removed
        /// while the game was closed), so opening the panel is exactly often enough, and
        /// the scan does a config lookup per kerbal — not something to run at frame rate.</summary>
        private void ReadBrokenCrew()
        {
            brokenCrew = TraitRepair.BrokenCrew().Count;
        }

        // The picker downloads avatars and owns those textures. Both of these are
        // where they stop being valid: Unity destroys textures across a scene load,
        // and a hidden panel has no business holding a roster's worth of them.
        internal override void OnHidden() => picker.Dispose();

        internal override void OnSceneChanged() => picker.Dispose();
    }
}
