/*
 * UI/Gui/Panels/ToolsPanel.cs – Quicksend, craft export and flag import, in uGUI.
 *
 * WebUI/src/screens/Tools.tsx is the visual specification; ToolActions.cs is the
 * implementation, unchanged and shared — this panel calls exactly what /gk/actions/*
 * calls, so a fix to the flag guards or the send payload lands in all three front ends
 * at once. The craft-state read moved into ToolActions with this slice for the same
 * reason: it used to be private to the bridge's routes.
 *
 * The one structural difference from the web screen: three cards, one status line.
 * SidebarPanel carries a single Status because only one action can be in flight at a
 * time anyway; `statusOwner` decides which card it is drawn under, so the result still
 * appears where the button that caused it is.
 */

using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class ToolsPanel : SidebarPanel
    {
        public override string Title => "Tools";

        private const int CardSend = 0;
        private const int CardExport = 1;
        private const int CardFlag = 2;

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

            if (!mod.Api.IsLinked)
            {
                UIF.Notice(col, "Not linked to a Discord account.",
                           "Link this install from the classic window to use these tools.");
                return;
            }

            El body;
            UIF.ScrollView(col, out body).Flex(1f, 1f);

            BuildQuicksend(body, mod);
            BuildExport(body);
            BuildImportFlag(body, mod);
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

            var urlField = UIF.TextField(card, flagUrl, "https://example.com/flag.png");
            urlField.OnChanged(s => flagUrl = s);

            var nameField = UIF.TextField(card, flagName, "Name (optional)");
            nameField.OnChanged(s => flagName = s);

            bool ready = !string.IsNullOrEmpty((flagUrl ?? "").Trim()) && !Busy;

            UIF.Button(card, Busy ? "Importing…" : "Import flag", () =>
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
            }, BtnStyle.Secondary, 30).Interactable(ready);

            if (statusOwner == CardFlag) DrawStatus(card);
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
        }

        // The picker downloads avatars and owns those textures. Both of these are
        // where they stop being valid: Unity destroys textures across a scene load,
        // and a hidden panel has no business holding a roster's worth of them.
        internal override void OnHidden() => picker.Dispose();

        internal override void OnSceneChanged() => picker.Dispose();
    }
}
