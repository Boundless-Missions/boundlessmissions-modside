/*
 * UI/CreateContractWindow.cs – In-game contract creation with corp selector.
 *
 * Lets the player browse corporations, select one, enter mission details,
 * and send a contract directly from KSP. Payment is escrowed from balance.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GeneKerman.UI
{
    public class CreateContractWindow
    {
        private Rect windowRect = new Rect(250, 100, 480, 520);
        private readonly int windowId = "GKCreateContract".GetHashCode();

        // State
        public bool IsVisible { get; set; }

        // Corps data
        private List<CorpEntry> corps = new List<CorpEntry>();
        private int selectedCorpIndex = -1;
        private bool loadingCorps;
        private bool corpsLoaded;

        // Form fields
        private string missionText = "";
        private string paymentText = "";
        private string fineText = "0";
        private string dueDateText = "";

        // User balance
        private int currentBalance;
        private string currentUserId = "";
        private string currentUserName = "";

        // Contract type: 0 Auto, 1 Craft Build, 2 Active Mission, 3 Rescue, 4 Flag Design
        private int contractType = 0;
        private const int AUTO_TYPE_INDEX = 0;
        private const int CRAFT_BUILD_INDEX = 1;
        private const int ACTIVE_TYPE_INDEX = 2;
        private const int RESCUE_TYPE_INDEX = 3;
        private const int FLAG_TYPE_INDEX = 4;
        private static readonly string[] ContractTypeLabels = { "Auto", "Craft Build", "Active Mission", "Rescue", "Flag Design" };
        private static readonly string[] ContractTypeApi = { "auto", "craft_build", "active_vessel", "rescue", "flag_design" };
        private static readonly string[] ContractTypeDescs = {
            "Let the server classify the mission.",
            "Recipient submits a blueprint from the VAB/SPH.",
            "Recipient flies a craft to the target.",
            "Recipient rescues your stranded kerbals.",
            "Recipient designs a flag (submitted & reviewed via Discord).",
        };

        // Auction mode: post as an open reverse auction instead of a direct contract.
        // Only valid for Craft Build / Active Mission. Runs durationText hours; the
        // lowest bidder is bound to a contract that inherits the selected type.
        private bool auctionMode = false;
        private string durationText = "24";

        // Rescue setup state
        private int rescueMode = 0; // 0 = orbit (Ap/Pe), 1 = surface (Lat/Lon)
        private readonly List<string> bodyNames = new List<string>();
        private readonly List<bool> bodyModded = new List<bool>();
        private int bodyIndex = -1;
        private Vector2 bodyScrollPos;
        private string apText = "100", peText = "100", marginAltText = "10";
        private string latText = "0", lonText = "0", marginPosText = "1";
        private readonly List<string> rescueCrew = new List<string>();
        private const double MIN_MARGIN_ORBIT_KM = 5.0;
        private const double MIN_MARGIN_SURFACE_DEG = 0.5;

        private static readonly HashSet<string> STOCK_BODIES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Kerbol", "Sun", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike",
            "Dres", "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo",
        };

        // Status
        private string statusMsg = "";
        private bool isSuccess;
        private bool isSending;

        // Modlist restriction mode
        // 0 = None, 1 = Squad Only, 2 = Squad + DLC, 3 = Active Modlist, 4 = Janitor's Closet
        private int modlistMode = 0;
        private static readonly string[] ModlistLabels = { "None", "Stock Only", "Stock + DLC", "My Modlist", "Janitor's Closet" };
        private static readonly string[] ModlistDescs = {
            "No part restrictions.",
            "Squad parts only, no Making History / Breaking Ground.",
            "All Squad parts including official DLC expansions.",
            "All mods currently installed on your game.",
            "Only mods visible in your Janitor's Closet profile.",
        };
        private bool? _jcAvailable; // null = not yet checked

        // Scroll
        private Vector2 corpScrollPos;
        private Vector2 formScrollPos;

        // Styles
        private GUIStyle windowStyle, headerStyle, labelStyle, valueStyle, checkboxStyle;
        private GUIStyle corpBtnStyle, corpSelectedStyle, textFieldStyle, textAreaStyle;
        private GUIStyle sendBtnStyle, cancelBtnStyle, errorStyle, successStyle;
        private bool stylesReady;

        struct CorpEntry
        {
            public string ownerId;
            public string ownerName;
            public string corpName;
        }

        public void Open(int balance, string userId = "", string userName = "")
        {
            IsVisible = true;
            currentBalance = balance;
            currentUserName = userName;
            if (userId != currentUserId)
            {
                currentUserId = userId;
                corpsLoaded = false; // re-filter with new identity
            }
            statusMsg = "";
            isSending = false;
            missionText = "";
            paymentText = "";
            fineText = "0";
            contractType = CRAFT_BUILD_INDEX;  // Auto is server-only; default to a concrete type
            auctionMode = false;
            ScanRescueContext();

            // Set default due date to 7 days from now
            dueDateText = DateTime.Now.AddDays(7).ToString("yyyy-MM-dd");

            // Load corps if not loaded
            if (!corpsLoaded && !loadingCorps)
                LoadCorps();
        }

        public void Close()
        {
            IsVisible = false;
        }

        // ── Modlist Helpers ─────────────────────────────────────────────────

        private bool IsJanitorsClosetAvailable()
        {
            if (_jcAvailable.HasValue) return _jcAvailable.Value;
            foreach (var asm in AssemblyLoader.loadedAssemblies)
            {
                if (asm.name.IndexOf("JanitorsCloset", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _jcAvailable = true;
                    return true;
                }
            }
            _jcAvailable = false;
            return false;
        }

        // ── Rescue Helpers ──────────────────────────────────────────────────

        /// <summary>Scan the live celestial bodies (flagging modded ones) and the
        /// active vessel's crew, so the rescue panel reflects the current game.</summary>
        private void ScanRescueContext()
        {
            // Remember the player's current pick so a re-scan (e.g. the refresh right
            // before sending) doesn't silently reset it back to the active body — the
            // body list is the same set of celestial bodies every scan.
            string previouslySelected = (bodyIndex >= 0 && bodyIndex < bodyNames.Count)
                ? bodyNames[bodyIndex] : null;

            bodyNames.Clear();
            bodyModded.Clear();
            bodyIndex = -1;
            if (FlightGlobals.Bodies != null)
            {
                foreach (var b in FlightGlobals.Bodies)
                {
                    if (b == null) continue;
                    bodyNames.Add(b.bodyName);
                    bodyModded.Add(!STOCK_BODIES.Contains(b.bodyName));
                }
            }

            rescueCrew.Clear();
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
            {
                // Restore the player's previous pick if they made one; otherwise default
                // the body to wherever the player currently is.
                string desired = previouslySelected
                    ?? (FlightGlobals.ActiveVessel.mainBody != null
                        ? FlightGlobals.ActiveVessel.mainBody.bodyName : null);
                for (int i = 0; i < bodyNames.Count; i++)
                    if (bodyNames[i] == desired) { bodyIndex = i; break; }

                foreach (var pcm in FlightGlobals.ActiveVessel.GetVesselCrew())
                    if (pcm != null) rescueCrew.Add(pcm.name);
            }
        }

        /// <summary>The player's active installed mod folders (auto part restriction
        /// for rescue) — same derivation as the "My Modlist" option.</summary>
        private string BuildActiveModlist()
        {
            var folders = new HashSet<string>();
            foreach (var p in PartLoader.LoadedPartsList)
                if (p != null && !string.IsNullOrEmpty(p.partUrl))
                    folders.Add(p.partUrl.Split('/')[0]);
            return string.Join(",", folders.ToArray());
        }

        private void DrawNumberRow(string label, ref string val)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(180));
            val = GUILayout.TextField(val, textFieldStyle, GUILayout.Width(110));
            GUILayout.EndHorizontal();
        }

        private static bool TryParseInv(string s, out double v)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private string BuildModlist()
        {
            switch (modlistMode)
            {
                case 0: return null;
                case 1: return "Squad";                  // Stock only — DLC lives under the separate "SquadExpansion" folder, so it's excluded automatically
                case 2: return "Squad,SquadExpansion";   // Stock + DLC (MakingHistory/Serenity both sit under SquadExpansion)
                case 3:
                {
                    var folders = new HashSet<string>();
                    foreach (var p in PartLoader.LoadedPartsList)
                        if (!string.IsNullOrEmpty(p.partUrl))
                            folders.Add(p.partUrl.Split('/')[0]);
                    return string.Join(",", folders.ToArray());
                }
                case 4:
                    return ReadJanitorsClosetModlist();
                default:
                    return null;
            }
        }

        // Reads the set of mod folders currently visible under Janitor's Closet's mod
        // filter. JC registers its mod-level filter into KSP's own
        // EditorPartList.ExcludeFilters under the id "Mod Filter", so we read it through
        // KSP's core API rather than reflecting into JC internals (whose layout varies
        // by version). The filter's FilterCriteria(part) returns true when the part is
        // kept/visible, so hiding a mod (e.g. Squad expansion) drops its folder here too.
        //
        // NOTE: ExcludeFilters only exists while in the VAB/SPH editor — returns null
        // otherwise so the caller can surface that this must be set from the editor.
        private string ReadJanitorsClosetModlist()
        {
            try
            {
                var editorList = KSP.UI.Screens.EditorPartList.Instance;
                if (editorList == null || editorList.ExcludeFilters == null)
                {
                    Debug.LogWarning("[GeneKerman] EditorPartList not available — open the VAB/SPH to read the JC mod filter.");
                    return null;
                }

                EditorPartListFilter<AvailablePart> modFilter = editorList.ExcludeFilters["Mod Filter"];
                if (modFilter == null || modFilter.FilterCriteria == null)
                {
                    Debug.LogWarning("[GeneKerman] JC 'Mod Filter' not registered in EditorPartList.ExcludeFilters.");
                    return null;
                }

                var criteria = modFilter.FilterCriteria; // true == part kept (mod allowed)
                var visibleFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in PartLoader.LoadedPartsList)
                {
                    if (part == null || string.IsNullOrEmpty(part.partUrl)) continue;
                    bool visible;
                    try { visible = criteria(part); }
                    catch { continue; }
                    if (visible)
                        visibleFolders.Add(part.partUrl.Split('/')[0]);
                }

                if (visibleFolders.Count > 0)
                {
                    Debug.Log($"[GeneKerman] JC modlist via 'Mod Filter' criteria: {visibleFolders.Count} visible folders");
                    return string.Join(",", visibleFolders.ToArray());
                }

                Debug.LogWarning("[GeneKerman] JC 'Mod Filter' matched no visible parts.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] JC modlist read failed: {ex.Message}\n{ex.StackTrace}");
            }

            return null;
        }

        private void LoadCorps()
        {
            loadingCorps = true;
            statusMsg = "Loading corporations...";
            GeneKermanMod.Instance.StartCoroutine(
                GeneKermanMod.Instance.Api.GetCorps((ok, data, error) =>
                {
                    loadingCorps = false;
                    if (ok && data != null)
                    {
                        corps.Clear();
                        selectedCorpIndex = -1;
                        var list = MiniJSON.GetList(data, "corps");
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                var d = item as Dictionary<string, object>;
                                if (d != null)
                                {
                                    string oid = MiniJSON.GetString(d, "owner_id", "");
                                    if (!string.IsNullOrEmpty(currentUserId) && oid == currentUserId)
                                        continue;
                                    corps.Add(new CorpEntry
                                    {
                                        ownerId = oid,
                                        ownerName = MiniJSON.GetString(d, "owner_name", "Unknown"),
                                        corpName = MiniJSON.GetString(d, "corp_name", "Unknown"),
                                    });
                                }
                            }
                        }
                        corpsLoaded = true;
                        statusMsg = $"Found {corps.Count} corporation(s)";
                    }
                    else
                    {
                        statusMsg = error ?? "Failed to load corporations";
                    }
                }));
        }

        // ── Draw ────────────────────────────────────────────────────────────

        public void Draw()
        {
            if (!IsVisible) return;

            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(480));
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            windowStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(16, 16, 12, 12),
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.10f, 0.14f, 0.96f)) },
            };

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.55f, 0.85f, 1f) },
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.7f, 0.8f, 0.9f) },
            };

            checkboxStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.7f, 0.8f, 0.9f) },
            };

            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.95f, 1f) },
            };

            var corpBg = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.15f, 0.20f, 0.9f));
            corpBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12, alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 6, 6),
                normal = { textColor = Color.white, background = corpBg },
                hover = { textColor = new Color(0.55f, 0.85f, 1f), background = corpBg },
            };

            var corpSelBg = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.35f, 0.55f, 0.9f));
            corpSelectedStyle = new GUIStyle(corpBtnStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.55f, 0.85f, 1f), background = corpSelBg },
            };

            textFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13, padding = new RectOffset(8, 8, 4, 4), border = new RectOffset(0, 0, 0, 0),
                normal = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.06f, 0.08f, 0.12f, 0.95f)) },
                focused = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.12f, 0.18f, 0.95f)) },
                hover = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.07f, 0.10f, 0.15f, 0.95f)) },
                active = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.12f, 0.18f, 0.95f)) },
            };

            textAreaStyle = new GUIStyle(textFieldStyle)
            {
                wordWrap = true,
            };

            sendBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.45f, 0.2f, 0.9f)) },
                hover = { textColor = Color.white, background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.55f, 0.25f, 0.9f)) },
                padding = new RectOffset(16, 16, 8, 8),
            };

            cancelBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f), background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.2f, 0.25f, 0.9f)) },
                padding = new RectOffset(12, 12, 6, 6),
            };

            errorStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, wordWrap = true,
                normal = { textColor = new Color(1f, 0.4f, 0.3f) },
            };

            successStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, wordWrap = true,
                normal = { textColor = new Color(0.3f, 1f, 0.4f) },
            };

            stylesReady = true;
        }

        private void DrawContent(int id)
        {
            InitStyles();

            GUILayout.BeginVertical(windowStyle);

            // Header
            GUILayout.Label("📝 New Contract", headerStyle);
            GUILayout.Space(4);

            // Balance display
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your Balance:", labelStyle, GUILayout.Width(100));
            GUILayout.Label($"{currentBalance} KCoins", valueStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            formScrollPos = GUILayout.BeginScrollView(formScrollPos, GUILayout.Height(380));

            // ── Contract type ──
            GUILayout.Label("Contract Type:", labelStyle);
            GUILayout.Space(2);
            for (int i = 0; i < ContractTypeLabels.Length; i++)
            {
                // Auto-classification is reserved for server-issued missions; players
                // must pick a concrete type so submissions are checked correctly.
                bool disabled = (i == AUTO_TYPE_INDEX);
                GUI.enabled = !disabled;

                GUILayout.BeginHorizontal();
                bool sel = GUILayout.Toggle(contractType == i, "", checkboxStyle, GUILayout.Width(18));
                if (sel && contractType != i)
                {
                    contractType = i;
                    if (i == RESCUE_TYPE_INDEX) ScanRescueContext();
                    // Auctions only apply to build / active missions.
                    if (i != CRAFT_BUILD_INDEX && i != ACTIVE_TYPE_INDEX) auctionMode = false;
                }
                string typeLabel = ContractTypeLabels[i] + (disabled ? "  (server only)" : "");
                GUILayout.Label(typeLabel, valueStyle, GUILayout.Width(140));
                GUILayout.Label(ContractTypeDescs[i], labelStyle);
                GUILayout.EndHorizontal();

                GUI.enabled = true;
            }
            GUILayout.Space(8);

            // ── Auction toggle (build / active only) — turns the direct contract
            //    into an open reverse auction, so there's no single recipient. ──
            if (contractType == CRAFT_BUILD_INDEX || contractType == ACTIVE_TYPE_INDEX)
            {
                GUILayout.BeginHorizontal();
                auctionMode = GUILayout.Toggle(auctionMode, "", checkboxStyle, GUILayout.Width(18));
                GUILayout.Label("Auction (open bidding)", valueStyle, GUILayout.Width(160));
                GUILayout.Label("Anyone bids the price down; lowest wins.", labelStyle);
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }

            // ── Recipient selector (hidden in auction mode — open to everyone) ──
            if (auctionMode)
            {
                GUILayout.Label("Open to everyone; the lowest bidder in Discord wins.", labelStyle);
            }
            else
            {
            GUILayout.Label(contractType == RESCUE_TYPE_INDEX ? "Select Rescuer:" : "Select Corporation:", labelStyle);

            if (loadingCorps)
            {
                GUILayout.Label("Loading corporations...", labelStyle);
            }
            else if (corps.Count == 0)
            {
                GUILayout.Label("No corporations found.", errorStyle);
                if (GUILayout.Button("Refresh", cancelBtnStyle, GUILayout.Width(80)))
                    LoadCorps();
            }
            else
            {
                corpScrollPos = GUILayout.BeginScrollView(corpScrollPos,
                    GUILayout.Height(Math.Min(corps.Count * 32, 130)));

                for (int i = 0; i < corps.Count; i++)
                {
                    var style = (i == selectedCorpIndex) ? corpSelectedStyle : corpBtnStyle;
                    string label = $"🏢 {corps[i].corpName}  ({corps[i].ownerName})";
                    if (GUILayout.Button(label, style))
                    {
                        selectedCorpIndex = i;
                    }
                }

                GUILayout.EndScrollView();
            }
            }  // end recipient selector (non-auction)

            GUILayout.Space(8);

            // ── Mission description ──
            GUILayout.Label("Mission Description:", labelStyle);
            missionText = GUILayout.TextArea(missionText, textAreaStyle, GUILayout.Height(60));

            GUILayout.Space(6);

            // ── Payment / auction starting price ──
            GUILayout.BeginHorizontal();
            GUILayout.Label(auctionMode ? "Start Price:" : "Payment:", labelStyle, GUILayout.Width(80));
            paymentText = GUILayout.TextField(paymentText, textFieldStyle, GUILayout.Width(120));
            GUILayout.Label("KCoins", labelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // ── Auction duration ──
            if (auctionMode)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Duration:", labelStyle, GUILayout.Width(80));
                durationText = GUILayout.TextField(durationText, textFieldStyle, GUILayout.Width(120));
                GUILayout.Label("hours", labelStyle);
                GUILayout.EndHorizontal();

                GUILayout.Space(4);
            }

            // ── Fine ──
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fine:", labelStyle, GUILayout.Width(80));
            fineText = GUILayout.TextField(fineText, textFieldStyle, GUILayout.Width(120));
            GUILayout.Label("KCoins", labelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // ── Due date ──
            GUILayout.BeginHorizontal();
            GUILayout.Label("Due Date:", labelStyle, GUILayout.Width(80));
            dueDateText = GUILayout.TextField(dueDateText, textFieldStyle, GUILayout.Width(150));
            GUILayout.Label("(YYYY-MM-DD)", labelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // ── Rescue setup ──
            if (contractType == RESCUE_TYPE_INDEX)
                DrawRescuePanel();

            // ── Status ──
            if (!string.IsNullOrEmpty(statusMsg))
            {
                GUILayout.Label(statusMsg, isSuccess ? successStyle : errorStyle);
                GUILayout.Space(4);
            }

            // ── Modlist Restriction (rescue auto-captures the active modlist;
            //    flag-design has no in-game build step, so part limits don't apply) ──
            if (contractType != RESCUE_TYPE_INDEX && contractType != FLAG_TYPE_INDEX)
            {
                GUILayout.Space(6);
                GUILayout.Label("Part Restriction:", labelStyle);
                GUILayout.Space(2);

                bool jcOk = IsJanitorsClosetAvailable();

                for (int i = 0; i < ModlistLabels.Length; i++)
                {
                    bool disabled = (i == 4 && !jcOk);
                    GUI.enabled = !disabled;

                    GUILayout.BeginHorizontal();
                    bool selected = GUILayout.Toggle(modlistMode == i, "", checkboxStyle, GUILayout.Width(18));
                    if (selected) modlistMode = i;

                    string label = ModlistLabels[i];
                    if (disabled) label += " (not installed)";
                    GUILayout.Label(label, valueStyle, GUILayout.Width(160));
                    GUILayout.Label(ModlistDescs[i], labelStyle);
                    GUILayout.EndHorizontal();

                    GUI.enabled = true;
                }
            }

            GUILayout.EndScrollView();

            GUILayout.Space(8);

            // ── Buttons ──
            GUILayout.BeginHorizontal();

            GUI.enabled = !isSending;
            if (GUILayout.Button(auctionMode ? "Post Auction" : "Send Contract", sendBtnStyle, GUILayout.Height(36)))
            {
                TrySend();
            }
            GUI.enabled = true;

            GUILayout.Space(8);

            if (GUILayout.Button("Cancel", cancelBtnStyle, GUILayout.Height(36)))
            {
                Close();
            }

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        // ── Rescue Panel ────────────────────────────────────────────────────

        private void DrawRescuePanel()
        {
            GUILayout.Space(6);
            GUILayout.Label("🛟 Rescue Setup", headerStyle);

            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
            {
                GUILayout.Label("⚠ Fly the crewed vessel you want rescued, then issue from flight.", errorStyle);
                return;
            }
            if (rescueCrew.Count == 0)
            {
                GUILayout.Label("⚠ Your active vessel has no crew to rescue.", errorStyle);
                if (GUILayout.Button("Rescan", cancelBtnStyle, GUILayout.Width(80))) ScanRescueContext();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Kerbals to send ({rescueCrew.Count}):", labelStyle);
            if (GUILayout.Button("Rescan", cancelBtnStyle, GUILayout.Width(70))) ScanRescueContext();
            GUILayout.EndHorizontal();
            foreach (var k in rescueCrew)
                GUILayout.Label($"  • {currentUserName}'s {k}", valueStyle);

            GUILayout.Space(6);
            GUILayout.Label("Deliver the rescued crew to:", labelStyle);

            // Body selector
            GUILayout.Label("Destination Body:", labelStyle);
            bodyScrollPos = GUILayout.BeginScrollView(bodyScrollPos,
                GUILayout.Height(Math.Min(Math.Max(bodyNames.Count, 1) * 28, 120)));
            for (int i = 0; i < bodyNames.Count; i++)
            {
                var style = (i == bodyIndex) ? corpSelectedStyle : corpBtnStyle;
                string lbl = bodyModded[i] ? $"🪐 {bodyNames[i]}  (modded)" : $"🌍 {bodyNames[i]}";
                if (GUILayout.Button(lbl, style)) bodyIndex = i;
            }
            GUILayout.EndScrollView();
            if (bodyIndex >= 0 && bodyModded[bodyIndex])
                GUILayout.Label("⚠ Modded planet; the rescuer is warned they need its planet pack.", labelStyle);

            GUILayout.Space(6);

            // Orbit / Surface mode
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(rescueMode == 0, " Orbit", checkboxStyle)) rescueMode = 0;
            GUILayout.Space(16);
            if (GUILayout.Toggle(rescueMode == 1, " Surface", checkboxStyle)) rescueMode = 1;
            GUILayout.EndHorizontal();
            GUILayout.Space(2);

            if (rescueMode == 0)
            {
                DrawNumberRow("Apoapsis (km):", ref apText);
                DrawNumberRow("Periapsis (km):", ref peText);
                DrawNumberRow($"Margin (km, min {MIN_MARGIN_ORBIT_KM}):", ref marginAltText);
            }
            else
            {
                DrawNumberRow("Latitude (°):", ref latText);
                DrawNumberRow("Longitude (°):", ref lonText);
                DrawNumberRow($"Margin (°, min {MIN_MARGIN_SURFACE_DEG}):", ref marginPosText);
            }

            GUILayout.Space(4);
            GUILayout.Label("Part restriction: auto (your active modlist).", labelStyle);
            GUILayout.Space(6);
        }

        private void TrySend()
        {
            // Validate (auctions are open to everyone, so no recipient to pick)
            if (!auctionMode && selectedCorpIndex < 0)
            {
                statusMsg = "❌ Select a corporation first.";
                isSuccess = false;
                return;
            }

            if (string.IsNullOrEmpty(missionText) || missionText.Length < 3)
            {
                statusMsg = "❌ Mission description is too short.";
                isSuccess = false;
                return;
            }

            int payment;
            if (!int.TryParse(paymentText, out payment) || payment <= 0)
            {
                statusMsg = "❌ Enter a valid payment amount.";
                isSuccess = false;
                return;
            }

            if (payment > currentBalance)
            {
                statusMsg = $"❌ Insufficient balance ({payment} needed, you have {currentBalance}).";
                isSuccess = false;
                return;
            }

            int fine;
            if (!int.TryParse(fineText, out fine) || fine < 0)
            {
                fine = 0;
            }

            if (string.IsNullOrEmpty(dueDateText))
            {
                statusMsg = "❌ Enter a due date.";
                isSuccess = false;
                return;
            }

            if (contractType == RESCUE_TYPE_INDEX)
            {
                TrySendRescue(payment, fine);
                return;
            }

            if (auctionMode)
            {
                TrySendAuction(payment, fine);
                return;
            }

            // Flag-design has no in-game build step, so it never carries a part restriction.
            string modlist = contractType == FLAG_TYPE_INDEX ? null : BuildModlist();

            // Janitor's Closet mode needs the editor's part filter, which only exists in
            // the VAB/SPH. Don't silently send an unrestricted contract if we couldn't read it.
            if (contractType != FLAG_TYPE_INDEX && modlistMode == 4 && string.IsNullOrEmpty(modlist))
            {
                statusMsg = "❌ Open the VAB or SPH to capture the Janitor's Closet filter.";
                isSuccess = false;
                return;
            }

            var corp = corps[selectedCorpIndex];
            isSending = true;
            statusMsg = "Sending contract...";

            GeneKermanMod.Instance.StartCoroutine(
                GeneKermanMod.Instance.Api.CreateContract(
                    corp.ownerId, missionText, payment, fine, dueDateText, modlist,
                    (ok, data, error) =>
                    {
                        isSending = false;
                        if (ok)
                        {
                            isSuccess = true;
                            statusMsg = $"✅ Contract sent to {corp.ownerName}!";
                            currentBalance -= payment;
                        }
                        else
                        {
                            isSuccess = false;
                            statusMsg = $"❌ {error ?? "Failed to create contract."}";
                        }
                    },
                    ContractTypeApi[contractType]));
        }

        private void TrySendAuction(int startValue, int fine)
        {
            int duration;
            if (!int.TryParse(durationText, out duration) || duration < 1)
            {
                statusMsg = "❌ Enter a valid auction duration (hours).";
                isSuccess = false;
                return;
            }

            // Auctions support the same part restriction as a normal contract.
            string modlist = BuildModlist();
            if (modlistMode == 4 && string.IsNullOrEmpty(modlist))
            {
                statusMsg = "❌ Open the VAB or SPH to capture the Janitor's Closet filter.";
                isSuccess = false;
                return;
            }

            isSending = true;
            statusMsg = "Posting auction...";

            GeneKermanMod.Instance.StartCoroutine(
                GeneKermanMod.Instance.Api.CreateAuction(
                    missionText, startValue, fine, dueDateText, duration, modlist,
                    ContractTypeApi[contractType],
                    (ok, data, error) =>
                    {
                        isSending = false;
                        if (ok)
                        {
                            isSuccess = true;
                            statusMsg = "✅ Auction posted! Bidding happens in Discord.";
                            currentBalance -= startValue;
                        }
                        else
                        {
                            isSuccess = false;
                            statusMsg = $"❌ {error ?? "Failed to post auction."}";
                        }
                    }));
        }

        private void TrySendRescue(int payment, int fine)
        {
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
            {
                statusMsg = "❌ You must be in flight on the crewed vessel.";
                isSuccess = false;
                return;
            }
            ScanRescueContext(); // refresh crew/bodies right before sending
            if (rescueCrew.Count == 0)
            {
                statusMsg = "❌ No crew aboard to rescue.";
                isSuccess = false;
                return;
            }
            if (bodyIndex < 0 || bodyIndex >= bodyNames.Count)
            {
                statusMsg = "❌ Pick a target body.";
                isSuccess = false;
                return;
            }

            string body = bodyNames[bodyIndex];
            bool isModded = bodyModded[bodyIndex];
            string mode = rescueMode == 0 ? "orbit" : "surface";
            double ap = 0, pe = 0, lat = 0, lon = 0, marginAlt = 0, marginPos = 0;

            if (rescueMode == 0)
            {
                if (!TryParseInv(apText, out ap) || !TryParseInv(peText, out pe) || ap < 0 || pe < 0)
                {
                    statusMsg = "❌ Enter valid Apoapsis/Periapsis (km).";
                    isSuccess = false;
                    return;
                }
                double mk;
                if (!TryParseInv(marginAltText, out mk) || mk < MIN_MARGIN_ORBIT_KM) mk = MIN_MARGIN_ORBIT_KM;
                ap *= 1000.0; pe *= 1000.0; marginAlt = mk * 1000.0; // km → m
            }
            else
            {
                if (!TryParseInv(latText, out lat) || !TryParseInv(lonText, out lon) || lat < -90 || lat > 90)
                {
                    statusMsg = "❌ Enter a valid Latitude (-90..90) and Longitude.";
                    isSuccess = false;
                    return;
                }
                double md;
                if (!TryParseInv(marginPosText, out md) || md < MIN_MARGIN_SURFACE_DEG) md = MIN_MARGIN_SURFACE_DEG;
                marginPos = md;
            }

            // Snapshot the active vessel (crew roster embedded so attributes survive the
            // transfer). Crew are NOT renamed here — they're tagged "{me}'s {kerbal}" when
            // the rescuer imports the wreck, and stripped when they're returned to me.
            string node = VesselTransfer.ExportActiveVessel(true);
            if (string.IsNullOrEmpty(node))
            {
                statusMsg = "❌ Could not snapshot your vessel.";
                isSuccess = false;
                return;
            }
            string pid = FlightGlobals.ActiveVessel.id.ToString();
            string vesselName = FlightGlobals.ActiveVessel.vesselName;

            // The names the rescuer will see (and must recover) = "{me}'s {kerbal}".
            var taggedKerbals = new List<object>();
            foreach (var k in rescueCrew) taggedKerbals.Add(VesselTransfer.TagName(currentUserName, k));
            string kerbalsJson = MiniJSON.Serialize(taggedKerbals);

            string modlist = BuildActiveModlist();
            var corp = corps[selectedCorpIndex];
            isSending = true;
            statusMsg = "Sending rescue contract...";

            GeneKermanMod.Instance.StartCoroutine(
                GeneKermanMod.Instance.Api.CreateRescueContract(
                    corp.ownerId, missionText, payment, fine, dueDateText, modlist,
                    body, mode, ap, pe, lat, lon, marginAlt, marginPos, isModded,
                    pid, kerbalsJson, node,
                    (ok, data, error) =>
                    {
                        isSending = false;
                        if (ok)
                        {
                            isSuccess = true;
                            statusMsg = $"✅ Rescue sent to {corp.ownerName}! Your vessel will be removed.";
                            currentBalance -= payment;
                            // Can't Die() the active vessel mid-flight — queue removal for a safe scene.
                            GeneKermanMod.Instance.QueueRescueVesselRemoval(pid, vesselName);
                        }
                        else
                        {
                            isSuccess = false;
                            statusMsg = $"❌ {error ?? "Failed to create rescue contract."}";
                        }
                    }));
        }
    }
}
