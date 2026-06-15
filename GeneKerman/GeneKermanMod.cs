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
        public static string ModPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "GeneKerman");
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

            // Initialize UI windows
            mainWindow = new UI.MainWindow();
            linkWindow = new UI.LinkWindow();
            submitWindow = new UI.SubmitWindow();
            createContractWindow = new UI.CreateContractWindow();
            notificationPopup = new UI.NotificationPopup();
            checkpointPrompt = new UI.CheckpointPrompt();
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
                StartCoroutine(CheckNotifications());

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
                if (ShowMainWindow && Api.IsLinked)
                    mainWindow.Draw();

                if (ShowLinkWindow)
                    linkWindow.Draw();

                submitWindow.Draw();
                createContractWindow.Draw();
                notificationPopup.Draw();
                checkpointPrompt.Draw();
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

        /// <summary>Open the main window's Contracts tab focused on a specific contract.</summary>
        public void OpenContractDetail(string contractId)
        {
            ShowMainWindow = true;
            mainWindow.OpenContractDetail(contractId);
        }

        public void OpenSubmitWindow(string contractId, string mission,
            string missionType = "active_vessel", string requiredSituation = "", string requiredBody = "", string requiredModlist = "",
            RescueTargetSpec rescueTarget = null, List<string> rescueKerbals = null)
        {
            submitWindow.Open(contractId, mission, missionType, requiredSituation, requiredBody, requiredModlist,
                rescueTarget, rescueKerbals);
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

        private readonly List<string> pendingRescueRemovals = new List<string>();
        private readonly Dictionary<string, string> rescueSubmittedPids = new Dictionary<string, string>();

        /// <summary>Queue a vessel (by pid) for removal at the next safe scene.</summary>
        public void QueueRescueVesselRemoval(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return;
            if (!pendingRescueRemovals.Contains(pid)) pendingRescueRemovals.Add(pid);
            ProcessPendingRescueRemovals(); // run now if we're already somewhere safe
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
            foreach (var pid in pendingRescueRemovals)
                if (VesselTransfer.RemoveVesselFromSave(pid)) done.Add(pid);
            foreach (var pid in done) pendingRescueRemovals.Remove(pid);
        }

        public void RefreshContracts()
        {
            mainWindow?.RefreshContracts();
        }
    }
}
