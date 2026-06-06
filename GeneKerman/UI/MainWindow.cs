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
        private bool stylesReady;

        private readonly string[] tabNames = { "📋 Missions", "📜 Contracts", "👤 Profile", "🔔 Notifications" };

        public void OnOpen()
        {
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

        // ── Draw ────────────────────────────────────────────────────────────

        public void Draw()
        {
            // Detect scene change — Unity destroys textures on transition
            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = GUILayout.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(550));
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            windowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.1f, 0.14f, 0.97f)) },
                padding = new RectOffset(10, 10, 10, 10)
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
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 0.9f)) },
                padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(0, 0, 3, 3)
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

            var rowBg = GKSkin.MakeTex(2, 2, new Color(0.09f, 0.09f, 0.13f, 0.85f));
            mailRowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = rowBg },
                padding = new RectOffset(4, 4, 3, 3),
                margin = new RectOffset(0, 0, 1, 1),
                fixedHeight = 26,
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

            stylesReady = true;
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
                GUILayout.Label($"🔔 {unread}", new GUIStyle(GUI.skin.label) {
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
                if (i == 3 && unread > 0) label += $" ({unread})";
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
                case 3: DrawNotificationsTab(); break;
            }
            GUILayout.EndScrollView();

            // Status bar
            if (!string.IsNullOrEmpty(statusMsg) && Time.realtimeSinceStartup - statusTime < 5f)
            {
                statusStyle.normal.textColor = statusMsg.StartsWith("✅") ? new Color(0.3f, 0.9f, 0.4f) : new Color(0.9f, 0.4f, 0.3f);
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
                if (GUILayout.Button("🔄 Refresh", GUILayout.Height(30)))
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
                string diffLabel = diff <= 3 ? "🟢" : diff <= 6 ? "🟡" : diff <= 8 ? "🔴" : "⚫";

                GUILayout.BeginVertical(style);
                GUILayout.BeginHorizontal();

                // Mission info
                GUILayout.BeginVertical();
                GUILayout.Label($"{diffLabel} #{MiniJSON.GetInt(m, "id")}  {MiniJSON.GetString(m, "desc_en")}", valueStyle);
                GUILayout.Label($"⭐ {diff}/10  ·  +{MiniJSON.GetInt(m, "xp")} XP  ·  +{MiniJSON.GetInt(m, "coins")} KCoins  ·  Fine: {MiniJSON.GetInt(m, "fine")}", labelStyle);
                GUILayout.EndVertical();

                // Accept button
                if (!missionsLocked)
                {
                    if (GUILayout.Button("Accept", acceptBtnStyle))
                    {
                        int missionId = MiniJSON.GetInt(m, "id");
                        GeneKermanMod.Instance.RunCoroutine(DoSelectMission(missionId));
                    }
                }

                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.Space(5);
            if (GUILayout.Button("🔄 Refresh Missions", GUILayout.Height(28)))
                RefreshMissions();
        }

        // ── Contracts Tab (Mail-style Inbox) ─────────────────────────────────

        // Mail state
        private HashSet<string> selectedContracts = new HashSet<string>();
        private bool selectAll;
        private int filterMode; // 0=All, 1=Incoming, 2=Outgoing
        private readonly string[] filterLabels = { "All", "Incoming", "Outgoing" };
        private string openContractId; // If set, shows detail view

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
            // Row 1: Compose + Delete + Refresh
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("✏ Compose", acceptBtnStyle, GUILayout.Height(26)))
            {
                int balance = profile != null ? MiniJSON.GetInt(profile, "balance") : 0;
                GeneKermanMod.Instance.OpenCreateContractWindow(balance);
            }

            GUI.enabled = selectedContracts.Count > 0;
            if (GUILayout.Button("Cancel", deleteBtnStyle, GUILayout.Height(26), GUILayout.Width(70)))
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

            if (GUILayout.Button("🔄", tabStyle, GUILayout.Height(26), GUILayout.Width(30)))
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
                        if (c != null) selectedContracts.Add(MiniJSON.GetString(c, "contract_id"));
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
                GUILayout.Label("📭 No contracts.", labelStyle);
                return;
            }

            string myId = "";
            if (profile != null)
                myId = MiniJSON.GetString(profile, "user_id", "");

            foreach (var cObj in contracts)
            {
                var c = cObj as Dictionary<string, object>;
                if (c == null) continue;

                string issuerId = MiniJSON.GetString(c, "issuer_id", "");
                bool isIncoming = issuerId != myId;

                // Apply filter
                if (filterMode == 1 && !isIncoming) continue;
                if (filterMode == 2 && isIncoming) continue;

                string cid = MiniJSON.GetString(c, "contract_id");
                string status = MiniJSON.GetString(c, "status");
                string issuerName = MiniJSON.GetString(c, "issuer_name");
                string contractorName = MiniJSON.GetString(c, "contractor_name");
                string mission = MiniJSON.GetString(c, "mission");
                string dueDate = MiniJSON.GetString(c, "due_date");
                string createdAt = MiniJSON.GetString(c, "created_at", "");
                int payment = MiniJSON.GetInt(c, "payment");

                // Status colors
                Color dotColor = GetStatusColor(status);

                // Row
                GUILayout.BeginHorizontal(mailRowStyle);

                // Checkbox
                bool wasSelected = selectedContracts.Contains(cid);
                bool isSelected = GUILayout.Toggle(wasSelected, "", GUILayout.Width(18));
                if (isSelected != wasSelected)
                {
                    if (isSelected) selectedContracts.Add(cid);
                    else selectedContracts.Remove(cid);
                }

                // Status dot
                var dotStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10, fixedWidth = 14,
                    normal = { textColor = dotColor },
                    alignment = TextAnchor.MiddleCenter,
                };
                GUILayout.Label("●", dotStyle, GUILayout.Width(14));

                // Direction arrow + name
                string dirLabel = isIncoming
                    ? $"← {issuerName}"
                    : $"→ {contractorName}";
                GUILayout.Label(dirLabel, mailSenderStyle, GUILayout.Width(120));

                // Mission subject (clickable)
                string subjectText = mission.Length > 40 ? mission.Substring(0, 37) + "..." : mission;
                if (GUILayout.Button(subjectText, mailSubjectStyle))
                {
                    openContractId = cid;
                }

                // Payment
                GUILayout.Label($"💰{payment}", mailAmountStyle, GUILayout.Width(65));

                // Date
                string dateLabel = !string.IsNullOrEmpty(dueDate) && dueDate.Length >= 10
                    ? dueDate.Substring(5) : dueDate; // Show MM-DD
                GUILayout.Label(dateLabel, mailDateStyle, GUILayout.Width(50));

                GUILayout.EndHorizontal();
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
            DrawDetailRow("Type", mType == "craft_build" ? "Craft Build" : "Active Vessel");

            GUILayout.EndVertical();

            GUILayout.Space(8);

            // Action buttons
            GUILayout.BeginHorizontal();

            if (status == "active")
            {
                if (GUILayout.Button("📤 Submit", acceptBtnStyle, GUILayout.Height(30)))
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    string mission = MiniJSON.GetString(c, "mission");
                    string mT = MiniJSON.GetString(c, "mission_type", "active_vessel");
                    string reqSit = MiniJSON.GetString(c, "required_situation", "");
                    string reqBody = MiniJSON.GetString(c, "required_body", "");
                    GeneKermanMod.Instance.OpenSubmitWindow(cid, mission, mT, reqSit, reqBody);
                }
            }
            else if (status == "pending")
            {
                if (GUILayout.Button("✅ Accept", acceptBtnStyle, GUILayout.Height(30)))
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    GeneKermanMod.Instance.RunCoroutine(DoAcceptContract(cid));
                    openContractId = null;
                }
                GUILayout.Space(8);
                if (GUILayout.Button("❌ Decline", deleteBtnStyle, GUILayout.Height(30)))
                {
                    string cid = MiniJSON.GetString(c, "contract_id");
                    GeneKermanMod.Instance.RunCoroutine(DoCancelContract(cid));
                    openContractId = null;
                }
            }
            else if (status == "completed")
            {
                string cid = MiniJSON.GetString(c, "contract_id");
                bool alreadyImported = GKContractScenario.Instance != null && GKContractScenario.Instance.HasImportedVessel(cid);

                if (alreadyImported)
                {
                    GUI.enabled = false;
                    GUILayout.Button("✅ Vessel Imported", submitBtnStyle, GUILayout.Height(30));
                    GUI.enabled = true;
                }
                else
                {
                    if (GUILayout.Button("📥 Import / Download", submitBtnStyle, GUILayout.Height(30)))
                    {
                        GeneKermanMod.Instance.RunCoroutine(DoDownloadCraft(cid));
                    }
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
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
                if (GUILayout.Button("🔄 Refresh", GUILayout.Height(30)))
                    RefreshProfile();
                return;
            }

            GUILayout.BeginVertical(boxDarkStyle);
            GUILayout.Label($"👤 {MiniJSON.GetString(profile, "username")}", headerStyle);
            GUILayout.Space(10);

            DrawStatRow("💰 Balance", $"{MiniJSON.GetInt(profile, "balance"):N0} {MiniJSON.GetString(profile, "currency_name", "KCoins")}");
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
            GUILayout.Label("⚙️ Account", valueStyle);
            GUILayout.Label($"Server: {GeneKermanMod.Instance.Api.ServerUrl}", labelStyle);
            if (GUILayout.Button("🔓 Unlink Account", GUILayout.Height(28)))
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
            GUILayout.Label(label, labelStyle, GUILayout.Width(120));
            GUILayout.Label(value, valueStyle);
            GUILayout.EndHorizontal();
        }

        private string GetLevelBadge(int level)
        {
            switch (level)
            {
                case 1: return "🌍1";  case 2: return "🌙2";  case 3: return "🔗3";
                case 4: return "🔴4";  case 5: return "🌎5";  case 6: return "💜6";
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
                GUILayout.Label("No new notifications.", labelStyle);
                if (GUILayout.Button("🔄 Refresh", GUILayout.Height(30)))
                    RefreshNotifications();
                return;
            }

            foreach (var nObj in notifications)
            {
                var n = nObj as Dictionary<string, object>;
                if (n == null) continue;

                GUILayout.BeginVertical(boxDarkStyle);
                GUILayout.Label(MiniJSON.GetString(n, "title"), valueStyle);
                GUILayout.Label(MiniJSON.GetString(n, "message"), labelStyle);
                GUILayout.Label(MiniJSON.GetString(n, "timestamp"), new GUIStyle(labelStyle) { fontSize = 10 });
                GUILayout.EndVertical();
            }

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("✅ Mark All Read", GUILayout.Height(28)))
            {
                GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.MarkNotificationsRead((ok, resp, status) =>
                {
                    if (ok)
                    {
                        notifications?.Clear();
                        GeneKermanMod.Instance.UnreadNotifications = 0;
                        SetStatus("✅ Notifications cleared.");
                    }
                }));
            }
            if (GUILayout.Button("🔄 Refresh", GUILayout.Height(28)))
                RefreshNotifications();
            GUILayout.EndHorizontal();
        }

        // ── Data Refresh ────────────────────────────────────────────────────

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

        private void RefreshContracts()
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
                    SetStatus($"✅ {MiniJSON.GetString(data, "message", "Mission accepted!")}");
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
                    SetStatus($"❌ {err ?? "Failed to select mission."}");
                }
            });
        }

        private System.Collections.IEnumerator DoAcceptContract(string contractId)
        {
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/accept", "{}", (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus("✅ Contract accepted!");
                    RefreshContracts();
                }
                else
                {
                    SetStatus("❌ Failed to accept contract.");
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
                    RefreshContracts();
                }
                else
                {
                    SetStatus("❌ Failed to cancel contract.");
                }
            });
        }

        private void SetStatus(string msg)
        {
            statusMsg = msg;
            statusTime = Time.realtimeSinceStartup;
        }

        private System.Collections.IEnumerator DoDownloadCraft(string contractId)
        {
            SetStatus("📥 Fetching craft info...");

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
                        SetStatus("📥 Downloading vessel data...");
                        GeneKermanMod.Instance.RunCoroutine(DoImportVessel(contractId, vesselNodeUrl));
                    }
                    else if (craftFiles != null && craftFiles.Count > 0)
                    {
                        var first = craftFiles[0] as Dictionary<string, object>;
                        if (first == null)
                        {
                            SetStatus("❌ Invalid craft file data.");
                            return;
                        }

                        string url = MiniJSON.GetString(first, "url", "");
                        string filename = MiniJSON.GetString(first, "filename", "craft.craft");

                        if (string.IsNullOrEmpty(url))
                        {
                            SetStatus("❌ No download URL.");
                            return;
                        }

                        SetStatus("📥 Downloading craft file...");
                        GeneKermanMod.Instance.RunCoroutine(
                            GeneKermanMod.Instance.Api.DownloadFile(url, (dlOk, fileData) =>
                            {
                                if (dlOk && fileData != null)
                                {
                                    string path = CraftInstaller.Install(fileData, filename, loadmeta);
                                    if (path != null)
                                    {
                                        string msg = $"✅ Craft installed: {System.IO.Path.GetFileName(path)}";
                                        if (!string.IsNullOrEmpty(loadmeta))
                                            msg += " (+ loadmeta)";
                                        SetStatus(msg);
                                    }
                                    else
                                        SetStatus("❌ Failed to install craft file.");
                                }
                                else
                                {
                                    SetStatus("❌ Download failed.");
                                }
                            }));
                    }
                    else
                    {
                        SetStatus("❌ No craft file or vessel data in this contract.");
                    }
                }
                else
                {
                    SetStatus("❌ Could not fetch craft data.");
                }
            });
        }

        private System.Collections.IEnumerator DoImportVessel(string contractId, string vesselNodeUrl)
        {
            Debug.Log($"[GeneKerman] DoImportVessel: Downloading from {vesselNodeUrl}");
            yield return GeneKermanMod.Instance.Api.DownloadFile(vesselNodeUrl, (ok, fileData) =>
            {
                if (!ok || fileData == null)
                {
                    Debug.LogWarning($"[GeneKerman] DoImportVessel: Download failed (ok={ok}, data={fileData?.Length ?? -1})");
                    SetStatus("❌ Failed to download vessel data.");
                    return;
                }

                Debug.Log($"[GeneKerman] DoImportVessel: Downloaded {fileData.Length} bytes");

                // Decompress gzip
                byte[] rawData = fileData;
                if (fileData.Length >= 2 && fileData[0] == 0x1F && fileData[1] == 0x8B)
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
                        Debug.Log($"[GeneKerman] Decompressed: {fileData.Length} → {rawData.Length} bytes");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] Vessel node decompression failed: {ex.Message}");
                        rawData = fileData;
                    }
                }

                string vesselNodeStr = System.Text.Encoding.UTF8.GetString(rawData);
                Debug.Log($"[GeneKerman] Vessel node string: {vesselNodeStr.Length} chars, starts with: '{vesselNodeStr.Substring(0, System.Math.Min(200, vesselNodeStr.Length))}'");

                string vesselName = VesselTransfer.ImportVessel(vesselNodeStr);

                if (!string.IsNullOrEmpty(vesselName))
                {
                    SetStatus($"🚀 Vessel imported: {vesselName} (crew randomized)");
                    if (GKContractScenario.Instance != null)
                    {
                        GKContractScenario.Instance.MarkVesselImported(contractId);
                    }
                }
                else
                {
                    SetStatus("❌ Failed to import vessel.");
                }
            });
        }

    }
}
