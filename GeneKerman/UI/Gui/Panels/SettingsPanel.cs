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

        /// <summary>Reachable under the version gate, and the reason to be there:
        /// pointing the mod at a server that accepts this build is how a player gets
        /// out of limited mode without restarting.</summary>
        internal override bool WorksOffline => true;

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
        private bool lastHideDetails;
        private bool lastStreamerMode;
        private string lastDetectedApp = "";
        private string lastServer = "";
        // The corp-ping switch is drawn from the profile blob, which arrives (and is
        // re-fetched) on ClientState's schedule rather than this panel's — so it is
        // watched the same way a link completing is.
        private bool lastProfileLoaded;
        private bool lastProfileLoading;
        private bool lastCorpPings;

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
            UIF.ScrollView(col, out body, "settings").Flex(1f, 1f);

            BuildServerCard(body, mod, api);
            BuildInterfaceCard(body, mod, api);
            BuildPrivacyCard(body, api);
            BuildBehaviourCard(body, mod, api);
            BuildDiscordCard(body, mod, api);
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
            // Whatever was in flight belonged to the old host and its callbacks are
            // dropped, so the caches and their loading flags have to be reset by hand.
            mod.State?.ServerChanged();

            done(true, api.IsLinked
                ? "Connected to " + api.ServerUrl + " as " + Username(mod) + "."
                : "Now pointing at " + api.ServerUrl +
                  ". This server has not seen you yet, so the link window is waiting in KSP.");
        }

        // ── Interface ───────────────────────────────────────────────────────
        //
        // Moved here from the classic window's settings tab, back when there was one, because
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

        }

        // ── Privacy ─────────────────────────────────────────────────────────
        //
        // Its own card rather than a row in "In-game behaviour": these two are about
        // what other people can see over your shoulder or on a stream, which is a
        // different question from how the mod behaves in the game.

        private void BuildPrivacyCard(El parent, ApiClient api)
        {
            var card = UIF.Card(parent, "Privacy").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(card, "Privacy", Theme.FontSm).Bold();

            UIF.Switch(card, "Hide profile pictures and corp names",
                       "Player lists show display names only. Pictures are not just hidden but " +
                       "never downloaded, so nothing on this PC asks Discord for them.",
                       api.HidePlayerDetails,
                       v => { api.SetHidePlayerDetails(v); MarkDirty(); });

            UIF.Switch(card, "Streamer mode",
                       "Turns the switch above on by itself while OBS, Streamlabs, XSplit or " +
                       "similar is running. Checks the names of programs running on this PC " +
                       "every few seconds and nothing else: no window titles, nothing sent " +
                       "anywhere. While this is off, nothing is checked at all.",
                       api.StreamerModeEnabled,
                       v => { api.SetStreamerModeEnabled(v); MarkDirty(); });

            if (!api.StreamerModeEnabled) return;

            // What it currently sees. Without this the switch is a promise with no
            // evidence — and "running" is genuinely all it knows: no OS says whether
            // a window is being captured, so an open OBS you are not streaming with
            // counts, and a capture card on another PC does not.
            string app = StreamerMode.DetectedApp;
            var strip = UIF.Box(card, "Detected").Column(1).Pad(Theme.Space2)
                           .Bg(Theme.Alpha(Theme.Muted, 0.5f), Theme.RadiusSm, Theme.Border);
            if (app != null)
            {
                UIF.Label(strip, app + " is running", Theme.FontXs, Theme.AccentForeground).Body();
                UIF.Muted(strip, api.HidePlayerDetails
                          ? "Details are hidden by the switch above anyway."
                          : "Profile pictures and corp names are hidden while it is.").Body();
            }
            else
            {
                UIF.Muted(strip, "No broadcasting software running.", Theme.FontXs).Body();
            }
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

            // Hidden while the server refuses hero-shot uploads (ApiClient
            // .CheckpointPhotosAvailable): with the feature held off, this switch would
            // be a control over nothing. The stored preference survives, so restoring
            // the gate restores each player's own setting rather than a default.
            if (ApiClient.CheckpointPhotosAvailable)
            {
                UIF.Switch(card, "Milestone photo prompts",
                           "Offers a hero shot on a rendezvous, flyby or asteroid encounter.",
                           api.CheckpointPhotosEnabled,
                           v => { api.SetCheckpointPhotosEnabled(v); MarkDirty(); });
            }

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
                "consent decision, so it lives in the paused notice KSP shows in place of " +
                "everything else, which also says what gets sent.").Body();

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

        // ── Discord ─────────────────────────────────────────────────────────
        //
        // The one card on this screen that is not a local setting. Everything above
        // is written to settings.cfg and takes effect here; this is stored on the
        // account, because the thing it controls — the @-mention the bot puts on a
        // corp-channel post — is written by the server, and a file on this PC has no
        // say in it. Hence the round trip, the Busy gate and the status line, none of
        // which the local switches need.

        private void BuildDiscordCard(El parent, GeneKermanMod mod, ApiClient api)
        {
            // Nothing to draw before there is an account to draw it for: unlinked,
            // and under the version gate where the profile fetch comes back 426,
            // this would be a switch over a value nobody has read.
            var state = mod != null ? mod.State : null;
            if (!api.IsLinked || state == null) return;

            var card = UIF.Card(parent, "Discord").Column(Theme.Space1).Pad(Theme.Space3);
            UIF.Label(card, "Discord", Theme.FontSm).Bold();

            if (state.ProfileData == null)
            {
                // Deliberately a line rather than a switch left at its default: a
                // switch drawn before the value arrives shows the *default*, and a
                // player who had turned this off would watch it flip under them.
                UIF.Muted(card, state.ProfileLoading
                          ? "Loading your account settings…"
                          : "Account settings are not loaded yet.").Body();
                if (!state.ProfileLoading)
                    UIF.Button(card, "Retry", () => state.RequestProfileRefresh(), BtnStyle.Ghost, 28);
                DrawStatus(card);
                return;
            }

            bool on = state.CorpPings;
            UIF.Switch(card, "Mention me in my corporation channel",
                       "Contract offers, disputes and hand-offs are posted to your corp channel " +
                       "with a ping so you see them. Off, the same posts still arrive and still " +
                       "say who they are for. Discord just will not notify you, so you would be " +
                       "reading the channel yourself. The in-game notifications are unaffected.",
                       on,
                       v => ApplyCorpPings(state, api, v));

            DrawStatus(card);
        }

        private void ApplyCorpPings(ClientState state, ApiClient api, bool enabled)
        {
            if (Busy) return;
            var done = BeginAction();

            // Optimistic, then corrected: the switch moves now and the server's answer
            // is what it settles on. `SetCorpPings` echoes the stored value back on
            // success and the *previous* one on failure, so both paths end with the
            // panel showing what is actually saved rather than what was asked for.
            state.NoteCorpPings(enabled);
            MarkDirty();

            GeneKermanMod.Instance.RunCoroutine(api.SetCorpPings(enabled, (ok, stored, err) =>
            {
                state.NoteCorpPings(stored);
                done(ok, ok
                     ? (stored ? "You will be mentioned in your corp channel."
                               : "Corp channel posts will no longer mention you.")
                     : (err ?? "Could not save the setting."));
            }));
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

        /// <summary>Whether the account preferences this panel draws have moved since
        /// the last Rebuild — the profile landing, being re-fetched, or another front
        /// end flipping the same switch.</summary>
        private bool ProfileChanged()
        {
            var state = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.State : null;
            return (state != null && state.ProfileData != null) != lastProfileLoaded
                || (state != null && state.ProfileLoading) != lastProfileLoading
                || (state == null || state.CorpPings) != lastCorpPings;
        }

        private void Snapshot(ApiClient api)
        {
            lastOfficial = api.UseOfficialServer;
            lastLinked = api.IsLinked;
            lastNotifications = api.NotificationsEnabled;
            lastPhotos = api.CheckpointPhotosEnabled;
            lastWebUi = api.WebUiEnabled;
            lastHideDetails = api.HidePlayerDetails;
            lastStreamerMode = api.StreamerModeEnabled;
            lastDetectedApp = StreamerMode.DetectedApp ?? "";
            lastServer = api.ServerUrl ?? "";

            var state = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.State : null;
            lastProfileLoaded = state != null && state.ProfileData != null;
            lastProfileLoading = state != null && state.ProfileLoading;
            lastCorpPings = state == null || state.CorpPings;
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
                api.HidePlayerDetails != lastHideDetails ||
                api.StreamerModeEnabled != lastStreamerMode ||
                // Not a setting and not something the player did: OBS starting is a
                // change this panel has to notice from the outside, same as a link
                // completing.
                (StreamerMode.DetectedApp ?? "") != lastDetectedApp ||
                (api.ServerUrl ?? "") != lastServer ||
                ProfileChanged())
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
