/*
 * UI/SubmitWindow.cs – Contract submission flow with classification enforcement.
 *
 * Mission types (AI-classified, cached on server):
 *   - "craft_build": Must submit from VAB/SPH. Sends: craft file + KVV/screenshot.
 *   - "active_vessel": Must submit from Flight. Sends: craft + loadmeta + telemetry + screenshot.
 *     Also validates vessel situation and body match requirements.
 *
 * The server tells us what type + requirements via the contract data.
 * We enforce it here so players get immediate feedback, not a server rejection.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GeneKerman.UI
{
    public class SubmitWindow
    {
        private Rect windowRect = new Rect(200, 80, 500, 560);
        private readonly int windowId = "GKSubmit".GetHashCode();

        // State
        public bool IsVisible { get; set; }
        public string ContractId { get; set; }
        public string ContractMission { get; set; }

        // Classification from server
        private string missionType = "active_vessel";  // "craft_build" or "active_vessel"
        private string requiredSituation = "";           // "ORBITING", "LANDED", etc.
        private string requiredBody = "";                // "Mun", "Duna", etc.

        private Vector2 scrollPos;
        private string statusMsg = "";

        // Editor mode data
        private string editorCraftName = "";
        private string editorCraftPath = "";
        private string editorCraftType = "";
        private int editorPartCount;
        private float editorCraftMass;
        private float editorCraftCost;

        // Flight data
        private VesselDataCollector.VesselSnapshot activeVessel;
        private List<VesselDataCollector.VesselSnapshot> nearbyVessels;

        // Submission
        private bool isSubmitting;
        private string screenshotPath;
        private bool includeModlist = true;
        private bool screenshotTaken;

        // Validation
        private bool sceneValid;       // Are we in the correct scene for this mission type?
        private bool vesselValid;      // Does the vessel match required situation/body?
        private string validationMsg = "";  // Why validation failed
        private string requiredModlist;
        private HashSet<string> allowedMods;
        private List<string> excludePaths;

        // Styles
        private GUIStyle windowStyle, headerStyle, boxStyle, labelStyle, valueStyle, checkboxStyle;
        private GUIStyle submitBtnStyle, cancelBtnStyle, errorStyle, successStyle;
        private bool stylesReady;

        public void Open(string contractId, string mission,
            string type = "active_vessel", string situation = "", string body = "", string modlist = "")
        {
            ContractId = contractId;
            ContractMission = mission;
            missionType = type ?? "active_vessel";
            requiredSituation = situation ?? "";
            requiredBody = body ?? "";
            IsVisible = true;
            isSubmitting = false;
            statusMsg = "";
            screenshotTaken = false;
            
            requiredModlist = modlist;
            allowedMods = null;
            excludePaths = null;
            if (!string.IsNullOrEmpty(requiredModlist))
            {
                allowedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                excludePaths = new List<string>();
                foreach (var token in requiredModlist.Split(','))
                {
                    string t = token.Trim();
                    if (t.StartsWith("-")) excludePaths.Add(t.Substring(1));
                    else allowedMods.Add(t);
                }
            }

            Validate();
        }

        private void Validate()
        {
            sceneValid = false;
            vesselValid = false;
            validationMsg = "";

            if (missionType == "craft_build")
            {
                // Craft build: must be in VAB/SPH
                if (HighLogic.LoadedSceneIsEditor)
                {
                    sceneValid = true;
                    vesselValid = true; // No vessel situation needed for craft builds
                    CaptureEditorCraft();
                }
                else
                {
                    validationMsg = "🏗️ This is a craft build mission.\nGo to the VAB or SPH to submit.";
                }
            }
            else // active_vessel
            {
                // Active vessel: must be in flight
                if (HighLogic.LoadedSceneIsFlight)
                {
                    sceneValid = true;
                    CaptureFlightData();
                    ValidateVesselState();
                }
                else
                {
                    validationMsg = "🚀 This is an active vessel mission.\nLaunch your craft and fly to the target to submit.";
                }
            }
        }

        private void ValidateVesselState()
        {
            if (activeVessel == null)
            {
                vesselValid = false;
                validationMsg = "No active vessel found.";
                return;
            }

            vesselValid = true;
            var issues = new List<string>();

            // Check body requirement
            if (!string.IsNullOrEmpty(requiredBody))
            {
                if (!string.Equals(activeVessel.body, requiredBody, StringComparison.OrdinalIgnoreCase))
                {
                    vesselValid = false;
                    issues.Add($"❌ Body mismatch: at {activeVessel.body}, need {requiredBody}");
                }
            }

            // Check situation requirement
            if (!string.IsNullOrEmpty(requiredSituation))
            {
                if (!string.Equals(activeVessel.situation, requiredSituation, StringComparison.OrdinalIgnoreCase))
                {
                    vesselValid = false;
                    issues.Add($"❌ Situation mismatch: {activeVessel.situation}, need {requiredSituation}");
                }
            }

            // Check part legality — list every offending part, not just the first.
            string illegalParts = FindIllegalParts(FlightGlobals.ActiveVessel?.parts);
            if (illegalParts != null)
            {
                vesselValid = false;
                issues.Add(illegalParts);
            }

            if (issues.Count > 0)
                validationMsg = string.Join("\n", issues);
        }

        public void Draw()
        {
            if (!IsVisible) return;

            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = GUILayout.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(500));
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            windowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.1f, 0.14f, 0.97f)) },
                padding = new RectOffset(12, 12, 10, 10)
            };

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.3f, 0.7f, 0.9f) }
            };

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 0.9f)) },
                padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(0, 0, 2, 2)
            };

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
            checkboxStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 11, normal = { textColor = new Color(0.65f, 0.7f, 0.75f) } };
            valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };

            errorStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, wordWrap = true,
                normal = { textColor = new Color(0.9f, 0.35f, 0.3f) }
            };

            successStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, wordWrap = true,
                normal = { textColor = new Color(0.3f, 0.9f, 0.4f) }
            };

            submitBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14, fontStyle = FontStyle.Bold, fixedHeight = 38,
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.55f, 0.3f, 0.9f)), textColor = Color.white },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.65f, 0.4f, 0.9f)), textColor = Color.white }
            };

            cancelBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11, fixedHeight = 26,
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.5f, 0.2f, 0.2f, 0.9f)), textColor = Color.white }
            };

            stylesReady = true;
        }

        private void DrawContent(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(windowStyle);

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("📤 Submit Contract", headerStyle);
            if (GUILayout.Button("✕", GUILayout.Width(25), GUILayout.Height(25)))
                IsVisible = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(3);
            GUILayout.Label($"Mission: {ContractMission}", valueStyle);

            // Mission type badge
            GUILayout.BeginHorizontal();
            string typeBadge = missionType == "craft_build" ? "🏗️ CRAFT BUILD" : "🚀 ACTIVE VESSEL";
            GUILayout.Label(typeBadge, new GUIStyle(labelStyle) { fontStyle = FontStyle.Bold,
                normal = { textColor = missionType == "craft_build" ? new Color(0.9f, 0.7f, 0.2f) : new Color(0.3f, 0.8f, 0.9f) } });
            if (!string.IsNullOrEmpty(requiredBody))
                GUILayout.Label($"📍 {requiredBody}", labelStyle);
            if (!string.IsNullOrEmpty(requiredSituation))
                GUILayout.Label($"📋 {requiredSituation}", labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(380));

            if (!sceneValid)
            {
                // Wrong scene — show error
                DrawWrongSceneMessage();
            }
            else if (missionType == "craft_build")
            {
                DrawEditorMode();
            }
            else
            {
                DrawFlightMode();
            }

            GUILayout.EndScrollView();

            // Modlist Toggle
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            includeModlist = GUILayout.Toggle(includeModlist, " Attach my active Modlist", checkboxStyle);
            GUILayout.EndHorizontal();

            // Status
            if (!string.IsNullOrEmpty(statusMsg))
            {
                var sStyle = statusMsg.StartsWith("✅") ? successStyle : errorStyle;
                GUILayout.Label(statusMsg, sStyle);
            }

            // Bottom buttons
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", cancelBtnStyle, GUILayout.Width(80)))
                IsVisible = false;
            GUILayout.FlexibleSpace();

            if (sceneValid)
            {
                GUI.enabled = !isSubmitting && CanSubmit();
                if (GUILayout.Button(isSubmitting ? "Submitting..." : "📤 Submit", submitBtnStyle, GUILayout.Width(150)))
                    DoSubmit();
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        // ── Wrong Scene ─────────────────────────────────────────────────────

        private void DrawWrongSceneMessage()
        {
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("⚠ Wrong Location", valueStyle);
            GUILayout.Space(5);
            GUILayout.Label(validationMsg, errorStyle);
            GUILayout.Space(10);

            if (missionType == "craft_build")
            {
                GUILayout.Label(
                    "Craft build missions require you to:\n" +
                    "• Open the craft in VAB or SPH\n" +
                    "• The loaded craft will be submitted automatically\n" +
                    "• KVV will capture a vessel render (if installed)",
                    labelStyle
                );
            }
            else
            {
                GUILayout.Label(
                    "Active vessel missions require you to:\n" +
                    "• Have the vessel in flight\n" +
                    (string.IsNullOrEmpty(requiredBody) ? "" : $"• Be at/around {requiredBody}\n") +
                    (string.IsNullOrEmpty(requiredSituation) ? "" : $"• Vessel status: {requiredSituation}\n") +
                    "• Vessel telemetry will be captured automatically",
                    labelStyle
                );
            }
            GUILayout.EndVertical();
        }

        // ── Editor Mode (craft_build) ───────────────────────────────────────

        private void CaptureEditorCraft()
        {
            editorCraftName = "";
            editorCraftPath = "";
            editorPartCount = 0;
            editorCraftMass = 0;
            editorCraftCost = 0;

            // Re-scan part legality from scratch each capture. This method is also called
            // standalone via the "Refresh Craft Data" button (not just through Validate),
            // so reset validity here or a fixed craft would stay flagged.
            if (allowedMods != null)
            {
                vesselValid = true;
                if (validationMsg.StartsWith("❌ Illegal part")) validationMsg = "";
            }

            try
            {
                var ship = EditorLogic.fetch?.ship;
                if (ship != null)
                {
                    editorCraftName = ship.shipName ?? "Untitled";
                    editorPartCount = ship.parts?.Count ?? 0;

                    if (ship.parts != null)
                    {
                        foreach (var part in ship.parts)
                        {
                            editorCraftMass += part.mass + part.GetResourceMass();
                            editorCraftCost += part.partInfo?.cost ?? 0f;
                        }

                        // List every part that violates the contract's restriction.
                        string illegalParts = FindIllegalParts(ship.parts);
                        if (illegalParts != null)
                        {
                            vesselValid = false;
                            validationMsg = illegalParts;
                        }
                    }

                    editorCraftType = EditorDriver.editorFacility == EditorFacility.VAB ? "VAB" : "SPH";

                    string saveFolder = HighLogic.SaveFolder ?? "default";
                    string shipDir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder,
                        "Ships", editorCraftType);
                    string craftFile = Path.Combine(shipDir, editorCraftName + ".craft");

                    if (File.Exists(craftFile))
                        editorCraftPath = craftFile;
                    else
                    {
                        string rootDir = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", editorCraftType);
                        craftFile = Path.Combine(rootDir, editorCraftName + ".craft");
                        if (File.Exists(craftFile))
                            editorCraftPath = craftFile;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Error reading editor craft: {ex.Message}");
            }
        }

        private void DrawEditorMode()
        {
            if (string.IsNullOrEmpty(editorCraftName))
            {
                GUILayout.Label("❌ No craft loaded in the editor.", valueStyle);
                GUILayout.Label("Open a craft in VAB or SPH first.", labelStyle);
                return;
            }

            GUILayout.Label($"Currently Loaded Craft [{editorCraftType}]", headerStyle);
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label($"🚀 {editorCraftName}", valueStyle);
            GUILayout.Label($"Parts: {editorPartCount}  ·  Mass: {editorCraftMass:F1}t  ·  Cost: {editorCraftCost:N0}", labelStyle);

            if (!string.IsNullOrEmpty(editorCraftPath))
                GUILayout.Label($"✅ Craft file ready", successStyle);
            else
                GUILayout.Label($"⚠ Save your craft first!", errorStyle);
            GUILayout.EndVertical();

            // Explain a greyed-out Submit: list the parts that break the restriction.
            if (!vesselValid && !string.IsNullOrEmpty(validationMsg))
            {
                GUILayout.Space(5);
                GUILayout.BeginVertical(boxStyle);
                GUILayout.Label(validationMsg, errorStyle);
                GUILayout.EndVertical();
            }

            GUILayout.Space(5);
            if (GUILayout.Button("🔄 Refresh Craft Data", GUILayout.Height(26)))
                CaptureEditorCraft();

            GUILayout.Space(10);
            DrawScreenshotSection();
        }

        // ── Flight Mode (active_vessel) ─────────────────────────────────────

        private void CaptureFlightData()
        {
            activeVessel = VesselDataCollector.CaptureActiveVessel();
            nearbyVessels = VesselDataCollector.CaptureLoadedVessels();
        }

        private void DrawFlightMode()
        {
            if (activeVessel == null)
            {
                GUILayout.Label("❌ No active vessel detected.", valueStyle);
                return;
            }

            // Validation status
            if (!vesselValid && !string.IsNullOrEmpty(validationMsg))
            {
                GUILayout.BeginVertical(boxStyle);
                GUILayout.Label("⚠ Vessel State Mismatch", valueStyle);
                GUILayout.Label(validationMsg, errorStyle);
                GUILayout.EndVertical();
                GUILayout.Space(5);
            }
            else if (vesselValid)
            {
                GUILayout.Label("✅ Vessel state matches requirements", successStyle);
                GUILayout.Space(3);
            }

            // Active vessel info
            GUILayout.Label("Active Vessel", headerStyle);
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label($"🚀 {activeVessel.vesselName}", valueStyle);
            GUILayout.Label($"📍 {activeVessel.body} — {activeVessel.situation}", labelStyle);
            GUILayout.Label($"Alt: {activeVessel.altitude:N0}m  ·  Parts: {activeVessel.partCount}  ·  Mass: {activeVessel.totalMass:F1}t", labelStyle);
            if (activeVessel.crewCount > 0)
                GUILayout.Label($"👨‍🚀 Crew: {activeVessel.crewCount}", labelStyle);
            if (activeVessel.sma > 0)
                GUILayout.Label($"Orbit: SMA={activeVessel.sma:N0}m  e={activeVessel.eccentricity:F3}  i={activeVessel.inclination:F1}°", labelStyle);
            GUILayout.EndVertical();

            GUILayout.Space(5);
            if (GUILayout.Button("🔄 Refresh Data", GUILayout.Height(26)))
            {
                CaptureFlightData();
                ValidateVesselState();
            }

            GUILayout.Space(10);
            DrawScreenshotSection();
        }

        // ── Screenshot ──────────────────────────────────────────────────────

        private void DrawScreenshotSection()
        {
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("📸 Vessel Render", valueStyle);
            GUILayout.Label("Orthographic vessel render will be captured automatically.", labelStyle);

            if (screenshotTaken)
            {
                GUILayout.Label($"✅ Captured: {Path.GetFileName(screenshotPath)}", successStyle);
                if (GUILayout.Button("🔄 Retake Render", GUILayout.Height(26)))
                    TakeScreenshot();
            }
            else
            {
                if (GUILayout.Button("📸 Capture Vessel Render", GUILayout.Height(30)))
                    TakeScreenshot();
            }
            GUILayout.EndVertical();
        }

        private void TakeScreenshot()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            screenshotPath = KVVIntegration.CaptureWithFallback(vessel);
            screenshotTaken = true;
            statusMsg = "📸 Captured! (may take a moment to save)";
        }

        // ── Submission ──────────────────────────────────────────────────────

        private bool CanSubmit()
        {
            if (!sceneValid) return false;
            if (!screenshotTaken) return false;

            if (missionType == "craft_build")
                // vesselValid carries the part-legality result from CaptureEditorCraft();
                // without it an illegal part (e.g. a Squad expansion part on a restricted
                // contract) would pass the gate and submit anyway.
                return !string.IsNullOrEmpty(editorCraftPath) && vesselValid;

            // active_vessel: need vessel data AND matching state
            return activeVessel != null && vesselValid;
        }

        private void DoSubmit()
        {
            isSubmitting = true;
            statusMsg = "Submitting...";
            GeneKermanMod.Instance.RunCoroutine(SubmitCoroutine());
        }

        private IEnumerator SubmitCoroutine()
        {
            yield return new WaitForSeconds(1.5f);

            byte[] craftData = null;
            string craftName = null;
            string loadmeta = null;
            string vesselDataJson = null;
            string vesselNodeData = null;

            if (missionType == "craft_build" && !string.IsNullOrEmpty(editorCraftPath))
            {
                craftData = VesselDataCollector.ReadCraftFile(editorCraftPath);
                craftName = Path.GetFileName(editorCraftPath);
                // No loadmeta or vessel node for craft_build
            }
            else if (missionType == "active_vessel" && activeVessel != null)
            {
                // Active vessel: send craft file + loadmeta + telemetry + full vessel state
                var submission = new Dictionary<string, object>
                {
                    { "contract_id", ContractId },
                    { "active_vessel", activeVessel.ToDict() },
                };

                if (nearbyVessels != null)
                {
                    var nearbyList = new List<object>();
                    foreach (var v in nearbyVessels)
                        nearbyList.Add(v.ToDict());
                    submission["nearby_vessels"] = nearbyList;
                }

                vesselDataJson = MiniJSON.Serialize(submission);

                // Also try to get the craft file for the active vessel
                string craftPath = VesselDataCollector.FindCraftFile(activeVessel.vesselName);
                if (!string.IsNullOrEmpty(craftPath))
                {
                    craftData = VesselDataCollector.ReadCraftFile(craftPath);
                    craftName = Path.GetFileName(craftPath);
                    loadmeta = VesselDataCollector.ReadLoadmeta(craftPath);
                }

                // Export the full vessel state for transfer
                vesselNodeData = VesselTransfer.ExportActiveVessel();
            }

            // Read screenshot
            var screenshots = new List<byte[]>();
            var ssNames = new List<string>();

            if (screenshotTaken && !string.IsNullOrEmpty(screenshotPath))
            {
                byte[] ssData = VesselDataCollector.ReadScreenshot(screenshotPath);
                if (ssData != null)
                {
                    screenshots.Add(ssData);
                    ssNames.Add(Path.GetFileName(screenshotPath));
                }
            }

            string modlist = null;
            if (includeModlist)
            {
                var folders = new HashSet<string>();
                foreach (var p in PartLoader.LoadedPartsList)
                {
                    if (!string.IsNullOrEmpty(p.partUrl))
                        folders.Add(p.partUrl.Split('/')[0]);
                }
                modlist = string.Join(",", folders.ToArray());
            }

            // Mod folders actually used by this craft, so the server can re-check the
            // submission against the contract's part restriction independently of the
            // client-side gate.
            string usedModlist = CollectUsedModFolders();

            yield return GeneKermanMod.Instance.Api.SubmitContract(
                ContractId, craftData, craftName, loadmeta, vesselDataJson,
                vesselNodeData, screenshots, ssNames, modlist, usedModlist,
                (ok, resp, status) =>
                {
                    isSubmitting = false;
                    if (ok && !string.IsNullOrEmpty(resp))
                    {
                        var result = MiniJSON.DeserializeDict(resp);
                        string reviewStatus = MiniJSON.GetString(result, "review_status", "");
                        string message = MiniJSON.GetString(result, "message", "Submitted!");

                        if (reviewStatus == "approved")
                        {
                            int xp = MiniJSON.GetInt(result, "xp_awarded");
                            int coins = MiniJSON.GetInt(result, "coins_awarded");
                            statusMsg = $"✅ {message} +{coins} KCoins, +{xp} XP";
                            GeneKermanMod.Instance.ShowNotification("✅ Mission Approved!", $"+{coins} KCoins, +{xp} XP");
                            EditorPartEnforcer.Instance?.StopEnforcing();
                        }
                        else if (reviewStatus == "refused")
                        {
                            statusMsg = $"❌ Refused: {MiniJSON.GetString(result, "reason", "")}";
                        }
                        else
                        {
                            // Submitted and awaiting review — clear enforcer so VAB is unlocked
                            statusMsg = $"✅ {message}";
                            EditorPartEnforcer.Instance?.StopEnforcing();
                        }
                    }
                    else
                    {
                        statusMsg = "❌ Submission failed. Check connection and try again.";
                    }
                }
            );
        }


        private string GetModFolder(string partUrl)
        {
            if (string.IsNullOrEmpty(partUrl)) return null;
            string[] parts = partUrl.Split('/');
            if (parts.Length > 0) return parts[0];
            return null;
        }

        // Builds a user-facing list of every part that violates the contract's part
        // restriction, so the player knows exactly why Submit is greyed out. Returns
        // null when there's no restriction or all parts are allowed.
        private string FindIllegalParts(System.Collections.Generic.IEnumerable<Part> parts)
        {
            if (allowedMods == null || parts == null) return null;

            var bad = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                var info = part?.partInfo;
                if (info == null || IsPartAllowed(info.partUrl)) continue;
                string label = $"• {info.title} (from {GetModFolder(info.partUrl)})";
                if (seen.Add(label)) bad.Add(label);
            }

            if (bad.Count == 0) return null;
            return "❌ Illegal parts for this contract:\n" + string.Join("\n", bad.ToArray());
        }

        // Distinct top-level mod folders used by the craft being submitted (editor ship
        // for craft builds, active vessel otherwise). Sent to the server for validation.
        private string CollectUsedModFolders()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            System.Collections.Generic.IEnumerable<Part> parts = null;
            if (missionType == "craft_build")
                parts = EditorLogic.fetch?.ship?.parts;
            else
                parts = FlightGlobals.ActiveVessel?.parts;

            if (parts == null) return null;

            foreach (var part in parts)
            {
                string url = part?.partInfo?.partUrl;
                if (!string.IsNullOrEmpty(url))
                    folders.Add(url.Split('/')[0]);
            }

            return folders.Count > 0 ? string.Join(",", folders.ToArray()) : null;
        }

        private bool IsPartAllowed(string partUrl)
        {
            if (allowedMods == null) return true;
            string folder = GetModFolder(partUrl);
            if (string.IsNullOrEmpty(folder)) return true;
            if (!allowedMods.Contains(folder)) return false;
            if (excludePaths != null)
                foreach (var excl in excludePaths)
                    if ((partUrl ?? "").StartsWith(excl, StringComparison.OrdinalIgnoreCase))
                        return false;
            return true;
        }
    }
}
