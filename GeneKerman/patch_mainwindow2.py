import re

with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "r") as f:
    content = f.read()

# 1. Replace DrawMailToolbar
toolbar_pattern = r'private void DrawMailToolbar\(\)\s*\{.*?\n        \}'
toolbar_replacement = """private void DrawMailToolbar()
        {
            // Row 1: Inbox/Trash + Compose + Refresh
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("📥 Inbox", !showTrash ? tabActiveStyle : tabStyle, GUILayout.Height(26), GUILayout.Width(80)))
                showTrash = false;
            if (GUILayout.Button("🗑️ Trash", showTrash ? tabActiveStyle : tabStyle, GUILayout.Height(26), GUILayout.Width(80)))
                showTrash = true;

            GUILayout.Space(10);

            if (!showTrash && GUILayout.Button("✏ Compose", acceptBtnStyle, GUILayout.Height(26)))
            {
                int balance = profile != null ? MiniJSON.GetInt(profile, "balance") : 0;
                GeneKermanMod.Instance.OpenCreateContractWindow(balance);
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
        }"""
content = re.sub(toolbar_pattern, toolbar_replacement, content, flags=re.DOTALL)

# 2. Replace DrawMailList and add GetWeekKey and AutoCollapseWeeks
list_pattern = r'private void DrawMailList\(\)\s*\{.*?\n        \}'
list_replacement = """private void DrawMailList()
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

            var grouped = new Dictionary<string, List<Dictionary<string, object>>>();
            var weekKeys = new List<string>();

            foreach (var cObj in contracts)
            {
                var c = cObj as Dictionary<string, object>;
                if (c == null) continue;

                string cid = MiniJSON.GetString(c, "contract_id");
                bool isTrashed = trashedContracts.Contains(cid);
                if (showTrash != isTrashed) continue;

                string issuerId = MiniJSON.GetString(c, "issuer_id", "");
                bool isIncoming = issuerId != myId;

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
                GUILayout.Label(showTrash ? "🗑️ Trash bin is empty." : "📭 No contracts match the filter.", labelStyle);
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
                    string issuerId = MiniJSON.GetString(c, "issuer_id", "");
                    bool isIncoming = issuerId != myId;

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
                    GUILayout.Label(dirLabel, mailSenderStyle, GUILayout.Width(120));

                    string subjectText = mission.Length > 30 ? mission.Substring(0, 27) + "..." : mission;
                    if (GUILayout.Button(subjectText, mailSubjectStyle))
                    {
                        openContractId = cid;
                    }

                    GUILayout.Label($"💰{payment}", mailAmountStyle, GUILayout.Width(60));

                    string dateLabel = !string.IsNullOrEmpty(dueDate) && dueDate.Length >= 10 ? dueDate.Substring(5) : dueDate;
                    GUILayout.Label(dateLabel, mailDateStyle, GUILayout.Width(45));

                    if (status == "completed" || status == "failed" || status == "declined" || status == "cancelled")
                    {
                        if (!showTrash)
                        {
                            if (GUILayout.Button("🗑️", tabStyle, GUILayout.Width(25), GUILayout.Height(20)))
                            {
                                trashedContracts.Add(cid);
                                SaveTrash();
                            }
                        }
                        else
                        {
                            if (GUILayout.Button("♻️", tabStyle, GUILayout.Width(25), GUILayout.Height(20)))
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
        }"""
content = re.sub(list_pattern, list_replacement, content, flags=re.DOTALL)

# 3. Add AutoCollapseWeeks to RefreshContracts callback
refresh_pattern = r'contracts = MiniJSON\.GetList\(data, "contracts"\);'
refresh_replacement = """contracts = MiniJSON.GetList(data, "contracts");
                AutoCollapseWeeks();"""
content = content.replace(refresh_pattern, refresh_replacement)

# 4. Add Trash/Restore button to DrawContractDetail
detail_pattern = r'GUI\.enabled = true;\n\s*\}\n\s*\}\n\s*GUILayout\.EndHorizontal\(\);'
detail_replacement = """GUI.enabled = true;
                }

                GUILayout.FlexibleSpace();
                if (!trashedContracts.Contains(cid))
                {
                    if (GUILayout.Button("🗑️ Trash", tabStyle, GUILayout.Height(30)))
                    {
                        trashedContracts.Add(cid);
                        SaveTrash();
                        openContractId = null;
                    }
                }
                else
                {
                    if (GUILayout.Button("♻️ Restore", tabStyle, GUILayout.Height(30)))
                    {
                        trashedContracts.Remove(cid);
                        SaveTrash();
                        openContractId = null;
                    }
                }
            }

            GUILayout.EndHorizontal();"""
content = re.sub(detail_pattern, detail_replacement, content)


with open("/home/ayd/Desktop/GK-DW/KSP Mod Side/GeneKerman/UI/MainWindow.cs", "w") as f:
    f.write(content)
