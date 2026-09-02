/*
 * GeneKermanMod.cs – Main KSP addon entry point.
 *
 * Lifecycle:
 *   - Starts at MainMenu, persists across all scenes
 *   - Initializes toolbar button, API client, notification polling
 *   - Manages state: Unlinked → Linking → Linked
 *   - Delegates UI rendering to window classes
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KSP.UI.Screens;
using UnityEngine;

namespace GeneKerman
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class GeneKermanMod : MonoBehaviour
    {
        // Singleton
        public static GeneKermanMod Instance { get; private set; }

        // Components
        public ApiClient Api { get; private set; }

        // State
        public bool ShowLinkWindow { get; set; }
        public bool ShowConsentWindow { get; set; }     // first-run privacy/terms opt-in gate
        public bool ShowDataPausedWindow { get; set; }  // shown when data sharing is opted out
        public int UnreadNotifications { get; set; }
        public string LinkedUsername { get; private set; } = "";
        /// <summary>This client's own <b>account id</b> — the immutable key
        /// `data/accounts.py` assigns, not a display name. It exists because
        /// <see cref="LinkedUsername"/> cannot decide ownership of anything: a Discord
        /// display name is self-chosen, changeable and not unique, so deciding "these
        /// arriving kerbals are mine" on it let anyone rename themselves to a victim
        /// and have the victim's own crew adopted onto an incoming vessel (and did the
        /// same by accident between two players sharing a nickname). See
        /// <see cref="VesselTransfer.DecideComingHome"/>.
        ///
        /// Empty until the link response or the first profile fetch lands, and empty
        /// forever against a server too old to send `user_id` — which is why the
        /// decision falls back to the name rather than refusing.</summary>
        public string LinkedAccountId { get; private set; } = "";

        // Paths
        public static string ModPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BoundlessMissions");
        public static string PluginDataPath => Path.Combine(ModPath, "PluginData");

        // Internal
        private float lastNotificationCheck;
        private float notificationInterval = 600f; // 10 minutes (fallback poll only)
        private float lastImportCheck;
        private const float ImportInterval = 30f; // craft-import queue poll cadence
        // When the current scene finished loading (onLevelWasLoadedGUIReady), or -1
        // while one is still coming up. HighLogic.LoadedScene flips the moment a load
        // is *requested*, so a scene test alone runs work during the load itself: on
        // 2026-08-30 the import poll spawned two vessels 3.5 s into a Space Center
        // load, KSP's own startup then instantiated every proto again, and the save
        // ended up with two VESSEL nodes per pid — which Kerbalism refuses to load
        // ("An item with the same key has already been added"). The same early save
        // ran against an empty vessel list and marked live crew as missing.
        private float sceneGuiReadyAt = -1f;
        private const float SceneSettleSeconds = 2f;

        /// <summary>True once the current scene has finished loading and has had a
        /// moment to run its Start() pass — the earliest it is safe to add a vessel to
        /// the universe or write the save. False during any scene load.</summary>
        public static bool SceneSettled
        {
            get
            {
                var m = Instance;
                if (m == null || m.sceneGuiReadyAt < 0f) return false;
                if (Time.realtimeSinceStartup - m.sceneGuiReadyAt < SceneSettleSeconds) return false;
                if (HighLogic.LoadedSceneIsFlight && !FlightGlobals.ready) return false;
                return true;
            }
        }
        // Rescue-craft reconciliation: one GET against the contract list, only in a
        // scene where a removal could actually run. Cheap enough to do on arrival at
        // the Space Center (OnSceneChange resets the timer), rare enough not to matter
        // if the player then sits there.
        private float lastRescueReconcile;
        private bool rescueReconcileRunning;
        private const float RescueReconcileInterval = 300f;
        // One roster sweep per arrival in a scene where it can run (OnSceneChange clears
        // it), rather than per frame — it walks the whole roster.
        private bool rosterSwept;
        /// <summary>One notification fetch per arrival in a scene where a removal could
        /// actually run. See <see cref="FetchNotificationsOnSceneArrival"/>.</summary>
        private bool notifFetchedThisScene;
        // Unknown-profession warning: once per session, not once per Space Center visit.
        private bool traitWarningShown;
        private bool initialized;
        // Tracks Consent.Accepted across frames so a mid-session lapse (manual
        // consent.cfg edit/delete, or a server policy bump) is caught on its edge.
        private bool lastConsentOk = true;

        // Live notification push + de-dup of already-toasted notifications
        private NotificationSocket notifSocket;
        private readonly HashSet<string> seenNotifIds = new HashSet<string>();

        /// <summary>Notifications that arrived with an instruction this client could not
        /// carry out yet — see <see cref="NeedsLoadedSaveToAct"/>. Held rather than
        /// dropped, because the live socket pushes each one exactly ONCE and the fallback
        /// poll runs only while that socket is down: with it up (the normal case) a
        /// notification consumed at the wrong moment is gone for good. Drained by
        /// <see cref="DrainDeferredNotifications"/> as soon as a save is loaded.</summary>
        private readonly List<Dictionary<string, object>> deferredNotifs =
            new List<Dictionary<string, object>>();
        private bool notifBacklogSeeded; // first poll seeds the panel without toasting

        // Toolbar
        private ApplicationLauncherButton toolbarButton;
        private Texture2D toolbarIcon;

        // Everything the mod knows about the account, and every call that changes
        // it. Not a window: the classic IMGUI window that used to own this state was
        // replaced by the sidebar, and only its data half survived (ClientState.cs).
        private ClientState clientState;

        // UI Windows
        private UI.LinkWindow linkWindow;
        private UI.ConsentWindow consentWindow;
        private UI.DataPausedWindow dataPausedWindow;
        private UI.CheckpointPrompt checkpointPrompt;
        private UI.DeviceVerifyWindow deviceVerifyWindow;

        private UI.UpdateRequiredWindow updateWindow;
        private UI.SuspendedWindow suspendedWindow;

        // Loopback web bridge, live only while the browser UI is in use (enableWebUi
        // in settings.cfg). Binds 127.0.0.1 on an ephemeral port. See Web/LocalServer.cs.
        private Web.LocalServer webServer;

#if GK_DEBUG_PANEL
        /// <summary>
        /// The agent-drivable test bridge. COMPILED OUT of production builds — see
        /// Web/DebugBridge.cs for why it is a second listener rather than routes on
        /// webServer. Started unconditionally in a dev build: the whole point is
        /// inspecting a client that has neither the browser UI switched on nor a WebUI
        /// bundle installed, which is every sidebar test there is.
        /// </summary>
        private Web.DebugBridge debugBridge;
#endif
        private UI.WebUiWindow webUiWindow;

        // In-game uGUI sidebar (UI/Gui/). Additive: it does not replace the IMGUI
        // windows or the browser UI, and it draws on its own Canvas rather than in
        // OnGUI — which is why it needs its own hide/scene/teardown handling below.
        private UI.Gui.SidebarController sidebar;

        // The submission screen, mounted as a draggable window on the sidebar's
        // canvas (see UI/Gui/FloatWindow.cs). It replaced the IMGUI UI/SubmitWindow.cs
        // outright, so this is the only submission UI the mod has.
        private UI.Gui.SubmitPanel submitPanel;

        // The "ask for more time" form, mounted the same way and for a related
        // reason: it is read against the deadline it moves, and it used to expand a
        // month grid inside the dispute action bar.
        private UI.Gui.MoreTimePanel moreTimePanel;

        // Device binding: set while we're blocked on an unrecognized-device challenge.
        private bool deviceGateActive;
        private string deviceGateChallenge;

        // Version gate: set when the server reports this DLL is no longer the latest.
        // While true, every server-backed feature is suppressed.
        public bool UpdateRequired { get; private set; }
        public string LatestVersion { get; private set; } = "";
        public string UpdateDownloadUrl { get; private set; } = "";

        /// <summary>
        /// The update gate's "Download latest" button hands this straight to
        /// Application.OpenURL, which shells out through xdg-open / ShellExecute — so a
        /// server-supplied `file://`, UNC path or local executable path would be a
        /// launch primitive, behind a modal window the player has to act on to get the
        /// mod back. Only an absolute https URL is stored; anything else is dropped and
        /// the window shows the version text alone. Same reasoning, and the same shape,
        /// as GkRoutes' open-url allow-list for the browser bridge.
        /// </summary>
        private static string SafeDownloadUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            Uri uri;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            {
                Debug.LogWarning("[GeneKerman] Update: the server's download link is not an " +
                                 "absolute https URL; not offering it.");
                return "";
            }
            return uri.AbsoluteUri;
        }

        /// <summary>
        /// The player dismissed the update prompt with "Continue anyway". The gate is
        /// still on — every server call keeps failing 426 and the server keeps refusing
        /// this DLL — but the purely local parts of the mod (flag import, flag-encoded
        /// craft export) and the Settings tab become reachable again. That last part is
        /// the point: without it a client rejected by the official server has no way to
        /// switch to a server that would accept it, which is a dead end you can only
        /// escape by editing settings.cfg by hand.
        ///
        /// Session-only, deliberately: it is never written to settings.cfg, so every
        /// launch re-shows the prompt and the nag cannot be permanently silenced.
        /// </summary>
        public bool UpdateAcknowledged { get; private set; }

        // Suspension gate: the account is temporarily blocked from the services (the
        // server answers every gated request 403 `suspended`). Unlike the update gate
        // there is nothing the player can do about it here, so there is no
        // "continue anyway" — only a re-check and the expiry.
        public bool Suspended { get; private set; }
        public string SuspensionReason { get; private set; } = "";
        /// <summary>Unix seconds (server clock) when the suspension lifts; 0 if the
        /// server didn't say, in which case only a re-check can clear it.</summary>
        public double SuspendedUntil { get; private set; }

        /// <summary>Dismiss the update gate for this session and open the limited UI.</summary>
        public void AcknowledgeUpdate()
        {
            if (!UpdateRequired) return;
            UpdateAcknowledged = true;
            updateWindow.Hide();
            // The sidebar draws while the gate is acknowledged (SidebarController
            // .ShouldRender) and narrows itself to the panels that work without a
            // server — which is the whole point of "continue anyway".
            sidebar?.SetOpen(true);
            Debug.Log("[GeneKerman] Update gate acknowledged, limited (offline) features enabled.");
        }

        // Milestone hero-shot capture (rendezvous / flyby / asteroid)
        private CheckpointDetector checkpointDetector;
        private bool checkpointCapturing;

        // Player-composed achievement hero-shot capture (manual, Tools tab)
        private bool achievementCapturing;

        // Vessel+position keys already submitted for achievement review, so the same
        // craft isn't re-reviewed (or re-rewarded) at the same position. The position
        // is part of the key, so a vessel CAN be captured/rewarded again after moving
        // to a different body/situation. Persisted across sessions.
        private HashSet<string> reviewedCaptures;
        private string ReviewedCapturesPath => Path.Combine(PluginDataPath, "achievement_captures.txt");

        // True while the game UI is hidden (stock F2 toggle, or our own capture
        // firing onHideUI). Our windows are drawn in OnGUI, which UIMasterController
        // does NOT govern, so we must suppress them ourselves — otherwise the mod
        // windows leak into screenshots and cinematic captures.
        private bool uiHidden;

        // ── Unity Lifecycle ─────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("[GeneKerman] Mod initialized.");
        }

        void Start()
        {
            // Ensure directories exist
            Directory.CreateDirectory(PluginDataPath);

            // Initialize API client
            Api = new ApiClient();
            notifSocket = new NotificationSocket(Api);
            // Authenticate the notification WebSocket with a short-lived single-use
            // ticket (fetched over HTTPS) instead of putting the 30-day session token
            // in the WS URL, where it would end up in server/proxy access logs.
            notifSocket.TicketProvider = onTicket => StartCoroutine(Api.GetWsTicket(onTicket));

            // Initialize UI windows
            clientState = new ClientState();
            linkWindow = new UI.LinkWindow();
            consentWindow = new UI.ConsentWindow();
            dataPausedWindow = new UI.DataPausedWindow();
            checkpointPrompt = new UI.CheckpointPrompt();
            deviceVerifyWindow = new UI.DeviceVerifyWindow();
            updateWindow = new UI.UpdateRequiredWindow();
            suspendedWindow = new UI.SuspendedWindow();
            webUiWindow = new UI.WebUiWindow();
            checkpointDetector = new CheckpointDetector(OnCheckpoint)
            {
                IsCaptureEnabled = () => Api != null && Api.CheckpointPhotosEnabled,
            };
            checkpointDetector.Register();

#if GK_DEBUG_PANEL
            debugBridge = new Web.DebugBridge();
            debugBridge.Start();
#endif

            // Build the sidebar canvas once. Parented to this (DontDestroyOnLoad)
            // GameObject, so the hierarchy survives every scene change and only its
            // textures need regenerating — see SidebarController.OnSceneChange.
            sidebar = new UI.Gui.SidebarController();
            sidebar.Build(transform);
            // Order mirrors the browser UI's tab order so the two read the same.
            sidebar.AddPanel(new UI.Gui.ProfilePanel());
            sidebar.AddPanel(new UI.Gui.MissionsPanel());
            sidebar.AddPanel(new UI.Gui.ContractsPanel());
            sidebar.AddPanel(new UI.Gui.NotificationsPanel());
            sidebar.AddPanel(new UI.Gui.MarketPanel());
            sidebar.AddPanel(new UI.Gui.FinancePanel());
            // Next to Tools, which is where quicksend lives: the two are read
            // together — the send picks from this list, and a send that finds
            // nobody to pick sends the player straight here.
            sidebar.AddPanel(new UI.Gui.FriendsPanel());
            sidebar.AddPanel(new UI.Gui.ToolsPanel());
            sidebar.AddPanel(new UI.Gui.SettingsPanel());

            // Submission is a window rather than a tab: it is read *against* the craft
            // on the build stage or the ship in flight, so it has to be movable and to
            // stay up while the player looks at something else. Offset right of centre
            // so it does not open on top of the sidebar panel itself.
            submitPanel = new UI.Gui.SubmitPanel();
            sidebar.AddWindow(submitPanel, 470f, 660f, new Vector2(360f, 0f));

            // A window for the same reason, sized for a month grid: the dispute card
            // it came off could fit neither the calendar nor the deadline it moves.
            moreTimePanel = new UI.Gui.MoreTimePanel();
            sidebar.AddWindow(moreTimePanel, 430f, 560f, new Vector2(340f, 0f));

            // Detect installed life-support / DeepFreeze mods once (drives rescue-kerbal
            // immunity and the Kerbalism rescue gate). Reflection-only; safe if none present.
            LifeSupportRegistry.LogDetected();

            // Load toolbar icon
            LoadToolbarIcon();

            // Register for toolbar
            GameEvents.onGUIApplicationLauncherReady.Add(OnToolbarReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnToolbarDestroyed);

            // Invalidate UI textures on scene changes
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelGuiReady);

            // Hide our IMGUI windows whenever the game UI is hidden (F2 or a capture).
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);

            // Gate outdated clients: ask the server whether this DLL is the latest.
            // Suppressed (sends nothing) until first-run consent is given; re-run from
            // OnConsentGranted at that point.
            StartCoroutine(CheckVersionRoutine());

            // Periodic anti-tamper attestation (catches a DLL swapped mid-session).
            StartCoroutine(AttestationLoop());

            // If already linked, do an initial data fetch and open the live socket —
            // unless the user has opted out of data sharing, in which case the mod
            // stays inert until they re-enable it.
            if (Api.IsLinked && Api.DataGatheringEnabled)
            {
                StartCoroutine(InitialFetch());
                if (Api.NotificationsEnabled)
                    notifSocket.Connect();
            }

            initialized = true;
            lastNotificationCheck = Time.realtimeSinceStartup;
        }

        // ── Web UI bridge ───────────────────────────────────────────────────

        /// <summary>Bridge origin (no nonce) — safe to display and to log.</summary>
        public string WebBridgeUrl => webServer?.Url;

        /// <summary>True when the player has chosen the browser UI over the classic windows.</summary>
        public bool WebUiMode => Api != null && Api.WebUiEnabled;

        /// <summary>
        /// Starts the bridge if needed and opens (or re-opens) the UI in the browser.
        /// Returns false if the bridge could not start, in which case the caller must
        /// fall back to the classic UI rather than leave the player with nothing.
        /// </summary>
        private bool OpenWebUi()
        {
            if (webServer == null) webServer = new Web.LocalServer();

            string launchUrl = webServer.IsRunning ? webServer.NewLaunchUrl() : webServer.Start();
            if (launchUrl == null)
            {
                webServer = null;
                return false;
            }

            // launchUrl carries a single-use nonce — never log it. Players upload
            // KSP.log to Discord routinely, and this mod attaches it to reports.
            Application.OpenURL(launchUrl);

            // Always raise the in-game panel too: if OpenURL silently did nothing
            // (Flatpak/Proton without xdg-open, Steam overlay), this is the only way the
            // player learns where the UI actually is.
            webUiWindow.Visible = true;
            return true;
        }

        /// <summary>"Reopen in browser" from WebUiWindow.</summary>
        public void ReopenWebUi()
        {
            if (!OpenWebUi())
                ScreenMessages.PostScreenMessage("Boundless Missions: web UI unavailable, using the in-game panel.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Switch UI mode at runtime and persist it.</summary>
        public void SetUiMode(bool web)
        {
            Api.SetWebUiEnabled(web);
            if (web) return;

            // Leaving web mode: tear the bridge down rather than leave a listening
            // socket open for a UI nobody is using.
            webUiWindow.Visible = false;
            webServer?.Stop();
            webServer = null;
        }

        // The sidebar gets none of `uiHidden` for free: it renders on a Canvas, and
        // UIMasterController does not govern our Canvas any more than it governs
        // OnGUI. Without these two lines it appears in every screenshot, cinematic
        // capture and F2 press.
        private void OnHideUI() { uiHidden = true; sidebar?.SetHidden(true); }
        private void OnShowUI() { uiHidden = false; sidebar?.SetHidden(false); }

        private void OnSceneChange(GameScenes scene)
        {
            UI.GKSkin.Invalidate();
            // Same problem GKSkin solves, different resource: Unity destroys the
            // sidebar's textures on a scene load. The hierarchy survives, so this
            // regenerates sprites and re-binds them rather than rebuilding the tree.
            sidebar?.OnSceneChange();
            // Drop any in-flight prompt and clear SOI/proximity state so milestones
            // don't carry over (or re-fire) across a scene load.
            checkpointPrompt?.Dismiss(false);
            checkpointDetector?.Reset();
            // Check rescue bookkeeping once on arrival wherever we land, rather than
            // making the player wait out the interval after walking into the Space
            // Center — which is exactly where a leftover craft is visible and fixable.
            lastRescueReconcile = 0f;
            rosterSwept = false;
            notifFetchedThisScene = false;
            sceneGuiReadyAt = -1f;
        }

        private void OnLevelGuiReady(GameScenes scene)
        {
            sceneGuiReadyAt = Time.realtimeSinceStartup;
        }

        void Update()
        {
            // Hoisted above every early return below. The bridge must keep draining its
            // job queue even when the player is unlinked or data-sharing is paused —
            // otherwise a request thread blocks for the full 30s timeout, and the page
            // cannot even ask *why* it is unavailable.
            webServer?.Pump();
#if GK_DEBUG_PANEL
            // Its own queue, so a stalled browser request cannot starve the harness and
            // a stalled harness request cannot starve the browser.
            debugBridge?.Pump();
#endif

            // Hoisted for the same reason as Pump: the sidebar exists in every
            // scene and for unlinked clients too — its own gate cascade decides
            // whether it draws, and its slide/pulse must keep running either way.
            sidebar?.Tick();

            // Above every gate below, and deliberately: this decides what the UI
            // *hides*, while the gates decide what the mod *sends*. Nothing here
            // leaves the PC — it reads the local process list, and only while the
            // player has switched streamer mode on. A frame costs one null check
            // and a clock comparison; the scan itself is scheduled off-thread.
            StreamerMode.Tick();

            // Hoisted for the same reason as RescueImmunityGuardian below, and it used
            // to sit under all three gates: draining this queue is local bookkeeping
            // with no network in it, and by the time something is *in* the queue the
            // vessel already belongs to someone else and the player has been told it
            // will disappear. Unlinking, pausing data-sharing or a server policy bump
            // forcing re-consent must not strand a ship in a save it was promised to
            // leave — those gates govern what we *send*, not what we owe the player.
            // The scenario is the save-side memory every rescue guard reads (spawn
            // dedup, freeze records, removal queue) — self-heal it before anything
            // consults it. One null check per frame when healthy.
            if (initialized) GKContractScenario.EnsureExists();

            if (initialized) ProcessPendingRescueRemovals();

            // Same reasoning, and the same gates deliberately skipped: this only ever
            // deletes kerbals that belong to somebody else's save, and a roster it has
            // already polluted is what stops the Astronaut Complex offering new hires.
            if (initialized) SweepRosterOnce();

            // Above the link gate for the same reason: it draws map markers from the
            // contract list this client already holds, sends nothing, and — run
            // unconditionally — is also what takes a marker back down when a rescue
            // ends. Gated below the return, an unlink would strand one on the map.
            if (initialized) RescueWaypoints.Tick();

            if (!initialized) return;

            if (!Api.IsLinked)
            {
                // A dropped token is a dropped identity. The socket the server holds is
                // authenticated as whoever was linked when it opened, so one left alive
                // across an unlink keeps delivering that account's notifications into
                // whatever account links next on this install. ApiClient.ClearToken
                // closes it at the source; this is the backstop for any path that
                // clears the token without going through it.
                if (notifSocket != null && (notifSocket.IsEnabled || notifSocket.IsConnected))
                    notifSocket.Disconnect();
                return;
            }

            // Hold rescue-kerbal life-support immunity and perform handoff on contact.
            // Purely local (no network), so it runs even when data-sharing is opted out —
            // a stranded crew must not start starving just because telemetry is paused.
            RescueImmunityGuardian.Tick();

            // Data-sharing opt-out (rule 8.2): run inert — no polling, no checkpoint
            // detection, no imports — and make sure the live socket is closed.
            if (!Api.DataGatheringEnabled)
            {
                if (notifSocket.IsConnected)
                    notifSocket.Disconnect();
                return;
            }

            // Consent can lapse mid-session: consent.cfg edited/deleted (re-read live
            // by Consent), or a server policy bump. Catch the true→false edge, go
            // inert, and raise the re-accept gate. Stay inert until re-accepted.
            bool consentOk = Consent.Accepted;
            if (!consentOk && lastConsentOk)
                OnConsentLapsed();
            lastConsentOk = consentOk;
            if (!consentOk) return;

            // Suspension gate. Everything below this line talks to the server and would
            // come back 403, so it all stays off. Note what is deliberately *above* it
            // and still runs: rescue removals, the roster sweep and life-support
            // immunity — a suspension blocks the services, it does not reach into the
            // player's save to break the things already owed to them.
            if (Suspended)
            {
                if (notifSocket.IsConnected)
                    notifSocket.Disconnect();
                // Free ourselves when the clock says so, rather than waiting for a
                // restart or a button press. If the server disagrees — clock skew, or a
                // fresh suspension — the next request comes straight back 403 and the
                // gate returns, which makes this a retry rather than a decision.
                if (SuspendedUntil > 0 && UI.SuspendedWindow.SecondsLeft(SuspendedUntil) <= 0)
                    ClearSuspension();
                return;
            }

            // Watch for flight milestones worth a hero shot. Independent of the
            // notifications toggle; gated by its own setting and paused while a prompt
            // or capture is already in progress.
            if (Api.CheckpointPhotosEnabled)
                checkpointDetector.Tick();

            // Auto-import crafts the player queued from Discord. Space Center is safe
            // for everything; the editor is safe for the file-write imports (blueprint
            // installs, flags) — DoProcessImport leaves live-vessel entries queued
            // there, so an accepted .craft lands in the Ships folder without a trip
            // out of the VAB. Flight polls too: it is a legal scene for a live-vessel
            // delivery (GiftInbox.CanDeliverHere), and a vessel accepted in the SPH
            // used to sit queued through a whole launch and only appear on the next
            // walk through the Space Center — fifteen minutes later, on 2026-08-30 —
            // which reads as "my craft never arrived". Never during a scene load (see
            // SceneSettled). Independent of the notifications toggle.
            if ((HighLogic.LoadedScene == GameScenes.SPACECENTER ||
                 HighLogic.LoadedScene == GameScenes.EDITOR ||
                 HighLogic.LoadedScene == GameScenes.FLIGHT) &&
                SceneSettled &&
                Time.realtimeSinceStartup - lastImportCheck > ImportInterval)
            {
                lastImportCheck = Time.realtimeSinceStartup;
                clientState.PollCraftImports();
            }

            // Friend-quicksend offers awaiting an accept/decline. Separate from the
            // import queue on purpose: an offer is shown, never auto-installed.
            GiftInbox.Tick();

            // Reconcile rescue craft against what the server says actually happened.
            // The removal notification is only the fast path; this is the one that has
            // to be right, because a notification is a transient the player can dismiss
            // (and only the newest 50 are kept). Same scene rule as the removal pass —
            // there is no point discovering work we could not do here anyway — and the
            // timer is reset on every scene change, so arriving at the Space Center
            // always checks once.
            if ((HighLogic.LoadedScene == GameScenes.SPACECENTER ||
                 HighLogic.LoadedScene == GameScenes.TRACKSTATION) &&
                !rescueReconcileRunning &&
                Time.realtimeSinceStartup - lastRescueReconcile > RescueReconcileInterval)
            {
                lastRescueReconcile = Time.realtimeSinceStartup;
                StartCoroutine(ReconcileRescueVessels());
            }

            // One catch-up fetch on arriving somewhere a removal can run. The live
            // socket pushes each notification exactly ONCE, and a push that lands on a
            // connection which has dropped but not yet been noticed is gone — the
            // fallback poll below cannot recover it, because that only runs while the
            // socket reports itself DOWN. Measured 2026-09-02 (§3.19): an accepted
            // quicksend's echo never arrived, the sender kept a ship the recipient also
            // had, and only restarting the game — which forces a fetch on connect —
            // recovered it.
            //
            // Same scene rule and the same reasoning as ReconcileRescueVessels above: a
            // notification is a transient, so anything that must be right cannot depend
            // on one arriving. Bounded to once per scene arrival rather than a timer, so
            // it costs one call per visit to the Space Center and nothing while sitting
            // there.
            FetchNotificationsOnSceneArrival();

            // Notifications can be toggled off in Settings — keep the socket closed
            // and skip polling entirely when disabled.
            if (!Api.NotificationsEnabled)
            {
                if (notifSocket.IsConnected)
                    notifSocket.Disconnect();
                return;
            }

            // Re-open the socket if notifications were just turned back on at runtime.
            if (!notifSocket.IsEnabled)
                notifSocket.Connect();

            // Drive the live notification socket (connect/reconnect on the main thread).
            notifSocket.Tick();

            // After a (re)connect, catch up once: notifications the server pushed
            // while the socket was down are lost (they hit a discarded connection),
            // so only a fetch recovers them — and re-syncs the contract list, which
            // otherwise wouldn't refresh until the player acts manually.
            if (notifSocket.ConsumeJustConnected())
            {
                StartCoroutine(CheckNotifications());
                // Catch up on the version gate too: a new build may have been
                // published while this client's socket was down.
                RecheckVersion();
            }

            // A new mod version was just published (server poke) — re-check live so
            // an outdated client gates itself without waiting for a restart.
            if (notifSocket.ConsumeVersionPoke())
                RecheckVersion();

            // The Privacy/Terms version was bumped (server poke) — re-fetch it (same
            // check carries policy_version) so this client raises the re-consent gate
            // live instead of only on its next restart.
            if (notifSocket.ConsumePolicyPoke())
                RecheckVersion();

            // Drain notifications pushed over the socket and surface new ones as toasts.
            while (notifSocket.TryDequeue(out var notif))
                HandleIncomingNotification(notif);

            // Anything parked because no save was loaded when it arrived.
            DrainDeferredNotifications();

            // Commands from the website. Peek-then-drop: one that can't be shown right
            // now (a time-critical prompt is already up) stays queued and is retried
            // next frame, until the socket's 30 s TTL discards it.
            while (notifSocket.TryPeekCommand(out var cmd))
            {
                if (!HandleWebCommand(cmd)) break;
                notifSocket.DropCommand();
            }

            // Fallback polling — only when the live socket is down.
            if (!notifSocket.IsConnected &&
                Time.realtimeSinceStartup - lastNotificationCheck > notificationInterval)
            {
                lastNotificationCheck = Time.realtimeSinceStartup;
                StartCoroutine(CheckNotifications());
            }
        }

        /// <summary>
        /// Toast a notification exactly once (de-duped by id) and update the unread
        /// badge. Used by both the live socket and the fallback poll.
        /// </summary>
        private void HandleIncomingNotification(Dictionary<string, object> notif, bool bumpBadge = true)
        {
            if (notif == null) return;

            // Server text is written for Discord — emoji and <:name:id> markup that
            // KSP's fonts can't draw. Cleaned once here so the toast, the feeds and
            // the log all see the same renderable string.
            notif["title"] = TextSanitizer.CleanNotif(MiniJSON.GetString(notif, "title"));
            notif["message"] = TextSanitizer.CleanNotif(MiniJSON.GetString(notif, "message"));

            // Some notifications are not just text: `rescue_craft_removed` and
            // `craft_gift_accepted` each carry an instruction to remove a vessel from
            // this save, and both handlers can only act with a save loaded. Marking one
            // seen before it has been acted on CONSUMES it — the handler no-ops, the id
            // is remembered, and no later poll ever reconsiders it.
            //
            // That is not hypothetical: on 2026-09-02 an accepted quicksend's echo
            // arrived while the game sat at the main menu on a different save, was
            // toasted, no-opped on `!VesselExists`, and was never retried once the right
            // save was loaded — leaving the sender holding a ship the recipient had also
            // been given (§3.19). The backlog-seeding pass in CheckNotifications already
            // guards exactly this ("retry on a later poll; don't mark seen"); the live
            // path did not.
            //
            // Deferred BEFORE the toast as well as before the seen-marking, so the retry
            // is silent: toasting now and again on the retry would trade a lost removal
            // for a duplicated notification every poll until a save is open.
            if (NeedsLoadedSaveToAct(notif) && !TryHandleInstruction(notif))
            {
                Park(notif);
                return;
            }

            string id = MiniJSON.GetString(notif, "id");
            if (!string.IsNullOrEmpty(id) && !seenNotifIds.Add(id))
                return; // already toasted

            if (bumpBadge) UnreadNotifications++;

            string contractId = "";
            var data = MiniJSON.GetDict(notif, "data");
            if (data != null) contractId = MiniJSON.GetString(data, "contract_id");

            Toast(
                MiniJSON.GetString(notif, "title"),
                MiniJSON.GetString(notif, "message"),
                contractId
            );

            // The instruction (if any) was already carried out by TryHandleInstruction
            // above — before the toast, so one we cannot act on yet is parked silently
            // rather than announced and then forgotten.

            // Keep the panel in sync so the item is already there when opened.
            clientState.AddNotification(notif);

            // Pulse the sidebar tab. Unconditional by design — every notification,
            // regardless of setting, regardless of whether the panel is already
            // open. It also marks the feed dirty so an open panel gains the row.
            sidebar?.Pulse();

            // Tee to the browser UI — deliberately in addition to the toast above, not
            // instead of it. A player in web mode may be flying with the browser behind
            // the game window; the in-game toast is the only thing they will actually
            // see, so it must keep firing regardless of UI mode.
            webServer?.Broadcast("notification", MiniJSON.Serialize(notif));
#if GK_DEBUG_PANEL
            // The same tee to the harness. This is what makes a driver event-driven
            // rather than a polling loop: a craft arriving, a contract being accepted
            // and a gift being declined are all notifications, and KSP is slow enough
            // that polling for them is the one genuinely expensive thing a test can do.
            debugBridge?.Broadcast("notification", MiniJSON.Serialize(notif));
#endif

            // Same reasoning as RefreshContracts() below: tell the page its lists are
            // stale so it can refetch, rather than making it poll.
            if (IsContractEvent(MiniJSON.GetString(notif, "type")))
                webServer?.Broadcast("contracts_changed", "{}");
#if GK_DEBUG_PANEL
            if (IsContractEvent(MiniJSON.GetString(notif, "type")))
                debugBridge?.Broadcast("contracts_changed", "{}");
#endif

            // Anything that changes a contract's state (a new offer, an acceptance,
            // a submission, an approval/refusal, a cancellation) refreshes the
            // Contracts tab live off the same socket push that produced this toast —
            // so the player never has to manually reopen the list. Skipped during the
            // first poll's backlog seeding, which doesn't route through here.
            if (IsContractEvent(MiniJSON.GetString(notif, "type")))
                RefreshContracts();

            // Money that moved while the player was flying — a contract of theirs
            // approved, a craft of theirs bought, a level-up bonus — only ever
            // reaches this client as a notification. The profile is otherwise
            // fetched on link, on opening the panel, and from the Profile tab's
            // refresh button; there is no timer. Without this the toast reads
            // "+300 KCoins" and every balance on screen keeps the old number.
            //
            // Deliberately fired for EVERY notification rather than a list of
            // money-moving types. The two costs are wildly asymmetric: a missing
            // type silently brings the stale-balance bug back for that one event,
            // while a spurious one is a single cheap GET — and a hardcoded list
            // here would drift from the server every time a notification is added.
            // The debounce keeps a burst to one fetch.
            RequestBalanceSync();
        }

        // Balance re-reads are coalesced: a backlog draining after a reconnect can
        // deliver a dozen notifications in one frame, and they all want the same
        // single answer.
        private float lastBalanceSync = -999f;
        private const float BalanceSyncCooldown = 5f;

        private void RequestBalanceSync()
        {
            if (Time.realtimeSinceStartup - lastBalanceSync < BalanceSyncCooldown) return;
            lastBalanceSync = Time.realtimeSinceStartup;
            clientState?.RequestProfileRefresh();
        }

        /// <summary>
        /// Act on a command frame from the website. Returns false to leave it queued and
        /// try again next frame (see the caller in Update).
        ///
        /// Deliberately an enumerated switch and not a dispatcher. This channel is
        /// reached with a browser session token, which has no device binding and no mod
        /// hash behind it, so every arm is a decision made once and reviewed: web
        /// commands may only *raise UI*, and they arrive as a prompt the player still
        /// has to accept. That keeps the worst case of a stolen token at "an unwanted
        /// prompt appeared" rather than "something happened in my save".
        /// </summary>
        private bool HandleWebCommand(Dictionary<string, object> cmd)
        {
            if (cmd == null) return true;

            switch (MiniJSON.GetString(cmd, "command", ""))
            {
                case "open_submit":
                    return PromptOpenSubmit(
                        MiniJSON.GetString(cmd, "contract_id", ""),
                        MiniJSON.GetString(cmd, "mission", ""));

                default:
                    // An unknown command means a newer server; drop it rather than
                    // queueing it forever.
                    return true;
            }
        }

        /// <summary>
        /// Offer to open the submit window for a contract, at the player's confirmation.
        /// Never opens it outright: a window yanked up over a landing is the failure mode
        /// this whole channel is shaped to avoid.
        /// </summary>
        private bool PromptOpenSubmit(string contractId, string mission)
        {
            if (string.IsNullOrEmpty(contractId)) return true;
            if (Api == null || !Api.IsLinked || Api.TransmissionBlocked) return true;

            // A checkpoint prompt is time-critical and mid-flight; a website button is
            // not. Wait for the slot rather than stealing it.
            if (checkpointPrompt.IsVisible) return false;

            string subject = string.IsNullOrEmpty(mission)
                ? "a contract"
                : "\"" + mission + "\"";

            checkpointPrompt.Show(
                "Requested from the website",
                "Open the submission window for " + subject + "?",
                onAccept: () => StartCoroutine(Web.GkRoutes.OpenSubmitRoutine(
                    contractId,
                    (ok, msg) => Toast(
                        ok ? "Submit" : "Submit unavailable", msg))),
                acceptLabel: "Open");

            return true;
        }

        // Notification types that imply a contract list change. Kept as an explicit
        // allow-list so a future non-contract notification won't trigger needless
        // contract fetches.
        private static bool IsContractEvent(string type)
        {
            switch (type)
            {
                case "contract_incoming":
                case "contract_accepted":
                case "contract_cancelled":
                case "submission_received":
                case "review_result":
                case "mission_accepted":
                case "rescue_delivered":
                case "rescue_failed":
                case "rescue_craft_removed":
                case "flag_delivered":
                    return true;
                default:
                    return false;
            }
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnToolbarReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnToolbarDestroyed);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelGuiReady);
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            checkpointDetector?.Unregister();
            notifSocket?.Disconnect();
            webServer?.Stop();
#if GK_DEBUG_PANEL
            // Deletes the handshake file too, so a driver learns the game is gone
            // instead of timing out against a dead port.
            debugBridge?.Stop();
#endif
            // Must run: Destroy() releases the InputLockManager lock, and a leaked
            // control lock outlives this GameObject and soft-bricks the save.
            sidebar?.Destroy();
            RemoveToolbarButton();
        }

        // ── Toolbar ─────────────────────────────────────────────────────────

        private void LoadToolbarIcon()
        {
            // Try to load custom icon, fall back to generated one
            string iconPath = Path.Combine(ModPath, "Textures", "icon_toolbar.png");
            if (File.Exists(iconPath))
            {
                toolbarIcon = new Texture2D(38, 38, TextureFormat.ARGB32, false);
                toolbarIcon.LoadImage(File.ReadAllBytes(iconPath));
            }
            else
            {
                // Generate a simple icon programmatically
                toolbarIcon = GenerateIcon();
            }
        }

        private Texture2D GenerateIcon()
        {
            int size = 38;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color bg = new Color(0.1f, 0.6f, 0.3f, 0.9f);
            Color fg = Color.white;

            // Fill background with rounded-ish shape
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    float dx = x - size / 2f;
                    float dy = y - size / 2f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist < size / 2f - 2)
                        tex.SetPixel(x, y, bg);
                    else if (dist < size / 2f)
                        tex.SetPixel(x, y, new Color(bg.r, bg.g, bg.b, 0.5f));
                    else
                        tex.SetPixel(x, y, Color.clear);
                }
            }

            // Draw "GK" text area (simple block letters)
            DrawLetter(tex, 8, 12, fg, 'G');
            DrawLetter(tex, 21, 12, fg, 'K');

            tex.Apply();
            return tex;
        }

        private void DrawLetter(Texture2D tex, int ox, int oy, Color c, char ch)
        {
            // Very simple 7x10 block letters for G and K
            if (ch == 'G')
            {
                for (int x = 0; x < 10; x++) tex.SetPixel(ox + x, oy + 14, c);
                for (int y = 0; y < 14; y++) tex.SetPixel(ox, oy + y, c);
                for (int x = 0; x < 10; x++) tex.SetPixel(ox + x, oy, c);
                for (int y = 0; y < 7; y++) tex.SetPixel(ox + 9, oy + y, c);
                for (int x = 5; x < 10; x++) tex.SetPixel(ox + x, oy + 7, c);
            }
            else if (ch == 'K')
            {
                for (int y = 0; y < 15; y++) tex.SetPixel(ox, oy + y, c);
                for (int i = 0; i < 7; i++) { tex.SetPixel(ox + 1 + i, oy + 7 + i, c); tex.SetPixel(ox + 1 + i, oy + 7 - i, c); }
            }
        }

        private void OnToolbarReady()
        {
            if (toolbarButton == null && toolbarIcon != null)
            {
                toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    OnToolbarClick, OnToolbarClick,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.ALWAYS,
                    toolbarIcon
                );
            }
        }

        private void OnToolbarDestroyed()
        {
            RemoveToolbarButton();
        }

        private void RemoveToolbarButton()
        {
            if (toolbarButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
                toolbarButton = null;
            }
        }

        private void OnToolbarClick()
        {
            // Outdated client, prompt not yet dismissed: the only thing the button
            // does is re-show the gate.
            if (UpdateRequired && !UpdateAcknowledged)
            {
                updateWindow.Show(ModVersion.Current, LatestVersion, UpdateDownloadUrl);
                return;
            }

            // Data sharing opted out (rule 8.2): the mod is inert. The only window the
            // button offers is the paused panel, which can re-enable sharing.
            if (!Api.DataGatheringEnabled)
            {
                ShowDataPausedWindow = !ShowDataPausedWindow;
                return;
            }

            // Privacy/terms opt-in not satisfied (rule 8.1): a fresh install, or a
            // client whose consent lapsed (policy bump / edited consent.cfg). The only
            // window the button offers is the opt-in gate; nothing else is reachable
            // until it's accepted. ConsentWindow opens the link/main flow on accept.
            if (!Consent.Accepted)
            {
                ShowConsentWindow = !ShowConsentWindow;
                return;
            }

            // Suspended: the notice is the only thing the button has to offer, and the
            // only live control on it is the re-check. A second click closes it, like
            // every other window this button toggles.
            if (Suspended)
            {
                if (suspendedWindow.Visible) suspendedWindow.Hide();
                else suspendedWindow.Show(SuspensionReason, SuspendedUntil);
                return;
            }

            // Acknowledged update gate: the sidebar is the only thing on offer, and it
            // comes up in limited mode. Linking is a server call that would just 426, so
            // an unlinked client goes here too rather than to the link window.
            if (UpdateRequired)
            {
                sidebar?.Toggle();
                return;
            }

            // Browser UI mode. Every gate above is deliberately still IMGUI: they are
            // either time-critical, a gate, or the recovery path, and none of them may
            // depend on a browser that might not open.
            if (Api.IsLinked && WebUiMode)
            {
                if (webUiWindow.Visible && webServer != null && webServer.IsRunning)
                {
                    webUiWindow.Visible = false; // second click closes the panel
                    return;
                }
                if (OpenWebUi()) return;

                // Bridge could not start (missing or mismatched WebUI bundle, port
                // trouble). Fall through to the sidebar rather than strand the player on
                // a button that does nothing.
                Debug.LogWarning("[GeneKerman] Web UI unavailable, falling back to the sidebar.");
                ScreenMessages.PostScreenMessage("Boundless Missions: web UI unavailable, using the in-game panel.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
            }

            // The sidebar is the interface. Nothing IMGUI is left except the gates
            // (consent, update, data-sharing, device) and the link screen below, each
            // of which draws precisely when the sidebar's canvas may not.
            if (Api.IsLinked) sidebar?.Toggle();
            else ShowLinkWindow = !ShowLinkWindow;
        }

        // ── GUI ─────────────────────────────────────────────────────────────

        void OnGUI()
        {
            // While the UI is hidden (capture in progress or F2), draw nothing so
            // the mod's windows never appear in screenshots or cinematic shots.
            if (uiHidden) return;

            // Swap in GeneKerman's themed skin for the duration of our own windows,
            // then restore the ambient skin. This scopes our dark button/textField/
            // scrollbar styling to GeneKerman only — without it, mutating the shared
            // GUI.skin would restyle every other mod's IMGUI too.
            GUISkin prevSkin = GUI.skin;
            GUI.skin = UI.GKSkin.GetSkin(prevSkin);
            try
            {
                // Outdated client: nothing but the update prompt is usable, until the
                // player dismisses it with "Continue anyway" (see UpdateAcknowledged),
                // which hands them the sidebar in limited mode.
                if (UpdateRequired && !UpdateAcknowledged)
                {
                    updateWindow.Draw();
                }
                // Data sharing opted out: only the paused panel is drawn.
                else if (!Api.DataGatheringEnabled)
                {
                    if (ShowDataPausedWindow)
                        dataPausedWindow.Draw();
                }
                // Privacy/terms opt-in gate (rule 8.1) stands in front of everything:
                // a fresh install, or a client whose consent lapsed (policy bump /
                // edited consent.cfg). Nothing else — not even the main window of a
                // linked client — is drawn until it's (re-)accepted.
                else if (!Consent.Accepted)
                {
                    if (ShowConsentWindow)
                        consentWindow.Draw();
                }
                // Suspended account. Below the three legal gates above (an opt-out or a
                // lapsed consent is the player's own decision and outranks ours) and
                // above everything else: every server-backed surface is refused for the
                // duration, and drawing them would present a mod that looks broken
                // instead of one that is telling them what happened.
                else if (Suspended)
                {
                    suspendedWindow.Draw();
                }
                // Acknowledged update gate. The branch is empty and must stay: limited
                // mode is the sidebar's now (it narrows itself to the panels that work
                // with no server), and what this arm still does is stop the block below
                // from running — nothing that transmits may be on screen here, which
                // means no link window and no device prompt.
                else if (UpdateRequired)
                {
                }
                else
                {
                    // Unlinked client past the opt-in gate: offer the link menu.
                    if (!Api.IsLinked && (ShowLinkWindow || ShowConsentWindow))
                        linkWindow.Draw();

                    // Where the browser UI is running, plus the escape hatch back to
                    // the in-game panel. Drawn in web mode only.
                    webUiWindow.Draw();


                    // These stay IMGUI permanently, in every mode. The checkpoint
                    // prompt is time-critical and must appear over the game while the
                    // player is flying — a browser is alt-tabbed or on another monitor.
                    // The device prompt is a security question about this PC. Toasts
                    // used to be drawn here too; they are now uGUI (UI/Gui/ToastHost),
                    // raised through Toast() below and gated by the sidebar's canvas —
                    // which is this same cascade, so nothing about when one may appear
                    // changed.
                    checkpointPrompt.Draw();
                    deviceVerifyWindow.Draw();
                }
            }
            finally
            {
                GUI.skin = prevSkin;
            }
        }

        // ── Milestone Hero Shots ────────────────────────────────────────────

        /// <summary>
        /// A flight milestone was detected. Offer the player a one-tap capture; the
        /// detector is paused until they answer so prompts don't stack.
        /// </summary>
        private void OnCheckpoint(Checkpoint cp)
        {
            // Close any prompt already up *before* suspending: Show() dismisses the
            // outgoing one, and that dismissal runs the old onClose — which un-suspends
            // the detector. Doing it first keeps the flag we set below.
            checkpointPrompt.Dismiss(false);
            checkpointDetector.Suspended = true;
            checkpointPrompt.Show(
                cp.title,
                cp.message,
                onAccept: () => StartCoroutine(RunCheckpointCapture(cp)),
                onClose: () => { checkpointDetector.Suspended = false; }
            );
        }

        private IEnumerator RunCheckpointCapture(Checkpoint cp)
        {
            if (checkpointCapturing) yield break;
            checkpointCapturing = true;

            string savedPath = null;
            yield return CinematicCapture.Capture(
                cp.label, cp.targetVessel, cp.targetBody,
                path => { savedPath = path; });

            checkpointCapturing = false;

            if (string.IsNullOrEmpty(savedPath))
                yield break;

            Toast("Photo captured", "Sharing to Discord…");

            // ScreenCapture writes asynchronously — wait for the file to flush before
            // reading it back for upload.
            yield return new WaitForSeconds(0.5f);

            byte[] png = VesselDataCollector.ReadScreenshot(savedPath);
            if (png == null || png.Length == 0)
            {
                Debug.LogWarning("[GeneKerman] Checkpoint photo not readable for upload: " + savedPath);
                yield break;
            }

            string vesselName = FlightGlobals.ActiveVessel?.vesselName ?? "";
            string body = cp.targetBody != null
                ? cp.targetBody.bodyName
                : FlightGlobals.ActiveVessel?.mainBody?.bodyName ?? "";
            string targetName = cp.targetVessel != null
                ? cp.targetVessel.vesselName
                : (cp.targetBody != null ? cp.targetBody.bodyName : "");

            yield return Api.UploadCheckpointPhoto(
                png, cp.kind, vesselName, body, targetName,
                (ok, resp, status) =>
                {
                    if (ok)
                        RaiseLocalNotification("Photo shared", "Posted to the community channel.");
                    else
                        RaiseLocalNotification("Share failed",
                            "Saved locally in PluginData/renders.");
                });
        }

        // ── Achievement Hero Shots (player-composed) ────────────────────────

        /// <summary>
        /// Begin the manual achievement-capture flow (Tools tab button). Shows a
        /// bottom overlay telling the player to frame a good angle; pressing Capture
        /// grabs the current view and submits it for server-side verification.
        /// </summary>
        public void StartAchievementCapture()
        {
            if (achievementCapturing) return;

            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
            {
                Toast("Achievement Shot", "Enter flight with an active vessel first.");
                return;
            }
            if (Api == null || !Api.IsLinked || Api.TransmissionBlocked)
            {
                Toast("Achievement Shot", "Link your account and enable data sharing first.");
                return;
            }

            // Reuse the bottom-centre prompt; give the player ample time to frame the
            // shot before it self-dismisses.
            checkpointPrompt.Show(
                "Achievement Shot",
                "Frame your best angle of the achievement, then press Capture to submit it for a title role.",
                onAccept: () => StartCoroutine(RunAchievementCapture()),
                timeoutOverride: 60f);
        }

        private IEnumerator RunAchievementCapture()
        {
            if (achievementCapturing) yield break;
            achievementCapturing = true;

            // Hide the HUD and our own windows for a clean grab (mirrors F2 / the
            // cinematic capture). OnHideUI gates our IMGUI so the overlay won't show.
            KSP.UI.UIMasterController.Instance?.HideUI();
            GameEvents.onHideUI.Fire();

            // Let the UI-hidden frame render, request the grab, then give it a frame
            // to be captured before we restore the view (same ordering CinematicCapture
            // relies on).
            yield return new WaitForEndOfFrame();
            string savedPath = VesselDataCollector.CaptureScreenshot();
            yield return new WaitForEndOfFrame();
            yield return null;

            KSP.UI.UIMasterController.Instance?.ShowUI();
            GameEvents.onShowUI.Fire();

            // ScreenCapture writes asynchronously — wait for the file to flush.
            yield return new WaitForSeconds(0.5f);

            byte[] png = VesselDataCollector.ReadScreenshot(savedPath);
            achievementCapturing = false;

            if (png == null || png.Length == 0)
            {
                RaiseLocalNotification("Capture failed", "Couldn't read the screenshot. Try again.");
                yield break;
            }

            var vessel = FlightGlobals.ActiveVessel;
            string vesselName = vessel?.vesselName ?? "";
            string body = vessel?.mainBody?.bodyName ?? "";
            string situation = vessel?.situation.ToString() ?? "";

            // Unique-per-vessel id; persists across save/load. Combined with the
            // current body+situation it forms the dedup key, so the same craft can
            // still be rewarded again after it reaches a different position.
            string vesselId = vessel != null ? vessel.persistentId.ToString() : "";
            string captureKey = vesselId + "|" + body + "|" + situation;

            LoadReviewedCaptures();
            bool review = !reviewedCaptures.Contains(captureKey);
            if (!review)
            {
                Toast("Sharing…",
                    "You've already earned this vessel's achievement here, so the shot goes to Discord.");
            }
            else
            {
                Toast("Submitting…", "Checking your shot for an achievement…");
            }

            yield return Api.UploadAchievementPhoto(
                png, vesselName, body, vesselId, situation, review,
                (ok, resp, status) =>
                {
                    string msg = ok
                        ? "Submitted."
                        : "Submission failed. Check your connection and try again.";
                    if (!string.IsNullOrEmpty(resp))
                    {
                        var data = MiniJSON.DeserializeDict(resp);
                        if (data != null)
                            msg = MiniJSON.GetString(data, "message", msg);
                    }
                    // Record the vessel+position once it's been reviewed, so the next
                    // capture of the same craft here is share-only (no re-reward).
                    if (ok && review)
                    {
                        reviewedCaptures.Add(captureKey);
                        SaveReviewedCaptures();
                    }
                    RaiseLocalNotification("Achievement Shot", msg);
                });
        }

        /// Load the set of already-reviewed vessel+position keys from disk (once).
        private void LoadReviewedCaptures()
        {
            if (reviewedCaptures != null) return;
            reviewedCaptures = new HashSet<string>();
            try
            {
                if (File.Exists(ReviewedCapturesPath))
                {
                    foreach (var line in File.ReadAllLines(ReviewedCapturesPath))
                    {
                        var key = line.Trim();
                        if (key.Length > 0) reviewedCaptures.Add(key);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not read achievement capture log: " + e.Message);
            }
        }

        private void SaveReviewedCaptures()
        {
            try
            {
                Directory.CreateDirectory(PluginDataPath);
                File.WriteAllLines(ReviewedCapturesPath, new List<string>(reviewedCaptures).ToArray());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not write achievement capture log: " + e.Message);
            }
        }

        // ── Data Fetching ───────────────────────────────────────────────────

        /// <summary>
        /// Adopt the identity carried on a profile response.
        ///
        /// Called from every path that receives one, which is the point. This used to
        /// live inline in <see cref="InitialFetch"/> alone — a coroutine that runs once,
        /// two seconds after startup — so a client whose server was unreachable at that
        /// instant logged "server unreachable" and then ran the entire session with an
        /// empty <see cref="LinkedAccountId"/>, with no retry short of re-linking or
        /// restarting. <c>ClientState.RefreshProfile</c> re-fetched the same document
        /// every refresh and updated only its own cache, so the identity never recovered.
        ///
        /// That is not a cosmetic gap. An empty id makes <c>DecideComingHome</c> skip the
        /// id comparison and fall through to a name compare that is empty too, so it
        /// answers false for everything: an honest returning kerbal never has its
        /// ownership tag stripped, and <c>PurgeBorrowedGhostCrew</c> then deletes it —
        /// the exact failure the second repair round produced. Launching KSP before the
        /// server was up was enough to reach it.
        ///
        /// Empty values are ignored rather than assigned, so a malformed or partial
        /// response cannot clear an identity that was already established.
        /// </summary>
        internal void NoteProfileIdentity(Dictionary<string, object> data)
        {
            if (data == null) return;

            string name = MiniJSON.GetString(data, "username");
            // The profile is the authoritative source of the account id: the link
            // response carries it too, but only this one is re-fetched every session, so
            // a client linked by an older server picks the id up here.
            string id = MiniJSON.GetString(data, "user_id");

            bool gained = string.IsNullOrEmpty(LinkedAccountId) && !string.IsNullOrEmpty(id);
            if (!string.IsNullOrEmpty(name)) LinkedUsername = name;
            if (!string.IsNullOrEmpty(id)) LinkedAccountId = id;

            // Logged only when it changes something, so a healthy client does not print
            // this on every refresh — but a late recovery, which is the case worth
            // seeing in a bug report, always says so.
            if (gained) Debug.Log("[GeneKerman] Profile identity established: " + LinkedUsername);
        }

        private IEnumerator InitialFetch()
        {
            yield return new WaitForSeconds(2f); // Let the game settle

            // Verify token is still valid
            yield return Api.GetProfile((ok, data, err) =>
            {
                if (ok)
                {
                    clientState.UpdateProfile(data);
                    NoteProfileIdentity(data);
                }
                else
                {
                    Debug.LogWarning("[GeneKerman] Token invalid or server unreachable: " + err);
                }
            });

            // Check notifications
            yield return CheckNotifications();

            // Anti-tamper attestation on (re)connect — best-effort; the server flags
            // a failure to moderators, so there's nothing for the client to handle.
            yield return Api.RunAttestation();
        }

        /// Re-attest periodically while linked so a DLL swapped after launch is still
        /// caught. The first pass also runs from InitialFetch right after linking.
        private IEnumerator AttestationLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(1800f);   // every 30 minutes
                if (Api != null && Api.IsLinked)
                    yield return Api.RunAttestation();
            }
        }

        public IEnumerator CheckNotifications()
        {
            yield return Api.GetNotifications((ok, data, err) =>
            {
                if (!ok) return;

                // Server returns the authoritative unread count (read + unread history).
                UnreadNotifications = MiniJSON.GetInt(data, "unread_count");

                bool seeding = !notifBacklogSeeded;
                var notifList = MiniJSON.GetList(data, "notifications");
                foreach (var n in notifList)
                {
                    var notif = n as Dictionary<string, object>;
                    if (notif == null) continue;

                    if (seeding)
                    {
                        // First poll after launch: items already live in the panel —
                        // record them as seen so we never toast the backlog. But an
                        // approval that landed while we were offline arrives here too, and
                        // its rescue-craft removal must still fire — so act on it now. If
                        // the save (scenario) isn't loaded yet we can't resolve the craft,
                        // so leave it unseen and let a later poll retry it once a save is in.
                        // Same rule as the live path: act first, and if the instruction
                        // could not be carried out, leave it UNSEEN so a later poll or a
                        // Space Center arrival retries it. Previously this only checked
                        // for a missing scenario; a vessel that could not be found (the
                        // wrong save loaded) counted as done and was lost — §3.19.
                        if (NeedsLoadedSaveToAct(notif) && !TryHandleInstruction(notif))
                            continue; // retry later; don't mark seen

                        string id = MiniJSON.GetString(notif, "id");
                        if (!string.IsNullOrEmpty(id)) seenNotifIds.Add(id);
                    }
                    else
                    {
                        // Badge already set from unread_count; just toast new ones.
                        HandleIncomingNotification(notif, bumpBadge: false);
                    }
                }
                notifBacklogSeeded = true;
            });
        }

        // ── Public API for UI windows ───────────────────────────────────────

        public void OnAccountLinked(Dictionary<string, object> data)
        {
            ShowLinkWindow = false;
            // Straight into the interface, the way the classic window used to open
            // itself here. The browser UI is not launched for them — that is a click
            // on the toolbar button, not something linking should do behind their back.
            sidebar?.SetOpen(true);
            clientState.RefreshAll();
            StartCoroutine(InitialFetch());
            // Disconnect first, always. Connect() only *enables* the socket — Tick()
            // reuses an already-open connection — so linking onto an install that
            // still held a live socket from the previous account would adopt it, and
            // this player would then be shown that player's notifications. Tearing it
            // down here forces a fresh handshake with the token we just minted.
            notifSocket.Disconnect();
            notifSocket.Connect();

            LinkedUsername = MiniJSON.GetString(data, "username");
            // Assigned unconditionally, exactly like the name above: this is a fresh
            // account, so carrying the previous one's id forward would be worse than
            // carrying none. LinkResponse has sent user_id since the account work; an
            // older server leaves it empty until InitialFetch above fills it in.
            LinkedAccountId = MiniJSON.GetString(data, "user_id");
            RaiseLocalNotification(
                "Account Linked!",
                "Welcome, " + LinkedUsername + "!"
            );
        }

        public void ShowNotification(string title, string message)
        {
            RaiseLocalNotification(title, message);
        }

        /// <summary>
        /// Raise a notification the client generates itself (photo shared, craft
        /// installed, device approved, …): toast it AND record it in the panel so
        /// it survives the 8-second toast fade. Unlike server notifications these
        /// have no Firestore record — they're session-local (see
        /// ClientState.AddLocalNotification) and disappear on a game restart.
        /// </summary>
        /// <summary>
        /// Raise a transient top-right toast and nothing else. Everything the mod
        /// shows this way goes through here, so the sidebar stays the single owner
        /// of the stack — a second front end raising its own would have its own
        /// lifetime, its own cap and its own idea of when the UI is hidden.
        ///
        /// Null-safe on the sidebar: the toast is the *transient* copy, and the
        /// callers that also want a durable one already call
        /// RaiseLocalNotification, which records into the feed either way.
        /// </summary>
        public void Toast(string title, string message, string contractId = null,
                          string localAction = null)
            => sidebar?.Toast(title, message, contractId, localAction);

        public void RaiseLocalNotification(string title, string message, string contractId = null,
                                          string localAction = null)
        {
            // Local strings historically carry Discord-style emoji too (🛟, 🗑) — the
            // game fonts draw them as tofu, so every notification goes through the
            // same wash the server ones get.
            title = TextSanitizer.CleanNotif(title);
            message = TextSanitizer.CleanNotif(message);
            Toast(title, message, contractId, localAction);

            if (clientState == null) return;

            var data = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(contractId)) data["contract_id"] = contractId;
            // A problem this install can fix itself carries the button that fixes it —
            // see LocalNotifActions, which both feeds render from this key.
            if (!string.IsNullOrEmpty(localAction)) data[LocalNotifActions.DataKey] = localAction;

            var notif = new Dictionary<string, object>
            {
                { "id", "local-" + Guid.NewGuid().ToString("N").Substring(0, 12) },
                { "type", "local" },
                { "title", title },
                { "message", message },
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'") },
                { "read", false },
                { "data", data },
            };

            UnreadNotifications++;
            clientState.AddLocalNotification(notif);
        }

        /// <summary>Called by the consent window once the user accepts the privacy
        /// policy, terms, and data-collection opt-in. Until this point nothing is
        /// transmitted (see ApiClient.TransmissionBlocked), so the version gate and any
        /// resume work are kicked off here — not at startup.</summary>
        public void OnConsentGranted()
        {
            ShowConsentWindow = false;

            // Hand the player the surface the gate was standing in front of. Accepting
            // is the one moment we know for certain they want in, so the next screen
            // has to open by itself: the gate is modal and closing it leaves an empty
            // screen, which reads as "nothing happened" and costs a second trip to the
            // toolbar to find the menu that should already have been there.
            //
            // Which surface depends on where the accept came from. A fresh install has
            // no account, so it is the link menu. A re-consent (policy bump, or an
            // edited consent.cfg) is an already-linked client whose interface was taken
            // away by OnConsentLapsed, so it is the sidebar — except in browser mode,
            // where the page is the interface and resumes on its own; opening a browser
            // tab is a click on the toolbar, not something accepting should do behind
            // their back (same rule as OnAccountLinked).
            if (!Api.IsLinked)
            {
                ShowLinkWindow = true;
            }
            else if (!WebUiMode)
            {
                sidebar?.SetOpen(true);
            }

            // The startup version check was suppressed pre-consent; engage it now.
            RecheckVersion();

            // Re-consent on an already-linked client (e.g. a policy-version bump):
            // resume fetching and notifications.
            if (Api.IsLinked && Api.DataGatheringEnabled)
            {
                StartCoroutine(InitialFetch());
                if (Api.NotificationsEnabled)
                    notifSocket.Connect();
            }
        }

        /// <summary>Consent no longer covers the required policy — the server bumped the
        /// policy version, or consent.cfg was edited/deleted out from under us. Shut the
        /// live UI and socket down and raise the re-accept gate; nothing transmits again
        /// (TransmissionBlocked) until the player re-accepts via OnConsentGranted.</summary>
        public void OnConsentLapsed()
        {
            notifSocket.Disconnect();
            // Nothing of ours is on screen after this: the sidebar's canvas stops
            // rendering the moment consent lapses (SidebarController.ShouldRender).
            ShowLinkWindow = false;
            ShowConsentWindow = true;   // surface the re-accept gate immediately
            Debug.Log("[GeneKerman] Consent lapsed, re-accept required before any data is sent.");
        }

        /// <summary>Flip the master data-sharing opt-out (rule 8.2) and bring the mod's
        /// live state in line: disabling closes the socket and every window and the
        /// mod goes inert; enabling resumes fetching/notifications if linked.</summary>
        public void SetDataGatheringEnabled(bool enabled)
        {
            if (Api.DataGatheringEnabled == enabled) return;
            Api.SetDataGatheringEnabled(enabled);

            if (!enabled)
            {
                // Opt-out: shut everything down so nothing is collected or sent.
                notifSocket.Disconnect();
                ShowLinkWindow = false;
                ShowConsentWindow = false;
                Debug.Log("[GeneKerman] Data sharing disabled, mod is now inert.");
            }
            else
            {
                ShowDataPausedWindow = false;
                Debug.Log("[GeneKerman] Data sharing enabled, resuming.");
                if (Api.IsLinked)
                {
                    StartCoroutine(InitialFetch());
                    if (Api.NotificationsEnabled)
                        notifSocket.Connect();
                }
            }
        }

        /// Ask the server whether this DLL is the published latest. Fails open: a
        /// failed/unreachable check, a disabled gate, or no published version all
        /// leave the mod fully usable. An outdated client is hard-blocked until it
        /// updates (or a re-check clears it).
        public System.Collections.IEnumerator CheckVersionRoutine()
        {
            yield return Api.CheckVersion((ok, data, err) =>
            {
                if (!ok || data == null) return;   // fail open — never block on a bad check

                // Privacy/Terms version gate (server-driven). Picked up on the same
                // check the DLL gate uses. If the server now requires a newer policy
                // than the player accepted, Consent.Accepted flips false → force the
                // re-consent gate and stop transmitting until they re-accept.
                int policyVersion = MiniJSON.GetInt(data, "policy_version", 0);
                if (policyVersion > 0)
                    Consent.SetRequiredVersion(policyVersion);
                if (Api.IsLinked && !Consent.Accepted)
                    OnConsentLapsed();

                bool enabled = MiniJSON.GetBool(data, "enabled", true);
                bool upToDate = MiniJSON.GetBool(data, "up_to_date", true);

                if (!enabled || upToDate)
                {
                    if (UpdateRequired)
                    {
                        // Cleared — e.g. the player switched to a server that accepts this
                        // build. Drop the acknowledgement too so the full UI comes back and
                        // a later gate gets a fresh prompt rather than silently inheriting
                        // this session's dismissal.
                        bool wasLimited = UpdateAcknowledged;
                        UpdateRequired = false;
                        UpdateAcknowledged = false;
                        updateWindow.Hide();

                        // Hand the player back a usable interface. Coming out of limited
                        // mode the sidebar is showing panels that hold no data — nothing
                        // was fetched while gated — and an unlinked client has nothing to
                        // fetch at all, which would leave them on a panel with no obvious
                        // next step instead of on the link screen.
                        if (wasLimited)
                        {
                            if (Api.IsLinked) clientState.RefreshAll();
                            else ShowLinkWindow = true;   // this server accepts us — offer linking
                        }
                    }
                    return;
                }

                LatestVersion = MiniJSON.GetString(data, "latest_version");
                UpdateDownloadUrl = SafeDownloadUrl(MiniJSON.GetString(data, "download_url"));
                UpdateRequired = true;

                // Already dismissed this session (e.g. this is the re-check fired by a
                // server switch, and the new server rejects us too): leave the player in
                // the limited UI instead of yanking the window back over it.
                if (UpdateAcknowledged) return;

                ShowLinkWindow = false;
                updateWindow.Show(ModVersion.Current, LatestVersion, UpdateDownloadUrl);
                Debug.Log($"[GeneKerman] Update required: {ModVersion.Current} → {LatestVersion}");
            });
        }

        /// Re-run the version check (e.g. after the player updated and the window's
        /// "Re-check" button is pressed).
        public void RecheckVersion() => StartCoroutine(CheckVersionRoutine());

        /// <summary>
        /// The player pointed the mod at a different server. Everything live is still
        /// aimed at the old one, so bring it in line: the notification socket is holding
        /// an open connection to the previous host, the new server has its own version
        /// gate (which is the escape hatch out of limited mode — a server that accepts
        /// this build clears it without a restart), and it has its own idea of whether
        /// we are linked at all.
        ///
        /// Called by both front ends, so the two cannot drift apart.
        /// </summary>
        public void OnServerChanged()
        {
            notifSocket.Disconnect();   // Update() re-opens it against the new host

            // No token for this server. Linking needs a 6-digit code typed in KSP and a
            // Discord approval, so the in-game window is the only place it can happen —
            // including in browser mode, where this is what the player sees next.
            if (!Api.IsLinked)
                ShowLinkWindow = true;

            RecheckVersion();
        }

        /// Called by ApiClient when ANY gated request is rejected with 426
        /// update_required (the server-enforced version gate). Raises the same
        /// blocking "update required" window the startup check uses, so a modified
        /// DLL that skipped the startup check is still stopped on its first call.
        public void OnVersionGate(string latestVersion, string downloadUrl)
        {
            LatestVersion = latestVersion ?? "";
            UpdateDownloadUrl = SafeDownloadUrl(downloadUrl);
            if (UpdateRequired) return;   // window already up
            UpdateRequired = true;
            ShowLinkWindow = false;
            updateWindow.Show(ModVersion.Current, LatestVersion, UpdateDownloadUrl);
            Debug.Log($"[GeneKerman] Update required (server-enforced): {ModVersion.Current} → {LatestVersion}");
        }

        /// <summary>
        /// Called by ApiClient when any gated request is refused with 403 `suspended`.
        /// Raises the notice and takes the mod off the air for the duration.
        ///
        /// The token is deliberately left alone (the server keeps accepting it — see
        /// the bot's admin_user_suspend): unlinking here would drop the player onto
        /// the link screen, where the only thing on offer is to link again, which
        /// would work and change nothing.
        /// </summary>
        public void OnSuspended(string reason, double untilUnix)
        {
            // Re-raised on every refused request while the gate is up (a burst of
            // in-flight calls all come back 403 together), so refresh the details and
            // return rather than re-showing the window over itself.
            SuspensionReason = reason ?? "";
            SuspendedUntil = untilUnix;
            if (Suspended)
            {
                suspendedWindow.Show(SuspensionReason, SuspendedUntil);
                return;
            }

            Suspended = true;
            notifSocket.Disconnect();
            sidebar?.SetOpen(false);
            ShowLinkWindow = false;
            suspendedWindow.Show(SuspensionReason, SuspendedUntil);
            Debug.Log("[GeneKerman] Account suspended until " + SuspendedUntil + ": " + SuspensionReason);
        }

        /// <summary>
        /// The suspension is over (expired, or lifted early and confirmed by a
        /// re-check). Put the mod back on the air and refill the panels, which hold
        /// nothing — no fetch ran while the gate was up.
        /// </summary>
        public void ClearSuspension()
        {
            if (!Suspended) return;
            Suspended = false;
            SuspensionReason = "";
            SuspendedUntil = 0;
            suspendedWindow.Hide();
            Debug.Log("[GeneKerman] Suspension cleared, resuming.");

            if (Api.IsLinked && Api.DataGatheringEnabled && Consent.Accepted)
            {
                clientState.RefreshAll();
                if (Api.NotificationsEnabled)
                    notifSocket.Connect();
            }
            else if (!Api.IsLinked)
            {
                ShowLinkWindow = true;
            }
        }

        /// <summary>
        /// Ask the server whether the suspension still stands — the notice's
        /// "Check again" button. Reports back one sentence for it to show.
        ///
        /// A check that never reached the server leaves the gate up: "we could not
        /// ask" is not "you are free to go", and clearing on a failed request would
        /// hand the player an interface whose every button 403s.
        /// </summary>
        public void RecheckSuspension(System.Action<string> onDone)
        {
            StartCoroutine(RecheckSuspensionRoutine(onDone));
        }

        private System.Collections.IEnumerator RecheckSuspensionRoutine(System.Action<string> onDone)
        {
            yield return Api.CheckSuspension((ok, data, err) =>
            {
                if (!ok || data == null)
                {
                    if (onDone != null) onDone("Couldn't reach the server. Try again in a moment.");
                    return;
                }
                if (!MiniJSON.GetBool(data, "suspended", false))
                {
                    ClearSuspension();
                    if (onDone != null) onDone("Your access is back.");
                    return;
                }
                // Still suspended, but the reason or expiry may have been changed.
                SuspensionReason = MiniJSON.GetString(data, "reason");
                SuspendedUntil = MiniJSON.GetDouble(data, "until", SuspendedUntil);
                suspendedWindow.Show(SuspensionReason, SuspendedUntil);
                string left = UI.SuspendedWindow.Remaining(SuspendedUntil);
                if (onDone != null)
                    onDone(string.IsNullOrEmpty(left)
                        ? "Still suspended."
                        : "Still suspended, " + left + " to go.");
            });
        }

        /// <summary>
        /// Called by ApiClient every time the session token is dropped — unlink, log
        /// out everywhere, a 401, a denied device. The live notification socket is
        /// authenticated by the token that opened it, so it has to go with it.
        ///
        /// Without this, an install that unlinked one account and linked another kept
        /// the first account's stream open (Connect() reuses a live socket), and every
        /// notification addressed to the account that used to be signed in here was
        /// delivered into the feed of the one that is — reading as "someone else's
        /// contract offer arriving in my game".
        ///
        /// Deliberately at the token, not at the six call sites that drop one: the
        /// socket's identity *is* the token, so the two must never be separable.
        /// </summary>
        public void OnTokenCleared()
        {
            notifSocket?.Disconnect();
        }

        /// Called by ApiClient when a request comes back 401 — this PC's session is
        /// no longer accepted (expired, or revoked by "log out of all devices", which
        /// invalidates every token minted before it). Nothing here can retry its way
        /// out, so unlink and put the link screen up, the same terminal handling the
        /// device-denied path below does.
        public void OnSessionRevoked()
        {
            if (!Api.IsLinked) return;   // already dropped
            Api.ClearToken();
            sidebar?.SetOpen(false);
            ShowLinkWindow = true;   // drop the user straight onto the link screen
            // Transient toast, not a stored notification: like the device-denied
            // unlink, persisting it would resurface it after the user re-links.
            Toast("Session expired",
                "This PC was unlinked. Get a new code at boundlessmissions.com/account, or run /b linkcode in Discord.");
            Debug.Log("[GeneKerman] Session revoked, returned to the link screen.");
        }

        /// Called by ApiClient when the server blocks this device (device binding).
        /// Opens the "approve in Discord" prompt and polls until the user decides.
        public void OnDeviceGate(string challengeId)
        {
            if (string.IsNullOrEmpty(challengeId)) return;
            if (deviceGateActive && deviceGateChallenge == challengeId) return; // already handling
            deviceGateActive = true;
            deviceGateChallenge = challengeId;
            deviceVerifyWindow.Show(
                "A new device is using your account, so this PC is blocked for now.\n\n" +
                "Check your Discord DMs:\n" +
                "• Press \"Yes, it's me\" if you switched PCs / reinstalled.\n" +
                "• Press \"No, report it\" only if it wasn't you.\n\n" +
                "Waiting for your response…");
            StartCoroutine(DeviceGateFlow(challengeId));
        }

        /// Fired when the account owner presses "🔔 Ping this PC" in their Discord DM.
        /// Makes a loud, unmistakable on-screen alert so whoever is sitting at THIS PC
        /// knows the login attempt is theirs (and can press "Yes, it's me" in Discord).
        private void ShowDevicePing()
        {
            const string msg = "GENE KERMAN: Is this you? Someone is verifying this PC's " +
                               "login from Discord. If this is your PC, press \"Yes, it's me\" " +
                               "in your Discord DM.";
            try
            {
                ScreenMessages.PostScreenMessage(msg, 12f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch { /* ScreenMessages unavailable in this scene — popup still shows */ }
            Toast("Is this you?",
                "Someone is verifying this PC's login from Discord.\n" +
                "If this is your PC, press \"Yes, it's me\" in your Discord DM.");
            deviceVerifyWindow.Show(
                "PING RECEIVED. Someone is checking whether this PC is yours.\n\n" +
                "If you're the account owner and meant to log in here, go to your\n" +
                "Discord DM and press \"Yes, it's me\".\n\n" +
                "Waiting for your response…");
        }

        private System.Collections.IEnumerator DeviceGateFlow(string challengeId)
        {
            // Capture the poll outcome, then act on it in the coroutine body so the
            // diagnostics upload can run to completion (while still linked) before we
            // clear the token — a fire-and-forget upload would race ClearToken.
            string outcome = null, reportId = null;
            yield return Api.PollDeviceApproval(challengeId, (state, rid) =>
            {
                outcome = state;
                reportId = rid;
            }, onPing: ShowDevicePing);

            deviceGateActive = false;
            deviceGateChallenge = null;

            // Always close the verify window on a terminal outcome — it's centered
            // on the same spot as the link window, so leaving it up would sit on top
            // and swallow clicks. Terminal messages go to the non-blocking popup.
            deviceVerifyWindow.Hide();

            if (outcome == "approved")
            {
                RaiseLocalNotification("Device approved", "This PC is now trusted.");
                StartCoroutine(InitialFetch());
            }
            else if (outcome == "denied")
            {
                // If the user reported this device, upload diagnostics for the
                // moderation ticket BEFORE unlinking (so the request keeps a valid
                // token), then unlink so this device stops trying.
                if (!string.IsNullOrEmpty(reportId))
                {
                    // Ask before anything leaves this machine. The server asking is not
                    // consent: whatever host the mod points at can answer a device poll
                    // with "denied" plus a report id, and KSP.log carries the mod list,
                    // the install paths (which include the OS account name) and the
                    // system specs. The bug-report card already works this way — its
                    // "attach my KSP.log" switch is the player's own choice — and this
                    // is the same file going to the same place.
                    deviceVerifyWindow.Ask(
                        "This PC was reported from your Discord account.\n\n" +
                        "May the mod attach this PC's KSP.log to that report? It lists " +
                        "your installed mods, your install paths and your system specs. " +
                        "Nothing is sent unless you choose to send it.",
                        "Send KSP.log", "Don't send");

                    // Bounded: an unanswered question must not park the rest of the
                    // flow (unlink, link screen) forever. No answer means no.
                    float deadline = Time.realtimeSinceStartup + 120f;
                    while (deviceVerifyWindow.Answer == null &&
                           Time.realtimeSinceStartup < deadline)
                        yield return null;
                    bool sendLog = deviceVerifyWindow.Answer == true;
                    deviceVerifyWindow.Hide();

                    if (sendLog)
                    {
                        Debug.Log("[GeneKerman] Device reported, uploading diagnostics…");
                        yield return Api.UploadDeviceReport(reportId, (ok, r, s) =>
                            Debug.Log($"[GeneKerman] Device report upload: ok={ok} ({s})"));
                    }
                    else
                    {
                        Debug.Log("[GeneKerman] Device reported; the player declined to " +
                                  "attach KSP.log, so nothing was uploaded.");
                    }
                }
                Api.ClearToken();
                sidebar?.SetOpen(false);
                ShowLinkWindow = true;   // drop the user straight onto the link screen
                // Terminal unlink — keep this a transient toast. Persisting it would
                // make it resurface in the panel the next time the user re-links.
                Toast("Device not approved",
                    "This PC was unlinked. Get a new code at boundlessmissions.com/account, or run /b linkcode in Discord.");
            }
            else // expired
            {
                RaiseLocalNotification("⌛ Device check expired",
                    "Reopen the mod window to try again.");
            }
        }

        /// <summary>Open the inbox on a specific contract — what clicking a toast
        /// about one does. The sidebar owns the switching (SidebarController
        /// .ShowContract), so the panel gets its OnShown either way.</summary>
        public void OpenContractDetail(string contractId)
        {
            sidebar?.SetOpen(true);
            sidebar?.ShowContract(contractId);
        }

        /// <summary>Open the feed — where a notification's own action button lives
        /// (see LocalNotifActions).</summary>
        public void OpenNotifications()
        {
            sidebar?.SetOpen(true);
            sidebar?.ShowNotifications();
        }

        public void OpenSubmitWindow(string contractId, string mission,
            string missionType = "active_vessel", string requiredSituation = "", string requiredBody = "", string requiredModlist = "",
            RescueTargetSpec rescueTarget = null, List<string> rescueKerbals = null,
            ContractConstraints constraints = null)
        {
            submitPanel?.Open(contractId, mission, missionType, requiredSituation, requiredBody,
                requiredModlist, rescueTarget, rescueKerbals, constraints);
        }

        /// <summary>
        /// Open the "ask for more time" window on a disputed contract. The one entry
        /// point, as OpenSubmitWindow is for submission — one instance, re-pointed per
        /// contract, so a second card cannot mint a second window.
        /// </summary>
        /// <param name="onSend">Makes the caller's completion at the moment Send is
        /// pressed (ContractsPanel.Done), not before — creating it early would mark the
        /// inbox busy for as long as the calendar is up.</param>
        /// <param name="onClosed">The window went away, by any route.</param>
        public void OpenMoreTimeWindow(string contractId, string mission, string issuerName,
            string currentDue, Func<Action<bool, string>> onSend, Action onClosed)
        {
            moreTimePanel?.Open(contractId, mission, issuerName, currentDue, onSend, onClosed);
        }

        /// <summary>
        /// The account state every front end reads: the sidebar's panels, the browser
        /// bridge and the notification socket. One copy, by reference — see
        /// ClientState.cs on why it is never handed out as a snapshot.
        /// </summary>
        internal ClientState State => clientState;

        public void RunCoroutine(IEnumerator routine)
        {
            StartCoroutine(routine);
        }

        // ── Rescue vessel ops ───────────────────────────────────────────────
        //
        // A vessel can only be safely removed when it isn't the focused flight
        // vessel, so removals are queued and run at the Space Center / Tracking
        // Station. The submitted-craft map and the removal queue both live in
        // GKContractScenario so they're persisted with the save: a rescue is
        // submitted, then approved possibly days (and several restarts) later, and
        // the craft must still be found and deleted across that gap.

        /// <summary>On a "rescue_craft_removed" notification, find the craft this client
        /// submitted for that contract and queue it for removal.
        ///
        /// This is the fast path only. A notification is a transient — the player can
        /// dismiss it, and the server keeps just the newest 50 — so it can never be the
        /// thing the removal depends on. ReconcileRescueVessels is the authority; this
        /// exists so the common case happens immediately rather than at the next
        /// Space Center visit.</summary>
        /// <summary>Fetch notifications once per arrival at the Space Center or Tracking
        /// Station — the scenes where a queued removal can actually run — so an
        /// instruction whose push was lost is still acted on. Cheap by construction: one
        /// call per arrival, never on a timer.</summary>
        private void FetchNotificationsOnSceneArrival()
        {
            if (notifFetchedThisScene) return;
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
            if (Api == null || !Api.IsLinked || !Api.NotificationsEnabled) return;
            if (GKContractScenario.Instance == null) return;   // no save yet — try again next scene

            notifFetchedThisScene = true;
            StartCoroutine(CheckNotifications());
        }

        /// <summary>Hold a notification whose instruction could not be carried out yet,
        /// de-duped by id. Session-scoped on purpose: across a restart the server copy is
        /// still unread and the backlog-seeding pass picks it up again, so persisting
        /// this would only add a second source of truth for the same fact.</summary>
        private void Park(Dictionary<string, object> notif)
        {
            string id = MiniJSON.GetString(notif, "id");
            foreach (var d in deferredNotifs)
                if (MiniJSON.GetString(d, "id") == id) return;
            deferredNotifs.Add(notif);
        }

        /// <summary>Re-handle notifications parked by <see cref="HandleIncomingNotification"/>
        /// once a save is loaded and their instruction can actually be carried out.
        ///
        /// The list is copied and cleared before replaying, so a notification that defers
        /// a second time (the scenario went away again mid-drain) is re-parked by the
        /// normal path rather than mutating the list being walked.</summary>
        private void DrainDeferredNotifications()
        {
            if (deferredNotifs.Count == 0) return;
            if (GKContractScenario.Instance == null) return;

            var pending = new List<Dictionary<string, object>>(deferredNotifs);
            deferredNotifs.Clear();
            foreach (var n in pending)
                HandleIncomingNotification(n, bumpBadge: false);
        }

        /// <summary>Whether this notification carries an instruction that cannot be
        /// carried out without a loaded save — i.e. one of the two whose handlers resolve
        /// a vessel in THIS save and quietly do nothing when there isn't one.
        ///
        /// Kept as a named predicate rather than inlined, because the cost of the list
        /// falling out of step with the handlers is a silently dropped vessel removal,
        /// and a reader adding a third such notification needs somewhere obvious to
        /// register it.</summary>
        private static bool NeedsLoadedSaveToAct(Dictionary<string, object> notif)
        {
            string type = MiniJSON.GetString(notif, "type");
            return type == "rescue_craft_removed" || type == "craft_gift_accepted";
        }

        /// <returns>Settled, in the same sense as <see cref="MaybeHandleGiftAccepted"/>.
        /// Unlike that one, a contract with no recorded submission in this save IS
        /// settled: that record is per-save and its absence is answered by
        /// ReconcileRescueVessels, which re-derives the intent from the contract.</returns>
        private bool MaybeHandleRescueRemoval(Dictionary<string, object> notif)
        {
            if (notif == null) return true;
            if (MiniJSON.GetString(notif, "type") != "rescue_craft_removed") return true;

            var data = MiniJSON.GetDict(notif, "data");
            string contractId = data != null ? MiniJSON.GetString(data, "contract_id") : "";
            if (string.IsNullOrEmpty(contractId)) return true;

            var scenario = GKContractScenario.Instance;
            if (scenario == null) return false; // no save loaded — ask again once there is one

            // Peek, queue, and only then forget: the record is the sole link between the
            // contract and a craft in this save, so it outlives a queue attempt that
            // could not land.
            List<string> pids;
            if (scenario.PeekRescueSubmissions(contractId, out pids) &&
                QueueRescueHandover(pids, RescueKerbalsOf(contractId)))
                scenario.ForgetRescueSubmission(contractId);
            return true;
        }

        /// <summary>Carry out whatever a notification instructs, or report that it cannot
        /// be done yet. One dispatch point, so a notification is never handled twice —
        /// which is what happened when the parking guard and the body each called a
        /// handler.</summary>
        private bool TryHandleInstruction(Dictionary<string, object> notif)
        {
            string type = MiniJSON.GetString(notif, "type");
            if (type == "rescue_craft_removed") return MaybeHandleRescueRemoval(notif);
            if (type == "craft_gift_accepted") return MaybeHandleGiftAccepted(notif);
            return true;   // nothing to carry out
        }

        /// <summary>
        /// Queue every craft handed over for one rescue — the rescue craft and any extras
        /// sent alongside it — for removal from this save. Returns true only when all of
        /// them are queued (or already gone), i.e. when the caller may forget the record.
        ///
        /// Two things are decided here rather than at the call sites, because getting
        /// either wrong destroys something.
        ///
        /// The fate is <see cref="VesselTransfer.CrewFate.BorrowedOnly"/> for every hull:
        /// the ships and the kerbals picked up are the hand-over, the rescuer's own
        /// pilots are not, and they go back to the roster as Available.
        ///
        /// The contract's kerbal list rides on the FIRST entry only. RemoveContractCrew
        /// hunts those names wherever they are in the save, so attaching them to an
        /// extra would have that extra's removal kill the rescued crew off the rescue
        /// craft — and if the rescue craft's own removal is deferred (the player is
        /// flying it), it would do so while the craft, and the contract, are still here.
        /// </summary>
        private bool QueueRescueHandover(List<string> pids, List<string> contractCrew)
        {
            if (pids == null || pids.Count == 0) return false;
            bool all = true;
            for (int i = 0; i < pids.Count; i++)
            {
                bool queued = QueueRescueVesselRemoval(
                    pids[i],
                    crewFate: VesselTransfer.CrewFate.BorrowedOnly,
                    crewNames: i == 0 ? contractCrew : null);
                if (!queued) all = false;
            }
            return all;
        }

        /// <summary>On a "craft_gift_accepted" notification, make sure the quicksent
        /// vessel it names really is out of this save.
        ///
        /// The normal case is a no-op: the vessel was queued out at send time and is
        /// long gone. What this catches is the rollback — a quickload or revert after
        /// the send restores the vessel AND wipes the queued removal from the scenario,
        /// while the offer lives on server-side. Quicksends have no contract for
        /// ReconcileRescueVessels to re-derive that intent from, so the acceptance echo
        /// (which carries the pid we reported at send time) is the backstop. No crew
        /// list survives the rollback; the hull removal still settles everyone aboard,
        /// which after a rollback is exactly where they are.</summary>
        /// <returns>True when the instruction is settled and need not be retried: either
        /// it was carried out, or there was never anything to do. False means "could not
        /// tell yet" — the caller parks it and asks again on the next Space Center
        /// arrival.
        ///
        /// The distinction that matters is the vessel not being found. That reads as
        /// "already gone: the normal case", and usually is — but it is equally what a
        /// DIFFERENT save being loaded looks like, and the two cannot be told apart from
        /// the pid alone. Guessing "settled" there is how §3.19 lost a hand-over. So it
        /// is reported unsettled and retried; the cost of being wrong is one cheap
        /// re-check per Space Center visit for the rest of the session, and the cost of
        /// the other answer is a duplicated ship.</returns>
        private bool MaybeHandleGiftAccepted(Dictionary<string, object> notif)
        {
            if (notif == null) return true;
            if (MiniJSON.GetString(notif, "type") != "craft_gift_accepted") return true;

            var data = MiniJSON.GetDict(notif, "data");
            string pid = data != null ? MiniJSON.GetString(data, "vessel_pid") : "";
            if (string.IsNullOrEmpty(pid)) return true;         // blueprint gift, or an old server
            if (GKContractScenario.Instance == null) return false;   // no save loaded — ask again

            // Corroborate before destroying anything. This is the shape
            // MaybeHandleRescueRemoval has always had — it looks its pids up in
            // PeekRescueSubmissions, so the server chooses which RECORDED hand-over to
            // settle rather than an arbitrary target — and the reason it matters more
            // here is the fate: LeavesWithCraft kills everyone aboard and removes them
            // from the roster, which KSP has no UI to undo. VesselExists is not a guard;
            // it says "this vessel is in the current save", which is the precondition
            // for the damage rather than against it.
            //
            // The record is per (save, pid, server) and lives in PluginData, not in the
            // scenario: the very event this echo repairs — a quickload after the send —
            // rolls the scenario back. Fails closed, so a lost record costs the player a
            // ship they remove themselves rather than a ship removed for them.
            string origin = Api != null ? Api.ServerUrl : null;
            if (!QuicksendLedger.WasHandedOver(pid, origin))
            {
                // Recorded, but under a different save folder → the player has another
                // career open. That is the same "cannot tell yet" the missing-vessel
                // branch below reports, and for the same reason: guessing "settled"
                // there is how §3.19 lost a hand-over.
                if (QuicksendLedger.WasHandedOverFromAnySave(pid, origin)) return false;

                Debug.LogWarning($"[GeneKerman] Ignoring a craft_gift_accepted for vessel {pid}: " +
                                 "this client has no record of handing it over to this server. " +
                                 "Nothing was removed.");
                return true;   // settled — there is nothing here for us to carry out
            }

            if (!VesselTransfer.VesselExists(pid)) return false;     // gone, or the wrong save

            Debug.Log($"[GeneKerman] Accepted quicksend {pid} is still in this save " +
                      "(rolled-back removal?), re-queueing.");
            QueueRescueVesselRemoval(pid, crewFate: VesselTransfer.CrewFate.LeavesWithCraft);
            return true;
        }

        /// <summary>Whether a removal for this pid is still queued — including the
        /// half-done state where the hull is gone but the entry is still settling
        /// crew who stepped off it.</summary>
        public bool HasQueuedRemoval(string pid)
        {
            var pending = GKContractScenario.Instance?.PendingRescueRemovals;
            return pending != null && !string.IsNullOrEmpty(pid) && pending.ContainsKey(pid);
        }

        /// <summary>The sentence that refuses sending a vessel whose removal is already
        /// queued, or null when it may go. A ship the player has already given away
        /// (quicksend, rescue issue) is still in the save while its removal is
        /// deferred — they are flying it — and nothing stopped it being exported a
        /// second time: on 2026-08-30 the same hull went out as a 2-part quicksend
        /// and, eleven minutes and a Set Orbit later, as a 14-part rescue wreck. Two
        /// recipients then held two different ships that were both "this one", and
        /// only one removal ever ran. Every path that hands a live vessel to somebody
        /// else asks this first.</summary>
        public string PendingRemovalRefusal(Vessel v)
        {
            if (v == null || !HasQueuedRemoval(v.id.ToString())) return null;
            return $"\"{v.vesselName}\" has already been sent and is waiting to leave your save " +
                   "(it goes when you leave it, at the latest at the Space Center). It can't be " +
                   "sent again.";
        }

        /// <summary>Drop a queued removal that must no longer run — the recipient
        /// declined the quicksend, so the vessel it targets stays ours. Returns true
        /// when an entry was actually removed.</summary>
        public bool CancelQueuedRemoval(string pid)
        {
            var pending = GKContractScenario.Instance?.PendingRescueRemovals;
            if (pending == null || string.IsNullOrEmpty(pid) || !pending.ContainsKey(pid))
                return false;
            pending.Remove(pid);
            // Persist the cancellation where a save is allowed (same rule as the
            // removal pass): a crash before KSP's next autosave would otherwise
            // resurrect the entry and delete a ship the player was told is staying.
            if (!HighLogic.LoadedSceneIsFlight) VesselTransfer.SaveNow();
            return true;
        }

        /// <summary>Queue a vessel (by pid) for removal at the next safe scene. Pass a
        /// vesselName when the caller already knows it (e.g. the active vessel); otherwise
        /// it's resolved from the pid while the craft still exists.
        ///
        /// <paramref name="crewFate"/> says what the crew aboard are owed — the default
        /// gives everyone up with the craft, which is right for the issuer of a rescue
        /// and wrong for its rescuer. See <see cref="VesselTransfer.CrewFate"/>.
        ///
        /// Returns true once the vessel is either queued or already gone — i.e. the
        /// caller may forget about it. False means nothing was recorded and the caller
        /// must keep whatever state would let it try again.
        ///
        /// <paramref name="returnToSpaceCenter"/>: when the removal has to defer
        /// because the player is flying the very vessel they just gave away, leave
        /// flight for the Space Center so it runs now instead of whenever they next
        /// wander there — every minute the ship lingers is a window where a cancel or
        /// decline can cross the un-run removal and duplicate it. Only the two
        /// explicit send actions pass true (issuing a rescue, quicksending the active
        /// vessel): the player just chose to part with the ship, so taking them home
        /// is finishing what they asked. The backstop callers (approval
        /// notifications, rollback re-queues) must never — yanking someone out of an
        /// unrelated flight because a friend pressed Accept is hostile.</summary>
        public bool QueueRescueVesselRemoval(string pid, string vesselName = null,
            VesselTransfer.CrewFate crewFate = VesselTransfer.CrewFate.LeavesWithCraft,
            IEnumerable<string> crewNames = null, bool returnToSpaceCenter = false)
        {
            if (string.IsNullOrEmpty(pid)) return false;
            var pending = GKContractScenario.Instance?.PendingRescueRemovals;
            if (pending == null)
            {
                // No live scenario means no save to queue against, and nowhere to
                // persist the intent — the caller has to retry. Loud, because silently
                // returning here is how a craft ends up staying in a save forever.
                Debug.LogWarning($"[GeneKerman] Rescue removal for pid {pid} could not be " +
                                 "queued: no scenario module (no save loaded). Will retry.");
                return false;
            }

            if (!pending.ContainsKey(pid))
                pending[pid] = new PendingRescueRemoval
                {
                    Name = string.IsNullOrEmpty(vesselName)
                        ? VesselTransfer.GetVesselName(pid) : vesselName,
                    CrewFate = crewFate,
                };

            // The crew list is what makes the removal exploit-proof (a kerbal who
            // stepped off the hull still leaves by name), so a caller that has it
            // enriches even an entry that already existed without one.
            if (crewNames != null)
            {
                var entry = pending[pid];
                foreach (var n in crewNames)
                    if (!string.IsNullOrEmpty(n) && !entry.Crew.Contains(n))
                        entry.Crew.Add(n);
            }

            // Carry it across the next scene change. The scenario module is destroyed and
            // rebuilt on every transition and its entries do not survive that (see
            // GKContractScenario.CarryRemember); without this a removal queued against
            // the vessel being flown — which cannot be executed until the player leaves
            // it — is silently dropped after they were told it would happen.
            GKContractScenario.CarryRemember(pid, pending[pid]);

            ProcessPendingRescueRemovals(); // run now wherever it's currently safe

            // Still queued → we couldn't delete it yet (it's the craft the player is
            // flying, or something aboard is still being untangled). Warn them so it
            // doesn't silently vanish later — unless we're about to take them to the
            // Space Center, where "scheduled for later" would be false the moment
            // they read it.
            if (pending.ContainsKey(pid))
            {
                if (returnToSpaceCenter && CanAutoReturnToSpaceCenter(pid))
                {
                    RaiseLocalNotification("Craft handed over",
                        $"\"{pending[pid].Name}\" now belongs to its recipient, returning " +
                        "to the Space Center to complete the hand-over.");
                    StartCoroutine(ReturnToSpaceCenterRoutine(pid));
                }
                else
                    RaiseLocalNotification("Craft scheduled for removal",
                        $"\"{pending[pid].Name}\" will be deleted when you leave it: at the " +
                        "latest, on your next visit to the Space Center.");
            }

            return true;
        }

        /// <summary>Whether leaving flight for the Space Center right now would be the
        /// same exit the pause menu offers: we're in flight, the deferred vessel is the
        /// one being flown (leaving flight for any *other* pending removal is
        /// ProcessPendingRescueRemovals' job, not a scene change's), and KSP itself
        /// says the state is serializable. Not clear-to-save (moving over terrain,
        /// mid-atmosphere) falls back to the classic deferral — forcing a save stock
        /// would refuse trades a lingering hull for a corrupted one.</summary>
        private static bool CanAutoReturnToSpaceCenter(string pid)
        {
            if (!HighLogic.LoadedSceneIsFlight) return false;
            var v = FlightGlobals.ActiveVessel;
            if (v == null || v.id.ToString() != pid) return false;
            // log: true — this is the decision point, and KSP's own "Cannot save"
            // line next to our notification is what made the 2026-08-30 case
            // diagnosable from KSP.log alone. See LiveHandoverRefusal.
            return LiveHandoverRefusal(log: true) == null;
        }

        /// <summary>Why handing the *active vessel* over cannot complete right now, or
        /// null when it can. The same read <see cref="CanAutoReturnToSpaceCenter"/>
        /// makes, minus the pid check, so a caller can ask before it has sent anything.
        ///
        /// This is the gate on a live-vessel quicksend, and it exists because only the
        /// auto-return branch is durable. That branch removes the ship inside a scene
        /// change KSP saves on; the deferred branch leaves the intent in the scenario's
        /// memory until the game next writes, and a quickload in that window rolls it
        /// back with nothing to re-derive it from — a quicksend has no contract for
        /// ReconcileRescueVessels to read, and the recipient's acceptance echo only
        /// helps if they accept. Observed in FAK1 on 2026-08-30: sent at 19:27:29 with
        /// the ship flagged landed at 861,594 km (so ClearToSave refused), quickloaded
        /// at 19:28:07 before any save, and the Space Center visit at 19:31:45 found an
        /// empty queue — the server offered a ship the sender still had.
        ///
        /// Refusing up front is the honest failure: nothing has been uploaded, so
        /// nothing is inconsistent, and the player gets a sentence they can act on
        /// instead of a promise that quietly expires.
        ///
        /// Deliberately NOT used when issuing a rescue: that intent is re-derivable
        /// from the contract, so ReconcileRescueVessels heals the deferred branch there
        /// and the same gate would refuse a send that was never at risk.
        ///
        /// <paramref name="log"/> picks KSP's logging overload. Default false, because
        /// the UI polls this about once a second to decide whether to offer the button
        /// and the logging overload writes a "Cannot save" line on every call.</summary>
        public static string LiveHandoverRefusal(bool log = false)
        {
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
                return "You have to be flying a vessel to hand it over.";

            ClearToSaveStatus status;
            // ClearToSave dereferences ActiveVessel without a null check of its own, so
            // the guard above is load-bearing rather than defensive tidiness.
            try { status = FlightGlobals.ClearToSave(log); }
            catch (Exception)
            {
                return "KSP can't say whether this flight is settled enough to hand the " +
                       "vessel over. Try again in a moment.";
            }

            if (status == ClearToSaveStatus.CLEAR) return null;
            return "This vessel can't be handed over right now: " + NotClearReason(status) +
                   " A hand-over has to take the ship out of your save the moment you send " +
                   "it, and that needs the flight settled enough for KSP to save.";
        }

        /// <summary>Stock's clear-to-save refusals, said as something to do about it.
        /// Stock's own strings are phrased about saving ("Cannot &lt;&lt;1&gt;&gt; while
        /// flying in atmosphere"), which reads as a non-sequitur to someone pressing
        /// Send. Every member of the enum is covered; the default is kept anyway, since
        /// a KSP update adding one must degrade to a vague sentence rather than to a
        /// blank one.</summary>
        private static string NotClearReason(ClearToSaveStatus status)
        {
            switch (status)
            {
                case ClearToSaveStatus.NOT_IN_ATMOSPHERE:
                    return "it's flying in atmosphere — land it or leave the air first.";
                case ClearToSaveStatus.NOT_UNDER_ACCELERATION:
                    return "it's under acceleration — let it coast first.";
                case ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE:
                    // Deliberately does not promise that stopping will help. A craft on
                    // wheels reports this state *permanently*: measured on a stock
                    // Aeris 4A parked at PRELAUNCH on the runway with the brakes on,
                    // KSP refused for six attempts across two minutes and never cleared.
                    // The old wording ("bring it to a stop first") described a temporary
                    // condition and sent the player to wait out something that never
                    // ends. A launchpad craft clears immediately, so naming the landing
                    // gear is the actionable half.
                    return "it's moving over the surface — bring it to a stop. A craft "
                         + "resting on wheels can report this no matter how still it "
                         + "looks; if it will not settle, hand it over from the launchpad "
                         + "or from orbit instead.";
                case ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH:
                    return "it's about to crash.";
                case ClearToSaveStatus.NOT_WHILE_ON_A_LADDER:
                    return "someone is on a ladder — get them aboard or onto the ground first.";
                case ClearToSaveStatus.NOT_WHILE_THROTTLED_UP:
                    return "the throttle is up — cut it first.";
                case ClearToSaveStatus.ORBIT_EVENT_IMMINENT:
                    return "an orbit change is imminent — wait until it's through.";
                default:
                    return "KSP won't let this flight be saved as it stands.";
            }
        }

        /// <summary>Save and leave for the Space Center, exactly as the pause menu's
        /// "Space Center" button does — the arrival Update then runs the pending
        /// removal in a scene where it's terminal. The pause lets the "handed over"
        /// toast register before the scene goes; every condition is re-checked after
        /// it because the player keeps control in the meantime (they may have left
        /// flight themselves, or put the vessel somewhere no longer clear to save —
        /// then the classic "next Space Center visit" promise still holds, so just
        /// fall back to it).</summary>
        /// <summary>
        /// Leave flight for the Space Center after a submission, the way the hand-over
        /// paths do — the difference being that this one has nothing to remove.
        ///
        /// A submission hands nothing over at the time it is made: the rescuer's craft is
        /// only removed once the issuer *approves*, which is what RecordRescueSubmission
        /// writes down. So there is no queued removal to wait for and no correctness
        /// reason to leave flight at all; this is purely so submitting feels like the
        /// other two outcomes rather than dropping the player back into a flight they are
        /// finished with.
        ///
        /// It still refuses when KSP would refuse to save. That is not a hedge: leaving
        /// flight writes the save, and forcing one stock would decline is the trade
        /// <see cref="CanAutoReturnToSpaceCenter"/> already declines to make. The cost of
        /// stopping here is only that the player leaves flight themselves — nothing is
        /// lost, unlike the hand-over case where a refused save leaves a hull behind.
        /// </summary>
        public void ReturnToSpaceCenterAfterSubmit()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;   // editor submits have no flight
            StartCoroutine(ReturnAfterSubmitRoutine());
        }

        private IEnumerator ReturnAfterSubmitRoutine()
        {
            // The same beat the hand-over routine waits, so the confirmation notification
            // is on screen before the scene starts changing.
            yield return new WaitForSeconds(1.5f);
            if (!HighLogic.LoadedSceneIsFlight) yield break;
            if (FlightGlobals.ActiveVessel == null) yield break;

            ClearToSaveStatus status;
            try { status = FlightGlobals.ClearToSave(true); }
            catch (Exception) { yield break; }
            if (status != ClearToSaveStatus.CLEAR)
            {
                // Said out loud rather than silently skipped: a return that usually
                // happens and sometimes does not, with no explanation, reads as a bug.
                RaiseLocalNotification("Submitted",
                    "Your submission is in. " + NotClearReason(status) +
                    " Leave flight when you're ready — nothing is waiting on it.");
                yield break;
            }

            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }

        private IEnumerator ReturnToSpaceCenterRoutine(string pid)
        {
            yield return new WaitForSeconds(1.5f);
            if (!HasQueuedRemoval(pid) || !CanAutoReturnToSpaceCenter(pid)) yield break;
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }

        /// <summary>Record the craft a rescuer submitted, so it can be removed once
        /// the issuer approves and it's delivered to them.</summary>
        public void RecordRescueSubmission(string contractId, string pid)
        {
            GKContractScenario.Instance?.RecordRescueSubmission(contractId, pid);
        }

        /// <summary>Record every craft a rescuer handed over for a contract — the rescue
        /// craft first, then the extras sent with it. Replaces the previous record: only
        /// the newest submission is ever delivered, so an older attempt's hulls must not
        /// still be queued for deletion when this one is approved.</summary>
        public void RecordRescueSubmission(string contractId, IEnumerable<string> pids)
        {
            GKContractScenario.Instance?.RecordRescueSubmission(contractId, pids);
        }

        /// <summary>The tagged kerbal names a rescue contract hands over, read from the
        /// cached contract list — on the rescuer's side these are the roster names
        /// exactly ("{issuer}'s {name}"). Null when the contract isn't in the cache;
        /// the removal then settles hull crew only, and the Space Center ghost sweep
        /// remains the backstop.
        ///
        /// "Exactly" is not free: if this save already held one of those names the import
        /// renamed the arrival, and the server's list still says what it always said. So
        /// the list is put through the wreck record's rename map before it leaves here —
        /// a no-op on every import that collided with nothing, and the difference between
        /// settling the stranded crew and hunting kerbals who do not exist.</summary>
        private List<string> RescueKerbalsOf(string contractId)
        {
            var list = clientState?.ContractList;
            if (list == null || string.IsNullOrEmpty(contractId)) return null;
            foreach (var o in list)
            {
                var c = o as Dictionary<string, object>;
                if (c == null || MiniJSON.GetString(c, "contract_id") != contractId) continue;
                var names = new List<string>();
                foreach (var k in MiniJSON.GetList(c, "rescue_kerbals"))
                    if (k != null && !string.IsNullOrEmpty(k.ToString())) names.Add(k.ToString());
                if (names.Count == 0) return null;
                return GKContractScenario.Instance?.LocalRescueCrewNames(contractId, names) ?? names;
            }
            return null;
        }

        /// <summary>
        /// Check both halves of the rescue hand-over against the server's contract list
        /// and queue anything that should have left this save but didn't.
        ///
        /// This is the backstop that makes the whole thing safe to get wrong once. Every
        /// other trigger is a single event that has to land at the right moment — a
        /// notification the player can dismiss before the game ever sees it, or a queue
        /// entry that a revert-to-launch rolls back while the contract on the server
        /// stays live. Contract state is the only thing that survives all of that, and
        /// it says everything we need:
        ///
        ///   • Issued a rescue (is_outgoing) that the server still knows about → that
        ///     craft is the rescuer's now, so a copy of it here is a removal that never
        ///     ran. The pid comes back from the server precisely so this can be checked
        ///     against a save that has forgotten it.
        ///   • Submitted a craft for someone's rescue and that contract is completed →
        ///     it has been delivered to them; ours goes.
        ///   • Submitted for a contract the list no longer carries (cancelled) → the
        ///     craft is staying, so drop the record rather than keep one we can never
        ///     resolve.
        ///
        /// Idempotent by construction: it re-derives what should be true instead of
        /// tracking what has been done, so a repeated pass costs a lookup and nothing else.
        /// </summary>
        private IEnumerator ReconcileRescueVessels()
        {
            if (GKContractScenario.Instance == null) yield break;

            rescueReconcileRunning = true;
            List<object> contracts = null;
            yield return Api.GetActiveContracts((ok, data, err) =>
            {
                if (ok) contracts = MiniJSON.GetList(data, "contracts");
            });
            rescueReconcileRunning = false;

            if (contracts == null) yield break;

            // The request took frames; the player may have left for the VAB (no scenario)
            // or into flight (nothing removable) in the meantime. Re-read rather than
            // acting on what was true when we asked.
            var scenario = GKContractScenario.Instance;
            if (scenario == null) yield break;

            var rescueStatus = new Dictionary<string, string>();
            var rescueCrews = new Dictionary<string, List<string>>();
            foreach (var o in contracts)
            {
                var c = o as Dictionary<string, object>;
                if (c == null) continue;
                if (MiniJSON.GetString(c, "mission_type") != "rescue") continue;

                string cid = MiniJSON.GetString(c, "contract_id");
                if (string.IsNullOrEmpty(cid)) continue;
                rescueStatus[cid] = MiniJSON.GetString(c, "status");
                var kerbalNames = new List<string>();
                foreach (var k in MiniJSON.GetList(c, "rescue_kerbals"))
                    if (k != null && !string.IsNullOrEmpty(k.ToString())) kerbalNames.Add(k.ToString());
                // Through the wreck record's rename map for the same reason as
                // RescueKerbalsOf: the server's list cannot know what this save called
                // them once an arriving name collided with one already in the roster.
                rescueCrews[cid] = scenario.LocalRescueCrewNames(cid, kerbalNames);

                if (!MiniJSON.GetBool(c, "is_outgoing")) continue;

                // Issuer side. Safe against a self-issued rescue: an imported wreck is
                // always given a fresh pid (VesselTransfer.PrepareInnerNode), so this can
                // only ever match the original the contract was cut from.
                string issuedPid = MiniJSON.GetString(c, "rescue_pid");
                if (string.IsNullOrEmpty(issuedPid)) continue;   // pre-dates the field
                if (!VesselTransfer.VesselExists(issuedPid)) continue;

                Debug.Log($"[GeneKerman] Reconcile: rescue {cid} is live but its craft " +
                          $"(pid {issuedPid}) is still in this save, queueing removal.");
                // Issuer side: the stranded crew are what was issued, so they go too —
                // by name as well as by hull, or one who EVA'd off beforehand stays
                // behind while their copy rides the contract. The contract carries them
                // tagged ("{me}'s {name}"); this roster holds them bare.
                var issuedCrew = new List<string>();
                foreach (var k in MiniJSON.GetList(c, "rescue_kerbals"))
                    if (k != null) issuedCrew.Add(VesselTransfer.StripOwnershipTag(k.ToString()));
                QueueRescueVesselRemoval(issuedPid,
                    crewFate: VesselTransfer.CrewFate.LeavesWithCraft,
                    crewNames: issuedCrew);
            }

            // Rescuer side. Iterating a copy of the keys, since both branches mutate the map.
            foreach (string cid in scenario.OutstandingRescueSubmissions())
            {
                string status;
                if (!rescueStatus.TryGetValue(cid, out status))
                {
                    // Gone from the list entirely — cancelled, so the craft stays ours.
                    Debug.Log($"[GeneKerman] Reconcile: rescue {cid} is no longer active; " +
                              "keeping the submitted craft and dropping its record.");
                    scenario.ForgetRescueSubmission(cid);
                    continue;
                }
                if (status != "completed") continue;   // not handed over yet

                List<string> pids;
                if (!scenario.PeekRescueSubmissions(cid, out pids)) continue;

                Debug.Log($"[GeneKerman] Reconcile: rescue {cid} was approved, queueing " +
                          $"removal of the {pids.Count} craft we submitted " +
                          $"(pids {string.Join(", ", pids.ToArray())}).");
                // Rescuer side: the ships and the kerbals we picked up are the hand-over.
                // The pilots who flew them there are not, and stay in our roster. The
                // contract's tagged names ride along so a rescued kerbal who stepped
                // off the craft still leaves by name (BorrowedOnly keeps our own out) —
                // on the rescue craft's entry alone; see QueueRescueHandover.
                List<string> handedOver;
                rescueCrews.TryGetValue(cid, out handedOver);
                if (QueueRescueHandover(pids, handedOver))
                    scenario.ForgetRescueSubmission(cid);
            }
        }

        /// <summary>Clear out borrowed kerbals whose craft is no longer in this save,
        /// once per arrival at the Space Center or Tracking Station. Older builds left
        /// them behind whenever a queued removal found its craft already gone, and the
        /// residue is not self-healing: it inflates KSP's active-crew count and narrows
        /// the name space its applicant generator draws from.</summary>
        private void SweepRosterOnce()
        {
            if (rosterSwept) return;
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION)
                return;   // a scene where the roster is settled and a save is safe
            // No scenario means no save loaded. It also means no immunity records, and
            // the sweep must never run without those — it would delete the very crew the
            // emergency freeze is holding. Being in a loaded scene at all is what
            // guarantees they are read: KSP runs OnLoad during the scene load, well
            // before this addon's first Update in it.
            if (GKContractScenario.Instance == null) return;

            rosterSwept = true;
            if (VesselTransfer.PurgeBorrowedGhostCrew() > 0) VesselTransfer.SaveNow();
            // Hand back any profession whose mod is installed again *before* reporting
            // what is still broken, or a roster this pass just healed would be announced
            // as broken in the same visit.
            RestoreRepairedTraits();
            ReportUnresolvableTraits();
            // After the Dead/Missing sweep above, so anything it already took is not
            // offered to the player a second time as something to release.
            ReportOrphanedBorrowedCrew();
        }

        /// <summary>Say out loud when the roster holds a kerbal whose profession nothing
        /// installed defines. KSP's own failure for this is a NullReference part-way
        /// through drawing the Astronaut Complex, which names neither the kerbal nor the
        /// trait — so the player is left with a screen that looks broken for no reason.
        /// Once per session: it is a property of the save, not of the visit.</summary>
        /// <summary>Offer to release borrowed kerbals stranded in the roster: no craft
        /// crews them, no emergency freeze holds them, and no live contract speaks for
        /// them. Reported once per session, like the trait warning, because it is a
        /// standing condition rather than an event.
        ///
        /// Reported rather than swept, and that is the whole design: see
        /// VesselTransfer.OrphanedBorrowedCrew for the rescue-in-progress case an
        /// automatic purge would destroy. What makes them worth mentioning at all is
        /// that the damage is invisible — KSP's applicant generator refuses any name
        /// that is a substring of an existing roster entry, silently, so the player sees
        /// an Astronaut Complex with nobody to hire and nothing that explains it.</summary>
        private bool orphanCrewWarningShown;

        private void ReportOrphanedBorrowedCrew()
        {
            if (orphanCrewWarningShown) return;
            var stranded = VesselTransfer.OrphanedBorrowedCrew();
            if (stranded.Count == 0) return;

            orphanCrewWarningShown = true;
            Debug.LogWarning($"[GeneKerman] {stranded.Count} borrowed kerbal(s) are stranded in " +
                             $"this roster with no craft and no contract: " +
                             $"{string.Join(", ", stranded.ToArray())}. They count against the " +
                             "astronaut-complex hire limit and block their own base names from " +
                             "the applicant generator; press Release stranded crew to drop them.");

            RaiseLocalNotification("Crew stuck in your roster",
                $"{stranded.Count} kerbal(s) borrowed from other players are stuck here with no " +
                $"craft and no active contract: {NameList(stranded, 6)}. They take up astronaut " +
                "complex slots and quietly block those names from ever being offered to you as " +
                "applicants. Press Release stranded crew to let them go — they come back if " +
                "their craft is ever imported again.",
                null, LocalNotifActions.ReleaseOrphanCrew);
        }

        private void ReportUnresolvableTraits()
        {
            if (traitWarningShown) return;
            var broken = VesselTransfer.FindUnresolvableTraitCrew();
            if (broken.Count == 0) return;

            traitWarningShown = true;
            Debug.LogWarning($"[GeneKerman] {broken.Count} kerbal(s) have a profession no installed " +
                             $"mod defines: {string.Join(", ", broken.ToArray())}. KSP throws while " +
                             "drawing any crew list containing them (Astronaut Complex, crew " +
                             "assignment); reinstall the mod that adds the profession, or press " +
                             "Fix professions on the notification.");

            // Name them in the message rather than sending the player to KSP.log: the
            // notification now carries the button that fixes this, and a fix is not a
            // thing to press without seeing who it applies to.
            RaiseLocalNotification("Unknown crew professions",
                $"{broken.Count} kerbal(s) in this save have a profession no installed mod " +
                $"defines: {NameList(broken, 6)}. The Astronaut Complex will fail to draw while " +
                "they are in the roster. Reinstall the mod that adds their profession, or press " +
                "Fix professions to give them a local one (reversible; the original comes back " +
                "if the mod does).",
                null, LocalNotifActions.RepairTraits);
        }

        /// <summary>Comma-separated, capped — a roster can hold dozens and a toast that
        /// lists all of them says less than one that lists a few.</summary>
        private static string NameList(List<string> names, int max)
        {
            if (names.Count <= max) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, max).ToArray()) +
                   $" (+{names.Count - max} more)";
        }

        /// <summary>Undo earlier repairs whose mod has been installed again, and say so.
        /// Silent when there is nothing to hand back, which is the normal case.</summary>
        private void RestoreRepairedTraits()
        {
            var restored = TraitRepair.RestoreRecovered();
            if (restored.Count == 0) return;

            RaiseLocalNotification("Crew professions restored",
                $"{restored.Count} kerbal(s) got their original profession back now that the mod " +
                $"defining it is installed: {NameList(restored, 6)}.");
        }

        /// <summary>How long a non-flight scene must have been up before a queued removal
        /// is executed in it. Long enough for KSP to finish wiring the orbit renderers a
        /// deletion would otherwise strand, short enough that the player reaches the Space
        /// Center and sees the craft gone as promised.</summary>
        private const float RemovalSceneSettleSeconds = 4f;

        private static GameScenes removalScene = (GameScenes)(-1);
        private static float removalSceneSince;

        private static bool SceneSettledForRemoval()
        {
            var scene = HighLogic.LoadedScene;
            if (scene != removalScene)
            {
                removalScene = scene;
                removalSceneSince = Time.realtimeSinceStartup;
                return false;
            }
            return Time.realtimeSinceStartup - removalSceneSince >= RemovalSceneSettleSeconds;
        }

        /// <summary>Paces in-flight removal retries — see ProcessPendingRescueRemovals.</summary>
        private float nextFlightRemovalPass;

        private void ProcessPendingRescueRemovals()
        {
            var pending = GKContractScenario.Instance?.PendingRescueRemovals;
            if (pending == null || pending.Count == 0) return;

            // Flight now processes too, so a hand-over doesn't sit in the save (with
            // an exploitable window: EVA the crew off and they used to survive the
            // eventual removal) until the player happens to visit the Space Center.
            // In flight only vessels that are NOT loaded are touched: Die() on a
            // loaded craft detonates it in front of the player, and the focused
            // vessel is refused by RemoveVesselFromSave anyway.
            bool inFlight = HighLogic.LoadedSceneIsFlight;
            if (!inFlight &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION)
                return;

            // This is called from Update. At the Space Center a pass terminal-dequeues
            // everything it can and the queue empties; in flight an entry can legally
            // wait (craft still loaded, kerbal on EVA), so pace the retries or every
            // deferral becomes a per-frame log line and roster scan.
            if (inFlight)
            {
                if (Time.realtimeSinceStartup < nextFlightRemovalPass) return;
                nextFlightRemovalPass = Time.realtimeSinceStartup + 10f;
            }
            else if (!SceneSettledForRemoval())
            {
                // Let the scene finish building before deleting anything out of it.
                //
                // Measured: removing the vessel the player had just been flying, ~100 ms
                // into the Space Center load, left KSP with an orbit renderer pointing at
                // a dead vessel — OrbitRendererBase.LateUpdate then threw a
                // NullReferenceException every frame (66,000 of them), the scene camera
                // never finished positioning, and the game was effectively hung. The
                // removal itself was correct; the timing was not.
                //
                // Only the non-flight path waits. In flight the pass already skips loaded
                // vessels and is paced by nextFlightRemovalPass, and the vessel being
                // flown is refused outright.
                return;
            }

            // Removed = we deleted it (announce); NotFound = already gone (dequeue
            // silently). Both are terminal for the hull — but an entry only leaves the
            // queue once its *crew list* is settled too: RemoveContractCrew hunts the
            // contract's kerbals by name wherever they are, and a kerbal it must defer
            // (on EVA next to the player) keeps the entry alive for the next pass.
            // Deferred/Failed hulls stay queued as before.
            //
            // Saving is deferred to the end for both halves of the problem it causes
            // per-vessel: N removals meant N full game saves, and each of those ran
            // while the entry it had just handled was still in the queue — so the file
            // on disk described a removal that had already happened. Dequeue first,
            // then write once, and the save is a true picture of the work done.
            var removed = new List<string>();
            var gone = new List<string>();
            foreach (var pid in new List<string>(pending.Keys))
            {
                var entry = pending[pid];
                var fate = entry != null ? entry.CrewFate : VesselTransfer.CrewFate.LeavesWithCraft;

                if (inFlight)
                {
                    var v = VesselTransfer.FindVessel(pid);
                    if (v != null && v.loaded) continue;   // too close — next pass
                }

                var result = VesselTransfer.RemoveVesselFromSave(pid, fate, persist: false);
                if (result != VesselTransfer.RemovalResult.Removed &&
                    result != VesselTransfer.RemovalResult.NotFound)
                    continue;

                bool crewSettled = entry == null ||
                    VesselTransfer.RemoveContractCrew(entry.Crew, fate);

                if (!crewSettled)
                {
                    // Hull handled; somebody walked off and can't be settled yet. The
                    // entry stays queued — the next pass finds the hull NotFound and
                    // retries only the crew, until the list comes back clean.
                    Debug.Log($"[GeneKerman] Rescue removal {pid}: hull done, crew pending, kept queued.");
                    continue;
                }

                if (result == VesselTransfer.RemovalResult.Removed) removed.Add(pid);
                else gone.Add(pid);
            }
            if (removed.Count == 0 && gone.Count == 0) return;

            var announce = new List<string>();
            foreach (var pid in removed)
            {
                var entry = pending[pid];
                announce.Add(entry != null ? entry.Name : pid);
                pending.Remove(pid);
                // Dequeue the carry with it, or the next scene change would restore an
                // entry this pass has already finished with.
                GKContractScenario.CarryForget(pid);
            }
            foreach (var pid in gone)
            {
                pending.Remove(pid);
                GKContractScenario.CarryForget(pid);
            }

            // A craft that was already gone (the NotFound case) never got the chance to
            // settle its crew, so its borrowed kerbals are still in the roster with
            // nothing to belong to. Sweep them now, while we are about to save anyway.
            VesselTransfer.PurgeBorrowedGhostCrew();

            // The queue lives in the scenario, so this write persists the dequeues as
            // well as the destroyed vessels — the two must not be able to disagree.
            // In flight the write is skipped (saving mid-flight is not this mod's call
            // to make); the scenario carries the dequeues into KSP's next autosave,
            // and a crash before one costs nothing — ReconcileRescueVessels re-derives
            // the queue from contract state.
            if (!inFlight) VesselTransfer.SaveNow();

            foreach (var name in announce)
                RaiseLocalNotification("Craft removed",
                    $"\"{name}\" was removed from your save.");
        }

        public void RefreshContracts()
        {
            clientState?.RefreshContracts();
        }
    }
}
