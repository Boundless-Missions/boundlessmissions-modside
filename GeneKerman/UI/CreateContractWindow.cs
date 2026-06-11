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
            "Squad parts only — no Making History / Breaking Ground.",
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

        public void Open(int balance, string userId = "")
        {
            IsVisible = true;
            currentBalance = balance;
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

        private string BuildModlist()
        {
            switch (modlistMode)
            {
                case 0: return null;
                case 1: return "Squad,-Squad/Expansions"; // Squad folder minus MH/BG subdirs
                case 2: return "Squad";                  // Full Squad folder including DLC
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

            windowRect = GUILayout.Window(windowId, windowRect, DrawContent, "",
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

            // ── Corp selector ──
            GUILayout.Label("Select Corporation:", labelStyle);

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

            GUILayout.Space(8);

            // ── Mission description ──
            GUILayout.Label("Mission Description:", labelStyle);
            missionText = GUILayout.TextArea(missionText, textAreaStyle, GUILayout.Height(60));

            GUILayout.Space(6);

            // ── Payment ──
            GUILayout.BeginHorizontal();
            GUILayout.Label("Payment:", labelStyle, GUILayout.Width(80));
            paymentText = GUILayout.TextField(paymentText, textFieldStyle, GUILayout.Width(120));
            GUILayout.Label("KCoins", labelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

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

            // ── Status ──
            if (!string.IsNullOrEmpty(statusMsg))
            {
                GUILayout.Label(statusMsg, isSuccess ? successStyle : errorStyle);
                GUILayout.Space(4);
            }

            // ── Modlist Restriction ──
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

            GUILayout.EndScrollView();

            GUILayout.Space(8);

            // ── Buttons ──
            GUILayout.BeginHorizontal();

            GUI.enabled = !isSending;
            if (GUILayout.Button("Send Contract", sendBtnStyle, GUILayout.Height(36)))
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

        private void TrySend()
        {
            // Validate
            if (selectedCorpIndex < 0)
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

            string modlist = BuildModlist();

            // Janitor's Closet mode needs the editor's part filter, which only exists in
            // the VAB/SPH. Don't silently send an unrestricted contract if we couldn't read it.
            if (modlistMode == 4 && string.IsNullOrEmpty(modlist))
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
                    }));
        }
    }
}
