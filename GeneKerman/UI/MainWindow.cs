/*
 * UI/MainWindow.cs – Primary mod window with tabbed interface.
 *
 * Tabs:
 *   1. Missions    — Weekly mission list with accept buttons
 *   2. Contracts   — Active contracts with submit buttons
 *   3. Profile     — Balance, XP, level display
 *   4. Notifications — Incoming requests and review results
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI
{
    public class MainWindow
    {
        private Rect windowRect = new Rect(100, 100, 550, 620);
        private readonly int windowId = "GKMain".GetHashCode();
        private int selectedTab;
        private Vector2 scrollPos;
        private string statusMsg = "";
        private float statusTime;

        // Cached data
        private Dictionary<string, object> profile;
        private List<object> missions;
        private string weekKey = "";
        private bool missionsLocked;
        private List<object> contracts;
        private List<object> notifications;

        // Loading states
        private bool loadingMissions, loadingContracts, loadingProfile, loadingNotifs;

        // Styles
        private GUIStyle windowStyle, tabStyle, tabActiveStyle, headerStyle;
        private GUIStyle missionEasyStyle, missionMedStyle, missionHardStyle, missionExtremeStyle;
        private GUIStyle boxDarkStyle, labelStyle, valueStyle, statusStyle;
        private GUIStyle acceptBtnStyle, submitBtnStyle, deleteBtnStyle;
        private GUIStyle checkboxStyle, mailRowStyle, mailSenderStyle;
        private GUIStyle mailSubjectStyle, mailAmountStyle, mailDateStyle;
        private GUIStyle iconBtnStyle;
        private GUIStyle resizeGripStyle;
        private bool stylesReady;

        // Icon textures loaded from Iconpack-1 (via GameData/GeneKerman/Textures/)
        private Texture2D iconRefresh, iconCompose, iconInbox, iconTrash, iconCross;
        private Texture2D iconUnlink, iconSubmit, iconVesselInfo;

        // Pre-built GUIContent for icon+text buttons
        private GUIContent gcRefresh, gcRefreshMissions, gcRefreshOnly;
        private GUIContent gcInbox, gcTrash, gcCompose, gcUnlink;
        private GUIContent gcDel, gcRestore, gcSell, gcOpen, gcVesselInfo;

        private readonly string[] tabNames = { "Missions", "Contracts", "Profile", "Market", "Notifications", "Settings" };
        private string serverUrlInput = "";
        private string disputeDateInput = ""; // proposed new deadline for a "More Time" dispute

        // Marketplace state
        private string sellPriceInput = "100";
        private string editorCraftName = "";
        private string editorCraftType = "VAB";
        private int editorPartCount;
        private float editorCraftMass, editorCraftCost;
        private string editorCraftPath = "";
        private List<object> marketListings;
        private bool loadingMarket;
        private bool selling;

        // Craft import queue — crafts the player selected in Discord. The mod polls
        // /api/v1/craft/imports/pending and auto-imports each into the active save.
        private bool importPollInFlight;
        private readonly HashSet<string> processingImports = new HashSet<string>();

        // Rescue wrecks already spawned this session, keyed by contract id, so a
        // retried/repeated accept coroutine can't spawn the stranded vessel twice.
        private readonly HashSet<string> spawnedRescueWrecks = new HashSet<string>();

        // Blueprint preview popup — shows the contractor's submitted render(s) so the
        // issuer can review the work before approving/refusing a submission.
        private bool showBlueprintPopup;
        private bool loadingBlueprints;
        private string blueprintVesselName = "";
        private string blueprintStatus = "";
        private readonly List<Texture2D> blueprintTextures = new List<Texture2D>();
        private Vector2 blueprintScroll;
        private Rect blueprintRect = new Rect(180, 90, 560, 560);
        private readonly int blueprintWindowId = "GKBlueprint".GetHashCode();
        // Resize + zoom state for the blueprint window (drag either corner grip to
        // resize; the +/−/Fit controls scale the image inside the scroll view).
        private float blueprintZoom = 1f;
        private bool blueprintResizingBR;   // bottom-right grip active
        private bool blueprintResizingTL;   // top-left grip active

        // Orbital telemetry standalone window — the server renders a vessel-state
        // diagram (orbit, Ap/Pe, situation, period) for active-vessel and rescue
        // submissions. Shown in its own resizable/zoomable window, separate from
        // the blueprints. Only populated when the submission includes telemetry.
        private Texture2D telemetryTexture;
        private bool showTelemetryWindow;
        private Vector2 telemetryScroll;
        private float telemetryZoom = 1f;
        private bool telemetryResizingBR;
        private bool telemetryResizingTL;
        private Rect telemetryRect = new Rect(210, 110, 720, 480);
        private readonly int telemetryWindowId = "GKTelemetry".GetHashCode();

        // Trash state
        private HashSet<string> trashedContracts = new HashSet<string>();
        private bool showTrash = false;
        private HashSet<string> collapsedWeeks = new HashSet<string>();

        private void LoadTrash()
        {
            string path = System.IO.Path.Combine(GeneKermanMod.PluginDataPath, "trashed_contracts.txt");
            trashedContracts.Clear();
            if (System.IO.File.Exists(path))
            {
                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    if (!string.IsNullOrEmpty(line)) trashedContracts.Add(line.Trim());
                }
            }
        }

        private void SaveTrash()
        {
            string path = System.IO.Path.Combine(GeneKermanMod.PluginDataPath, "trashed_contracts.txt");
            System.IO.File.WriteAllLines(path, new List<string>(trashedContracts).ToArray());
        }

        public void OnOpen()
        {
            LoadTrash();
            RefreshAll();
        }

        public void RefreshAll()
        {
            RefreshProfile();
            RefreshMissions();
            RefreshContracts();
            RefreshNotifications();
        }

        public void UpdateProfile(Dictionary<string, object> data)
        {
            profile = data;
        }

        /// <summary>
        /// Insert a live notification (from the WebSocket push) at the top of the panel
        /// so it is already there when the user opens the tab. De-duped by id; the unread
        /// badge is managed by the caller, so this does not recount.
        /// </summary>
        public void AddNotification(Dictionary<string, object> n)
        {
            if (n == null) return;
            if (notifications == null) notifications = new List<object>();

            string id = MiniJSON.GetString(n, "id");
            foreach (var o in notifications)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && MiniJSON.GetString(d, "id") == id) return; // already present
            }
            notifications.Insert(0, n); // newest first
        }

        /// <summary>Switch to the Contracts tab and open a specific contract's detail view.</summary>
        public void OpenContractDetail(string contractId)
        {
            if (string.IsNullOrEmpty(contractId)) return;
            selectedTab = 1;          // Contracts
            showTrash = false;
            openContractId = contractId;
            scrollPos = Vector2.zero;
            RefreshContracts();       // ensure the contract is loaded for the detail view
        }

        // ── Draw ────────────────────────────────────────────────────────────

        public void Draw()
        {
            // Detect scene change — Unity destroys textures on transition
            if (GKSkin.NeedsRebuild())
            {
                stylesReady = false;
                // The downloaded blueprint/telemetry textures were destroyed with
                // the scene; drop the popups rather than draw dead handles.
                if (showBlueprintPopup) CloseBlueprintPreview();
            }

            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(550));

            // Resizable windows: drive the layout from the rect's current size
            // (set by the corner grips) instead of a fixed width. The window func
            // mutates the rect field directly when a grip is dragged — for size, and
            // for the origin when resizing from the top-left. We only adopt the
            // window's returned (drag-moved) position when no grip is active, so a
            // resize never fights the pre-resize position the window hands back.
            if (showBlueprintPopup)
            {
                Rect ret = ClickThroughHelper.Window(blueprintWindowId, blueprintRect,
                    DrawBlueprintPopup, "", GUIStyle.none,
                    GUILayout.Width(blueprintRect.width), GUILayout.Height(blueprintRect.height));
                if (!blueprintResizingBR && !blueprintResizingTL)
                {
                    blueprintRect.x = ret.x;
                    blueprintRect.y = ret.y;
                }
            }

            if (showTelemetryWindow)
            {
                Rect ret = ClickThroughHelper.Window(telemetryWindowId, telemetryRect,
                    DrawTelemetryWindow, "", GUIStyle.none,
                    GUILayout.Width(telemetryRect.width), GUILayout.Height(telemetryRect.height));
                if (!telemetryResizingBR && !telemetryResizingTL)
                {
                    telemetryRect.x = ret.x;
                    telemetryRect.y = ret.y;
                }
            }
        }

        private void InitStyles()
        {
            if (stylesReady) return;

                        windowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.12f, 0.15f, 1f)) },
                padding = new RectOffset(10, 10, 10, 10),
                border = new RectOffset(0, 0, 0, 0)
            };

            tabStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12, fixedHeight = 32,
                border = new RectOffset(0, 0, 0, 0),
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.15f, 0.2f, 0.9f)), textColor = new Color(0.7f, 0.7f, 0.7f) },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 0.9f)), textColor = Color.white },
            };

            tabActiveStyle = new GUIStyle(tabStyle)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.55f, 0.3f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.55f, 0.3f, 0.9f)), textColor = Color.white },
                fontStyle = FontStyle.Bold
            };

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.3f, 0.9f, 0.5f) }
            };

                        boxDarkStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 1f)) },
                padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(0, 0, 3, 3),
                border = new RectOffset(0, 0, 0, 0)
            };

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
            valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };

            statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = true };

            // Difficulty-colored mission styles
            missionEasyStyle = MakeMissionStyle(new Color(0.2f, 0.7f, 0.3f));
            missionMedStyle = MakeMissionStyle(new Color(0.8f, 0.7f, 0.2f));
            missionHardStyle = MakeMissionStyle(new Color(0.8f, 0.3f, 0.2f));
            missionExtremeStyle = MakeMissionStyle(new Color(0.5f, 0.1f, 0.5f));

            acceptBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11, fixedHeight = 28,
                border = new RectOffset(0, 0, 0, 0),
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.55f, 0.3f, 0.9f)), textColor = Color.white },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.65f, 0.4f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.45f, 0.25f, 0.9f)), textColor = Color.white },
            };

            submitBtnStyle = new GUIStyle(acceptBtnStyle)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.3f, 0.4f, 0.8f, 0.9f)), textColor = Color.white },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.35f, 0.5f, 0.9f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.25f, 0.35f, 0.7f, 0.9f)), textColor = Color.white },
            };

            deleteBtnStyle = new GUIStyle(acceptBtnStyle)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.55f, 0.15f, 0.15f, 0.9f)), textColor = Color.white },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.7f, 0.2f, 0.2f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.45f, 0.1f, 0.1f, 0.9f)), textColor = Color.white },
            };

            checkboxStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.65f, 0.7f, 0.75f) },
            };

                        var rowBg = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.1f, 0.14f, 1f));
            mailRowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = rowBg },
                padding = new RectOffset(4, 4, 3, 3),
                margin = new RectOffset(0, 0, 1, 1),
                fixedHeight = 26,
                border = new RectOffset(0, 0, 0, 0)
            };

            mailSenderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.7f, 0.8f, 0.9f) },
                alignment = TextAnchor.MiddleLeft,
            };

            mailSubjectStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11, alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(4, 4, 2, 2),
                normal = { textColor = new Color(0.85f, 0.88f, 0.92f), background = GKSkin.MakeTex(2, 2, new Color(0, 0, 0, 0)) },
                hover = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.15f, 0.22f, 0.5f)) },
                active = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.15f, 0.22f, 0.5f)) },
            };

            mailAmountStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.4f, 0.8f, 0.3f) },
            };

            mailDateStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10, alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.5f, 0.5f, 0.55f) },
            };

            // NOTE: the dark scrollbar / button / textField theme for controls that
            // fall back to GUI.skin lives in GKSkin.GetSkin() and is applied scoped
            // to our windows only (see GeneKermanMod.OnGUI). It must NOT be set on the
            // shared GUI.skin here — that leaks the theme into every other mod's IMGUI.

            // Small icon-only button style for per-row actions (del/restore)
            iconBtnStyle = new GUIStyle(tabStyle)
            {
                fixedHeight = 0,
                padding = new RectOffset(4, 4, 3, 3),
                alignment = TextAnchor.MiddleCenter,
                imagePosition = ImagePosition.ImageOnly
            };

            // Load icon textures from Iconpack-1 (deployed to Textures/)
            iconRefresh = GKSkin.LoadIcon("Refresh");
            iconCompose = GKSkin.LoadIcon("Compose");
            iconInbox   = GKSkin.LoadIcon("Inbox");
            iconTrash   = GKSkin.LoadIcon("TrashBin");
            iconCross   = GKSkin.LoadIcon("Cross");
            iconUnlink  = GKSkin.LoadIcon("Unlink");
            iconSubmit  = GKSkin.LoadIcon("Submit");
            iconVesselInfo = GKSkin.LoadIcon("aVesselInfo", 18);

            // Build GUIContent objects — icon + label text. If an icon failed to
            // load (file missing), fall back to the old text-only labels.
            gcRefresh         = MakeGC(iconRefresh, " Refresh",          "[~] Refresh");
            gcRefreshMissions = MakeGC(iconRefresh, " Refresh Missions", "[~] Refresh Missions");
            gcRefreshOnly     = MakeGC(iconRefresh, "",                  "[~]");
            gcInbox           = MakeGC(iconInbox,   " Inbox",            "[+] Inbox");
            gcTrash           = MakeGC(iconTrash,   " Trash",            "[X] Trash");
            gcCompose         = MakeGC(iconCompose, " Compose",          "[New] Compose");
            gcUnlink          = MakeGC(iconUnlink,  " Unlink Account",   "[U] Unlink Account");
            gcDel             = MakeGC(GKSkin.LoadIcon("TrashBin", 10),  "",  "[Del]");
            gcRestore         = MakeGC(GKSkin.LoadIcon("Inbox", 10),  "",  "[Res]");
            gcSell            = MakeGC(iconSubmit,  " Sell on Marketplace", "[$] Sell on Marketplace");
            gcOpen            = MakeGC(iconInbox,   " Open",             "[>] Open");
            // Active vessel info / orbital telemetry button (falls back to 🛰).
            gcVesselInfo      = MakeGC(iconVesselInfo, "",              "🛰");

            // Corner grips for resizable popups (blueprint / telemetry windows).
            resizeGripStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = new Color(0.55f, 0.6f, 0.7f, 0.9f) }
            };

            stylesReady = true;
        }

        /// <summary>Build a GUIContent with icon + label, or fallback text if the icon is null.</summary>
        private static GUIContent MakeGC(Texture2D icon, string label, string fallback)
        {
            if (icon != null)
                return new GUIContent(label, icon);
            return new GUIContent(fallback);
        }

        private GUIStyle MakeMissionStyle(Color accent)
        {
            return new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(accent.r * 0.2f, accent.g * 0.2f, accent.b * 0.2f, 0.85f)) },
                padding = new RectOffset(10, 10, 6, 6), margin = new RectOffset(0, 0, 2, 2)
            };
        }

        private void DrawContent(int id)
        {
            InitStyles();

            GUILayout.BeginVertical(windowStyle);

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("Gene Kerman Mission Manager", headerStyle);
            int unread = GeneKermanMod.Instance.UnreadNotifications;
            if (unread > 0)
            {
                GUILayout.Label($" {unread}", new GUIStyle(GUI.skin.label) {
                    fontSize = 14, fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 0.8f, 0.2f) }
                }, GUILayout.Width(40));
            }
            if (GUILayout.Button("✕", tabStyle, GUILayout.Width(25), GUILayout.Height(25)))
                GeneKermanMod.Instance.ShowMainWindow = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // Tabs
            GUILayout.BeginHorizontal();
            for (int i = 0; i < tabNames.Length; i++)
            {
                string label = tabNames[i];
                if (i == 4 && unread > 0) label += $" ({unread})";
                if (GUILayout.Button(label, i == selectedTab ? tabActiveStyle : tabStyle))
                {
                    selectedTab = i;
                    scrollPos = Vector2.zero;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // Tab content
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(480));
            switch (selectedTab)
            {
                case 0: DrawMissionsTab(); break;
                case 1: DrawContractsTab(); break;
                case 2: DrawProfileTab(); break;
                case 3: DrawMarketplaceTab(); break;
                case 4: DrawNotificationsTab(); break;
                case 5: DrawSettingsTab(); break;
            }
            GUILayout.EndScrollView();

            // Status bar
            if (!string.IsNullOrEmpty(statusMsg) && Time.realtimeSinceStartup - statusTime < 5f)
            {
                statusStyle.normal.textColor = statusMsg.StartsWith("(Ok)") ? new Color(0.3f, 0.9f, 0.4f) : new Color(0.9f, 0.4f, 0.3f);
                GUILayout.Label(statusMsg, statusStyle);
            }

            GUILayout.EndVertical();

            GUI.DragWindow();
        }

        // ── Missions Tab ────────────────────────────────────────────────────

        private void DrawMissionsTab()
        {
            if (loadingMissions)
            {
                GUILayout.Label("Loading missions...", labelStyle);
                return;
            }

            if (missions == null || missions.Count == 0)
            {
                GUILayout.Label("No missions available.", labelStyle);
                if (GUILayout.Button(gcRefresh, GUILayout.Height(30)))
                    RefreshMissions();
                return;
            }

            if (missionsLocked)
            {
                GUILayout.Label("🔒 Mission selection is locked (Sunday).", valueStyle);
                GUILayout.Space(5);
            }

            foreach (var mObj in missions)
            {
                var m = mObj as Dictionary<string, object>;
                if (m == null) continue;

                int diff = MiniJSON.GetInt(m, "difficulty");
                GUIStyle style = diff <= 3 ? missionEasyStyle : diff <= 6 ? missionMedStyle : diff <= 8 ? missionHardStyle : missionExtremeStyle;
                string diffLabel = diff <= 3 ? "(E)" : diff <= 6 ? "(M)" : diff <= 8 ? "(H)" : "(X)";

                GUILayout.BeginVertical(style);
                GUILayout.BeginHorizontal();

                // Mission info
                GUILayout.BeginVertical();
                GUILayout.Label($"{diffLabel} #{MiniJSON.GetInt(m, "id")}  {MiniJSON.GetString(m, "desc_en")}", valueStyle);
                GUILayout.Label($"⭐ {diff}/10  ·  +{MiniJSON.GetInt(m, "xp")} XP  ·  +{MiniJSON.GetInt(m, "coins")} KCoins  ·  Fine: {MiniJSON.GetInt(m, "fine")}", labelStyle);
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();

                // Accept button
                if (!missionsLocked)
                {
                    if (GUILayout.Button("Accept", acceptBtnStyle, GUILayout.Width(70), GUILayout.Height(30)))
                    {
                        int missionId = MiniJSON.GetInt(m, "id");
                        GeneKermanMod.Instance.RunCoroutine(DoSelectMission(missionId));
                    }
                }

                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.Space(5);
            if (GUILayout.Button(gcRefreshMissions, GUILayout.Height(28)))
                RefreshMissions();
        }

        // ── Contracts Tab (Mail-style Inbox) ─────────────────────────────────

        // Mail state
        private HashSet<string> selectedContracts = new HashSet<string>();
        private bool selectAll;
        private int filterMode; // 0=All, 1=Incoming, 2=Outgoing
        private readonly string[] filterLabels = { "All", "Incoming", "Outgoing" };
        private string openContractId; // If set, shows detail view
        private string rescueAcceptConfirmId; // Contract awaiting modded-target accept confirm

        /// <summary>Coerce a MiniJSON list into a List&lt;string&gt; (used for rescue kerbal names).</summary>
        private static List<string> ToStringList(List<object> list)
        {
            var result = new List<string>();
            if (list != null)
                foreach (var o in list)
                    if (o != null) result.Add(o.ToString());
            return result;
        }

        private void DrawContractsTab()
        {
            // If a contract is open in detail view, show that instead
            if (!string.IsNullOrEmpty(openContractId))
            {
                DrawContractDetail();
                return;
            }

            DrawMailToolbar();
            GUILayout.Space(4);
            DrawMailList();
        }

        private void DrawMailToolbar()
        {
            // Row 1: Inbox/Trash + Compose + Refresh
            GUILayout.BeginHorizontal();

            if (GUILayout.Button(gcInbox, !showTrash ? tabActiveStyle : tabStyle, GUILayout.Height(26), GUILayout.Width(80)))
                showTrash = false;
            if (GUILayout.Button(gcTrash, showTrash ? tabActiveStyle : tabStyle, GUILayout.Height(26), GUILayout.Width(80)))
                showTrash = true;

            GUILayout.Space(10);

            if (!showTrash && GUILayout.Button(gcCompose, acceptBtnStyle, GUILayout.Height(26)))
            {
                int balance = profile != null ? MiniJSON.GetInt(profile, "balance") : 0;
                string userId = profile != null ? MiniJSON.GetString(profile, "user_id", "") : "";
                string userName = profile != null ? MiniJSON.GetString(profile, "username", "") : "";
                GeneKermanMod.Instance.OpenCreateContractWindow(balance, userId, userName);
            }

            GUI.enabled = selectedContracts.Count > 0;
            if (!showTrash && GUILayout.Button("Cancel", deleteBtnStyle, GUILayout.Height(26), GUILayout.Width(70)))
            {
                // Only cancel pending contracts from selection
                foreach (string cid in selectedContracts)
                {
                    var ct = FindContractById(cid);
                    if (ct != null && MiniJSON.GetString(ct, "status") == "pending")
                        GeneKermanMod.Instance.RunCoroutine(DoCancelContract(cid));
                }
                selectedContracts.Clear();
                selectAll = false;
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(gcRefreshOnly, tabStyle, GUILayout.Height(26), GUILayout.Width(30)))
                RefreshContracts();

            GUILayout.EndHorizontal();

            // Row 2: Select All + Filter
            GUILayout.BeginHorizontal();

            bool newSelectAll = GUILayout.Toggle(selectAll, " Select All", checkboxStyle, GUILayout.Width(90));
            if (newSelectAll != selectAll)
            {
                selectAll = newSelectAll;
                selectedContracts.Clear();
                if (selectAll && contracts != null)
                {
                    foreach (var cObj in contracts)
                    {
                        var c = cObj as Dictionary<string, object>;
                        if (c != null) 
                        {
                            string cid = MiniJSON.GetString(c, "contract_id");
                            if (trashedContracts.Contains(cid) == showTrash)
                                selectedContracts.Add(cid);
                        }
                    }
                }
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label("Filter:", labelStyle, GUILayout.Width(40));
            for (int i = 0; i < filterLabels.Length; i++)
            {
                var style = (i == filterMode) ? tabActiveStyle : tabStyle;
                if (GUILayout.Button(filterLabels[i], style, GUILayout.Height(22), GUILayout.Width(70)))
                    filterMode = i;
            }

            GUILayout.EndHorizontal();
        }

        private void DrawMailList()
        {
            if (loadingContracts)
            {
                GUILayout.Label("Loading...", labelStyle);
                return;
            }

            if (contracts == null || contracts.Count == 0)
            {
                GUILayout.Space(20);
                GUILayout.Label(" No contracts.", labelStyle);
                return;
            }

            var grouped = new Dictionary<string, List<Dictionary<string, object>>>();
            var weekKeys = new List<string>();

            foreach (var cObj in contracts)
            {
                var c = cObj as Dictionary<string, object>;
                if (c == null) continue;

                string cid = MiniJSON.GetString(c, "contract_id");
                bool isTrashed = trashedContracts.Contains(cid);
                if (showTrash != isTrashed) continue;

                bool isIncoming = !MiniJSON.GetBool(c, "is_outgoing");

                if (filterMode == 1 && !isIncoming) continue;
                if (filterMode == 2 && isIncoming) continue;

                string createdAtStr = MiniJSON.GetString(c, "created_at", "");
                string weekKey = GetWeekKey(createdAtStr);

                if (!grouped.ContainsKey(weekKey))
                {
                    grouped[weekKey] = new List<Dictionary<string, object>>();
                    weekKeys.Add(weekKey);
                }
                grouped[weekKey].Add(c);
            }

            if (grouped.Count == 0)
            {
                GUILayout.Space(20);
                GUILayout.Label(showTrash ? "[X] Trash bin is empty." : " No contracts match the filter.", labelStyle);
                return;
            }

            foreach (string week in weekKeys)
            {
                var items = grouped[week];
                
                GUILayout.BeginHorizontal(boxDarkStyle);
                bool collapsed = collapsedWeeks.Contains(week);
                if (GUILayout.Button((collapsed ? "▶ " : "▼ ") + week + $" ({items.Count})", mailSubjectStyle))
                {
                    if (collapsed) collapsedWeeks.Remove(week);
                    else collapsedWeeks.Add(week);
                }
                GUILayout.EndHorizontal();

                if (collapsed) continue;

                foreach (var c in items)
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    string status = MiniJSON.GetString(c, "status");
                    string issuerName = MiniJSON.GetString(c, "issuer_name");
                    string contractorName = MiniJSON.GetString(c, "contractor_name");
                    string mission = MiniJSON.GetString(c, "mission");
                    string dueDate = MiniJSON.GetString(c, "due_date");
                    int payment = MiniJSON.GetInt(c, "payment");
                    bool isIncoming = !MiniJSON.GetBool(c, "is_outgoing");

                    Color dotColor = GetStatusColor(status);

                    GUILayout.BeginHorizontal(mailRowStyle);

                    bool wasSelected = selectedContracts.Contains(cid);
                    bool isSelected = GUILayout.Toggle(wasSelected, "", GUILayout.Width(18));
                    if (isSelected != wasSelected)
                    {
                        if (isSelected) selectedContracts.Add(cid);
                        else selectedContracts.Remove(cid);
                    }

                    var dotStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 10, fixedWidth = 14,
                        normal = { textColor = dotColor },
                        alignment = TextAnchor.MiddleCenter,
                    };
                    GUILayout.Label("●", dotStyle, GUILayout.Width(14));

                    string dirLabel = isIncoming ? $"← {issuerName}" : $"→ {contractorName}";
                    GUILayout.Label(dirLabel, mailSenderStyle, GUILayout.Width(110));

                    // [R] badge marks contracts that have a part restriction. The badge
                    // and title turn red only while that contract's part filter is
                    // actively being enforced in the editor — not merely present.
                    string modlist = MiniJSON.GetString(c, "modlist", "");
                    bool restricted = !string.IsNullOrEmpty(modlist);
                    bool filterActive = restricted
                        && EditorPartEnforcer.Instance != null
                        && EditorPartEnforcer.Instance.IsEnforcing()
                        && EditorPartEnforcer.Instance.ActiveContractId == cid;

                    Color amber = new Color(0.9f, 0.7f, 0.2f);
                    Color red = new Color(0.95f, 0.32f, 0.32f);

                    if (restricted)
                    {
                        var lockStyle = new GUIStyle(GUI.skin.label)
                        {
                            fontSize = 10, fixedWidth = 18,
                            normal = { textColor = filterActive ? red : amber },
                            alignment = TextAnchor.MiddleCenter,
                        };
                        GUILayout.Label("[R]", lockStyle, GUILayout.Width(18));
                    }
                    else
                    {
                        GUILayout.Space(18);
                    }

                    string subjectText = mission.Length > 28 ? mission.Substring(0, 25) + "..." : mission;
                    var subjectStyle = mailSubjectStyle;
                    if (filterActive)
                    {
                        subjectStyle = new GUIStyle(mailSubjectStyle);
                        subjectStyle.normal.textColor = red;
                        subjectStyle.hover.textColor = new Color(1f, 0.5f, 0.5f);
                    }
                    if (GUILayout.Button(subjectText, subjectStyle))
                    {
                        openContractId = cid;
                    }

                    GUILayout.Label($"${payment}", mailAmountStyle, GUILayout.Width(60));

                    string dateLabel = !string.IsNullOrEmpty(dueDate) && dueDate.Length >= 10 ? dueDate.Substring(5) : dueDate;
                    GUILayout.Label(dateLabel, mailDateStyle, GUILayout.Width(45));

                    if (status == "completed" || status == "failed" || status == "declined" || status == "cancelled")
                    {
                        if (!showTrash)
                        {
                            if (GUILayout.Button(gcDel, iconBtnStyle, GUILayout.Width(22), GUILayout.Height(20)))
                            {
                                trashedContracts.Add(cid);
                                SaveTrash();
                            }
                        }
                        else
                        {
                            if (GUILayout.Button(gcRestore, iconBtnStyle, GUILayout.Width(22), GUILayout.Height(20)))
                            {
                                trashedContracts.Remove(cid);
                                SaveTrash();
                            }
                        }
                    }
                    else
                    {
                        GUILayout.Space(29);
                    }

                    GUILayout.EndHorizontal();
                }
            }
        }

        private string GetWeekKey(string isoDate)
        {
            if (string.IsNullOrEmpty(isoDate) || isoDate.Length < 10) return "Unknown Date";
            System.DateTime dt;
            if (System.DateTime.TryParse(isoDate, out dt))
            {
                int diff = (7 + (dt.DayOfWeek - System.DayOfWeek.Monday)) % 7;
                var monday = dt.AddDays(-1 * diff).Date;
                return "Week of " + monday.ToString("MMM d, yyyy");
            }
            return "Unknown Date";
        }

        private void AutoCollapseWeeks()
        {
            if (contracts == null) return;
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach(var cObj in contracts) {
                var c = cObj as Dictionary<string, object>;
                if (c == null) continue;
                if (trashedContracts.Contains(MiniJSON.GetString(c, "contract_id"))) continue;
                string w = GetWeekKey(MiniJSON.GetString(c, "created_at"));
                if (!counts.ContainsKey(w)) { counts[w] = 0; order.Add(w); }
                counts[w]++;
            }
            
            int total = 0;
            collapsedWeeks.Clear();
            foreach(string w in order) {
                if (total >= 10) collapsedWeeks.Add(w);
                total += counts[w];
            }
        }

        private void DrawContractDetail()
        {
            var c = FindContractById(openContractId);
            if (c == null)
            {
                openContractId = null;
                return;
            }

            string status = MiniJSON.GetString(c, "status");
            Color statusColor = GetStatusColor(status);

            // Back button
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("← Back", tabStyle, GUILayout.Height(26), GUILayout.Width(70)))
                openContractId = null;
            GUILayout.FlexibleSpace();
            var statusLabelStyle = new GUIStyle(valueStyle) { normal = { textColor = statusColor } };
            GUILayout.Label(status.ToUpper(), statusLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            GUILayout.BeginVertical(boxDarkStyle);

            // Header
            GUILayout.Label(MiniJSON.GetString(c, "mission"), valueStyle);
            GUILayout.Space(8);

            // Details grid
            DrawDetailRow("From", MiniJSON.GetString(c, "issuer_name"));
            DrawDetailRow("To", MiniJSON.GetString(c, "contractor_name"));
            DrawDetailRow("Payment", $"{MiniJSON.GetInt(c, "payment")} KCoins");

            int fine = MiniJSON.GetInt(c, "fine");
            if (fine > 0)
                DrawDetailRow("Fine", $"{fine} KCoins");

            DrawDetailRow("Due Date", MiniJSON.GetString(c, "due_date"));

            string createdAt = MiniJSON.GetString(c, "created_at", "");
            if (!string.IsNullOrEmpty(createdAt) && createdAt.Length >= 10)
                DrawDetailRow("Created", createdAt.Substring(0, 10));

            string mType = MiniJSON.GetString(c, "mission_type", "active_vessel");
            string typeLabel = mType == "craft_build" ? "Craft Build"
                : mType == "rescue" ? "🛟 Rescue Mission" : "Active Vessel";
            DrawDetailRow("Type", typeLabel);

            // Rescue: show the target (body + orbit/surface spot) and the stranded crew.
            if (mType == "rescue")
            {
                var rt = MiniJSON.GetDict(c, "rescue_target");
                if (rt != null)
                {
                    string rbody = MiniJSON.GetString(rt, "body", "?");
                    string rmode = MiniJSON.GetString(rt, "mode", "orbit");
                    if (rmode == "surface")
                        DrawDetailRow("Target", $"{rbody} surface @ {MiniJSON.GetDouble(rt, "lat"):F1}°,{MiniJSON.GetDouble(rt, "lon"):F1}°");
                    else
                        DrawDetailRow("Target", $"{rbody} orbit Ap {MiniJSON.GetDouble(rt, "ap") / 1000:F0}km / Pe {MiniJSON.GetDouble(rt, "pe") / 1000:F0}km");
                    if (MiniJSON.GetBool(rt, "is_modded"))
                        DrawDetailRow("⚠ Mod", $"Needs the {rbody} planet pack");
                }
                var krew = MiniJSON.GetList(c, "rescue_kerbals");
                if (krew != null && krew.Count > 0)
                    DrawDetailRow("Kerbals", $"{krew.Count} to recover");
            }

            GUILayout.EndVertical();

            GUILayout.Space(8);

            // Modlist VAB Enforcer Toggle (if in Editor). Always shown for active
            // contracts in the editor so the absence of a restriction is explicit
            // rather than the box silently vanishing.
            if (HighLogic.LoadedSceneIsEditor && status == "active")
            {
                string reqModlist = MiniJSON.GetString(c, "modlist", "");
                GUILayout.BeginVertical(boxDarkStyle);
                GUILayout.Label("🛠 Required Mods", headerStyle);

                if (string.IsNullOrEmpty(reqModlist))
                {
                    GUILayout.Label("No part restriction — all parts allowed.", labelStyle);
                }
                else
                {
                    GUILayout.Label(reqModlist, labelStyle);
                    GUILayout.Space(5);

                    bool currentlyEnforcing = EditorPartEnforcer.Instance != null &&
                                              EditorPartEnforcer.Instance.IsEnforcing() &&
                                              EditorPartEnforcer.Instance.ActiveContractId == openContractId;

                    bool enforce = GUILayout.Toggle(currentlyEnforcing, " Enforce VAB part limits for this contract", checkboxStyle);

                    if (enforce != currentlyEnforcing)
                    {
                        if (enforce)
                            EditorPartEnforcer.Instance?.EnforceModlist(openContractId, reqModlist);
                        else
                            EditorPartEnforcer.Instance?.StopEnforcing();
                    }
                }
                GUILayout.EndVertical();
                GUILayout.Space(8);
            }

            // Action buttons
            GUILayout.BeginHorizontal();

            if (status == "active")
            {
                if (GUILayout.Button("[Sub] Submit", acceptBtnStyle, GUILayout.Height(30)))
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    string mission = MiniJSON.GetString(c, "mission");
                    string mT = MiniJSON.GetString(c, "mission_type", "active_vessel");
                    string reqSit = MiniJSON.GetString(c, "required_situation", "");
                    string reqBody = MiniJSON.GetString(c, "required_body", "");
                    string reqModlist = MiniJSON.GetString(c, "modlist", "");
                    RescueTargetSpec rescueSpec = null;
                    List<string> rescueKerbals = null;
                    if (mT == "rescue")
                    {
                        rescueSpec = RescueTargetSpec.FromDict(MiniJSON.GetDict(c, "rescue_target"));
                        rescueKerbals = ToStringList(MiniJSON.GetList(c, "rescue_kerbals"));
                    }
                    GeneKermanMod.Instance.OpenSubmitWindow(cid, mission, mT, reqSit, reqBody, reqModlist,
                        rescueSpec, rescueKerbals);
                }
            }
            else if (status == "pending")
            {
                // Only the contractor (incoming) may Accept/Decline. The issuer of an
                // outgoing contract is not the recipient, so they can only withdraw it.
                bool isOutgoing = MiniJSON.GetBool(c, "is_outgoing");
                if (isOutgoing)
                {
                    if (GUILayout.Button("(No) Withdraw", deleteBtnStyle, GUILayout.Height(30)))
                    {
                        string cid = MiniJSON.GetString(c, "contract_id");
                        GeneKermanMod.Instance.RunCoroutine(DoCancelContract(cid));
                        openContractId = null;
                    }
                }
                else
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    string issuerName = MiniJSON.GetString(c, "issuer_name", "");
                    bool isRescue = MiniJSON.GetString(c, "mission_type", "") == "rescue";
                    bool moddedTarget = MiniJSON.GetBool(c, "is_modded_target");

                    // Rescue to a modded planet: warn and require a second confirm click
                    // so the rescuer doesn't accept a mission they can't reach.
                    if (isRescue && moddedTarget && rescueAcceptConfirmId != cid)
                    {
                        string rbody = "this planet";
                        var rtw = MiniJSON.GetDict(c, "rescue_target");
                        if (rtw != null) rbody = MiniJSON.GetString(rtw, "body", rbody);
                        GUILayout.Label($"⚠ Targets {rbody} (modded). You need its planet pack installed.", valueStyle);
                        if (GUILayout.Button("⚠ Accept anyway", acceptBtnStyle, GUILayout.Height(30)))
                            rescueAcceptConfirmId = cid;
                    }
                    else if (GUILayout.Button("(Ok) Accept", acceptBtnStyle, GUILayout.Height(30)))
                    {
                        rescueAcceptConfirmId = null;
                        GeneKermanMod.Instance.RunCoroutine(DoAcceptContract(cid, issuerName));
                        openContractId = null;
                    }
                    GUILayout.Space(8);
                    if (GUILayout.Button("(No) Decline", deleteBtnStyle, GUILayout.Height(30)))
                    {
                        rescueAcceptConfirmId = null;
                        GeneKermanMod.Instance.RunCoroutine(DoCancelContract(cid));
                        openContractId = null;
                    }
                }
            }
            else if (status == "submitted")
            {
                // The contractor has submitted work. The issuer (outgoing side) reviews
                // it here — approve releases payment and completes the contract (which is
                // what unlocks the Import button); refuse opens a dispute. The contractor
                // just waits. Previously this had no branch, so the issuer had to switch
                // to Discord to review, and the Import button never appeared in-game.
                bool isOutgoing = MiniJSON.GetBool(c, "is_outgoing");
                if (isOutgoing)
                {
                    // The submission preview (blueprint render + orbital telemetry) is
                    // available to both parties via the dedicated row below.
                    if (GUILayout.Button("(Ok) Approve", acceptBtnStyle, GUILayout.Height(30)))
                    {
                        string cid = MiniJSON.GetString(c, "contract_id");
                        GeneKermanMod.Instance.RunCoroutine(DoReviewContract(cid, true));
                    }
                    GUILayout.Space(8);
                    if (GUILayout.Button("(No) Refuse", deleteBtnStyle, GUILayout.Height(30)))
                    {
                        string cid = MiniJSON.GetString(c, "contract_id");
                        GeneKermanMod.Instance.RunCoroutine(DoReviewContract(cid, false));
                    }
                }
                else
                {
                    GUI.enabled = false;
                    GUILayout.Button("Waiting for issuer review...", submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;
                }
            }
            else if (status == "disputed")
            {
                // Submission was refused. The contractor (incoming side) resolves it
                // here, mirroring the Discord dispute buttons. The issuer just waits.
                bool isOutgoing = MiniJSON.GetBool(c, "is_outgoing");
                if (isOutgoing)
                {
                    GUI.enabled = false;
                    GUILayout.Button("Waiting for the contractor to resolve...", submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;
                }
                else
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    bool botIssued = MiniJSON.GetBool(c, "is_bot_issued");

                    // "More Time" on a human contract needs a proposed new date.
                    if (!botIssued)
                    {
                        if (string.IsNullOrEmpty(disputeDateInput))
                            disputeDateInput = System.DateTime.Now.AddDays(7).ToString("yyyy-MM-dd");
                        GUILayout.Label("New date:", labelStyle, GUILayout.Width(62));
                        disputeDateInput = GUILayout.TextField(disputeDateInput, GUILayout.Width(86), GUILayout.Height(30));
                    }

                    if (GUILayout.Button("More Time", submitBtnStyle, GUILayout.Height(30)))
                        GeneKermanMod.Instance.RunCoroutine(DoDispute(cid, "more_time", botIssued ? null : disputeDateInput));
                    GUILayout.Space(6);
                    if (GUILayout.Button("Pay Fine", deleteBtnStyle, GUILayout.Height(30)))
                        GeneKermanMod.Instance.RunCoroutine(DoDispute(cid, "pay_fine", null));
                    GUILayout.Space(6);
                    // Sue sends the blueprint + details to the mods for review — the
                    // recourse when the AI reviewer (or the issuer) refused wrongly.
                    if (GUILayout.Button("Sue", acceptBtnStyle, GUILayout.Height(30)))
                        GeneKermanMod.Instance.RunCoroutine(DoDispute(cid, "sue", null));

                    // Settle requires a human counterparty — not applicable to AI contracts.
                    if (!botIssued)
                    {
                        GUILayout.Space(6);
                        if (GUILayout.Button("Settle", submitBtnStyle, GUILayout.Height(30)))
                            GeneKermanMod.Instance.RunCoroutine(DoDispute(cid, "settle", null));
                    }
                }
            }
            else if (status == "completed")
            {
                string cid = MiniJSON.GetString(c, "contract_id");
                bool botIssued = MiniJSON.GetBool(c, "is_bot_issued");
                string completedType = MiniJSON.GetString(c, "mission_type", "active_vessel");

                // Flag-design contracts have no craft to download — the flag is
                // installed automatically into the issuer's flag picker by the
                // craft-import queue at the Space Center. So show status, not a button.
                if (completedType == "flag_design")
                {
                    bool isIssuer = MiniJSON.GetBool(c, "is_outgoing");
                    GUI.enabled = false;
                    GUILayout.Button(isIssuer
                        ? "🚩 Flag added to your flag picker (visit Space Center)"
                        : "🚩 Flag delivered to the issuer",
                        submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;
                }
                // Bot-contract crafts are delivered to the builder's corporation
                // channel as a blueprint — never imported as a live vessel (that
                // would let them be re-imported/duplicated). Only player-to-player
                // contracts deliver their craft into the save here.
                else if (botIssued)
                {
                    GUI.enabled = false;
                    GUILayout.Button("(Ok) Delivered to your corp", submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;
                }
                else if (GKContractScenario.Instance != null && GKContractScenario.Instance.HasImportedVessel(cid))
                {
                    GUI.enabled = false;
                    GUILayout.Button("(Ok) Vessel Imported", submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;
                }
                else
                {
                    if (GUILayout.Button("[+] Import / Download", submitBtnStyle, GUILayout.Height(30)))
                    {
                        // The deliverable craft belongs to whoever built/submitted it (the
                        // contractor) — its crew get tagged with their name on import.
                        string ownerName = MiniJSON.GetString(c, "contractor_name", "");
                        GeneKermanMod.Instance.RunCoroutine(DoDownloadCraft(cid, ownerName));
                    }
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Submission preview — once work has been submitted, either party can pull
            // up the contractor's blueprint render(s) and (for active-vessel / rescue
            // contracts) the orbital telemetry "mission state" image right here, in any
            // state from review onward. The 🛰 Telemetry button inside the popup appears
            // only when a telemetry image was rendered for this submission.
            if (status == "submitted" || status == "disputed" || status == "completed")
            {
                GUILayout.Space(6);
                if (GUILayout.Button("🖼 View Submission", submitBtnStyle, GUILayout.Height(28)))
                    OpenBlueprintPreview(MiniJSON.GetString(c, "contract_id"));
            }
        }

        private void DrawDetailRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", labelStyle, GUILayout.Width(80));
            GUILayout.Label(value, valueStyle);
            GUILayout.EndHorizontal();
        }

        private Color GetStatusColor(string status)
        {
            switch (status)
            {
                case "active":    return new Color(0.3f, 0.8f, 0.4f);
                case "submitted": return new Color(0.3f, 0.5f, 0.9f);
                case "completed": return new Color(0.2f, 0.7f, 0.9f);
                case "disputed":  return new Color(0.9f, 0.4f, 0.3f);
                case "pending":   return new Color(0.9f, 0.75f, 0.2f);
                default:          return new Color(0.5f, 0.5f, 0.5f);
            }
        }

        private Dictionary<string, object> FindContractById(string cid)
        {
            if (contracts == null) return null;
            foreach (var cObj in contracts)
            {
                var c = cObj as Dictionary<string, object>;
                if (c != null && MiniJSON.GetString(c, "contract_id") == cid)
                    return c;
            }
            return null;
        }

        // ── Profile Tab ─────────────────────────────────────────────────────

        private void DrawProfileTab()
        {
            if (loadingProfile || profile == null)
            {
                GUILayout.Label("Loading profile...", labelStyle);
                if (GUILayout.Button(gcRefresh, GUILayout.Height(30)))
                    RefreshProfile();
                return;
            }

            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label($" {MiniJSON.GetString(profile, "username")}", headerStyle);
            GUILayout.Space(10);

            DrawStatRow("$ Balance", $"{MiniJSON.GetInt(profile, "balance"):N0} {MiniJSON.GetString(profile, "currency_name", "KCoins")}");
            DrawStatRow("✨ XP", $"{MiniJSON.GetInt(profile, "xp"):N0}");
            DrawStatRow("📊 Level", $"{MiniJSON.GetInt(profile, "level")}");
            DrawStatRow("💬 Messages", $"{MiniJSON.GetInt(profile, "messages"):N0}");

            // Unlocked levels
            var levels = MiniJSON.GetList(profile, "unlocked_levels");
            if (levels.Count > 0)
            {
                GUILayout.Space(10);
                GUILayout.Label("🏆 KSP Achievements", valueStyle);
                string badges = "";
                foreach (var l in levels)
                {
                    int lvl = 0;
                    if (l is long ll) lvl = (int)ll;
                    else if (l is double dd) lvl = (int)dd;
                    badges += GetLevelBadge(lvl) + "  ";
                }
                GUILayout.Label(badges, labelStyle);
            }

            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Unlink button
            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label(" Account", valueStyle);
            GUILayout.Label($"Server: {GeneKermanMod.Instance.Api.ServerUrl}", labelStyle);
            if (GUILayout.Button(gcUnlink, GUILayout.Height(28)))
            {
                GeneKermanMod.Instance.Api.ClearToken();
                GeneKermanMod.Instance.ShowMainWindow = false;
                GeneKermanMod.Instance.ShowLinkWindow = true;
                SetStatus("Account unlinked.");
            }
            GUILayout.EndVertical();
        }

        private void DrawStatRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(110));
            GUILayout.Label(value, valueStyle);
            GUILayout.EndHorizontal();
        }

        private string GetLevelBadge(int level)
        {
            switch (level)
            {
                case 1: return "🌍1";  case 2: return "🌙2";  case 3: return "🔗3";
                case 4: return "(H)4";  case 5: return "🌎5";  case 6: return "💜6";
                case 7: return "☄️7";  case 8: return "🌕8";  case 9: return "🪐9";
                case 10: return "⭐10"; case 11: return "♂️11"; case 12: return "♀️12";
                case 13: return "🔵13"; case 14: return "🌟14"; case 15: return "💫15";
                default: return $"Lv{level}";
            }
        }

        // ── Notifications Tab ───────────────────────────────────────────────

        private void DrawNotificationsTab()
        {
            if (loadingNotifs)
            {
                GUILayout.Label("Loading notifications...", labelStyle);
                return;
            }

            if (notifications == null || notifications.Count == 0)
            {
                GUILayout.Label("No notifications.", labelStyle);
                if (GUILayout.Button(gcRefresh, GUILayout.Height(30)))
                    RefreshNotifications();
                return;
            }

            // Dismiss is deferred until after the loop — removing from `notifications`
            // mid-iteration would throw.
            string dismissId = null;

            foreach (var nObj in notifications)
            {
                var n = nObj as Dictionary<string, object>;
                if (n == null) continue;

                string id = MiniJSON.GetString(n, "id");
                bool read = MiniJSON.GetBool(n, "read");

                string contractId = "";
                var data = MiniJSON.GetDict(n, "data");
                if (data != null) contractId = MiniJSON.GetString(data, "contract_id");

                var prevColor = GUI.color;
                if (read) GUI.color = new Color(1f, 1f, 1f, 0.55f); // dim read items

                GUILayout.BeginVertical(boxDarkStyle);

                GUILayout.Label((read ? "   " : "● ") + MiniJSON.GetString(n, "title"), valueStyle);
                GUILayout.Label(MiniJSON.GetString(n, "message"), labelStyle);
                GUILayout.Label(MiniJSON.GetString(n, "timestamp"), new GUIStyle(labelStyle) { fontSize = 10 });

                GUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(contractId)
                    && GUILayout.Button(gcOpen, tabStyle, GUILayout.Height(22), GUILayout.Width(80)))
                {
                    OpenContractDetail(contractId);
                }
                GUILayout.FlexibleSpace();
                if (!read && GUILayout.Button("(Ok) Mark read", tabStyle, GUILayout.Height(22), GUILayout.Width(110)))
                {
                    DoMarkNotificationRead(n, id);
                }
                if (GUILayout.Button("[X]", deleteBtnStyle, GUILayout.Height(22), GUILayout.Width(40)))
                {
                    dismissId = id;
                }
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                GUI.color = prevColor;
            }

            if (dismissId != null)
                DoDismissNotification(dismissId);

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("(Ok) Mark All Read", GUILayout.Height(28)))
            {
                GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.MarkNotificationsRead((ok, resp, status) =>
                {
                    if (ok)
                    {
                        if (notifications != null)
                            foreach (var o in notifications)
                            {
                                var d = o as Dictionary<string, object>;
                                if (d != null) d["read"] = true;
                            }
                        RecountUnread();
                        SetStatus("(Ok) All notifications marked read.");
                    }
                }));
            }
            if (GUILayout.Button(gcRefresh, GUILayout.Height(28)))
                RefreshNotifications();
            GUILayout.EndHorizontal();
        }

        private void DoMarkNotificationRead(Dictionary<string, object> n, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.MarkNotificationRead(id, (ok, resp, status) =>
            {
                if (ok)
                {
                    n["read"] = true;
                    RecountUnread();
                }
            }));
        }

        private void DoDismissNotification(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.DismissNotification(id, (ok, resp, status) =>
            {
                if (ok && notifications != null)
                {
                    notifications.RemoveAll(o =>
                    {
                        var d = o as Dictionary<string, object>;
                        return d != null && MiniJSON.GetString(d, "id") == id;
                    });
                    RecountUnread();
                    SetStatus("(Ok) Notification dismissed.");
                }
            }));
        }

        /// <summary>Recompute the unread badge from the currently loaded notifications.</summary>
        private void RecountUnread()
        {
            int unread = 0;
            if (notifications != null)
                foreach (var o in notifications)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && !MiniJSON.GetBool(d, "read")) unread++;
                }
            GeneKermanMod.Instance.UnreadNotifications = unread;
        }

        // ── Settings Tab ────────────────────────────────────────────────────────

        private void DrawSettingsTab()
        {
            var api = GeneKermanMod.Instance.Api;

            GUILayout.BeginVertical(windowStyle);
            GUILayout.Label("🔌 Connection Settings", new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(10);
            GUILayout.Label("Choose the official UPoK server, or connect to a custom IP if you are running your own.", labelStyle);
            GUILayout.Space(12);

            // Connection mode switch: Official Server vs Custom IP
            GUILayout.BeginHorizontal();
            bool official = api.UseOfficialServer;
            var selStyle = new GUIStyle(tabStyle) { fontStyle = FontStyle.Bold };
            GUI.backgroundColor = official ? new Color(0.2f, 0.7f, 0.35f) : Color.white;
            if (GUILayout.Button("Official Server", official ? selStyle : tabStyle, GUILayout.Height(28)))
            {
                api.SetOfficialServer();
                SetStatus("(Ok) Using official server.");
                ReconnectAfterServerChange();
            }
            GUI.backgroundColor = !official ? new Color(0.2f, 0.7f, 0.35f) : Color.white;
            if (GUILayout.Button("Custom IP", !official ? selStyle : tabStyle, GUILayout.Height(28)))
            {
                // Switch to custom using whatever is in the box (or the saved custom URL).
                if (string.IsNullOrEmpty(serverUrlInput))
                    serverUrlInput = api.CustomServerUrl;
                api.SetCustomServer(serverUrlInput);
                serverUrlInput = api.ServerUrl;
                SetStatus("(Ok) Using custom IP.");
                ReconnectAfterServerChange();
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            if (official)
            {
                GUILayout.Label($"Connected to the official server:\n{ApiClient.OfficialServerUrl}", labelStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Server URL:", GUILayout.Width(80));

                if (string.IsNullOrEmpty(serverUrlInput))
                    serverUrlInput = api.ServerUrl;

                serverUrlInput = GUILayout.TextField(serverUrlInput, GUILayout.Height(24));

                if (GUILayout.Button("Set", tabStyle, GUILayout.Width(60), GUILayout.Height(24)))
                {
                    api.SetCustomServer(serverUrlInput);
                    serverUrlInput = api.ServerUrl;
                    SetStatus("(Ok) Server URL updated.");
                    ReconnectAfterServerChange();
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(18);

            // Notifications toggle
            GUILayout.Label("🔔 Notifications", new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold });
            GUILayout.Space(4);
            bool notifsOn = api.NotificationsEnabled;
            bool newNotifs = GUILayout.Toggle(notifsOn, " Show in-game notification popups");
            if (newNotifs != notifsOn)
            {
                api.SetNotificationsEnabled(newNotifs);
                SetStatus(newNotifs ? "(Ok) Notifications enabled." : "(Ok) Notifications disabled.");
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// After the server target changes, reset loading flags (so RefreshAll does not
        /// skip) and try to reconnect.
        /// </summary>
        private void ReconnectAfterServerChange()
        {
            loadingProfile = false;
            loadingMissions = false;
            loadingContracts = false;
            loadingNotifs = false;
            RefreshAll();
        }

        // ── Data Fetching Coroutines ────────────────────────────────────────────────────

        private void RefreshProfile()
        {
            loadingProfile = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetProfile((ok, data, err) =>
            {
                loadingProfile = false;
                if (ok) profile = data;
            }));
        }

        private void RefreshMissions()
        {
            loadingMissions = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetWeeklyMissions((ok, data, err) =>
            {
                loadingMissions = false;
                if (ok)
                {
                    missions = MiniJSON.GetList(data, "missions");
                    weekKey = MiniJSON.GetString(data, "week_key");
                    missionsLocked = MiniJSON.GetBool(data, "is_locked");
                }
            }));
        }

        public void RefreshContracts()
        {
            loadingContracts = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetActiveContracts((ok, data, err) =>
            {
                loadingContracts = false;
                if (ok) contracts = MiniJSON.GetList(data, "contracts");
            }));
        }

        private void RefreshNotifications()
        {
            loadingNotifs = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetNotifications((ok, data, err) =>
            {
                loadingNotifs = false;
                if (ok)
                {
                    notifications = MiniJSON.GetList(data, "notifications");
                    GeneKermanMod.Instance.UnreadNotifications = MiniJSON.GetInt(data, "unread_count");
                }
            }));
        }

        // ── Actions ─────────────────────────────────────────────────────────

        private System.Collections.IEnumerator DoSelectMission(int missionId)
        {
            // Store mission info for contract injection
            Dictionary<string, object> selectedMission = null;
            if (missions != null)
            {
                selectedMission = missions.Find(m =>
                {
                    var d = m as Dictionary<string, object>;
                    return d != null && MiniJSON.GetInt(d, "id") == missionId;
                }) as Dictionary<string, object>;
            }

            yield return GeneKermanMod.Instance.Api.SelectMission(missionId, (ok, data, err) =>
            {
                if (ok)
                {
                    SetStatus($"(Ok) {MiniJSON.GetString(data, "message", "Mission accepted!")}");
                    RefreshContracts();

                    // Inject into stock contract system
                    if (GKContractScenario.Instance != null && selectedMission != null)
                    {
                        string cid = MiniJSON.GetString(data, "contract_id", "");
                        if (!string.IsNullOrEmpty(cid))
                        {
                            GKContractScenario.Instance.InjectContract(
                                cid,
                                MiniJSON.GetString(selectedMission, "desc_en"),
                                MiniJSON.GetInt(selectedMission, "coins"),
                                MiniJSON.GetInt(selectedMission, "difficulty"),
                                "" // due date from response
                            );
                        }
                    }
                }
                else
                {
                    SetStatus($"(No) {err ?? "Failed to select mission."}");
                }
            });
        }

        private System.Collections.IEnumerator DoAcceptContract(string contractId, string issuerName = "")
        {
            string wreckUrl = null;
            RescueTargetSpec wreckTarget = null;
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/accept", "{}", (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus("(Ok) Contract accepted!");
                    if (!string.IsNullOrEmpty(resp))
                    {
                        var data = MiniJSON.DeserializeDict(resp);
                        wreckUrl = MiniJSON.GetString(data, "rescue_vessel_node_url", null);
                        wreckTarget = RescueTargetSpec.FromDict(MiniJSON.GetDict(data, "rescue_target"));
                    }
                    RefreshContracts();
                }
                else
                {
                    SetStatus("(No) Failed to accept contract.");
                }
            });

            // Rescue: spawn the stranded vessel; its crew are tagged with the issuer's name.
            if (!string.IsNullOrEmpty(wreckUrl) && wreckTarget != null)
                yield return DoSpawnRescueWreck(contractId, wreckUrl, wreckTarget, issuerName);
        }

        private System.Collections.IEnumerator DoSpawnRescueWreck(string contractId, string wreckUrl, RescueTargetSpec target, string issuerName)
        {
            // Guard against a double spawn if accept is retried for the same contract.
            if (!string.IsNullOrEmpty(contractId) && !spawnedRescueWrecks.Add(contractId))
                yield break;
            string myName = GeneKermanMod.Instance.LinkedUsername;
            yield return GeneKermanMod.Instance.Api.DownloadFile(wreckUrl, (ok, fileData) =>
            {
                if (!ok || fileData == null)
                {
                    spawnedRescueWrecks.Remove(contractId); // let a later retry try again
                    SetStatus("⚠ Accepted, but could not download the stranded vessel.");
                    return;
                }
                string node = DecompressToString(fileData);
                // Spawn the stranded vessel where it actually is (its real orbit from the
                // snapshot) — NOT at the delivery target. The target is where the rescuer
                // must DELIVER the crew, so the wreck has to be elsewhere or there's no
                // mission. Import freezes the snapshot's orbit epoch to "now" so the wreck
                // appears exactly where the issuer left it, instead of KSP propagating the
                // stale epoch forward and placing it wherever the source vessel would be at
                // the current universe time. Crew are tagged with the issuer's name; the
                // rescuer collects them and brings them to the target to complete.
                string name = VesselTransfer.ImportVesselAtTarget(node, null, issuerName, myName);
                SetStatus(!string.IsNullOrEmpty(name)
                    ? $"🛟 Stranded vessel '{name}' is adrift — find it and bring the crew to {target.body}."
                    : "⚠ Could not spawn the stranded vessel — try from the Space Center.");
            });
        }

        private System.Collections.IEnumerator DoReviewContract(string contractId, bool approve)
        {
            string body = approve ? "{\"approve\":true}" : "{\"approve\":false}";
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/review", body, (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus(approve ? "(Ok) Submission approved!" : "🗑 Submission refused (dispute opened).");
                    openContractId = null;
                    RefreshContracts();
                }
                else
                {
                    SetStatus("(No) Failed to review submission.");
                }
            });
        }

        private System.Collections.IEnumerator DoDispute(string contractId, string action, string newDate)
        {
            var body = new Dictionary<string, object> { { "action", action } };
            if (!string.IsNullOrEmpty(newDate)) body["new_date"] = newDate;

            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/contracts/{contractId}/dispute", MiniJSON.Serialize(body),
                (ok, resp, status) =>
            {
                // The endpoint returns HTTP 200 with success=false for soft failures
                // (e.g. insufficient funds), so check the body, not just the status.
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && (d == null || MiniJSON.GetBool(d, "success", true));
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";

                if (success)
                {
                    SetStatus("(Ok) " + (string.IsNullOrEmpty(msg) ? "Done." : msg));
                    openContractId = null;
                    disputeDateInput = "";
                    RefreshContracts();
                }
                else
                {
                    SetStatus("(No) " + (string.IsNullOrEmpty(msg) ? "Action failed." : msg));
                }
            });
        }

        private System.Collections.IEnumerator DoCancelContract(string contractId)
        {
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/cancel", "{}", (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus("🗑 Contract cancelled.");
                    // Clear enforcer if it was active for this contract
                    if (EditorPartEnforcer.Instance != null &&
                        EditorPartEnforcer.Instance.ActiveContractId == contractId)
                        EditorPartEnforcer.Instance.StopEnforcing();
                    RefreshContracts();
                }
                else
                {
                    SetStatus("(No) Failed to cancel contract.");
                }
            });
        }

        private void SetStatus(string msg)
        {
            statusMsg = msg;
            statusTime = Time.realtimeSinceStartup;
        }

        private System.Collections.IEnumerator DoDownloadCraft(string contractId, string ownerName = "")
        {
            SetStatus("[+] Fetching craft info...");

            // Get craft file URLs from the API
            yield return GeneKermanMod.Instance.Api.Get($"/api/v1/craft/download/{contractId}", (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                {
                    var data = MiniJSON.DeserializeDict(resp);
                    var craftFiles = MiniJSON.GetList(data, "craft_files");
                    string vesselNodeUrl = MiniJSON.GetString(data, "vessel_node_url", null);
                    string loadmeta = MiniJSON.GetString(data, "loadmeta", null);

                    // Priority: vessel node (full vessel state) > craft file (blueprint only)
                    if (!string.IsNullOrEmpty(vesselNodeUrl))
                    {
                        SetStatus("[+] Downloading vessel data...");
                        GeneKermanMod.Instance.RunCoroutine(DoImportVessel(contractId, vesselNodeUrl, ownerName));
                    }
                    else if (craftFiles != null && craftFiles.Count > 0)
                    {
                        var first = craftFiles[0] as Dictionary<string, object>;
                        if (first == null)
                        {
                            SetStatus("(No) Invalid craft file data.");
                            return;
                        }

                        string url = MiniJSON.GetString(first, "url", "");
                        string filename = MiniJSON.GetString(first, "filename", "craft.craft");

                        if (string.IsNullOrEmpty(url))
                        {
                            SetStatus("(No) No download URL.");
                            return;
                        }

                        SetStatus("[+] Downloading craft file...");
                        GeneKermanMod.Instance.RunCoroutine(
                            GeneKermanMod.Instance.Api.DownloadFile(url, (dlOk, fileData) =>
                            {
                                if (dlOk && fileData != null)
                                {
                                    string path = CraftInstaller.Install(fileData, filename, loadmeta);
                                    if (path != null)
                                    {
                                        string msg = $"(Ok) Craft installed: {System.IO.Path.GetFileName(path)}";
                                        if (!string.IsNullOrEmpty(loadmeta))
                                            msg += " (+ loadmeta)";
                                        SetStatus(msg);
                                    }
                                    else
                                        SetStatus("(No) Failed to install craft file.");
                                }
                                else
                                {
                                    SetStatus("(No) Download failed.");
                                }
                            }));
                    }
                    else
                    {
                        SetStatus("(No) No craft file or vessel data in this contract.");
                    }
                }
                else
                {
                    SetStatus("(No) Could not fetch craft data.");
                }
            });
        }

        /// <summary>Gunzip (if needed) a downloaded vessel-node blob into its UTF-8 text.</summary>
        private static string DecompressToString(byte[] fileData)
        {
            byte[] rawData = fileData;
            if (fileData != null && fileData.Length >= 2 && fileData[0] == 0x1F && fileData[1] == 0x8B)
            {
                try
                {
                    using (var ms = new System.IO.MemoryStream(fileData))
                    using (var gz = new System.IO.Compression.GZipStream(ms,
                        System.IO.Compression.CompressionMode.Decompress))
                    using (var output = new System.IO.MemoryStream())
                    {
                        gz.CopyTo(output);
                        rawData = output.ToArray();
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] Vessel node decompression failed: {ex.Message}");
                    rawData = fileData;
                }
            }
            return System.Text.Encoding.UTF8.GetString(rawData);
        }

        private System.Collections.IEnumerator DoImportVessel(string contractId, string vesselNodeUrl, string ownerName = "")
        {
            Debug.Log($"[GeneKerman] DoImportVessel: Downloading from {vesselNodeUrl}");
            string myName = GeneKermanMod.Instance.LinkedUsername;
            yield return GeneKermanMod.Instance.Api.DownloadFile(vesselNodeUrl, (ok, fileData) =>
            {
                if (!ok || fileData == null)
                {
                    Debug.LogWarning($"[GeneKerman] DoImportVessel: Download failed (ok={ok}, data={fileData?.Length ?? -1})");
                    SetStatus("(No) Failed to download vessel data.");
                    return;
                }

                Debug.Log($"[GeneKerman] DoImportVessel: Downloaded {fileData.Length} bytes");

                string vesselNodeStr = DecompressToString(fileData);
                Debug.Log($"[GeneKerman] Vessel node string: {vesselNodeStr.Length} chars");

                string vesselName = VesselTransfer.ImportVessel(vesselNodeStr, ownerName, myName);

                if (!string.IsNullOrEmpty(vesselName))
                {
                    SetStatus($"[!] Vessel imported: {vesselName}");
                    if (GKContractScenario.Instance != null)
                    {
                        GKContractScenario.Instance.MarkVesselImported(contractId);
                    }
                }
                else
                {
                    SetStatus("(No) Failed to import vessel.");
                }
            });
        }

        // ── Marketplace Tab ─────────────────────────────────────────────────

        private void DrawMarketplaceTab()
        {
            GUILayout.Label("Marketplace", headerStyle);
            GUILayout.Space(4);

            // Sell panel — only meaningful in the editor where a craft is loaded.
            if (HighLogic.LoadedSceneIsEditor)
            {
                CaptureEditorCraft();
                GUILayout.BeginVertical(boxDarkStyle);
                GUILayout.Label("Sell Loaded Craft", valueStyle);
                if (string.IsNullOrEmpty(editorCraftName))
                {
                    GUILayout.Label("No craft loaded. Open one in the VAB/SPH.", labelStyle);
                }
                else
                {
                    GUILayout.Label($"[*] {editorCraftName} [{editorCraftType}]", labelStyle);
                    GUILayout.Label($"Parts: {editorPartCount} . Mass: {editorCraftMass:F1}t . Cost: {editorCraftCost:N0}", labelStyle);

                    if (string.IsNullOrEmpty(editorCraftPath))
                    {
                        GUILayout.Label("Save your craft first to sell it.", labelStyle);
                        if (GUILayout.Button(gcRefresh, tabStyle, GUILayout.Height(24), GUILayout.Width(90)))
                            CaptureEditorCraft();
                    }
                    else
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Price:", labelStyle, GUILayout.Width(45));
                        sellPriceInput = GUILayout.TextField(sellPriceInput, GUILayout.Width(90), GUILayout.Height(26));
                        GUILayout.Label("KCoins", labelStyle);
                        GUILayout.EndHorizontal();
                        GUILayout.Space(4);
                        GUI.enabled = !selling;
                        if (GUILayout.Button(selling ? new GUIContent("Listing...") : gcSell, acceptBtnStyle, GUILayout.Height(30)))
                            GeneKermanMod.Instance.RunCoroutine(DoSellCraft());
                        GUI.enabled = true;
                    }
                }
                GUILayout.EndVertical();
                GUILayout.Space(6);
            }
            else
            {
                GUILayout.Label("Open a craft in the VAB or SPH to put it up for sale.", labelStyle);
                GUILayout.Space(6);
            }

            // Active listings.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Listings", valueStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(gcRefresh, tabStyle, GUILayout.Height(24), GUILayout.Width(90)))
                LoadMarketListings();
            GUILayout.EndHorizontal();

            if (loadingMarket)
            {
                GUILayout.Label("Loading...", labelStyle);
                return;
            }
            if (marketListings == null || marketListings.Count == 0)
            {
                GUILayout.Label("No crafts for sale right now.", labelStyle);
                return;
            }

            string myId = profile != null ? MiniJSON.GetString(profile, "user_id", "") : "";
            foreach (var obj in marketListings)
            {
                var l = obj as Dictionary<string, object>;
                if (l == null) continue;
                GUILayout.BeginVertical(boxDarkStyle);
                GUILayout.Label($"[*] {MiniJSON.GetString(l, "craft_name", "Craft")}", valueStyle);
                GUILayout.Label($"{MiniJSON.GetInt(l, "price", 0):N0} KCoins . {MiniJSON.GetInt(l, "part_count", 0)} parts . by {MiniJSON.GetString(l, "seller_name", "?")}", labelStyle);
                GUILayout.Label($"Sold: {MiniJSON.GetInt(l, "sales_count", 0)}  .  Buy from the Discord channel", labelStyle);
                string sellerId = MiniJSON.GetString(l, "seller_id", "");
                if (!string.IsNullOrEmpty(myId) && sellerId == myId)
                {
                    if (GUILayout.Button("[X] Delist", deleteBtnStyle, GUILayout.Height(24)))
                        GeneKermanMod.Instance.RunCoroutine(DoDelistListing(MiniJSON.GetString(l, "listing_id", "")));
                }
                GUILayout.EndVertical();
                GUILayout.Space(4);
            }
        }

        /// <summary>Reads the currently-loaded editor craft's metadata and resolves
        /// its saved .craft path (mirrors SubmitWindow's capture logic).</summary>
        private void CaptureEditorCraft()
        {
            editorCraftName = "";
            editorPartCount = 0;
            editorCraftMass = 0f;
            editorCraftCost = 0f;
            editorCraftPath = "";

            try
            {
                var ship = EditorLogic.fetch?.ship;
                if (ship == null) return;

                editorCraftName = ship.shipName ?? "Untitled";
                editorPartCount = ship.parts?.Count ?? 0;
                if (ship.parts != null)
                {
                    foreach (var part in ship.parts)
                    {
                        editorCraftMass += part.mass + part.GetResourceMass();
                        editorCraftCost += part.partInfo?.cost ?? 0f;
                    }
                }

                editorCraftType = EditorDriver.editorFacility == EditorFacility.VAB ? "VAB" : "SPH";

                string saveFolder = HighLogic.SaveFolder ?? "default";
                string shipDir = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder,
                    "Ships", editorCraftType);
                string craftFile = System.IO.Path.Combine(shipDir, editorCraftName + ".craft");
                if (System.IO.File.Exists(craftFile))
                {
                    editorCraftPath = craftFile;
                }
                else
                {
                    string rootDir = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "Ships", editorCraftType);
                    craftFile = System.IO.Path.Combine(rootDir, editorCraftName + ".craft");
                    if (System.IO.File.Exists(craftFile))
                        editorCraftPath = craftFile;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Error reading editor craft: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator DoSellCraft()
        {
            if (string.IsNullOrEmpty(editorCraftPath) || !System.IO.File.Exists(editorCraftPath))
            {
                SetStatus("(No) Save your craft first.");
                yield break;
            }
            if (!int.TryParse(sellPriceInput, out int price) || price <= 0)
            {
                SetStatus("(No) Enter a valid price.");
                yield break;
            }

            byte[] craftBytes;
            try
            {
                craftBytes = System.IO.File.ReadAllBytes(editorCraftPath);
                // Carry the craft's custom mission flags so the buyer sees them.
                craftBytes = FlagTransfer.EmbedFlagsInCraft(craftBytes);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Failed to read craft: {ex.Message}");
                SetStatus("(No) Could not read craft file.");
                yield break;
            }

            selling = true;
            SetStatus("Rendering blueprint...");

            // Render the craft's blueprint so it can be shown publicly on the listing.
            byte[] blueprintBytes = null;
            try
            {
                string bpPath = VesselRenderer.CaptureVessel();
                if (!string.IsNullOrEmpty(bpPath) && System.IO.File.Exists(bpPath))
                    blueprintBytes = System.IO.File.ReadAllBytes(bpPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Blueprint render failed: {ex.Message}");
            }

            SetStatus("Listing craft...");
            string fileName = editorCraftName + ".craft";
            yield return GeneKermanMod.Instance.Api.ListCraftForSale(
                craftBytes, fileName, editorCraftName, editorCraftType,
                editorPartCount, editorCraftMass, editorCraftCost, price,
                blueprintBytes,
                (ok, resp, status) =>
                {
                    selling = false;
                    if (ok && !string.IsNullOrEmpty(resp))
                    {
                        var data = MiniJSON.DeserializeDict(resp);
                        if (MiniJSON.GetBool(data, "success", false))
                            SetStatus("(Ok) Craft listed for sale!");
                        else
                            SetStatus("(No) " + MiniJSON.GetString(data, "message", "Failed to list."));
                    }
                    else
                    {
                        SetStatus("(No) Failed to list craft.");
                    }
                });
            LoadMarketListings();
        }

        private void LoadMarketListings()
        {
            loadingMarket = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetMarketplaceListings((ok, data, err) =>
            {
                loadingMarket = false;
                if (ok && data != null)
                    marketListings = MiniJSON.GetList(data, "listings");
                else
                    SetStatus("(No) " + (err ?? "Failed to load listings."));
            }));
        }

        private System.Collections.IEnumerator DoDelistListing(string listingId)
        {
            if (string.IsNullOrEmpty(listingId)) yield break;
            SetStatus("Delisting...");
            yield return GeneKermanMod.Instance.Api.DelistCraft(listingId, (ok, resp, status) =>
            {
                SetStatus(ok ? "(Ok) Craft delisted." : "(No) Failed to delist.");
            });
            LoadMarketListings();
        }

        // ── Craft Import Queue (auto-import) ─────────────────────────────────
        //
        // Crafts are selected for import in Discord (the /library command for
        // bot-contract crafts, or the "Load to KSP" button on a marketplace
        // purchase DM). That queues an entry on the player's account; here we poll
        // the queue and import each craft into the active save automatically.
        //
        // Called from GeneKermanMod.Update on a timer while at the Space Center —
        // a safe scene for both live-vessel imports and blueprint installs.

        public void PollCraftImports()
        {
            if (importPollInFlight) return;
            importPollInFlight = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.Get(
                "/api/v1/craft/imports/pending", (ok, resp, status) =>
            {
                importPollInFlight = false;
                if (!ok || string.IsNullOrEmpty(resp)) return;

                var data = MiniJSON.DeserializeDict(resp);
                foreach (var obj in MiniJSON.GetList(data, "imports"))
                {
                    var entry = obj as Dictionary<string, object>;
                    if (entry == null) continue;
                    string importId = MiniJSON.GetString(entry, "import_id", "");
                    if (string.IsNullOrEmpty(importId) || processingImports.Contains(importId))
                        continue;
                    processingImports.Add(importId);
                    GeneKermanMod.Instance.RunCoroutine(DoProcessImport(entry));
                }
            }));
        }

        private System.Collections.IEnumerator DoProcessImport(Dictionary<string, object> entry)
        {
            string importId = MiniJSON.GetString(entry, "import_id", "");
            string craftName = MiniJSON.GetString(entry, "craft_name", "Craft");
            string craftUrl = MiniJSON.GetString(entry, "craft_url", null);
            string craftFilename = MiniJSON.GetString(entry, "craft_filename", "craft.craft");
            string loadmeta = MiniJSON.GetString(entry, "loadmeta", null);
            string source = MiniJSON.GetString(entry, "source", "");
            string vesselNodeUrl = MiniJSON.GetString(entry, "vessel_node_url", null);
            string ownerName = MiniJSON.GetString(entry, "owner_name", "");
            string flagUrl = MiniJSON.GetString(entry, "flag_url", null);

            // Flag-design payout: a delivered flag PNG. Install it into the flag picker
            // (GameData/GeneKerman/Flags) — never a craft or live vessel.
            if (source == "flag" && !string.IsNullOrEmpty(flagUrl))
            {
                bool flagInstalled = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(flagUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    FlagTransfer.InstallStandaloneFlag(craftName, fileData);
                    flagInstalled = true;
                    GeneKermanMod.Instance.ShowNotification("🚩 Flag Installed",
                        $"{craftName} is now available in your flag picker.");
                });
                if (!flagInstalled)
                {
                    // Leave it queued for the next poll (e.g. a download hiccup).
                    processingImports.Remove(importId);
                    yield break;
                }
                yield return GeneKermanMod.Instance.Api.Post(
                    $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
                processingImports.Remove(importId);
                yield break;
            }

            // Rescue deliveries are LIVE vessels (the rescued kerbals coming home, or a
            // cancelled rescue's vessel returning to its spot). Crew are tagged/stripped
            // by owner on import — the issuer's own kerbals come back to their original
            // names; anyone else's keep their owner tag.
            if (source == "rescue_delivery" && !string.IsNullOrEmpty(vesselNodeUrl))
            {
                string myName = GeneKermanMod.Instance.LinkedUsername;
                bool spawned = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(vesselNodeUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string node = DecompressToString(fileData);
                    string vesselName = VesselTransfer.ImportVesselAtTarget(node, null, ownerName, myName);
                    if (!string.IsNullOrEmpty(vesselName))
                    {
                        spawned = true;
                        GeneKermanMod.Instance.ShowNotification("🛟 Rescue Delivered",
                            $"{vesselName} has arrived in your save.");
                    }
                });
                if (!spawned)
                {
                    processingImports.Remove(importId);
                    yield break;
                }
                yield return GeneKermanMod.Instance.Api.Post(
                    $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
                processingImports.Remove(importId);
                yield break;
            }

            // Queue entries are blueprint installs (marketplace purchases). We drop
            // the craft into the save's Ships folder — never spawn a live vessel.
            if (!string.IsNullOrEmpty(craftUrl))
            {
                bool installed = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(craftUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string path = CraftInstaller.Install(fileData, craftFilename, loadmeta);
                    if (path != null)
                    {
                        installed = true;
                        GeneKermanMod.Instance.ShowNotification("🚀 Craft Imported",
                            $"{craftName} saved to your Ships folder.");
                    }
                });
                if (!installed)
                {
                    // Leave it queued for the next poll (e.g. a download hiccup).
                    processingImports.Remove(importId);
                    yield break;
                }
            }

            // Ack — remove from the player's queue so it isn't installed again.
            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
            processingImports.Remove(importId);
        }

        // ── Blueprint Preview Popup ──────────────────────────────────────────
        //
        // When reviewing a contractor's submission, the issuer can open this popup
        // to see the submitted vessel render(s)/blueprint image before approving or
        // refusing — no need to switch to Discord to look at the work.

        public void OpenBlueprintPreview(string contractId)
        {
            CloseBlueprintPreview();
            showBlueprintPopup = true;
            loadingBlueprints = true;
            blueprintStatus = "Loading submission...";
            blueprintVesselName = "";
            GeneKermanMod.Instance.RunCoroutine(FetchBlueprints(contractId));
        }

        private System.Collections.IEnumerator FetchBlueprints(string contractId)
        {
            string resp = null;
            bool ok = false;
            yield return GeneKermanMod.Instance.Api.Get(
                $"/api/v1/contracts/{contractId}/submission", (s, body, status) =>
                {
                    ok = s;
                    resp = body;
                });

            if (!ok || string.IsNullOrEmpty(resp))
            {
                loadingBlueprints = false;
                blueprintStatus = "❌ Could not load the submission.";
                yield break;
            }

            var data = MiniJSON.DeserializeDict(resp);
            blueprintVesselName = MiniJSON.GetString(data, "vessel_name", "");
            var images = MiniJSON.GetList(data, "images");

            // Orbital telemetry diagram (active-vessel / rescue submissions only).
            // Downloaded into its own texture and shown in a separate window — it is
            // not a blueprint, so it doesn't belong in the blueprint scroll list.
            string telemetryUrl = MiniJSON.GetString(data, "telemetry_url", "");
            if (!string.IsNullOrEmpty(telemetryUrl))
            {
                yield return GeneKermanMod.Instance.Api.DownloadFile(telemetryUrl, (dok, bytes) =>
                {
                    if (!dok || bytes == null) return;
                    var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (tex.LoadImage(bytes))
                        telemetryTexture = tex;
                });
            }

            if (images == null || images.Count == 0)
            {
                loadingBlueprints = false;
                blueprintStatus = "No blueprint images were submitted for this contract.";
                yield break;
            }

            foreach (var obj in images)
            {
                var entry = obj as Dictionary<string, object>;
                if (entry == null) continue;
                string url = MiniJSON.GetString(entry, "url", null);
                if (string.IsNullOrEmpty(url)) continue;

                yield return GeneKermanMod.Instance.Api.DownloadFile(url, (dok, bytes) =>
                {
                    if (!dok || bytes == null) return;
                    var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (tex.LoadImage(bytes))
                        blueprintTextures.Add(tex);
                });
            }

            loadingBlueprints = false;
            blueprintStatus = blueprintTextures.Count > 0
                ? ""
                : "❌ Failed to load the submitted images.";
        }

        private void CloseBlueprintPreview()
        {
            showBlueprintPopup = false;
            loadingBlueprints = false;
            blueprintStatus = "";
            blueprintVesselName = "";
            blueprintScroll = Vector2.zero;
            blueprintZoom = 1f;
            blueprintResizingBR = false;
            blueprintResizingTL = false;
            foreach (var tex in blueprintTextures)
                if (tex != null) Object.Destroy(tex);
            blueprintTextures.Clear();
            // Telemetry belongs to this same submission — tear it down too so a
            // stale diagram never lingers when a different contract is opened.
            CloseTelemetryWindow();
        }

        private void DrawBlueprintPopup(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(windowStyle);

            GUILayout.BeginHorizontal();
            string title = string.IsNullOrEmpty(blueprintVesselName)
                ? "🖼 Submitted Blueprint"
                : $"🖼 {blueprintVesselName}";
            GUILayout.Label(title, headerStyle);
            GUILayout.FlexibleSpace();

            // Zoom controls (only meaningful once images are loaded).
            bool hasImages = !loadingBlueprints && string.IsNullOrEmpty(blueprintStatus)
                             && blueprintTextures.Count > 0;
            if (hasImages)
            {
                if (GUILayout.Button("－", tabStyle, GUILayout.Width(30), GUILayout.Height(26)))
                    blueprintZoom = Mathf.Max(0.25f, blueprintZoom - 0.25f);
                if (GUILayout.Button("＋", tabStyle, GUILayout.Width(30), GUILayout.Height(26)))
                    blueprintZoom = Mathf.Min(4f, blueprintZoom + 0.25f);
                if (GUILayout.Button("Fit", tabStyle, GUILayout.Width(40), GUILayout.Height(26)))
                    blueprintZoom = 1f;
            }

            // Active vessel info — opens the orbital telemetry diagram in its own window.
            if (telemetryTexture != null &&
                GUILayout.Button(gcVesselInfo, tabStyle, GUILayout.Width(34), GUILayout.Height(26)))
            {
                OpenTelemetryWindow();
            }

            if (GUILayout.Button("✕", tabStyle, GUILayout.Width(30), GUILayout.Height(26)))
            {
                CloseBlueprintPreview();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            if (loadingBlueprints || !string.IsNullOrEmpty(blueprintStatus))
            {
                GUILayout.Label(blueprintStatus, labelStyle);
            }
            else
            {
                // Reserve everything between the header and footer for the scroll
                // view so the images grow/shrink with the window.
                float viewH = Mathf.Max(120f, blueprintRect.height - 110f);
                blueprintScroll = GUILayout.BeginScrollView(blueprintScroll, GUILayout.Height(viewH));
                // Fit each image to the current window width, then apply the zoom
                // factor; preserve aspect ratio throughout.
                float fitW = Mathf.Max(80f, blueprintRect.width - 50f);
                foreach (var tex in blueprintTextures)
                {
                    if (tex == null) continue;
                    float baseScale = tex.width > fitW ? fitW / tex.width : 1f;
                    float w = tex.width * baseScale * blueprintZoom;
                    float h = tex.height * baseScale * blueprintZoom;
                    Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
                    GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                    GUILayout.Space(8);
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Close", deleteBtnStyle, GUILayout.Height(28)))
            {
                CloseBlueprintPreview();
                GUILayout.EndVertical();
                return;
            }

            GUILayout.EndVertical();
            blueprintRect = ResizeGrip(blueprintRect, ref blueprintResizingBR, 360f, 320f, topLeft: false);
            blueprintRect = ResizeGrip(blueprintRect, ref blueprintResizingTL, 360f, 320f, topLeft: true);
            GUI.DragWindow();
        }

        // ── Orbital Telemetry Window ─────────────────────────────────────────
        //
        // A standalone, resizable/zoomable window for the server-rendered vessel
        // state diagram. Opened from the blueprint popup's 🛰 button whenever the
        // submission carried telemetry (active-vessel / rescue contracts).

        private void OpenTelemetryWindow()
        {
            if (telemetryTexture == null) return;
            showTelemetryWindow = true;
            telemetryScroll = Vector2.zero;
            telemetryZoom = 1f;
        }

        private void CloseTelemetryWindow()
        {
            showTelemetryWindow = false;
            telemetryScroll = Vector2.zero;
            telemetryZoom = 1f;
            telemetryResizingBR = false;
            telemetryResizingTL = false;
            if (telemetryTexture != null)
            {
                Object.Destroy(telemetryTexture);
                telemetryTexture = null;
            }
        }

        private void DrawTelemetryWindow(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(windowStyle);

            GUILayout.BeginHorizontal();
            string title = string.IsNullOrEmpty(blueprintVesselName)
                ? "🛰 Orbital Telemetry"
                : $"🛰 {blueprintVesselName} — Telemetry";
            GUILayout.Label(title, headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("－", tabStyle, GUILayout.Width(30), GUILayout.Height(26)))
                telemetryZoom = Mathf.Max(0.25f, telemetryZoom - 0.25f);
            if (GUILayout.Button("＋", tabStyle, GUILayout.Width(30), GUILayout.Height(26)))
                telemetryZoom = Mathf.Min(4f, telemetryZoom + 0.25f);
            if (GUILayout.Button("Fit", tabStyle, GUILayout.Width(40), GUILayout.Height(26)))
                telemetryZoom = 1f;
            if (GUILayout.Button("✕", tabStyle, GUILayout.Width(30), GUILayout.Height(26)))
            {
                showTelemetryWindow = false;  // keep the texture; reopenable from 🛰
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            if (telemetryTexture == null)
            {
                GUILayout.Label("No telemetry available for this submission.", labelStyle);
            }
            else
            {
                float viewH = Mathf.Max(120f, telemetryRect.height - 80f);
                telemetryScroll = GUILayout.BeginScrollView(telemetryScroll, GUILayout.Height(viewH));
                float fitW = Mathf.Max(80f, telemetryRect.width - 50f);
                float baseScale = telemetryTexture.width > fitW ? fitW / telemetryTexture.width : 1f;
                float w = telemetryTexture.width * baseScale * telemetryZoom;
                float h = telemetryTexture.height * baseScale * telemetryZoom;
                Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
                GUI.DrawTexture(r, telemetryTexture, ScaleMode.ScaleToFit);
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
            telemetryRect = ResizeGrip(telemetryRect, ref telemetryResizingBR, 360f, 280f, topLeft: false);
            telemetryRect = ResizeGrip(telemetryRect, ref telemetryResizingTL, 360f, 280f, topLeft: true);
            GUI.DragWindow();
        }

        // ── Resizable Window Grip ────────────────────────────────────────────
        //
        // Draws a draggable grip in one corner of the window and applies drag deltas
        // to the rect (clamped to a minimum). The bottom-right grip grows the size;
        // the top-left grip moves the origin while keeping the opposite corner fixed.
        // Must be called inside the window function and BEFORE GUI.DragWindow() —
        // consuming the mouse events here stops the drag from also moving the window.
        //
        // The grip claims GUIUtility.hotControl on mouse-down and holds it until
        // mouse-up, so IMGUI keeps delivering MouseDrag/MouseUp even when the cursor
        // races outside the tiny grip rect (or the window) — without this, a fast
        // drag drops out of the handle and scaling stops mid-motion.
        //
        // Each corner uses its own <paramref name="resizing"/> flag so the two grips
        // never apply the same drag twice; GetControlID is called every event pass in
        // a fixed order so the ids stay stable.
        private Rect ResizeGrip(Rect rect, ref bool resizing, float minW, float minH, bool topLeft)
        {
            const float grip = 20f;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Rect handle = topLeft
                ? new Rect(0f, 0f, grip, grip)
                : new Rect(rect.width - grip, rect.height - grip, grip, grip);
            GUI.Label(handle, topLeft ? "◤" : "◢", resizeGripStyle);

            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (handle.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;   // capture the drag globally
                        resizing = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        if (topLeft)
                        {
                            // Resize toward the top-left: shrink/grow from the origin
                            // and shift x/y so the bottom-right corner stays put.
                            float newW = Mathf.Max(minW, rect.width - e.delta.x);
                            float newH = Mathf.Max(minH, rect.height - e.delta.y);
                            rect.x += rect.width - newW;
                            rect.y += rect.height - newH;
                            rect.width = newW;
                            rect.height = newH;
                        }
                        else
                        {
                            rect.width = Mathf.Max(minW, rect.width + e.delta.x);
                            rect.height = Mathf.Max(minH, rect.height + e.delta.y);
                        }
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        resizing = false;
                        e.Use();
                    }
                    break;
            }
            return rect;
        }

    }
}
