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
        public bool ShowMainWindow { get; set; }
        public bool ShowLinkWindow { get; set; }
        public bool ShowConsentWindow { get; set; }     // first-run privacy/terms opt-in gate
        public bool ShowDataPausedWindow { get; set; }  // shown when data sharing is opted out
        public int UnreadNotifications { get; set; }
        public string LinkedUsername { get; private set; } = "";

        // Paths
        public static string ModPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BoundlessMissions");
        public static string PluginDataPath => Path.Combine(ModPath, "PluginData");

        // Internal
        private float lastNotificationCheck;
        private float notificationInterval = 600f; // 10 minutes (fallback poll only)
        private float lastImportCheck;
        private const float ImportInterval = 30f; // craft-import queue poll cadence
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
        // Unknown-profession warning: once per session, not once per Space Center visit.
        private bool traitWarningShown;
        private bool initialized;
        // Tracks Consent.Accepted across frames so a mid-session lapse (manual
        // consent.cfg edit/delete, or a server policy bump) is caught on its edge.
        private bool lastConsentOk = true;

        // Live notification push + de-dup of already-toasted notifications
        private NotificationSocket notifSocket;
        private readonly HashSet<string> seenNotifIds = new HashSet<string>();
        private bool notifBacklogSeeded; // first poll seeds the panel without toasting

        // Toolbar
        private ApplicationLauncherButton toolbarButton;
        private Texture2D toolbarIcon;

        // UI Windows
        private UI.MainWindow mainWindow;
        private UI.LinkWindow linkWindow;
        private UI.ConsentWindow consentWindow;
        private UI.DataPausedWindow dataPausedWindow;
        private UI.SubmitWindow submitWindow;
        private UI.CreateContractWindow createContractWindow;
        private UI.NotificationPopup notificationPopup;
        private UI.CheckpointPrompt checkpointPrompt;
        private UI.DeviceVerifyWindow deviceVerifyWindow;

        private UI.UpdateRequiredWindow updateWindow;

        // Loopback web bridge, live only while the browser UI is in use (enableWebUi
        // in settings.cfg). Binds 127.0.0.1 on an ephemeral port. See Web/LocalServer.cs.
        private Web.LocalServer webServer;
        private UI.WebUiWindow webUiWindow;

        // In-game uGUI sidebar (UI/Gui/). Additive: it does not replace the IMGUI
        // windows or the browser UI, and it draws on its own Canvas rather than in
        // OnGUI — which is why it needs its own hide/scene/teardown handling below.
        private UI.Gui.SidebarController sidebar;

        // Device binding: set while we're blocked on an unrecognized-device challenge.
        private bool deviceGateActive;
        private string deviceGateChallenge;

        // Version gate: set when the server reports this DLL is no longer the latest.
        // While true, every server-backed feature is suppressed.
        public bool UpdateRequired { get; private set; }
        public string LatestVersion { get; private set; } = "";
        public string UpdateDownloadUrl { get; private set; } = "";

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

        /// <summary>Dismiss the update gate for this session and open the limited UI.</summary>
        public void AcknowledgeUpdate()
        {
            if (!UpdateRequired) return;
            UpdateAcknowledged = true;
            updateWindow.Hide();
            ShowMainWindow = true;
            mainWindow.OnOpen();
            Debug.Log("[GeneKerman] Update gate acknowledged — limited (offline) features enabled.");
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
            mainWindow = new UI.MainWindow();
            linkWindow = new UI.LinkWindow();
            consentWindow = new UI.ConsentWindow();
            dataPausedWindow = new UI.DataPausedWindow();
            submitWindow = new UI.SubmitWindow();
            createContractWindow = new UI.CreateContractWindow();
            notificationPopup = new UI.NotificationPopup();
            checkpointPrompt = new UI.CheckpointPrompt();
            deviceVerifyWindow = new UI.DeviceVerifyWindow();
            updateWindow = new UI.UpdateRequiredWindow();
            webUiWindow = new UI.WebUiWindow();
            checkpointDetector = new CheckpointDetector(OnCheckpoint)
            {
                IsCaptureEnabled = () => Api != null && Api.CheckpointPhotosEnabled,
            };
            checkpointDetector.Register();

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
            sidebar.AddPanel(new UI.Gui.ToolsPanel());
            sidebar.AddPanel(new UI.Gui.SettingsPanel());

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
            // KSP.log to Discord routinely, and DeviceId.GetKspLog() does it automatically.
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
                ScreenMessages.PostScreenMessage("Boundless Missions: web UI unavailable, using classic UI.",
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
        }

        void Update()
        {
            // Hoisted above every early return below. The bridge must keep draining its
            // job queue even when the player is unlinked or data-sharing is paused —
            // otherwise a request thread blocks for the full 30s timeout, and the page
            // cannot even ask *why* it is unavailable.
            webServer?.Pump();

            // Hoisted for the same reason as Pump: the sidebar exists in every
            // scene and for unlinked clients too — its own gate cascade decides
            // whether it draws, and its slide/pulse must keep running either way.
            sidebar?.Tick();

            // Hoisted for the same reason as RescueImmunityGuardian below, and it used
            // to sit under all three gates: draining this queue is local bookkeeping
            // with no network in it, and by the time something is *in* the queue the
            // vessel already belongs to someone else and the player has been told it
            // will disappear. Unlinking, pausing data-sharing or a server policy bump
            // forcing re-consent must not strand a ship in a save it was promised to
            // leave — those gates govern what we *send*, not what we owe the player.
            if (initialized) ProcessPendingRescueRemovals();

            // Same reasoning, and the same gates deliberately skipped: this only ever
            // deletes kerbals that belong to somebody else's save, and a roster it has
            // already polluted is what stops the Astronaut Complex offering new hires.
            if (initialized) SweepRosterOnce();

            if (!initialized || !Api.IsLinked) return;

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

            // Watch for flight milestones worth a hero shot. Independent of the
            // notifications toggle; gated by its own setting and paused while a prompt
            // or capture is already in progress.
            if (Api.CheckpointPhotosEnabled)
                checkpointDetector.Tick();

            // Auto-import crafts the player queued from Discord. Only at the Space
            // Center — a scene where both live-vessel imports and blueprint installs
            // are safe. Independent of the notifications toggle.
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER &&
                Time.realtimeSinceStartup - lastImportCheck > ImportInterval)
            {
                lastImportCheck = Time.realtimeSinceStartup;
                mainWindow.PollCraftImports();
            }

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

            string id = MiniJSON.GetString(notif, "id");
            if (!string.IsNullOrEmpty(id) && !seenNotifIds.Add(id))
                return; // already toasted

            if (bumpBadge) UnreadNotifications++;

            string contractId = "";
            var data = MiniJSON.GetDict(notif, "data");
            if (data != null) contractId = MiniJSON.GetString(data, "contract_id");

            notificationPopup.Show(
                MiniJSON.GetString(notif, "title"),
                MiniJSON.GetString(notif, "message"),
                contractId
            );

            // Rescue: when the issuer approves, the rescue craft is delivered to them
            // and removed from the rescuer's save. Queue removal of the craft this
            // client submitted for that contract.
            MaybeHandleRescueRemoval(notif);

            // Keep the panel in sync so the item is already there when opened.
            mainWindow.AddNotification(notif);

            // Pulse the sidebar tab. Unconditional by design — every notification,
            // regardless of setting, regardless of whether the panel is already
            // open. It also marks the feed dirty so an open panel gains the row.
            sidebar?.Pulse();

            // Tee to the browser UI — deliberately in addition to the toast above, not
            // instead of it. A player in web mode may be flying with the browser behind
            // the game window; the in-game toast is the only thing they will actually
            // see, so it must keep firing regardless of UI mode.
            webServer?.Broadcast("notification", MiniJSON.Serialize(notif));

            // Same reasoning as RefreshContracts() below: tell the page its lists are
            // stale so it can refetch, rather than making it poll.
            if (IsContractEvent(MiniJSON.GetString(notif, "type")))
                webServer?.Broadcast("contracts_changed", "{}");

            // Anything that changes a contract's state (a new offer, an acceptance,
            // a submission, an approval/refusal, a cancellation) refreshes the
            // Contracts tab live off the same socket push that produced this toast —
            // so the player never has to manually reopen the list. Skipped during the
            // first poll's backlog seeding, which doesn't route through here.
            if (IsContractEvent(MiniJSON.GetString(notif, "type")))
                RefreshContracts();
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
                "🌐 Requested from the website",
                "Open the submission window for " + subject + "?",
                onAccept: () => StartCoroutine(Web.GkRoutes.OpenSubmitRoutine(
                    contractId,
                    (ok, msg) => notificationPopup.Show(
                        ok ? "📤 Submit" : "📤 Submit unavailable", msg))),
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
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            checkpointDetector?.Unregister();
            notifSocket?.Disconnect();
            webServer?.Stop();
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

            // Acknowledged update gate: the main window is the only thing on offer, and
            // it comes up in limited mode. Linking is a server call that would just 426,
            // so an unlinked client goes here too rather than to the link window.
            if (UpdateRequired)
            {
                ShowMainWindow = !ShowMainWindow;
                if (ShowMainWindow)
                    mainWindow.OnOpen();
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
                // trouble). Fall through to the classic window rather than strand the
                // player on a button that does nothing.
                Debug.LogWarning("[GeneKerman] Web UI unavailable — falling back to the classic window.");
                ScreenMessages.PostScreenMessage("Boundless Missions: web UI unavailable, using classic UI.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
            }

            // The sidebar is what the button opens now. The classic window is still
            // built, still loaded and still holds the things the sidebar does not do
            // (rescue contracts, submission, wreck spawning) — it is reached from
            // Settings → Open the classic window rather than from here.
            if (Api.IsLinked) sidebar?.Toggle();
            else ShowLinkWindow = !ShowLinkWindow;
        }

        /// <summary>
        /// Raise the classic IMGUI window. Called from the sidebar's Settings panel,
        /// which is the only route to it now that the toolbar button opens the
        /// sidebar — and there has to be one, because the sidebar deliberately does
        /// not carry every flow (see ContractForm's note on rescue contracts).
        /// </summary>
        public void OpenClassicWindow()
        {
            ShowMainWindow = true;
            mainWindow.OnOpen();
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
                // which drops them into the limited main window below.
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
                // Acknowledged update gate: only the main window, and MainWindow itself
                // narrows to the tabs that work without the server (Tools, Settings).
                // Nothing that transmits is drawn — no submit/create/notification popups,
                // no link window.
                else if (UpdateRequired)
                {
                    if (ShowMainWindow)
                        mainWindow.Draw();
                }
                else
                {
                    if (ShowMainWindow && Api.IsLinked)
                        mainWindow.Draw();

                    // Unlinked client past the opt-in gate: offer the link menu.
                    if (!Api.IsLinked && (ShowLinkWindow || ShowConsentWindow))
                        linkWindow.Draw();

                    // Where the UI is running, plus the escape hatches back to it and
                    // to the classic windows. Drawn in web mode only.
                    webUiWindow.Draw();

                    submitWindow.Draw();
                    createContractWindow.Draw();

                    // These four stay IMGUI permanently, in every mode. Toasts and the
                    // checkpoint prompt are time-critical and must appear over the game
                    // while the player is flying — a browser is alt-tabbed or on another
                    // monitor. The device prompt is a security question about this PC.
                    notificationPopup.Draw();
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

            notificationPopup.Show("📷 Photo captured", "Sharing to Discord…");

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
                        RaiseLocalNotification("📡 Photo shared", "Posted to the community channel.");
                    else
                        RaiseLocalNotification("⚠ Share failed",
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
                notificationPopup.Show("🏅 Achievement Shot", "Enter flight with an active vessel first.");
                return;
            }
            if (Api == null || !Api.IsLinked || Api.TransmissionBlocked)
            {
                notificationPopup.Show("🏅 Achievement Shot", "Link your account and enable data sharing first.");
                return;
            }

            // Reuse the bottom-centre prompt; give the player ample time to frame the
            // shot before it self-dismisses.
            checkpointPrompt.Show(
                "🏅 Achievement Shot",
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
                RaiseLocalNotification("⚠ Capture failed", "Couldn't read the screenshot. Try again.");
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
                notificationPopup.Show("📡 Sharing…",
                    "You've already earned this vessel's achievement here, so the shot goes to Discord.");
            }
            else
            {
                notificationPopup.Show("📡 Submitting…", "Checking your shot for an achievement…");
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
                    RaiseLocalNotification(ok ? "🏅 Achievement Shot" : "⚠ Achievement Shot", msg);
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

        private IEnumerator InitialFetch()
        {
            yield return new WaitForSeconds(2f); // Let the game settle

            // Verify token is still valid
            yield return Api.GetProfile((ok, data, err) =>
            {
                if (ok)
                {
                    mainWindow.UpdateProfile(data);
                    LinkedUsername = MiniJSON.GetString(data, "username");
                    Debug.Log("[GeneKerman] Profile loaded: " + LinkedUsername);
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
                        bool isRescueRemoval =
                            MiniJSON.GetString(notif, "type") == "rescue_craft_removed";
                        if (isRescueRemoval && GKContractScenario.Instance == null)
                            continue; // retry on a later poll; don't mark seen

                        if (isRescueRemoval) MaybeHandleRescueRemoval(notif);

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
            ShowMainWindow = true;
            mainWindow.OnOpen();
            StartCoroutine(InitialFetch());
            notifSocket.Connect();

            LinkedUsername = MiniJSON.GetString(data, "username");
            RaiseLocalNotification(
                "✅ Account Linked!",
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
        /// MainWindow.AddLocalNotification) and disappear on a game restart.
        /// </summary>
        public void RaiseLocalNotification(string title, string message, string contractId = null,
                                          string localAction = null)
        {
            notificationPopup.Show(title, message, contractId, localAction);

            if (mainWindow == null) return;

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
            mainWindow.AddLocalNotification(notif);
        }

        /// <summary>Called by the consent window once the user accepts the privacy
        /// policy, terms, and data-collection opt-in. Until this point nothing is
        /// transmitted (see ApiClient.TransmissionBlocked), so the version gate and any
        /// resume work are kicked off here — not at startup.</summary>
        public void OnConsentGranted()
        {
            ShowConsentWindow = false;
            ShowLinkWindow = true;   // proceed to the link menu

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
            ShowMainWindow = false;
            ShowLinkWindow = false;
            ShowConsentWindow = true;   // surface the re-accept gate immediately
            Debug.Log("[GeneKerman] Consent lapsed — re-accept required before any data is sent.");
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
                ShowMainWindow = false;
                ShowLinkWindow = false;
                ShowConsentWindow = false;
                Debug.Log("[GeneKerman] Data sharing disabled — mod is now inert.");
            }
            else
            {
                ShowDataPausedWindow = false;
                Debug.Log("[GeneKerman] Data sharing enabled — resuming.");
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

                        // Hand the player back a usable window. Coming out of limited mode
                        // the main window is open but holds no data (nothing was fetched
                        // while gated), and for an unlinked client OnGUI would stop drawing
                        // it entirely — leaving the screen blank with no obvious next step.
                        if (wasLimited && ShowMainWindow)
                        {
                            if (Api.IsLinked)
                            {
                                mainWindow.OnOpen();     // now a real refresh
                            }
                            else
                            {
                                ShowMainWindow = false;
                                ShowLinkWindow = true;   // this server accepts us — offer linking
                            }
                        }
                    }
                    return;
                }

                LatestVersion = MiniJSON.GetString(data, "latest_version");
                UpdateDownloadUrl = MiniJSON.GetString(data, "download_url");
                UpdateRequired = true;

                // Already dismissed this session (e.g. this is the re-check fired by a
                // server switch, and the new server rejects us too): leave the player in
                // the limited UI instead of yanking the window back over it.
                if (UpdateAcknowledged) return;

                ShowMainWindow = false;
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
            UpdateDownloadUrl = downloadUrl ?? "";
            if (UpdateRequired) return;   // window already up
            UpdateRequired = true;
            ShowMainWindow = false;
            ShowLinkWindow = false;
            updateWindow.Show(ModVersion.Current, LatestVersion, UpdateDownloadUrl);
            Debug.Log($"[GeneKerman] Update required (server-enforced): {ModVersion.Current} → {LatestVersion}");
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
                "• Press \"✅ Yes, it's me\" if you switched PCs / reinstalled.\n" +
                "• Press \"🚫 No, report it\" only if it wasn't you.\n\n" +
                "Waiting for your response…");
            StartCoroutine(DeviceGateFlow(challengeId));
        }

        /// Fired when the account owner presses "🔔 Ping this PC" in their Discord DM.
        /// Makes a loud, unmistakable on-screen alert so whoever is sitting at THIS PC
        /// knows the login attempt is theirs (and can press "Yes, it's me" in Discord).
        private void ShowDevicePing()
        {
            const string msg = "🔔 GENE KERMAN: Is this you? Someone is verifying this PC's " +
                               "login from Discord. If this is your PC, press \"✅ Yes, it's me\" " +
                               "in your Discord DM.";
            try
            {
                ScreenMessages.PostScreenMessage(msg, 12f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch { /* ScreenMessages unavailable in this scene — popup still shows */ }
            notificationPopup.Show("🔔 Is this you?",
                "Someone is verifying this PC's login from Discord.\n" +
                "If this is your PC, press \"✅ Yes, it's me\" in your Discord DM.");
            deviceVerifyWindow.Show(
                "🔔 PING RECEIVED. Someone is checking whether this PC is yours.\n\n" +
                "If you're the account owner and meant to log in here, go to your\n" +
                "Discord DM and press \"✅ Yes, it's me\".\n\n" +
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
                RaiseLocalNotification("✅ Device approved", "This PC is now trusted.");
                StartCoroutine(InitialFetch());
            }
            else if (outcome == "denied")
            {
                // If the user reported this device, upload diagnostics for the
                // moderation ticket BEFORE unlinking (so the request keeps a valid
                // token), then unlink so this device stops trying.
                if (!string.IsNullOrEmpty(reportId))
                {
                    Debug.Log("[GeneKerman] Device reported — uploading diagnostics…");
                    yield return Api.UploadDeviceReport(reportId, (ok, r, s) =>
                        Debug.Log($"[GeneKerman] Device report upload: ok={ok} ({s})"));
                }
                Api.ClearToken();
                ShowMainWindow = false;
                ShowLinkWindow = true;   // drop the user straight onto the link screen
                // Terminal unlink — keep this a transient toast. Persisting it would
                // make it resurface in the panel the next time the user re-links.
                notificationPopup.Show("🚫 Device not approved",
                    "This PC was unlinked. Run /g linkcode in Discord to link again.");
            }
            else // expired
            {
                RaiseLocalNotification("⌛ Device check expired",
                    "Reopen the mod window to try again.");
            }
        }

        /// <summary>Open the main window's Contracts tab focused on a specific contract.</summary>
        public void OpenContractDetail(string contractId)
        {
            ShowMainWindow = true;
            mainWindow.OpenContractDetail(contractId);
        }

        /// <summary>Open the classic window on the feed — where a notification's own
        /// action button lives (see LocalNotifActions).</summary>
        public void OpenNotifications()
        {
            ShowMainWindow = true;
            if (mainWindow != null) mainWindow.OpenNotifications();
        }

        public void OpenSubmitWindow(string contractId, string mission,
            string missionType = "active_vessel", string requiredSituation = "", string requiredBody = "", string requiredModlist = "",
            RescueTargetSpec rescueTarget = null, List<string> rescueKerbals = null,
            ContractConstraints constraints = null)
        {
            submitWindow.Open(contractId, mission, missionType, requiredSituation, requiredBody, requiredModlist,
                rescueTarget, rescueKerbals, constraints);
        }

        /// <summary>
        /// The classic main window, for front ends that display data it already
        /// owns (currently the uGUI sidebar's notification feed). Internal, and
        /// read-only by convention: MainWindow stays the one place that fetches.
        /// </summary>
        internal UI.MainWindow MainWindowRef => mainWindow;

        public void RunCoroutine(IEnumerator routine)
        {
            StartCoroutine(routine);
        }

        public void OpenCreateContractWindow(int balance, string userId = "", string userName = "")
        {
            createContractWindow.Open(balance, userId, userName);
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
        private void MaybeHandleRescueRemoval(Dictionary<string, object> notif)
        {
            if (notif == null) return;
            if (MiniJSON.GetString(notif, "type") != "rescue_craft_removed") return;

            var data = MiniJSON.GetDict(notif, "data");
            string contractId = data != null ? MiniJSON.GetString(data, "contract_id") : "";
            if (string.IsNullOrEmpty(contractId)) return;

            var scenario = GKContractScenario.Instance;
            if (scenario == null) return; // no save loaded (editor, main menu) — reconcile catches it

            // Peek, queue, and only then forget: the record is the sole link between the
            // contract and a craft in this save, so it outlives a queue attempt that
            // could not land.
            string pid;
            if (scenario.PeekRescueSubmission(contractId, out pid) &&
                QueueRescueVesselRemoval(pid, crewFate: VesselTransfer.CrewFate.BorrowedOnly))
                scenario.ForgetRescueSubmission(contractId);
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
        /// must keep whatever state would let it try again.</summary>
        public bool QueueRescueVesselRemoval(string pid, string vesselName = null,
            VesselTransfer.CrewFate crewFate = VesselTransfer.CrewFate.LeavesWithCraft)
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

            ProcessPendingRescueRemovals(); // run now if we're already somewhere safe

            // Still queued → we couldn't delete it yet (player is in flight). Warn them so
            // the craft doesn't silently vanish the next time they reach the Space Center.
            if (pending.ContainsKey(pid))
                RaiseLocalNotification("🛰️ Craft scheduled for removal",
                    $"\"{pending[pid].Name}\" will be deleted when you return to the Space Center.");

            return true;
        }

        /// <summary>Record the craft a rescuer submitted, so it can be removed once
        /// the issuer approves and it's delivered to them.</summary>
        public void RecordRescueSubmission(string contractId, string pid)
        {
            GKContractScenario.Instance?.RecordRescueSubmission(contractId, pid);
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
            foreach (var o in contracts)
            {
                var c = o as Dictionary<string, object>;
                if (c == null) continue;
                if (MiniJSON.GetString(c, "mission_type") != "rescue") continue;

                string cid = MiniJSON.GetString(c, "contract_id");
                if (string.IsNullOrEmpty(cid)) continue;
                rescueStatus[cid] = MiniJSON.GetString(c, "status");

                if (!MiniJSON.GetBool(c, "is_outgoing")) continue;

                // Issuer side. Safe against a self-issued rescue: an imported wreck is
                // always given a fresh pid (VesselTransfer.PrepareInnerNode), so this can
                // only ever match the original the contract was cut from.
                string issuedPid = MiniJSON.GetString(c, "rescue_pid");
                if (string.IsNullOrEmpty(issuedPid)) continue;   // pre-dates the field
                if (!VesselTransfer.VesselExists(issuedPid)) continue;

                Debug.Log($"[GeneKerman] Reconcile: rescue {cid} is live but its craft " +
                          $"(pid {issuedPid}) is still in this save — queueing removal.");
                // Issuer side: the stranded crew are what was issued, so they go too.
                QueueRescueVesselRemoval(issuedPid,
                    crewFate: VesselTransfer.CrewFate.LeavesWithCraft);
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

                string pid;
                if (!scenario.PeekRescueSubmission(cid, out pid)) continue;

                Debug.Log($"[GeneKerman] Reconcile: rescue {cid} was approved — queueing " +
                          $"removal of the craft we submitted (pid {pid}).");
                // Rescuer side: the ship and the kerbals we picked up are the hand-over.
                // The pilots who flew it there are not, and stay in our roster.
                if (QueueRescueVesselRemoval(pid, crewFate: VesselTransfer.CrewFate.BorrowedOnly))
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
        }

        /// <summary>Say out loud when the roster holds a kerbal whose profession nothing
        /// installed defines. KSP's own failure for this is a NullReference part-way
        /// through drawing the Astronaut Complex, which names neither the kerbal nor the
        /// trait — so the player is left with a screen that looks broken for no reason.
        /// Once per session: it is a property of the save, not of the visit.</summary>
        private void ReportUnresolvableTraits()
        {
            if (traitWarningShown) return;
            var broken = VesselTransfer.FindUnresolvableTraitCrew();
            if (broken.Count == 0) return;

            traitWarningShown = true;
            Debug.LogWarning($"[GeneKerman] {broken.Count} kerbal(s) have a profession no installed " +
                             $"mod defines: {string.Join(", ", broken.ToArray())}. KSP throws while " +
                             "drawing any crew list containing them (Astronaut Complex, crew " +
                             "assignment) — reinstall the mod that adds the profession, or press " +
                             "Fix professions on the notification.");

            // Name them in the message rather than sending the player to KSP.log: the
            // notification now carries the button that fixes this, and a fix is not a
            // thing to press without seeing who it applies to.
            RaiseLocalNotification("⚠️ Unknown crew professions",
                $"{broken.Count} kerbal(s) in this save have a profession no installed mod " +
                $"defines: {NameList(broken, 6)}. The Astronaut Complex will fail to draw while " +
                "they are in the roster. Reinstall the mod that adds their profession, or press " +
                "Fix professions to give them a local one (reversible — the original comes back " +
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

            RaiseLocalNotification("🧑‍🚀 Crew professions restored",
                $"{restored.Count} kerbal(s) got their original profession back now that the mod " +
                $"defining it is installed: {NameList(restored, 6)}.");
        }

        private void ProcessPendingRescueRemovals()
        {
            var pending = GKContractScenario.Instance?.PendingRescueRemovals;
            if (pending == null || pending.Count == 0) return;
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION)
                return; // can't safely Die() the focused flight vessel — wait

            // Removed = we deleted it (announce); NotFound = already gone (dequeue
            // silently). Both are terminal — dropping them stops the per-frame retry
            // that otherwise floods the log when a queued pid isn't in this save.
            // Deferred/Failed stay queued for a later safe-scene pass.
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
                switch (VesselTransfer.RemoveVesselFromSave(pid, fate, persist: false))
                {
                    case VesselTransfer.RemovalResult.Removed: removed.Add(pid); break;
                    case VesselTransfer.RemovalResult.NotFound: gone.Add(pid); break;
                }
            }
            if (removed.Count == 0 && gone.Count == 0) return;

            var announce = new List<string>();
            foreach (var pid in removed)
            {
                var entry = pending[pid];
                announce.Add(entry != null ? entry.Name : pid);
                pending.Remove(pid);
            }
            foreach (var pid in gone)
                pending.Remove(pid);

            // A craft that was already gone (the NotFound case) never got the chance to
            // settle its crew, so its borrowed kerbals are still in the roster with
            // nothing to belong to. Sweep them now, while we are about to save anyway.
            VesselTransfer.PurgeBorrowedGhostCrew();

            // The queue lives in the scenario, so this write persists the dequeues as
            // well as the destroyed vessels — the two must not be able to disagree.
            VesselTransfer.SaveNow();

            foreach (var name in announce)
                RaiseLocalNotification("🗑️ Craft removed",
                    $"\"{name}\" was removed from your save.");
        }

        public void RefreshContracts()
        {
            mainWindow?.RefreshContracts();
        }
    }
}
