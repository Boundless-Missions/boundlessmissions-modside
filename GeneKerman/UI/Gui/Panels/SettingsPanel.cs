/*
 * UI/Gui/Panels/SettingsPanel.cs – The mod's settings, in uGUI.
 *
 * WebUI/src/screens/Settings.tsx is the visual specification. The behaviour is not
 * re-implemented: ApiClient.SetOfficialServer/SetCustomServer/NormalizeServerUrl and
 * GeneKermanMod.OnServerChanged already *are* the shared implementation that the
 * browser UI reaches through /gk/settings and the classic window calls directly.
 * This panel is a third caller of the same four methods, so the three front ends
 * cannot drift apart.
 *
 * Two things here are specific to being in-game rather than in a browser tab:
 *
 *  1. The address box takes a control lock while it has focus (SidebarController's
 *     TextLockMask). A browser tab has the player's keyboard to itself; a Canvas
 *     over the flight scene does not.
 *  2. Turning data sharing off makes the mod inert, and SidebarController.ShouldRender
 *     watches that flag — so the last thing that button does is delete the panel it
 *     was pressed in. Hence the confirm step, and hence the note that turning it back
 *     on happens in the classic window (rule 8.2: opting back in is a consent act and
 *     belongs beside the text that says what is shared).
 */

using System;

namespace GeneKerman.UI.Gui
{
    internal sealed class SettingsPanel : SidebarPanel
    {
        public override string Title => "Settings";

        /// <summary>
        /// The address box's contents, kept out here rather than read back off the
        /// field: a rebuild destroys the field, and the draft has to survive that.
        /// Seeded once per opening, never re-seeded, so a poll cannot yank characters
        /// out from under someone mid-type.
        /// </summary>
        private string urlDraft = "";
        private bool draftSeeded;

        private bool dataOffConfirm;

        // Snapshot of what the last Rebuild drew, so Poll can notice another front
        // end changing the same settings underneath us.
        private bool lastOfficial;
        private bool lastLinked;
        private bool lastNotifications;
        private bool lastPhotos;
        private bool lastWebUi;
        private string lastServer = "";

        protected override void Rebuild()
        {
            var mod = GeneKermanMod.Instance;
            var api = mod?.Api;
            if (api == null) return;

            Snapshot(api);
            if (!draftSeeded)
            {
                urlDraft = api.CustomServerUrl ?? "";
                draftSeeded = true;
            }

            var col = UIF.Box(Host, "Settings").Column(Theme.Space2).Flex(1f, 1f);

            // No Refresh: everything on this screen is read straight out of the
            // running mod, so there is nothing to re-fetch.
            UIF.PanelHeader(col, "Settings", null);

            El body;
            UIF.ScrollView(col, out body).Flex(1f, 1f);

            BuildServerCard(body, mod, api);
            BuildInterfaceCard(body, mod, api);
            BuildBehaviourCard(body, mod, api);
            BuildAboutCard(body);
        }

        // ── Server ──────────────────────────────────────────────────────────

        private void BuildServerCard(El parent, GeneKermanMod mod, ApiClient api)
        {
            var card = UIF.Card(parent, "Server").Column(Theme.Space2).Pad(Theme.Space3);
            UIF.Label(card, "Server", Theme.FontSm).Bold();
            UIF.Muted(card,
                "The official server, or your own if you are running one. Each server issues " +
                "its own login, so the mod remembers them separately. Switching back does not " +
                "mean linking again.").Body();

            var choices = UIF.Box(card, "Choices").Row(Theme.Space2);
            UIF.Choice(choices, "Official server", HostOf(ApiClient.OfficialServerUrl),
                       api.UseOfficialServer, () => ApplyServer(mod, api, true))
               .PrefW(0).Flex(1f);
            UIF.Choice(choices, "Custom server", HostOf(api.CustomServerUrl),
                       !api.UseOfficialServer, () => ApplyServer(mod, api, false))
               .PrefW(0).Flex(1f);

            if (!api.UseOfficialServer)
            {
                UIF.Muted(card, "ADDRESS");

                var row = UIF.Box(card, "Address").Row(Theme.Space2).H(30);
                var field = UIF.TextField(row, urlDraft, "localhost:5022");
                field.E.PrefW(0).Flex(1f);
                field.OnChanged(s => urlDraft = s);
                field.OnSubmit(s => { urlDraft = s; ApplyServer(mod, api, false); });
                field.Interactable(!Busy);

                UIF.Button(row, "Connect", () => ApplyServer(mod, api, false), BtnStyle.Primary, 30)
                   .Interactable(!Busy)
                   .E.W(76);

                UIF.Muted(card,
                    "Host and port only, like localhost:5022. http:// is assumed if you leave the " +
                    "scheme off.").Body();
            }

            // Where the mod is actually pointing, as opposed to which button looks
            // selected. They differ for exactly as long as a switch is in flight, and
            // that is the moment the player most wants to know.
            var strip = UIF.Box(card, "Connected").Column(1).Pad(Theme.Space2)
                           .Bg(Theme.Alpha(Theme.Muted, 0.5f), Theme.RadiusSm, Theme.Border);
            // Ellipsis, not wrap: a URL has no spaces to break at, so wrapping it
            // would simply run past the strip's edge.
            UIF.Label(strip, api.ServerUrl, Theme.FontXs).Ellipsis();
            UIF.Muted(strip, api.IsLinked ? "linked as " + Username(mod) : "not linked");

            DrawStatus(card);
        }

        private void ApplyServer(GeneKermanMod mod, ApiClient api, bool official)
        {
            var done = BeginAction();

            bool changed;
            if (official)
            {
                changed = api.SetOfficialServer();
            }
            else
            {
                // Normalised here rather than inside SetCustomServer, so an unusable
                // address is explained instead of the box quietly snapping back.
                string url = ApiClient.NormalizeServerUrl(urlDraft, out string error);
                if (url == null) { done(false, error); return; }

                changed = api.SetCustomServer(url);
                urlDraft = api.CustomServerUrl ?? urlDraft;
            }

            if (!changed) { done(true, "Already connected to that server."); return; }

            // The one call that brings the rest of the mod in line: the notification
            // socket is still holding a connection to the old host, the new server has
            // its own version gate, and its own idea of whether we are linked.
            mod.OnServerChanged();

            done(true, api.IsLinked
                ? "Connected to " + api.ServerUrl + " as " + Username(mod) + "."
                : "Now pointing at " + api.ServerUrl +
                  ". This server has not seen you yet, so the link window is waiting in KSP.");
        }

        // ── Interface ───────────────────────────────────────────────────────
        //
        // Moved here from the classic window's settings tab (MainWindow.cs), because
        // this panel is now what the toolbar button opens: a setting that decides
        // what that button does had become one you could only reach through the
        // window it was steering you away from.

        private void BuildInterfaceCard(El parent, GeneKermanMod mod, ApiClient api)
        {
            var card = UIF.Card(parent, "Interface").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(card, "Interface", Theme.FontSm).Bold();

            UIF.Switch(card, "Open in my web browser",
                       "The toolbar button opens the interface in a browser tab instead of this " +
                       "sidebar. Served from this PC only (127.0.0.1); nothing is exposed to your " +
                       "network. Best with two monitors or in windowed mode.",
                       api.WebUiEnabled,
                       v => { mod.SetUiMode(v); MarkDirty(); });

            UIF.Divider(card);

            // The classic window is not a fallback here, it is the rest of the mod, and
            // the sidebar says so where something is missing. That list keeps shrinking:
            // issuing a rescue and spawning its wreck both live in the sidebar now, so
            // submission — which needs the HUD hidden for a screenshot — is what is left.
            UIF.Muted(card,
                "The classic window has the flows this sidebar does not carry — " +
                "submitting work.").Body();
            UIF.Button(card, "Open the classic window", mod.OpenClassicWindow, BtnStyle.Ghost, 28);
        }

        // ── In-game behaviour ───────────────────────────────────────────────

        private void BuildBehaviourCard(El parent, GeneKermanMod mod, ApiClient api)
        {
            var card = UIF.Card(parent, "Behaviour").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(card, "In-game behaviour", Theme.FontSm).Bold();

            UIF.Switch(card, "Notification popups",
                       "Toasts over the game when something happens.",
                       api.NotificationsEnabled,
                       v => { api.SetNotificationsEnabled(v); MarkDirty(); });

            UIF.Switch(card, "Milestone photo prompts",
                       "Offers a hero shot on a rendezvous, flyby or asteroid encounter.",
                       api.CheckpointPhotosEnabled,
                       v => { api.SetCheckpointPhotosEnabled(v); MarkDirty(); });

            UIF.Switch(card, "Emergency freeze on rescues",
                       "Stranded crew consume no life support until you reach them, whichever " +
                       "LS mod either of you runs. Off means they starve on your schedule.",
                       api.EmergencyFreezeEnabled,
                       v => { api.SetEmergencyFreezeEnabled(v); MarkDirty(); });

            UIF.Divider(card);

            // Only the "on" state is drawn: with data sharing off the sidebar does not
            // render at all (SidebarController.ShouldRender), so an off-state row here
            // would be unreachable by construction.
            var block = UIF.Box(card, "DataSharing").Column(Theme.Space2);
            UIF.Label(block, "Data sharing", Theme.FontSm).Bold();

            if (!dataOffConfirm)
            {
                UIF.Muted(block,
                    "On. Turning it off makes the mod inert immediately: nothing is collected " +
                    "or sent.").Body();
                UIF.Button(block, "Turn off data sharing",
                           () => { dataOffConfirm = true; MarkDirty(); }, BtnStyle.Ghost, 28);
                return;
            }

            UIF.Muted(block,
                "The mod goes inert and this sidebar closes with it. Turning it back on is a " +
                "consent decision and lives in the classic window, next to the panel that says " +
                "what gets sent.").Body();

            var confirm = UIF.Box(block, "ConfirmRow").Row(Theme.Space2).H(28);
            UIF.Button(confirm, "Turn off", () =>
            {
                dataOffConfirm = false;
                // Last statement on purpose: this disables the canvas this button is
                // drawn on, so nothing after it is guaranteed to run in a live panel.
                mod.SetDataGatheringEnabled(false);
            }, BtnStyle.Destructive, 28).E.Flex(1f);
            UIF.Button(confirm, "Cancel", () => { dataOffConfirm = false; MarkDirty(); },
                       BtnStyle.Ghost, 28).E.Flex(1f);
        }

        // ── About ───────────────────────────────────────────────────────────

        private static void BuildAboutCard(El parent)
        {
            // No "update required" line, unlike the web screen: the update gate hides
            // the whole sidebar (ShouldRender again), so it could never be seen here.
            var card = UIF.Card(parent, "About").Column(0).Pad(Theme.Space3);
            var row = UIF.Box(card, "Version").Row(Theme.Space2).H(20);
            UIF.Muted(row, "MOD VERSION");
            UIF.Grow(row);
            UIF.Label(row, ModVersion.Current, Theme.FontSm).Align(TextAlign.Right);
        }

        // ── Bookkeeping ─────────────────────────────────────────────────────

        private static string Username(GeneKermanMod mod)
            => string.IsNullOrEmpty(mod?.LinkedUsername) ? "your account" : mod.LinkedUsername;

        /// <summary>The scheme is noise in a 170px card. Falls back to the raw string
        /// for anything that will not parse, which is what a half-typed address is.</summary>
        private static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "not set";
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Authority : url;
        }

        private void Snapshot(ApiClient api)
        {
            lastOfficial = api.UseOfficialServer;
            lastLinked = api.IsLinked;
            lastNotifications = api.NotificationsEnabled;
            lastPhotos = api.CheckpointPhotosEnabled;
            lastWebUi = api.WebUiEnabled;
            lastServer = api.ServerUrl ?? "";
        }

        protected override void Poll()
        {
            var api = GeneKermanMod.Instance?.Api;
            if (api == null) return;

            // These are all writable from the classic window and the browser UI too,
            // and linked flips on its own when a link completes. Comparing is how a
            // retained panel notices a change it did not make.
            if (api.UseOfficialServer != lastOfficial ||
                api.IsLinked != lastLinked ||
                api.NotificationsEnabled != lastNotifications ||
                api.CheckpointPhotosEnabled != lastPhotos ||
                api.WebUiEnabled != lastWebUi ||
                (api.ServerUrl ?? "") != lastServer)
            {
                MarkDirty();
            }
        }

        internal override void OnShown()
        {
            // Re-seed the address from the mod's saved value: whatever was half-typed
            // last time is not what the player means to send now.
            draftSeeded = false;
            dataOffConfirm = false;
            ClearStatus();
        }
    }
}
