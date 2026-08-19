/*
 * UI/MainWindow.cs – Primary mod window with tabbed interface.
 *
 * Tabs:
 *   1. Missions    — Weekly mission list with accept buttons
 *   2. Contracts   — Active contracts with submit buttons
 *   3. Profile     — Balance, XP, level display
 *   4. Notifications — Incoming requests and review results
 */

using System;
using System.Collections.Generic;
using System.Linq;
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
        // Client-raised notifications (photo shared, craft installed, device
        // approved, …) have no server record, so they're kept here and re-merged
        // into `notifications` on every refresh — a bare server fetch would wipe
        // them. Session-local: not persisted, gone on restart.
        private readonly List<object> localNotifications = new List<object>();

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
        private GUIStyle notifBadgeStyle;
        private GUIStyle resizeGripStyle;
        private bool stylesReady;

        // Icon textures loaded from Iconpack-1 (via GameData/BoundlessMissions/Textures/)
        private Texture2D iconRefresh, iconCompose, iconInbox, iconTrash, iconCross;
        private Texture2D iconUnlink, iconSubmit, iconVesselInfo;
        private Texture2D iconBell, iconCamera;

        // Pre-built GUIContent for icon+text buttons
        private GUIContent gcRefresh, gcRefreshMissions, gcRefreshOnly;
        private GUIContent gcInbox, gcTrash, gcCompose, gcUnlink;
        private GUIContent gcDel, gcRestore, gcSell, gcOpen, gcVesselInfo;

        // The Notifications view is no longer a tab — it's reached by clicking the
        // unread badge in the header. Its content renders under this pseudo-index
        // (one past the real tab buttons, so no tab highlights while it's open).
        private const int NotifTab = 6;
        private readonly string[] tabNames = { "Missions", "Contracts", "Profile", "Market", "Tools", "Settings" };
        private string serverUrlInput = "";
        private string disputeDateInput = ""; // proposed new deadline for a "More Time" dispute

        // Marketplace state
        private string sellPriceInput = "100";
        private string editorCraftName = "";
        private string editorCraftType = "VAB";
        private int editorPartCount;
        private float editorCraftMass, editorCraftCost;
        private string editorCraftPath = "";
        private bool selling;

        // Tools tab state
        private string flagUrlInput = "";   // URL of an image to fast-import as a flag
        private string flagNameInput = "";  // optional display name for the imported flag
        private bool importingFlag;
        private string lastExportPath = ""; // where the last flag-encoded craft was written
        private List<object> sendRecipients; // corp owners = the roster of players to send to
        private bool loadingRecipients;
        private bool sending;

        // Craft import queue — crafts the player selected in Discord. The mod polls
        // /api/v1/craft/imports/pending and auto-imports each into the active save.
        private bool importPollInFlight;
        private readonly HashSet<string> processingImports = new HashSet<string>();

        // Rescue wrecks whose download is currently in flight, keyed by contract id, so
        // a rapid double-click of the Spawn button can't kick off two spawns at once.
        // Permanent dedup lives in GKContractScenario.HasImportedVessel (per-save).
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
        // the blueprints. Only populated when the submission includes telemetry. A
        // multi-craft submission carries one diagram per craft, shown stacked.
        private readonly List<Texture2D> telemetryTextures = new List<Texture2D>();
        private bool showTelemetryWindow;
        private Vector2 telemetryScroll;
        private float telemetryZoom = 1f;
        private bool telemetryResizingBR;
        private bool telemetryResizingTL;
        private Rect telemetryRect = new Rect(210, 110, 720, 480);
        private readonly int telemetryWindowId = "GKTelemetry".GetHashCode();

        // Trash state. The bin itself lives in ContractInbox, which the sidebar's
        // inbox reads too — trashing something here has to hide it there as well.
        private bool showTrash = false;
        private HashSet<string> collapsedWeeks = new HashSet<string>();

        /// <summary>
        /// The client is behind the server's required version and the player chose
        /// "Continue anyway". Only the tabs that work with no server are shown; every
        /// fetch is skipped, since each one would just come back 426.
        /// </summary>
        private static bool LimitedMode =>
            GeneKermanMod.Instance != null && GeneKermanMod.Instance.UpdateRequired;

        /// Tabs reachable in limited mode, as indices into tabNames: Tools, Settings.
        private static readonly int[] LimitedTabs = { 4, 5 };

        public void OnOpen()
        {
            if (LimitedMode)
            {
                // Open on Settings — the reason to be here at all is usually to point
                // the mod at a different server.
                selectedTab = 5;
                scrollPos = Vector2.zero;
                return;
            }
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
        // ── Read-only views for the uGUI sidebar ────────────────────────────
        //
        // The sidebar's notification panel *displays* this feed; MainWindow keeps
        // owning the fetch, the local-notification merge, the de-dup and the unread
        // count. Exposing the list rather than copying it is the point — a second
        // copy would drift the moment either side gained a mutation.

        /// <summary>The loaded feed, newest first. Null before the first fetch.</summary>
        internal IList<object> NotificationFeed => notifications;

        /// <summary>True while a notification fetch is in flight.</summary>
        internal bool NotificationsLoading => loadingNotifs;

        /// <summary>Kick a refresh from another front end (same call the tab's button makes).</summary>
        internal void RequestNotificationRefresh() => RefreshNotifications();

        /// <summary>The account profile blob. Null before the first fetch.</summary>
        internal Dictionary<string, object> ProfileData => profile;
        internal bool ProfileLoading => loadingProfile;
        internal void RequestProfileRefresh() => RefreshProfile();

        /// <summary>This week's missions, plus the two facts that qualify them.</summary>
        internal IList<object> MissionList => missions;
        internal string MissionWeekKey => weekKey;
        internal bool MissionsLocked => missionsLocked;
        internal bool MissionsLoading => loadingMissions;
        internal void RequestMissionsRefresh() => RefreshMissions();

        /// <summary>Active + incoming contracts, as the API returned them.</summary>
        internal IList<object> ContractList => contracts;
        internal bool ContractsLoading => loadingContracts;
        internal void RequestContractsRefresh() => RefreshContracts();

        /// <summary>One contract by id, from the same cache the list renders.</summary>
        internal Dictionary<string, object> FindContract(string contractId) => FindContractById(contractId);

        // ── Actions, for the uGUI sidebar ───────────────────────────────────
        //
        // These run the *same* coroutines the classic window runs, rather than
        // reissuing the API calls from the sidebar. That matters more than it
        // looks: the bodies carry side effects a second copy would silently drop —
        // DoSelectMission injects the contract into KSP's stock contract system,
        // DoCancelContract and DoGiveUpContract stop EditorPartEnforcer if it is
        // gating parts for that contract, and every one of them re-reads the list
        // afterwards. The Python side learned this the hard way in 6a-i, where two
        // copies of "give up" disagreed about whether the fine was charged.
        //
        // Each takes an onDone the coroutine invokes exactly once, so a caller that
        // is not MainWindow can report the outcome; MainWindow's own status line
        // still updates either way.

        internal void RequestSelectMission(int missionId, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoSelectMission(missionId, onDone));

        internal void RequestAcceptContract(string contractId, string issuerName, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoAcceptContract(contractId, issuerName, onDone));

        internal void RequestCancelContract(string contractId, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoCancelContract(contractId, onDone));

        internal void RequestGiveUpContract(string contractId, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoGiveUpContract(contractId, onDone));

        internal void RequestReviewContract(string contractId, bool approve, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoReviewContract(contractId, approve, onDone));

        internal void RequestDispute(string contractId, string action, string newDate, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoDispute(contractId, action, newDate, onDone));

        internal void RequestDownloadCraft(string contractId, string ownerName, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoDownloadCraft(contractId, ownerName, onDone));

        /// <summary>Spawn an accepted rescue's stranded vessel into this save. Takes the
        /// contract dict rather than the pieces because everything the spawn needs — the
        /// wreck URL, the target, the tagged crew names, the LS flag — is read off it,
        /// and a caller assembling those itself would be a second place to get them
        /// wrong.</summary>
        internal void RequestSpawnRescueWreck(Dictionary<string, object> contract, Action<bool, string> onDone)
        {
            if (contract == null) { onDone?.Invoke(false, "No contract."); return; }

            string cid = MiniJSON.GetString(contract, "contract_id");
            string wreckUrl = MiniJSON.GetString(contract, "rescue_vessel_node_url", null);
            if (string.IsNullOrEmpty(wreckUrl))
            {
                onDone?.Invoke(false, "Vessel data unavailable. Refresh contracts and try again.");
                return;
            }

            var kerbals = MiniJSON.GetList(contract, "rescue_kerbals")
                .Select(o => o?.ToString())
                .Where(s => !string.IsNullOrEmpty(s)).ToList();

            GeneKermanMod.Instance.RunCoroutine(DoSpawnRescueWreck(
                cid, wreckUrl,
                RescueTargetSpec.FromDict(MiniJSON.GetDict(contract, "rescue_target")),
                MiniJSON.GetString(contract, "issuer_name", ""),
                kerbals,
                MiniJSON.GetString(contract, "life_support", "none"),
                onDone));
        }

        internal void RequestLogoutAllDevices()
            => GeneKermanMod.Instance.RunCoroutine(DoLogoutAllDevices());

        /// <summary>Mark one notification read. The feed object is found here rather
        /// than passed in, so a caller cannot hand us a dict that is not in the list
        /// and leave the badge counting something that is no longer on screen.</summary>
        internal void RequestMarkNotificationRead(string id, Action<bool, string> onDone)
        {
            var n = FindNotification(id);
            if (n == null) { onDone?.Invoke(false, "That notification is gone."); return; }
            DoMarkNotificationRead(n, id, onDone);
        }

        internal void RequestDismissNotification(string id, Action<bool, string> onDone)
            => DoDismissNotification(id, onDone);

        internal void RequestMarkAllNotificationsRead(Action<bool, string> onDone)
            => DoMarkAllNotificationsRead(onDone);

        private Dictionary<string, object> FindNotification(string id)
        {
            if (notifications == null || string.IsNullOrEmpty(id)) return null;
            foreach (var o in notifications)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && MiniJSON.GetString(d, "id") == id) return d;
            }
            return null;
        }

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

        /// <summary>
        /// Add a client-originated notification (no server record) to the panel.
        /// Kept in a separate backing list so RefreshNotifications can merge it back
        /// after replacing `notifications` with the server's. The unread badge is
        /// managed by the caller (GeneKermanMod.RaiseLocalNotification).
        /// </summary>
        public void AddLocalNotification(Dictionary<string, object> n)
        {
            if (n == null) return;
            localNotifications.Insert(0, n);          // newest first
            if (notifications == null) notifications = new List<object>();
            notifications.Insert(0, n);               // show now, without a refresh
        }

        /// <summary>True for ids minted by RaiseLocalNotification (no server record).</summary>
        private static bool IsLocalNotif(string id)
        {
            return id != null && id.StartsWith("local-");
        }

        /// <summary>Switch to the feed. Used when a toast has something to press rather
        /// than a contract to open (see LocalNotifActions).</summary>
        public void OpenNotifications()
        {
            selectedTab = NotifTab;
            scrollPos = Vector2.zero;
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
                // KSP's default label skin word-wraps; that pushes long sender
                // names ("← Boundless Missions") onto a second line and overflows
                // the fixed-height row. Keep it to a single clipped line instead.
                wordWrap = false,
                clipping = TextClipping.Clip,
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
            iconBell    = GKSkin.LoadIcon("notificationbell", 18);
            iconCamera  = GKSkin.LoadIcon("cam", 18);

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

            // Header notification badge — the clickable unread counter. Amber text on
            // the tab background so it reads as "you have unread mail".
            notifBadgeStyle = new GUIStyle(tabStyle)
            {
                fontSize = 13, fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 0, 0),
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.35f, 0.28f, 0.1f, 0.95f)), textColor = new Color(1f, 0.82f, 0.25f) },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.45f, 0.36f, 0.14f, 0.95f)), textColor = new Color(1f, 0.9f, 0.4f) },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.3f, 0.24f, 0.08f, 0.95f)), textColor = new Color(1f, 0.82f, 0.25f) },
            };

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
            GUILayout.Label("Boundless Missions", headerStyle);
            GUILayout.FlexibleSpace();
            // Capture Achievement Shot — line up a good angle in flight, then
            // capture. The server verifies the shot and grants the matching KSP
            // title role. Disabled (greyed) until you're flying a vessel.
            bool limited = LimitedMode;
            // Achievement capture and the notification bell both need the server, so
            // in limited mode the header carries nothing but the close button.
            if (!limited)
            {
                bool canCapture = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null;
                string camTip = canCapture ? "Capture Achievement Shot" : "Enter flight to capture an achievement shot";
                GUI.enabled = canCapture;
                GUIContent camContent = iconCamera != null
                    ? new GUIContent(iconCamera, camTip)
                    : new GUIContent("📷", camTip);
                if (GUILayout.Button(camContent, tabStyle, GUILayout.Height(25), GUILayout.Width(34)))
                    GeneKermanMod.Instance.StartAchievementCapture();
                GUI.enabled = true;
                int unread = GeneKermanMod.Instance.UnreadNotifications;
                // The bell IS the button — clicking it opens the notifications view
                // (there is no Notifications tab anymore). The bell icon always shows,
                // even at zero unread, so the header never reads as blank; the unread
                // count is appended next to it only when there's something to read.
                GUIContent bellContent;
                if (iconBell != null)
                    bellContent = unread > 0 ? new GUIContent(" " + unread, iconBell) : new GUIContent(iconBell);
                else
                    bellContent = new GUIContent(unread > 0 ? $"🔔 {unread}" : "🔔");
                if (GUILayout.Button(bellContent, unread > 0 ? notifBadgeStyle : tabStyle,
                        GUILayout.Height(25), GUILayout.MinWidth(34)))
                {
                    selectedTab = NotifTab;
                    scrollPos = Vector2.zero;
                }
            }
            if (GUILayout.Button("✕", tabStyle, GUILayout.Width(25), GUILayout.Height(25)))
                GeneKermanMod.Instance.ShowMainWindow = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            if (limited) DrawLimitedBanner();

            // Tabs
            GUILayout.BeginHorizontal();
            if (limited)
            {
                // A tab left selected from before the gate (or the notifications
                // pseudo-tab) would fall through the switch below and render nothing.
                if (System.Array.IndexOf(LimitedTabs, selectedTab) < 0)
                    selectedTab = LimitedTabs[LimitedTabs.Length - 1];

                foreach (int i in LimitedTabs)
                {
                    if (GUILayout.Button(tabNames[i], i == selectedTab ? tabActiveStyle : tabStyle))
                    {
                        selectedTab = i;
                        scrollPos = Vector2.zero;
                    }
                }
            }
            else
            {
                for (int i = 0; i < tabNames.Length; i++)
                {
                    string label = tabNames[i];
                    if (GUILayout.Button(label, i == selectedTab ? tabActiveStyle : tabStyle))
                    {
                        selectedTab = i;
                        scrollPos = Vector2.zero;
                    }
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
                case 4: DrawToolsTab(); break;
                case 5: DrawSettingsTab(); break;
                case NotifTab: DrawNotificationsTab(); break;
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

        /// <summary>
        /// Persistent reminder shown above the tabs while the client is running behind
        /// an acknowledged version gate, so "why is half the mod missing?" is answered
        /// on screen rather than in the log.
        /// </summary>
        private void DrawLimitedBanner()
        {
            var mod = GeneKermanMod.Instance;

            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label("⚠ Limited mode: this version is out of date", new GUIStyle(valueStyle)
            {
                normal = { textColor = new Color(0.95f, 0.6f, 0.3f) }
            });
            string latest = string.IsNullOrEmpty(mod.LatestVersion) ? "a newer build" : mod.LatestVersion;
            GUILayout.Label(
                $"The server refused version {ModVersion.Current} and wants {latest}. Missions, " +
                "contracts, the marketplace and sending craft are unavailable. Flag import/export " +
                "still work, and you can point the mod at another server below.",
                new GUIStyle(labelStyle) { wordWrap = true });
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Re-check", tabStyle, GUILayout.Height(22)))
            {
                mod.RecheckVersion();
                SetStatus("Re-checking version...");
            }
            if (!string.IsNullOrEmpty(mod.UpdateDownloadUrl)
                && GUILayout.Button("Download latest ↗", tabStyle, GUILayout.Height(22)))
            {
                Application.OpenURL(mod.UpdateDownloadUrl);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(5);
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
        private string giveUpConfirmId; // Active contract awaiting a give-up confirm click
        private bool logoutAllConfirm;  // "Log out all devices" awaiting a confirm click

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
                            if (ContractInbox.IsTrashed(cid) == showTrash)
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
                bool isTrashed = ContractInbox.IsTrashed(cid);
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

                    string sender = isIncoming ? issuerName : contractorName;
                    if (!string.IsNullOrEmpty(sender) && sender.Length > 15)
                        sender = sender.Substring(0, 14) + "…";
                    string dirLabel = (isIncoming ? "← " : "→ ") + sender;
                    GUILayout.Label(dirLabel, mailSenderStyle, GUILayout.Width(110));

                    // [R] badge marks contracts that have a part restriction. The badge
                    // and title turn red only while that contract's part filter is
                    // actively being enforced in the editor — not merely present.
                    string modlist = MiniJSON.GetString(c, "modlist", "");
                    bool hasPartLimits = !ContractConstraints.Parse(MiniJSON.GetDict(c, "constraints")).IsEmpty;
                    bool restricted = !string.IsNullOrEmpty(modlist) || hasPartLimits;
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

                    if (ContractInbox.IsFinished(status))
                    {
                        if (!showTrash)
                        {
                            if (GUILayout.Button(gcDel, iconBtnStyle, GUILayout.Width(22), GUILayout.Height(20)))
                            {
                                ContractInbox.SetTrashed(cid, true);
                            }
                        }
                        else
                        {
                            if (GUILayout.Button(gcRestore, iconBtnStyle, GUILayout.Width(22), GUILayout.Height(20)))
                            {
                                ContractInbox.SetTrashed(cid, false);
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

        /// <summary>The week heading a contract is filed under. Shared with the
        /// sidebar's inbox — see ContractInbox.WeekKey.</summary>
        private string GetWeekKey(string isoDate) => ContractInbox.WeekKey(isoDate);

        private void AutoCollapseWeeks()
        {
            if (contracts == null) return;
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach(var cObj in contracts) {
                var c = cObj as Dictionary<string, object>;
                if (c == null) continue;
                if (ContractInbox.IsTrashed(MiniJSON.GetString(c, "contract_id"))) continue;
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
                    {
                        DrawDetailRow("Target", $"{rbody} orbit Ap {MiniJSON.GetDouble(rt, "ap") / 1000:F0}km / Pe {MiniJSON.GetDouble(rt, "pe") / 1000:F0}km");
                        // Which orbit, when the issuer named one. Ap/Pe alone don't say.
                        string orbitReq = RescueTargetSpec.FromDict(rt).DescribeOrbitRequirement();
                        if (!string.IsNullOrEmpty(orbitReq))
                            DrawDetailRow("Orbit", orbitReq);
                    }
                    if (MiniJSON.GetBool(rt, "is_modded"))
                        DrawDetailRow("⚠ Mod", $"Needs the {rbody} planet pack");

                    // What counts as done: the crew alone, or the wreck with them.
                    bool wreckToo = MiniJSON.GetString(rt, "recovery", "crew") == "vessel";
                    DrawDetailRow("Recover", wreckToo ? "Crew + the stranded vessel" : "Crew only");
                    double minDv = MiniJSON.GetDouble(rt, "min_dv", 0);
                    if (minDv > 0)
                        DrawDetailRow("Δv left", $"≥{minDv:F0} m/s on arrival");
                }
                var krew = MiniJSON.GetList(c, "rescue_kerbals");
                if (krew != null && krew.Count > 0)
                    DrawDetailRow("Kerbals", $"{krew.Count} to recover");
            }

            GUILayout.EndVertical();

            GUILayout.Space(8);

            // What this contract restricts, and — in the editor — the toggle that makes
            // the part picker obey it.
            //
            // The limits line is drawn wherever the contract is read, not only in the
            // VAB/SPH. It used to be editor-only, which hid the one kind of limit the
            // editor can never enforce: an orbit ("polar", "keostationary") is a flight
            // state, so the player was told about it in the one scene they cannot be in
            // when it applies. The enforcement toggle stays editor-only, because
            // EditorPartEnforcer is an [KSPAddon(EditorAny)] MonoBehaviour and there is
            // no instance to talk to anywhere else.
            {
                string reqModlist = MiniJSON.GetString(c, "modlist", "");
                ContractConstraints limits = ContractConstraints.Parse(MiniJSON.GetDict(c, "constraints"));
                bool inEditor = HighLogic.LoadedSceneIsEditor && status == "active";

                if (inEditor || !limits.IsEmpty)
                {
                    GUILayout.BeginVertical(boxDarkStyle);
                    GUILayout.Label("🛠 Required Mods", headerStyle);

                    if (string.IsNullOrEmpty(reqModlist) && limits.IsEmpty)
                    {
                        GUILayout.Label("No part restriction; all parts allowed.", labelStyle);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(reqModlist))
                            GUILayout.Label(reqModlist, labelStyle);
                        // Mission limits: part rules ("can't use the Thud", "nuclear only")
                        // and any orbit the mission text names.
                        if (!limits.IsEmpty)
                            GUILayout.Label("⚠ Mission limits: " + limits.Describe(), labelStyle);
                        GUILayout.Space(5);

                        if (inEditor)
                        {
                            bool currentlyEnforcing = EditorPartEnforcer.Instance != null &&
                                                      EditorPartEnforcer.Instance.IsEnforcing() &&
                                                      EditorPartEnforcer.Instance.ActiveContractId == openContractId;

                            bool enforce = GUILayout.Toggle(currentlyEnforcing, " Enforce VAB part limits for this contract", checkboxStyle);

                            if (enforce != currentlyEnforcing)
                            {
                                if (enforce)
                                    EditorPartEnforcer.Instance?.EnforceModlist(openContractId, reqModlist, limits);
                                else
                                    EditorPartEnforcer.Instance?.StopEnforcing();
                            }
                        }
                    }
                    GUILayout.EndVertical();
                    GUILayout.Space(8);
                }
            }

            // Rescue: spawn the stranded vessel on demand. Done here (a button) rather
            // than automatically on accept, so the player triggers it from a valid scene
            // and can retry if it fails. Spawn-state is persisted per-save in
            // GKContractScenario, so this survives restarts and never double-spawns —
            // which is what makes a failed/missed spawn recoverable.
            if (status == "active" && mType == "rescue")
            {
                string rcid = MiniJSON.GetString(c, "contract_id");
                GUILayout.BeginVertical(boxDarkStyle);
                GUILayout.Label("🛟 Stranded Vessel", headerStyle);

                bool alreadySpawned = GKContractScenario.Instance != null
                    && GKContractScenario.Instance.HasImportedVessel(rcid);
                bool sceneOk = HighLogic.LoadedSceneIsFlight
                    || HighLogic.LoadedScene == GameScenes.SPACECENTER
                    || HighLogic.LoadedScene == GameScenes.TRACKSTATION;

                if (alreadySpawned)
                {
                    GUILayout.Label("(Ok) Already spawned into this save.", labelStyle);
                    // Stranded crew ride out the wait in emergency freeze (removed from the
                    // sim and released by every LS mod, so nothing consumes them). They thaw
                    // automatically when you get within 10 km; this button forces it.
                    var freezeRec = RescueImmunityGuardian.GetRecord(rcid);
                    if (freezeRec != null)
                    {
                        GUILayout.Label($"{freezeRec.Crew.Count} kerbal(s) in emergency freeze. " +
                                        "They defreeze when you get close.", labelStyle);
                        // Only worth saying when the two installs actually differ: that is
                        // the case where the wreck's own supplies are useless here.
                        if (LsFreeze.IsCrossMod(freezeRec.BuiltWithLs))
                            GUILayout.Label(
                                $"⚠ Built for {LsFreeze.DisplayNameFor(freezeRec.BuiltWithLs)}; you run " +
                                $"{LsFreeze.DisplayNameFor(LsFreeze.LocalLsKey)}. Emergency rations were " +
                                "stowed aboard so the crew survive the defreeze.", labelStyle);
                        if (GUILayout.Button("Defreeze the crew now", acceptBtnStyle, GUILayout.Height(28)))
                            RescueImmunityGuardian.ReviveContract(rcid);
                    }
                }
                else if (!sceneOk)
                {
                    GUILayout.Label("Enter Flight, Space Center, or Tracking Station, then spawn the stranded vessel.", labelStyle);
                }
                else
                {
                    string wreckUrl = MiniJSON.GetString(c, "rescue_vessel_node_url", null);
                    if (string.IsNullOrEmpty(wreckUrl))
                    {
                        GUILayout.Label("Vessel data unavailable. Refresh contracts and retry.", labelStyle);
                    }
                    else if (GUILayout.Button("🛟 Spawn stranded vessel", acceptBtnStyle, GUILayout.Height(30)))
                    {
                        string issuerName = MiniJSON.GetString(c, "issuer_name", "");
                        RescueTargetSpec target = RescueTargetSpec.FromDict(MiniJSON.GetDict(c, "rescue_target"));
                        var kerbals = MiniJSON.GetList(c, "rescue_kerbals")
                            .Select(o => o?.ToString())
                            .Where(s => !string.IsNullOrEmpty(s)).ToList();
                        // What the issuer's install provisioned the wreck for — recorded so
                        // the rescuer can see a mismatch with their own life-support mod.
                        string builtWithLs = MiniJSON.GetString(c, "life_support", "none");
                        GeneKermanMod.Instance.RunCoroutine(
                            DoSpawnRescueWreck(rcid, wreckUrl, target, issuerName, kerbals, builtWithLs));
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
                    ContractConstraints limits = ContractConstraints.Parse(MiniJSON.GetDict(c, "constraints"));
                    RescueTargetSpec rescueSpec = null;
                    List<string> rescueKerbals = null;
                    if (mT == "rescue")
                    {
                        rescueSpec = RescueTargetSpec.FromDict(MiniJSON.GetDict(c, "rescue_target"));
                        rescueKerbals = ToStringList(MiniJSON.GetList(c, "rescue_kerbals"));
                    }
                    GeneKermanMod.Instance.OpenSubmitWindow(cid, mission, mT, reqSit, reqBody, reqModlist,
                        rescueSpec, rescueKerbals, limits);
                }

                // Give Up: the contractor can back out of work they accepted, paying the
                // agreed fine. Issuer side (outgoing) doesn't give up — they'd cancel.
                // A two-click confirm guards against fat-fingering away the fine.
                if (!MiniJSON.GetBool(c, "is_outgoing"))
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    int giveUpFine = (int)MiniJSON.GetDouble(c, "fine", 0);
                    GUILayout.Space(8);
                    if (giveUpConfirmId == cid)
                    {
                        if (GUILayout.Button("⚠ Confirm give up", deleteBtnStyle, GUILayout.Height(30)))
                        {
                            giveUpConfirmId = null;
                            GeneKermanMod.Instance.RunCoroutine(DoGiveUpContract(cid));
                            openContractId = null;
                        }
                    }
                    else
                    {
                        string label = giveUpFine > 0 ? $"🏳️ Give Up (fine {giveUpFine})" : "🏳️ Give Up";
                        if (GUILayout.Button(label, deleteBtnStyle, GUILayout.Height(30)))
                            giveUpConfirmId = cid;
                    }
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
                // An open settle / more-time request means the ball is in the issuer's
                // court, not the contractor's. Answering one is browser-UI and Discord
                // only, so this window says who is actually being waited on rather than
                // claiming the contractor is stalling.
                var pendingReq = MiniJSON.GetDict(c, "pending_request");
                string reqKind = pendingReq != null ? MiniJSON.GetString(pendingReq, "kind") : "";

                if (isOutgoing)
                {
                    string cid = MiniJSON.GetString(c, "contract_id");

                    GUI.enabled = false;
                    if (reqKind == "settle")
                        GUILayout.Button("They asked to settle. Answer in the browser UI or Discord.",
                                         submitBtnStyle, GUILayout.Height(30));
                    else if (reqKind == "more_time")
                        GUILayout.Button("They asked for more time (" +
                                         MiniJSON.GetString(pendingReq, "new_date") +
                                         "). Answer in the browser UI or Discord.",
                                         submitBtnStyle, GUILayout.Height(30));
                    else
                        GUILayout.Button("Waiting for the contractor to resolve...", submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;

                    // Changing your mind about a refusal is the one exit from a dispute
                    // that favours the contractor, so it lives here regardless of which
                    // request (if any) is outstanding. Same call as approving a fresh
                    // submission — the server allows it from `disputed` too.
                    GUILayout.Space(6);
                    if (GUILayout.Button("(Yes) Accept After All", acceptBtnStyle, GUILayout.Height(30)))
                        GeneKermanMod.Instance.RunCoroutine(DoReviewContract(cid, true));
                }
                else
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    bool botIssued = MiniJSON.GetBool(c, "is_bot_issued");
                    // One extension request per dispute — the server refuses a second,
                    // so offering the button would only produce an error. A granted
                    // extension reopens the contract, and being refused again starts a
                    // fresh dispute with the allowance restored.
                    bool moreTimeUsed = MiniJSON.GetBool(c, "more_time_used");

                    // "More Time" on a human contract needs a proposed new date.
                    if (!botIssued && !moreTimeUsed)
                    {
                        if (string.IsNullOrEmpty(disputeDateInput))
                            disputeDateInput = System.DateTime.Now.AddDays(7).ToString("yyyy-MM-dd");
                        GUILayout.Label("New date:", labelStyle, GUILayout.Width(62));
                        disputeDateInput = GUILayout.TextField(disputeDateInput, GUILayout.Width(86), GUILayout.Height(30));
                    }

                    if (!moreTimeUsed)
                    {
                        if (GUILayout.Button("More Time", submitBtnStyle, GUILayout.Height(30)))
                            GeneKermanMod.Instance.RunCoroutine(DoDispute(cid, "more_time", botIssued ? null : disputeDateInput));
                        GUILayout.Space(6);
                    }
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

            // A dispute is the one state nothing else ever ends, so the server closes it
            // on a timer and collects the fine. Both parties get told when, because the
            // contractor is the one it costs and the issuer is the one it pays.
            if (status == "disputed")
            {
                string autoFine = MiniJSON.GetString(c, "auto_fine_at");
                if (!string.IsNullOrEmpty(autoFine))
                {
                    GUILayout.Space(4);
                    GUILayout.Label("⏱ Unresolved: fine collected automatically on " +
                                    FormatLocal(autoFine), labelStyle);
                }
            }

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

        /// <summary>
        /// An API timestamp in the player's own timezone.
        ///
        /// The server sends naive UTC (Python's isoformat, no trailing Z), so it has to
        /// be told that — left to itself DateTime.Parse reads it as local, which on a
        /// UTC+3 machine shows a deadline three hours later than the one being enforced.
        /// Falls back to the raw string rather than throwing inside OnGUI.
        /// </summary>
        private static string FormatLocal(string iso)
        {
            System.DateTime dt;
            if (System.DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.AssumeUniversal |
                                         System.Globalization.DateTimeStyles.AdjustToUniversal,
                                         out dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso;
        }

        private void DrawDetailRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", labelStyle, GUILayout.Width(80));
            GUILayout.Label(value, valueStyle);
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Status → dot colour. Internal and static so the uGUI sidebar renders the
        /// same mapping rather than a second one that drifts; it holds no instance
        /// state, so there is nothing to share but the switch.
        /// </summary>
        internal static Color GetStatusColor(string status)
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

            // Log out of all devices — invalidates every session token linked to
            // this account (including this one). A two-click confirm guards against
            // an accidental tap, since it logs this device out too.
            GUILayout.Space(6);
            if (logoutAllConfirm)
            {
                GUILayout.Label("This logs out every device, including this one. Continue?", labelStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("⚠ Confirm log out all", deleteBtnStyle, GUILayout.Height(28)))
                {
                    logoutAllConfirm = false;
                    GeneKermanMod.Instance.RunCoroutine(DoLogoutAllDevices());
                }
                if (GUILayout.Button("Cancel", GUILayout.Height(28)))
                    logoutAllConfirm = false;
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("🔒 Log Out All Devices", GUILayout.Height(28)))
            {
                logoutAllConfirm = true;
            }
            GUILayout.EndVertical();
        }

        /// Calls the server to invalidate every token for this account. On success
        /// our token is already cleared (by ApiClient.LogoutAllDevices), so return
        /// to the link screen. On failure nothing was revoked — stay linked.
        private System.Collections.IEnumerator DoLogoutAllDevices()
        {
            yield return GeneKermanMod.Instance.Api.LogoutAllDevices((ok, resp, status) =>
            {
                if (ok)
                {
                    GeneKermanMod.Instance.ShowMainWindow = false;
                    GeneKermanMod.Instance.ShowLinkWindow = true;
                    SetStatus("Logged out of all devices.");
                }
                else
                {
                    SetStatus("(No) Could not log out all devices. Try again.");
                }
            });
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
            // mid-iteration would throw. A local action is deferred for the same reason
            // and it is not optional: running one raises a result notification, which
            // prepends to this very list.
            string dismissId = null;
            Dictionary<string, object> runAction = null;

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

                GUILayout.Label((read ? "   " : "● ") + Gui.Fmt.Plain(MiniJSON.GetString(n, "title")), valueStyle);
                GUILayout.Label(Gui.Fmt.Plain(MiniJSON.GetString(n, "message")), labelStyle);
                GUILayout.Label(MiniJSON.GetString(n, "timestamp"), new GUIStyle(labelStyle) { fontSize = 10 });

                GUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(contractId)
                    && GUILayout.Button(gcOpen, tabStyle, GUILayout.Height(22), GUILayout.Width(80)))
                {
                    OpenContractDetail(contractId);
                }
                string action = LocalNotifActions.Of(n);
                if (!string.IsNullOrEmpty(action)
                    && GUILayout.Button(LocalNotifActions.LabelFor(action), tabStyle,
                                        GUILayout.Height(22), GUILayout.Width(130)))
                {
                    runAction = n;
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

            if (runAction != null)
            {
                LocalNotifActions.Run(runAction);
                // The warning has been acted on, so it is no longer news. Marking it read
                // here rather than leaving it unread keeps the badge honest: the player
                // has seen it — they pressed its button.
                DoMarkNotificationRead(runAction, MiniJSON.GetString(runAction, "id"));
            }

            if (dismissId != null)
                DoDismissNotification(dismissId);

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("(Ok) Mark All Read", GUILayout.Height(28)))
                DoMarkAllNotificationsRead();
            if (GUILayout.Button(gcRefresh, GUILayout.Height(28)))
                RefreshNotifications();
            GUILayout.EndHorizontal();
        }

        private void DoMarkNotificationRead(Dictionary<string, object> n, string id,
                                            Action<bool, string> onDone = null)
        {
            if (string.IsNullOrEmpty(id)) { onDone?.Invoke(false, "No notification."); return; }
            // Local notifications have no server record — mark them read in place.
            if (IsLocalNotif(id))
            {
                n["read"] = true;
                RecountUnread();
                onDone?.Invoke(true, "Marked read.");
                return;
            }
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.MarkNotificationRead(id, (ok, resp, status) =>
            {
                if (ok)
                {
                    n["read"] = true;
                    RecountUnread();
                }
                onDone?.Invoke(ok, ok ? "Marked read." : "Could not mark it read.");
            }));
        }

        /// <summary>
        /// Mark the whole feed read. Extracted from the notifications tab when the
        /// sidebar grew the same button: the read flags, the unread badge and the
        /// server call have to move together, and two copies of that is how a badge
        /// ends up disagreeing with the list under it.
        /// </summary>
        private void DoMarkAllNotificationsRead(Action<bool, string> onDone = null)
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
                onDone?.Invoke(ok, ok ? "All notifications marked read." : "Could not mark them read.");
            }));
        }

        private void DoDismissNotification(string id, Action<bool, string> onDone = null)
        {
            if (string.IsNullOrEmpty(id)) { onDone?.Invoke(false, "No notification."); return; }
            // Local notifications have no server record — drop them from both lists.
            if (IsLocalNotif(id))
            {
                localNotifications.RemoveAll(o =>
                {
                    var d = o as Dictionary<string, object>;
                    return d != null && MiniJSON.GetString(d, "id") == id;
                });
                if (notifications != null)
                    notifications.RemoveAll(o =>
                    {
                        var d = o as Dictionary<string, object>;
                        return d != null && MiniJSON.GetString(d, "id") == id;
                    });
                RecountUnread();
                SetStatus("(Ok) Notification dismissed.");
                onDone?.Invoke(true, "Notification dismissed.");
                return;
            }
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
                onDone?.Invoke(ok, ok ? "Notification dismissed." : "Could not dismiss it.");
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

        // ── Tools Tab ───────────────────────────────────────────────────────
        //
        // Quick utilities that don't belong to the mission/contract flow:
        //   • Fast-import a flag from an image URL into the flag picker.
        //   • Export the loaded editor craft with its custom flags baked in.
        //   • Quicksend the active vessel (live) or loaded craft (blueprint) to a
        //     friend, delivered via their KSP import queue.

        private void DrawToolsTab()
        {
            GUILayout.Label("🛠 Tools", headerStyle);
            GUILayout.Space(4);

            // ── Tool 1: Import a flag from a URL ─────────────────────────────
            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label("🚩 Import Flag from URL", valueStyle);
            GUILayout.Label("Paste an image URL (PNG/JPG) to add it to your flag picker.", labelStyle);
            flagUrlInput = GUILayout.TextField(flagUrlInput, GUILayout.Height(24));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name (optional):", labelStyle, GUILayout.Width(110));
            flagNameInput = GUILayout.TextField(flagNameInput, GUILayout.Height(24));
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUI.enabled = !importingFlag && !string.IsNullOrEmpty(flagUrlInput.Trim());
            if (GUILayout.Button(importingFlag ? "Importing..." : "Import Flag", acceptBtnStyle, GUILayout.Height(28)))
                GeneKermanMod.Instance.RunCoroutine(DoImportFlagFromUrl());
            GUI.enabled = true;
            GUILayout.EndVertical();

            GUILayout.Space(6);

            // ── Tool 2: Export a flag-encoded craft file ─────────────────────
            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label("💾 Export Flag-Encoded Craft", valueStyle);
            GUILayout.Label("Writes the loaded craft with its custom flags baked in, so it carries over when you share the file.", labelStyle);
            if (HighLogic.LoadedSceneIsEditor)
            {
                CaptureEditorCraft();
                if (string.IsNullOrEmpty(editorCraftName))
                {
                    GUILayout.Label("No craft loaded in the VAB/SPH.", labelStyle);
                }
                else if (string.IsNullOrEmpty(editorCraftPath))
                {
                    GUILayout.Label($"Save '{editorCraftName}' first to export it.", labelStyle);
                }
                else
                {
                    GUILayout.Label($"[*] {editorCraftName} [{editorCraftType}]", labelStyle);
                    if (GUILayout.Button("Export Craft (+flags)", submitBtnStyle, GUILayout.Height(28)))
                        DoExportFlagCraft();
                    if (!string.IsNullOrEmpty(lastExportPath))
                        GUILayout.Label($"(Ok) Saved to: {lastExportPath}", new GUIStyle(labelStyle) { fontSize = 10, wordWrap = true });
                }
            }
            else
            {
                GUILayout.Label("Open a craft in the VAB or SPH to export it.", labelStyle);
            }
            GUILayout.EndVertical();

            GUILayout.Space(6);

            // ── Tool 3: Quicksend to a friend ────────────────────────────────
            // Server-backed (recipient list + upload), so it is the one tool that stays
            // off in limited mode.
            if (LimitedMode)
            {
                GUILayout.BeginVertical(boxDarkStyle);
                GUILayout.Label("📤 Quicksend to a Friend", valueStyle);
                GUILayout.Label("Unavailable until you update: sending needs the server.",
                    new GUIStyle(labelStyle) { wordWrap = true });
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label("📤 Quicksend to a Friend", valueStyle);

            bool inFlight = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null;
            bool inEditor = HighLogic.LoadedSceneIsEditor && !string.IsNullOrEmpty(editorCraftName)
                            && !string.IsNullOrEmpty(editorCraftPath);

            string sendKind = null;   // "vessel" (live) or "craft" (blueprint)
            string sendLabel = null;
            if (inFlight)
            {
                sendKind = "vessel";
                sendLabel = $"Active vessel '{FlightGlobals.ActiveVessel.vesselName}' → delivered as a live, flyable vessel.";
            }
            else if (inEditor)
            {
                sendKind = "craft";
                sendLabel = $"Loaded craft '{editorCraftName}' → delivered as a craft blueprint in their Ships folder.";
            }

            if (sendKind == null)
            {
                GUILayout.Label("Fly a vessel, or open a saved craft in the editor, to send it.", labelStyle);
            }
            else
            {
                GUILayout.Label(sendLabel, new GUIStyle(labelStyle) { wordWrap = true });
                GUILayout.Space(4);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Friends:", labelStyle, GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(gcRefreshOnly, tabStyle, GUILayout.Height(22), GUILayout.Width(30)))
                    LoadRecipients();
                GUILayout.EndHorizontal();

                if (loadingRecipients)
                {
                    GUILayout.Label("Loading players...", labelStyle);
                }
                else if (sendRecipients == null)
                {
                    if (GUILayout.Button("Load player list", tabStyle, GUILayout.Height(24)))
                        LoadRecipients();
                }
                else
                {
                    string myId = profile != null ? MiniJSON.GetString(profile, "user_id", "") : "";
                    int shown = 0;
                    foreach (var rObj in sendRecipients)
                    {
                        var r = rObj as Dictionary<string, object>;
                        if (r == null) continue;
                        string ownerId = MiniJSON.GetString(r, "owner_id", "");
                        string ownerName = MiniJSON.GetString(r, "owner_name", "Unknown");
                        string corpName = MiniJSON.GetString(r, "corp_name", "");
                        if (string.IsNullOrEmpty(ownerId) || ownerId == myId) continue; // no self-send
                        shown++;

                        GUILayout.BeginHorizontal();
                        string who = string.IsNullOrEmpty(corpName) ? ownerName : $"{ownerName} ({corpName})";
                        GUILayout.Label(who, labelStyle);
                        GUILayout.FlexibleSpace();
                        GUI.enabled = !sending;
                        if (GUILayout.Button("→ Send", acceptBtnStyle, GUILayout.Height(22), GUILayout.Width(80)))
                            GeneKermanMod.Instance.RunCoroutine(DoQuicksend(ownerId, ownerName, sendKind));
                        GUI.enabled = true;
                        GUILayout.EndHorizontal();
                    }
                    if (shown == 0)
                        GUILayout.Label("No other players found to send to.", labelStyle);
                }
            }
            GUILayout.EndVertical();
        }

        private System.Collections.IEnumerator DoImportFlagFromUrl()
        {
            string url = flagUrlInput.Trim();
            if (string.IsNullOrEmpty(url)) yield break;
            importingFlag = true;
            SetStatus("🚩 Downloading flag...");

            string name = string.IsNullOrEmpty(flagNameInput.Trim()) ? "Imported Flag" : flagNameInput.Trim();
            yield return GeneKermanMod.Instance.Api.DownloadFile(url, (ok, data) =>
            {
                importingFlag = false;
                if (!ok || data == null || data.Length == 0)
                {
                    SetStatus("(No) Could not download the image. Check the URL.");
                    return;
                }
                bool installed = FlagTransfer.InstallStandaloneFlag(name, data);
                SetStatus(installed
                    ? "(Ok) Flag added to your flag picker."
                    : "(Ok) Flag already present in your picker.");
                flagUrlInput = "";
                flagNameInput = "";
            });
        }

        private void DoExportFlagCraft()
        {
            try
            {
                if (string.IsNullOrEmpty(editorCraftPath) || !System.IO.File.Exists(editorCraftPath))
                {
                    SetStatus("(No) Save your craft first.");
                    return;
                }
                byte[] craftBytes = System.IO.File.ReadAllBytes(editorCraftPath);
                craftBytes = ScaleBridge.BakeEditorCraft(craftBytes);     // bake scale (first)
                craftBytes = FlagTransfer.EmbedFlagsInCraft(craftBytes);
                craftBytes = TweakScaleGuard.EmbedVersionInCraft(craftBytes); // backstop warning
                craftBytes = TextureTransfer.EmbedInCraft(craftBytes);   // carry paint job
                craftBytes = RealFuelsTransfer.EmbedInCraft(craftBytes); // carry fuel config
                craftBytes = CkanGenerator.EmbedModsInCraft(craftBytes); // carry mod list
                craftBytes = CraftThumb.EmbedThumbForCurrentCraft(craftBytes); // NW thumbnail (last)

                string dir = System.IO.Path.Combine(GeneKermanMod.PluginDataPath, "ExportedCrafts");
                System.IO.Directory.CreateDirectory(dir);
                string outPath = System.IO.Path.Combine(dir, editorCraftName + ".craft");
                System.IO.File.WriteAllBytes(outPath, craftBytes);

                lastExportPath = outPath;
                SetStatus("(Ok) Flag-encoded craft exported.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Export flag-encoded craft failed: {ex.Message}");
                SetStatus("(No) Failed to export craft.");
            }
        }

        private void LoadRecipients()
        {
            loadingRecipients = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetCorps((ok, data, err) =>
            {
                loadingRecipients = false;
                if (ok && data != null)
                    sendRecipients = MiniJSON.GetList(data, "corps");
                else
                    SetStatus("(No) " + (err ?? "Failed to load players."));
            }));
        }

        private System.Collections.IEnumerator DoQuicksend(string recipientId, string recipientName, string kind)
        {
            // The payload assembly (bake chain), blueprint render and upload all live
            // in ToolActions.Quicksend — the same implementation the sidebar and web
            // bridge run. This window only keeps its status line.
            sending = true;
            SetStatus($"📤 Sending to {recipientName}...");
            yield return ToolActions.Quicksend(recipientId, recipientName, kind,
                editorCraftPath, editorCraftName, (ok, message) =>
            {
                sending = false;
                SetStatus((ok ? "(Ok) " : "(No) ") + message);
            });
        }

        // ── Settings Tab ────────────────────────────────────────────────────────

        private void DrawSettingsTab()
        {
            var api = GeneKermanMod.Instance.Api;

            GUILayout.BeginVertical(windowStyle);
            GUILayout.Label("🔌 Connection Settings", new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(10);
            GUILayout.Label("Choose the official BM server, or connect to a custom IP if you are running your own.", labelStyle);
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

            GUILayout.Space(18);

            // The interface setting used to live here. It moved to the sidebar's
            // Settings panel (UI/Gui/Panels/SettingsPanel.cs) when the toolbar button
            // started opening the sidebar: the switch that decides what that button
            // does cannot sit behind the window it steers you away from.

            // Privacy & data sharing (KSP add-on rule 8.2): a master opt-out reachable
            // from the toolbar in every scene, incl. the Space Center. Turning it off
            // makes the mod inert (it collects and sends nothing) until re-enabled.
            GUILayout.Label("🔒 Privacy & Data", new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold });
            GUILayout.Space(4);
            bool dataOn = api.DataGatheringEnabled;
            bool newData = GUILayout.Toggle(dataOn, " Share data with the Boundless Missions servers");
            if (newData != dataOn)
            {
                // Disabling closes this window, so confirm via the toolbar's paused panel.
                GeneKermanMod.Instance.SetDataGatheringEnabled(newData);
                SetStatus(newData ? "(Ok) Data sharing enabled." : "(Ok) Data sharing disabled. The mod is now inactive.");
            }
            GUILayout.Label(
                "When off, no information is collected or sent and the mod stops working " +
                "until you turn it back on.",
                labelStyle);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Privacy Policy ↗", tabStyle, GUILayout.Height(22)))
                Application.OpenURL(ApiClient.PrivacyPolicyUrl);
            if (GUILayout.Button("Terms of Service ↗", tabStyle, GUILayout.Height(22)))
                Application.OpenURL(ApiClient.TermsOfServiceUrl);
            GUILayout.EndHorizontal();

            GUILayout.Space(18);

            // Version / build info
            GUILayout.Label("ℹ️ Version", new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold });
            GUILayout.Space(4);
            GUILayout.Label($"Mod version: {GeneKerman.ModVersion.Current}", labelStyle);
            string buildHash = GeneKerman.ModVersion.Sha256;
            if (!string.IsNullOrEmpty(buildHash))
                GUILayout.Label($"Build: {buildHash.Substring(0, System.Math.Min(12, buildHash.Length))}", labelStyle);

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

            // Socket teardown, the re-check against the server we just switched to, and
            // the link prompt if this server has never seen us. Shared with the browser
            // UI's settings screen so the two front ends behave identically.
            GeneKermanMod.Instance.OnServerChanged();

            // Under a version gate, that re-check is the escape hatch: if the new server
            // accepts this build, CheckVersionRoutine clears UpdateRequired and the full
            // UI returns without a restart. If it also rejects us, we stay in limited
            // mode. Skip RefreshAll either way — those calls would 426 against a gating
            // server and are re-run by OnOpen once the gate clears.
            if (LimitedMode)
            {
                SetStatus("Checking the new server...");
                return;
            }

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
            // Make sure the bot has this install's part list so it can resolve the
            // exact parts named in any mission limits (hash-gated, ~once per session).
            PartCatalogUploader.EnsureUploaded(GeneKermanMod.Instance.Api);
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
                    notifications = MiniJSON.GetList(data, "notifications") ?? new List<object>();
                    int unread = MiniJSON.GetInt(data, "unread_count");
                    // Re-attach session-local notifications the server doesn't know
                    // about (newest first), and fold their unread count into the badge.
                    for (int i = localNotifications.Count - 1; i >= 0; i--)
                    {
                        notifications.Insert(0, localNotifications[i]);
                        var d = localNotifications[i] as Dictionary<string, object>;
                        if (d != null && !MiniJSON.GetBool(d, "read")) unread++;
                    }
                    GeneKermanMod.Instance.UnreadNotifications = unread;
                }
            }));
        }

        // ── Actions ─────────────────────────────────────────────────────────

        private System.Collections.IEnumerator DoSelectMission(int missionId, Action<bool, string> onDone = null)
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
                    onDone?.Invoke(true, MiniJSON.GetString(data, "message", "Mission accepted."));

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
                    onDone?.Invoke(false, err ?? "Failed to select mission.");
                }
            });
        }

        private System.Collections.IEnumerator DoAcceptContract(string contractId, string issuerName = "", Action<bool, string> onDone = null)
        {
            // Accept only flips the contract to active. The rescue wreck is NOT spawned
            // here anymore — it spawns on demand via the "Spawn stranded vessel" button on
            // the active contract, so the player triggers it from a valid scene (Flight /
            // Space Center / Tracking Station) and can retry if a spawn fails. Auto-spawning
            // on accept silently lost the wreck whenever accept happened from the editor.
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/accept", "{}", (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus("(Ok) Contract accepted!");
                    RefreshContracts();
                    onDone?.Invoke(true, "Contract accepted.");
                }
                else
                {
                    SetStatus("(No) Failed to accept contract.");
                    onDone?.Invoke(false, "Failed to accept contract.");
                }
            });
        }

        private System.Collections.IEnumerator DoSpawnRescueWreck(string contractId, string wreckUrl, RescueTargetSpec target, string issuerName, List<string> rescueKerbals, string builtWithLs = "none", Action<bool, string> onDone = null)
        {
            // Permanent, per-save dedup: if the wreck is already in this save, never
            // spawn a second one. This is persisted in GKContractScenario, so it holds
            // across restarts (the in-memory set below only guards a double-click while
            // a download is mid-flight).
            if (!string.IsNullOrEmpty(contractId) && GKContractScenario.Instance != null
                && GKContractScenario.Instance.HasImportedVessel(contractId))
            {
                SetStatus("🛟 Stranded vessel already spawned for this contract.");
                onDone?.Invoke(false, "Already spawned into this save.");
                yield break;
            }
            // Transient guard against a double-click while the download is in flight.
            if (!string.IsNullOrEmpty(contractId) && !spawnedRescueWrecks.Add(contractId))
            {
                onDone?.Invoke(false, "Already spawning, give it a moment.");
                yield break;
            }
            string myName = GeneKermanMod.Instance.LinkedUsername;
            yield return GeneKermanMod.Instance.Api.DownloadFile(wreckUrl, (ok, fileData) =>
            {
                if (!ok || fileData == null)
                {
                    SetStatus("⚠ Could not download the stranded vessel. Try again.");
                    onDone?.Invoke(false, "Could not download the stranded vessel. Try again.");
                    return;
                }
                string node = CraftDelivery.DecompressToString(fileData);
                // Spawn the stranded vessel where it actually is (its real orbit from the
                // snapshot) — NOT at the delivery target. The target is where the rescuer
                // must DELIVER the crew, so the wreck has to be elsewhere or there's no
                // mission. Import freezes the snapshot's orbit epoch to "now" so the wreck
                // appears exactly where the issuer left it, instead of KSP propagating the
                // stale epoch forward and placing it wherever the source vessel would be at
                // the current universe time. Crew are tagged with the issuer's name; the
                // rescuer collects them and brings them to the target to complete.
                string name = VesselTransfer.ImportVesselAtTarget(node, null, issuerName, myName);
                if (!string.IsNullOrEmpty(name))
                {
                    // Mark imported only on a real spawn, so a scene-guard / parse
                    // failure leaves the contract retryable instead of locked out.
                    GKContractScenario.Instance?.MarkVesselImported(contractId);

                    // Emergency freeze: the stranded crew are lifted out of the simulation
                    // (and released by every installed LS mod) until the rescuer reaches the
                    // wreck, which also gets a ration kit of THIS install's life support in
                    // case it was built for another mod.
                    RescueImmunityGuardian.Register(contractId, VesselTransfer.LastSpawnedPid,
                                                    rescueKerbals, builtWithLs);
                    string dest = target != null ? target.body : "the target";
                    SetStatus($"🛟 Stranded vessel '{name}' is adrift. Find it and bring the crew to {dest}.");
                    onDone?.Invoke(true, $"'{name}' is adrift. Find it and bring the crew to {dest}.");
                }
                else
                {
                    SetStatus("⚠ Could not spawn. Enter Flight, Space Center, or Tracking Station and try again.");
                    onDone?.Invoke(false, "Could not spawn. Enter Flight, Space Center, or Tracking Station and try again.");
                }
            });
            // Always clear the transient guard so a failed attempt can be retried.
            if (!string.IsNullOrEmpty(contractId)) spawnedRescueWrecks.Remove(contractId);
        }

        private System.Collections.IEnumerator DoReviewContract(string contractId, bool approve, Action<bool, string> onDone = null)
        {
            string body = approve ? "{\"approve\":true}" : "{\"approve\":false}";
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/review", body, (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus(approve ? "(Ok) Submission approved!" : "🗑 Submission refused (dispute opened).");
                    openContractId = null;
                    RefreshContracts();
                    onDone?.Invoke(true, approve ? "Submission approved." : "Submission refused; a dispute is open.");
                }
                else
                {
                    SetStatus("(No) Failed to review submission.");
                    onDone?.Invoke(false, "Failed to review submission.");
                }
            });
        }

        private System.Collections.IEnumerator DoDispute(string contractId, string action, string newDate, Action<bool, string> onDone = null)
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
                    onDone?.Invoke(true, string.IsNullOrEmpty(msg) ? "Done." : msg);
                }
                else
                {
                    SetStatus("(No) " + (string.IsNullOrEmpty(msg) ? "Action failed." : msg));
                    onDone?.Invoke(false, string.IsNullOrEmpty(msg) ? "Action failed." : msg);
                }
            });
        }

        private System.Collections.IEnumerator DoCancelContract(string contractId, Action<bool, string> onDone = null)
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
                    onDone?.Invoke(true, "Contract cancelled.");
                }
                else
                {
                    SetStatus("(No) Failed to cancel contract.");
                    onDone?.Invoke(false, "Failed to cancel contract.");
                }
            });
        }

        private System.Collections.IEnumerator DoGiveUpContract(string contractId, Action<bool, string> onDone = null)
        {
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/give_up", "{}", (ok, resp, status) =>
            {
                // The endpoint returns 200 + success:false for soft failures (e.g. the
                // contractor can't cover the fine), so read the body rather than trust
                // the HTTP status alone — and show the server's own message.
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && d != null && MiniJSON.GetBool(d, "success", false);
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";

                if (success)
                {
                    SetStatus("🏳️ " + (string.IsNullOrEmpty(msg) ? "Contract given up." : msg));
                    // Clear the editor enforcer if it was gating parts for this contract.
                    if (EditorPartEnforcer.Instance != null &&
                        EditorPartEnforcer.Instance.ActiveContractId == contractId)
                        EditorPartEnforcer.Instance.StopEnforcing();
                    RefreshContracts();
                    onDone?.Invoke(true, string.IsNullOrEmpty(msg) ? "Contract given up." : msg);
                }
                else
                {
                    SetStatus("(No) " + (string.IsNullOrEmpty(msg) ? "Failed to give up contract." : msg));
                    onDone?.Invoke(false, string.IsNullOrEmpty(msg) ? "Failed to give up contract." : msg);
                }
            });
        }

        private void SetStatus(string msg)
        {
            statusMsg = msg;
            statusTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Thin wrapper over CraftDelivery so the classic window keeps its status line.
        /// The logic itself lives in CraftDelivery.cs, shared with the web bridge — two
        /// copies of a two-round-trip install would drift the moment either changed.
        /// </summary>
        private System.Collections.IEnumerator DoDownloadCraft(string contractId, string ownerName = "", Action<bool, string> onDone = null)
        {
            SetStatus("[+] Fetching craft info...");
            yield return CraftDelivery.Deliver(contractId, ownerName, (ok, msg) =>
            {
                SetStatus((ok ? "(Ok) " : "(No) ") + msg);
                onDone?.Invoke(ok, msg);
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

            // Browsing, buying and managing listings now lives on the website — link out
            // to it rather than duplicating the storefront in-game.
            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label("Browse & Buy", valueStyle);
            GUILayout.Label("The full marketplace (browsing, filtering and buying crafts, plus managing your uploads) is on the website.", labelStyle);
            GUILayout.Space(4);
            if (GUILayout.Button("[>] Open Marketplace Website", acceptBtnStyle, GUILayout.Height(30)))
                Application.OpenURL(GeneKermanMod.Instance.Api.MarketplaceUrl);
            GUILayout.EndVertical();
        }

        /// <summary>Reads the currently-loaded editor craft's metadata and resolves
        /// its saved .craft path (mirrors SubmissionSession's capture logic).</summary>
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
                // Mass and full funds cost (dry + module modifiers incl. TweakScale +
                // fuel) — the same walk the listing itself uses, so the figures shown
                // here and the ones uploaded cannot drift.
                ToolActions.ReadEditorValue(out editorCraftMass, out editorCraftCost);

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

        /// <summary>
        /// List the loaded craft. The whole payload — flags, mod list, blueprint,
        /// thumbnail, life-support flag — is assembled by ToolActions.SellCurrentCraft,
        /// which the sidebar's Market panel calls too; this window only supplies the
        /// price box and the status line.
        /// </summary>
        private System.Collections.IEnumerator DoSellCraft()
        {
            selling = true;
            yield return ToolActions.SellCurrentCraft(sellPriceInput, SetStatus, (ok, message) =>
            {
                selling = false;
                SetStatus((ok ? "(Ok) " : "(No) ") + message);
            });
        }

        // ── Craft Import Queue (auto-import) ─────────────────────────────────
        //
        // Crafts are selected for import in Discord (the /library command for
        // bot-contract crafts, or the "Load to KSP" button on a marketplace
        // purchase DM). That queues an entry on the player's account; here we poll
        // the queue and import each craft into the active save automatically.
        //
        // Called from GeneKermanMod.Update on a timer at the Space Center and in the
        // editor (where only the file-write imports run — live vessels wait for a
        // scene they can spawn in), and once from GiftInbox.Accept to deliver an
        // accepted gift on the spot.

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
            // (GameData/BoundlessMissions/Flags) — never a craft or live vessel.
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

            // Rescue deliveries and friend quicksends are LIVE vessels. Rescue: the
            // rescued kerbals coming home (or a cancelled rescue returning to its spot).
            // gift_vessel: a vessel a friend sent straight to your save. Crew are
            // tagged/stripped by owner on import — your own kerbals come back to their
            // original names; anyone else's keep their owner tag.
            if ((source == "rescue_delivery" || source == "gift_vessel") && !string.IsNullOrEmpty(vesselNodeUrl))
            {
                // The poll also runs in the editor now (for blueprint installs), but a
                // live vessel cannot spawn there — leave the entry queued for a scene
                // that can, and skip the download it would waste.
                if (!GiftInbox.CanDeliverHere(source))
                {
                    processingImports.Remove(importId);
                    yield break;
                }
                string myName = GeneKermanMod.Instance.LinkedUsername;
                bool spawned = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(vesselNodeUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string node = CraftDelivery.DecompressToString(fileData);
                    string vesselName = VesselTransfer.ImportVesselAtTarget(node, null, ownerName, myName);
                    if (!string.IsNullOrEmpty(vesselName))
                    {
                        spawned = true;
                        string title = source == "gift_vessel" ? "🎁 Vessel Received" : "🛟 Rescue Delivered";
                        string from = string.IsNullOrEmpty(ownerName) ? "" : $" from {ownerName}";
                        GeneKermanMod.Instance.ShowNotification(title,
                            $"{vesselName}{(source == "gift_vessel" ? from : "")} has arrived in your save.");
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
            // The fetch/parse/download lives in SubmissionPreviewLoader so the uGUI
            // sidebar shows the same images without a second copy of the
            // telemetry_urls/telemetry_url fallback. This window keeps its own popup
            // state and its own ownership of the textures.
            yield return SubmissionPreviewLoader.Fetch(contractId, preview =>
            {
                blueprintVesselName = preview.VesselName;
                blueprintTextures.AddRange(preview.Blueprints);
                telemetryTextures.AddRange(preview.Telemetry);

                loadingBlueprints = false;
                blueprintStatus = preview.Error ?? "";
            });
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
                if (tex != null) UnityEngine.Object.Destroy(tex);
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

            // Active vessel info — opens the orbital telemetry diagram(s) in their window.
            if (telemetryTextures.Count > 0 &&
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
            if (telemetryTextures.Count == 0) return;
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
            foreach (var tex in telemetryTextures)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            telemetryTextures.Clear();
        }

        private void DrawTelemetryWindow(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(windowStyle);

            GUILayout.BeginHorizontal();
            string title = telemetryTextures.Count > 1
                ? $"🛰 Orbital Telemetry ({telemetryTextures.Count} craft)"
                : (string.IsNullOrEmpty(blueprintVesselName)
                    ? "🛰 Orbital Telemetry"
                    : $"🛰 {blueprintVesselName}: Telemetry");
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

            if (telemetryTextures.Count == 0)
            {
                GUILayout.Label("No telemetry available for this submission.", labelStyle);
            }
            else
            {
                float viewH = Mathf.Max(120f, telemetryRect.height - 80f);
                telemetryScroll = GUILayout.BeginScrollView(telemetryScroll, GUILayout.Height(viewH));
                float fitW = Mathf.Max(80f, telemetryRect.width - 50f);
                foreach (var tex in telemetryTextures)
                {
                    if (tex == null) continue;
                    float baseScale = tex.width > fitW ? fitW / tex.width : 1f;
                    float w = tex.width * baseScale * telemetryZoom;
                    float h = tex.height * baseScale * telemetryZoom;
                    Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
                    GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                    GUILayout.Space(8);
                }
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
