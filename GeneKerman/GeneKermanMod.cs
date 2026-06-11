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

        // Paths
        public static string ModPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "GeneKerman");
        public static string PluginDataPath => Path.Combine(ModPath, "PluginData");

        // Internal
        private float lastNotificationCheck;
        private float notificationInterval = 600f; // 10 minutes
        private bool initialized;

        // Toolbar
        private ApplicationLauncherButton toolbarButton;
        private Texture2D toolbarIcon;

        // UI Windows
        private UI.MainWindow mainWindow;
        private UI.LinkWindow linkWindow;
        private UI.SubmitWindow submitWindow;
        private UI.CreateContractWindow createContractWindow;
        private UI.NotificationPopup notificationPopup;

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

            // Initialize UI windows
            mainWindow = new UI.MainWindow();
            linkWindow = new UI.LinkWindow();
            submitWindow = new UI.SubmitWindow();
            createContractWindow = new UI.CreateContractWindow();
            notificationPopup = new UI.NotificationPopup();

            // Load toolbar icon
            LoadToolbarIcon();

            // Register for toolbar
            GameEvents.onGUIApplicationLauncherReady.Add(OnToolbarReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnToolbarDestroyed);

            // Invalidate UI textures on scene changes
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);

            // If already linked, do an initial data fetch
            if (Api.IsLinked)
            {
                StartCoroutine(InitialFetch());
            }

            initialized = true;
            lastNotificationCheck = Time.realtimeSinceStartup;
        }

        private void OnSceneChange(GameScenes scene)
        {
            UI.GKSkin.Invalidate();
        }

        void Update()
        {
            if (!initialized || !Api.IsLinked) return;

            // Periodic notification check
            if (Time.realtimeSinceStartup - lastNotificationCheck > notificationInterval)
            {
                lastNotificationCheck = Time.realtimeSinceStartup;
                StartCoroutine(CheckNotifications());
            }
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnToolbarReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnToolbarDestroyed);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
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
            if (ShowMainWindow && Api.IsLinked)
                mainWindow.Draw();

            if (ShowLinkWindow)
                linkWindow.Draw();

            submitWindow.Draw();
            createContractWindow.Draw();
            notificationPopup.Draw();
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
                    Debug.Log("[GeneKerman] Profile loaded: " + MiniJSON.GetString(data, "username"));
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
                if (ok)
                {
                    int count = MiniJSON.GetInt(data, "unread_count");
                    UnreadNotifications = count;

                    var notifList = MiniJSON.GetList(data, "notifications");
                    foreach (var n in notifList)
                    {
                        var notif = n as Dictionary<string, object>;
                        if (notif != null)
                        {
                            notificationPopup.Show(
                                MiniJSON.GetString(notif, "title"),
                                MiniJSON.GetString(notif, "message")
                            );
                        }
                    }
                }
            });
        }

        // ── Public API for UI windows ───────────────────────────────────────

        public void OnAccountLinked(Dictionary<string, object> data)
        {
            ShowLinkWindow = false;
            ShowMainWindow = true;
            mainWindow.OnOpen();
            StartCoroutine(InitialFetch());

            notificationPopup.Show(
                "✅ Account Linked!",
                "Welcome, " + MiniJSON.GetString(data, "username") + "!"
            );
        }

        public void ShowNotification(string title, string message)
        {
            notificationPopup.Show(title, message);
        }

        public void OpenSubmitWindow(string contractId, string mission,
            string missionType = "active_vessel", string requiredSituation = "", string requiredBody = "", string requiredModlist = "")
        {
            submitWindow.Open(contractId, mission, missionType, requiredSituation, requiredBody, requiredModlist);
        }

        public void RunCoroutine(IEnumerator routine)
        {
            StartCoroutine(routine);
        }

        public void OpenCreateContractWindow(int balance, string userId = "")
        {
            createContractWindow.Open(balance, userId);
        }
    }
}
