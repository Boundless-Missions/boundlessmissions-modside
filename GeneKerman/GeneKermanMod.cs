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
        private bool initialized;

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
        private UI.SubmitWindow submitWindow;
        private UI.CreateContractWindow createContractWindow;
        private UI.NotificationPopup notificationPopup;
        private UI.CheckpointPrompt checkpointPrompt;
        private UI.DeviceVerifyWindow deviceVerifyWindow;

        private UI.UpdateRequiredWindow updateWindow;

        // Device binding: set while we're blocked on an unrecognized-device challenge.
        private bool deviceGateActive;
        private string deviceGateChallenge;

        // Version gate: set when the server reports this DLL is no longer the latest.
        // While true, every mod window except the update prompt is suppressed.
        public bool UpdateRequired { get; private set; }
        public string LatestVersion { get; private set; } = "";
        public string UpdateDownloadUrl { get; private set; } = "";

        // Milestone hero-shot capture (rendezvous / flyby / asteroid)
        private CheckpointDetector checkpointDetector;
        private bool checkpointCapturing;

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
            submitWindow = new UI.SubmitWindow();
            createContractWindow = new UI.CreateContractWindow();
            notificationPopup = new UI.NotificationPopup();
            checkpointPrompt = new UI.CheckpointPrompt();
            deviceVerifyWindow = new UI.DeviceVerifyWindow();
            updateWindow = new UI.UpdateRequiredWindow();
            checkpointDetector = new CheckpointDetector(OnCheckpoint)
            {
                IsCaptureEnabled = () => Api != null && Api.CheckpointPhotosEnabled,
            };
            checkpointDetector.Register();

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
            StartCoroutine(CheckVersionRoutine());

            // Periodic anti-tamper attestation (catches a DLL swapped mid-session).
            StartCoroutine(AttestationLoop());

            // If already linked, do an initial data fetch and open the live socket
            if (Api.IsLinked)
            {
                StartCoroutine(InitialFetch());
                if (Api.NotificationsEnabled)
                    notifSocket.Connect();
            }

            initialized = true;
            lastNotificationCheck = Time.realtimeSinceStartup;
        }

        private void OnHideUI() => uiHidden = true;
        private void OnShowUI() => uiHidden = false;

        private void OnSceneChange(GameScenes scene)
        {
            UI.GKSkin.Invalidate();
            // Drop any in-flight prompt and clear SOI/proximity state so milestones
            // don't carry over (or re-fire) across a scene load.
            checkpointPrompt?.Dismiss(false);
            checkpointDetector?.Reset();
        }

        void Update()
        {
            if (!initialized || !Api.IsLinked) return;

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

            // Remove rescued-away vessels once we're in a scene where killing a
            // vessel is safe (never the focused flight vessel).
            ProcessPendingRescueRemovals();

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

            // Drain notifications pushed over the socket and surface new ones as toasts.
            while (notifSocket.TryDequeue(out var notif))
                HandleIncomingNotification(notif);

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
            if (MiniJSON.GetString(notif, "type") == "rescue_craft_removed" && !string.IsNullOrEmpty(contractId))
            {
                string pid;
                if (rescueSubmittedPids.TryGetValue(contractId, out pid))
                {
                    QueueRescueVesselRemoval(pid);
                    rescueSubmittedPids.Remove(contractId);
                }
            }

            // Keep the panel in sync so the item is already there when opened.
            mainWindow.AddNotification(notif);

            // Anything that changes a contract's state (a new offer, an acceptance,
            // a submission, an approval/refusal, a cancellation) refreshes the
            // Contracts tab live off the same socket push that produced this toast —
            // so the player never has to manually reopen the list. Skipped during the
            // first poll's backlog seeding, which doesn't route through here.
            if (IsContractEvent(MiniJSON.GetString(notif, "type")))
                RefreshContracts();
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
            // Outdated client: the only thing the button does is re-show the gate.
            if (UpdateRequired)
            {
                updateWindow.Show(ModVersion.Current, LatestVersion, UpdateDownloadUrl);
                return;
            }

            if (Api.IsLinked)
            {
                ShowMainWindow = !ShowMainWindow;
                if (ShowMainWindow)
                    mainWindow.OnOpen();
            }
            else
            {
                ShowLinkWindow = !ShowLinkWindow;
            }
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
                // Outdated client: nothing but the update prompt is usable.
                if (UpdateRequired)
                {
                    updateWindow.Draw();
                }
                else
                {
                    if (ShowMainWindow && Api.IsLinked)
                        mainWindow.Draw();

                    if (ShowLinkWindow)
                        linkWindow.Draw();

                    submitWindow.Draw();
                    createContractWindow.Draw();
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
                        notificationPopup.Show("📡 Photo shared", "Posted to the community channel.");
                    else
                        notificationPopup.Show("⚠ Share failed",
                            "Saved locally in PluginData/renders.");
                });
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
                        // record them as seen so we never toast the backlog.
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
            notificationPopup.Show(
                "✅ Account Linked!",
                "Welcome, " + LinkedUsername + "!"
            );
        }

        public void ShowNotification(string title, string message)
        {
            notificationPopup.Show(title, message);
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

                bool enabled = MiniJSON.GetBool(data, "enabled", true);
                bool upToDate = MiniJSON.GetBool(data, "up_to_date", true);

                if (!enabled || upToDate)
                {
                    if (UpdateRequired)
                    {
                        UpdateRequired = false;
                        updateWindow.Hide();
                    }
                    return;
                }

                LatestVersion = MiniJSON.GetString(data, "latest_version");
                UpdateDownloadUrl = MiniJSON.GetString(data, "download_url");
                UpdateRequired = true;
                ShowMainWindow = false;
                ShowLinkWindow = false;
                updateWindow.Show(ModVersion.Current, LatestVersion, UpdateDownloadUrl);
                Debug.Log($"[GeneKerman] Update required: {ModVersion.Current} → {LatestVersion}");
            });
        }

        /// Re-run the version check (e.g. after the player updated and the window's
        /// "Re-check" button is pressed).
        public void RecheckVersion() => StartCoroutine(CheckVersionRoutine());

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
                "• Press \"🚫 No — report\" only if it wasn't you.\n\n" +
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
                "🔔 PING RECEIVED — someone is checking whether this PC is yours.\n\n" +
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
                notificationPopup.Show("✅ Device approved", "This PC is now trusted.");
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
                notificationPopup.Show("🚫 Device not approved",
                    "This PC was unlinked. Run /g linkcode in Discord to link again.");
            }
            else // expired
            {
                notificationPopup.Show("⌛ Device check expired",
                    "Reopen the mod window to try again.");
            }
        }

        /// <summary>Open the main window's Contracts tab focused on a specific contract.</summary>
        public void OpenContractDetail(string contractId)
        {
            ShowMainWindow = true;
            mainWindow.OpenContractDetail(contractId);
        }

        public void OpenSubmitWindow(string contractId, string mission,
            string missionType = "active_vessel", string requiredSituation = "", string requiredBody = "", string requiredModlist = "",
            RescueTargetSpec rescueTarget = null, List<string> rescueKerbals = null,
            ContractConstraints constraints = null)
        {
            submitWindow.Open(contractId, mission, missionType, requiredSituation, requiredBody, requiredModlist,
                rescueTarget, rescueKerbals, constraints);
        }

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
        // Station. rescueSubmittedPids remembers which craft the rescuer handed
        // over per contract, so we know what to remove when the issuer approves.

        // pid → friendly vessel name, captured at queue time so the removal notice can
        // still name the craft after it's destroyed.
        private readonly Dictionary<string, string> pendingRescueRemovals = new Dictionary<string, string>();
        private readonly Dictionary<string, string> rescueSubmittedPids = new Dictionary<string, string>();

        /// <summary>Queue a vessel (by pid) for removal at the next safe scene. Pass a
        /// vesselName when the caller already knows it (e.g. the active vessel); otherwise
        /// it's resolved from the pid while the craft still exists.</summary>
        public void QueueRescueVesselRemoval(string pid, string vesselName = null)
        {
            if (string.IsNullOrEmpty(pid)) return;
            if (!pendingRescueRemovals.ContainsKey(pid))
                pendingRescueRemovals[pid] = string.IsNullOrEmpty(vesselName)
                    ? VesselTransfer.GetVesselName(pid) : vesselName;

            ProcessPendingRescueRemovals(); // run now if we're already somewhere safe

            // Still queued → we couldn't delete it yet (player is in flight). Warn them so
            // the craft doesn't silently vanish the next time they reach the Space Center.
            if (pendingRescueRemovals.ContainsKey(pid))
                notificationPopup.Show("🛰️ Craft scheduled for removal",
                    $"\"{pendingRescueRemovals[pid]}\" will be deleted when you return to the Space Center.");
        }

        /// <summary>Record the craft a rescuer submitted, so it can be removed once
        /// the issuer approves and it's delivered to them.</summary>
        public void RecordRescueSubmission(string contractId, string pid)
        {
            if (!string.IsNullOrEmpty(contractId) && !string.IsNullOrEmpty(pid))
                rescueSubmittedPids[contractId] = pid;
        }

        private void ProcessPendingRescueRemovals()
        {
            if (pendingRescueRemovals.Count == 0) return;
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION)
                return; // can't safely Die() the focused flight vessel — wait

            var done = new List<string>();
            foreach (var kv in pendingRescueRemovals)
                if (VesselTransfer.RemoveVesselFromSave(kv.Key)) done.Add(kv.Key);
            foreach (var pid in done)
            {
                string name = pendingRescueRemovals[pid];
                pendingRescueRemovals.Remove(pid);
                notificationPopup.Show("🗑️ Craft removed",
                    $"\"{name}\" was removed from your save.");
            }
        }

        public void RefreshContracts()
        {
            mainWindow?.RefreshContracts();
        }
    }
}
