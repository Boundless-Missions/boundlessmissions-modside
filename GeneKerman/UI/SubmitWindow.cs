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
        private string missionType = "active_vessel";  // "craft_build", "active_vessel", or "rescue"
        private string requiredSituation = "";           // "ORBITING", "LANDED", etc.
        private string requiredBody = "";                // "Mun", "Duna", etc.

        // Rescue mission data
        private RescueTargetSpec rescueTarget;
        private List<string> rescueKerbals;
        private ContractConstraints partLimits; // mission part-usage limits (may be null)
        private bool IsRescue { get { return missionType == "rescue"; } }

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

        // Extra crafts in physics range that can be packed into the submission.
        private class NearbyEntry
        {
            public Vessel vessel;
            public VesselDataCollector.VesselSnapshot snap;
            public bool selected;
            public double distance;   // metres from the active vessel
        }
        private List<NearbyEntry> nearbyEntries = new List<NearbyEntry>();
        private Vector2 nearbyScroll;
        private string rangeFilterKm = "2.5";

        // Physics Range Extender lifecycle: we pause PRE while the submit window is
        // open (so far craft unload and the in-range list is the stock bubble), then
        // restore it on close. preDisabledByUs is true only when we actually paused it.
        private bool preDisabledByUs;
        private bool physicsStabilizing;   // waiting for far vessels to unload

        // Submission
        private bool isSubmitting;
        private List<string> screenshotPaths = new List<string>();
        private bool screenshotTaken;

        // Render freshness. A render is a picture of a craft at one moment; the craft
        // can be changed afterwards and the submission would then carry an image of
        // something that was never sent. renderFingerprint is the structural signature
        // of everything the renders show, taken at capture time; renderStale is set
        // when the live craft no longer matches it, which blocks Submit until the
        // player retakes them. nextStaleCheck throttles the comparison — it walks the
        // part list, and OnGUI runs several times a frame.
        private int renderFingerprint;
        private bool renderStale;
        private float nextStaleCheck;

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
            string type = "active_vessel", string situation = "", string body = "", string modlist = "",
            RescueTargetSpec rescueTargetSpec = null, List<string> rescueKerbalNames = null,
            ContractConstraints constraints = null)
        {
            ContractId = contractId;
            ContractMission = mission;
            missionType = type ?? "active_vessel";
            requiredSituation = situation ?? "";
            requiredBody = body ?? "";
            rescueTarget = rescueTargetSpec;
            rescueKerbals = rescueKerbalNames;
            partLimits = (constraints != null && !constraints.IsEmpty) ? constraints : null;
            // For rescue, derive the body the rescuer must reach from the target.
            if (IsRescue && rescueTarget != null && string.IsNullOrEmpty(requiredBody))
                requiredBody = rescueTarget.body;
            IsVisible = true;
            isSubmitting = false;
            statusMsg = "";
            screenshotTaken = false;
            renderStale = false;
            renderFingerprint = 0;
            nextStaleCheck = 0f;

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
                    // Pause PRE (if any), let far craft unload, then capture the
                    // active vessel + the stock-range neighbours offered as extras.
                    GeneKermanMod.Instance.RunCoroutine(PreparePhysicsThenCapture());
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

            // Orbit-type requirement (polar/equatorial/keostationary/…): the craft must
            // be in the specific orbit the mission names. The server re-checks this
            // authoritatively from the submitted telemetry.
            if (partLimits?.Orbit != null && !partLimits.Orbit.IsEmpty)
            {
                foreach (var v in partLimits.Orbit.CheckOrbit(activeVessel))
                {
                    vesselValid = false;
                    issues.Add(v);
                }
            }

            // Rescue: all stranded kerbals aboard + correct orbit/surface within margins.
            if (IsRescue)
                ValidateRescue(issues);

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

        /// <summary>
        /// Every orbit requirement on this contract in one line, or "" when it names no
        /// particular orbit. Two sources, because there are two ways to ask: the regime
        /// parsed out of the mission text (any contract), and the plane/regime an issuer
        /// picked for a rescue target.
        /// </summary>
        private string DescribeOrbitRequirement()
        {
            var bits = new List<string>();
            if (partLimits != null && partLimits.Orbit != null && !partLimits.Orbit.IsEmpty)
                bits.Add(partLimits.Orbit.LabelList());
            if (IsRescue && rescueTarget != null &&
                (rescueTarget.mode ?? "orbit").ToLower() != "surface")
            {
                string t = rescueTarget.DescribeOrbitRequirement();
                if (!string.IsNullOrEmpty(t)) bits.Add(t);
            }
            return string.Join(" · ", bits.ToArray());
        }

        /// <summary>
        /// Rescue checks: the active vessel must carry every stranded kerbal, be at the
        /// target orbit (Ap/Pe within margin) or surface spot (Lat/Lon within margin),
        /// and — on a "vessel" recovery — have the wreck itself aboard. A Δv floor, when
        /// the issuer set one, applies to either mode. Appends failures to
        /// <paramref name="issues"/>.
        /// </summary>
        private void ValidateRescue(List<string> issues)
        {
            // All stranded kerbals aboard? Required in both modes — bringing the hull
            // home without its crew is not a rescue.
            if (rescueKerbals != null && rescueKerbals.Count > 0)
            {
                var aboard = new HashSet<string>(StringComparer.Ordinal);
                var vessel = FlightGlobals.ActiveVessel;
                if (vessel != null)
                    foreach (var pcm in VesselTransfer.CrewOf(vessel))
                        if (pcm != null) aboard.Add(pcm.name);

                var missing = new List<string>();
                foreach (var k in rescueKerbals)
                    if (!aboard.Contains(k)) missing.Add(k);

                if (missing.Count > 0)
                {
                    vesselValid = false;
                    issues.Add($"❌ Missing kerbals ({missing.Count}): {string.Join(", ", missing.ToArray())}");
                }
            }

            if (rescueTarget == null) return;

            ValidateWreckAboard(issues);
            ValidateRescueDeltaV(issues);

            bool surface = (rescueTarget.mode ?? "orbit").ToLower() == "surface";
            if (surface)
            {
                if (!(activeVessel.situation == "LANDED" || activeVessel.situation == "SPLASHED"))
                {
                    vesselValid = false;
                    issues.Add($"❌ Must be landed at {rescueTarget.body} (currently {activeVessel.situation}).");
                }
                else
                {
                    double dLat = Math.Abs(activeVessel.latitude - rescueTarget.lat);
                    double dLon = Math.Abs(activeVessel.longitude - rescueTarget.lon);
                    if (dLon > 180) dLon = 360 - dLon; // wrap
                    double margin = Math.Max(rescueTarget.marginPos, 0.01);
                    if (dLat > margin || dLon > margin)
                    {
                        vesselValid = false;
                        issues.Add($"❌ Off target: at {activeVessel.latitude:F2}°,{activeVessel.longitude:F2}°, " +
                                   $"need {rescueTarget.lat:F2}°,{rescueTarget.lon:F2}° (±{margin:F2}°).");
                    }
                }
            }
            else
            {
                if (activeVessel.situation != "ORBITING")
                {
                    vesselValid = false;
                    issues.Add($"❌ Must be orbiting {rescueTarget.body} (currently {activeVessel.situation}).");
                }
                else
                {
                    double margin = Math.Max(rescueTarget.marginAlt, 1.0); // metres
                    double dAp = Math.Abs(activeVessel.apoapsis - rescueTarget.ap);
                    double dPe = Math.Abs(activeVessel.periapsis - rescueTarget.pe);
                    if (dAp > margin || dPe > margin)
                    {
                        vesselValid = false;
                        issues.Add($"❌ Orbit off target: Ap {activeVessel.apoapsis / 1000:F0}km / " +
                                   $"Pe {activeVessel.periapsis / 1000:F0}km, need " +
                                   $"Ap {rescueTarget.ap / 1000:F0}km / Pe {rescueTarget.pe / 1000:F0}km (±{margin / 1000:F0}km).");
                    }

                    // Plane and regime, when the issuer asked for them. Ap/Pe are the
                    // cheap half of a rendezvous — the plane is the half that costs
                    // delta-v, so a rescue that names one is a materially different job.
                    string plane = OrbitConstraint.CheckInclination(
                        rescueTarget.incl, rescueTarget.marginIncl, activeVessel.inclination);
                    if (plane != null)
                    {
                        vesselValid = false;
                        issues.Add("❌ " + plane);
                    }
                    foreach (var v in rescueTarget.OrbitTypeConstraint().CheckOrbit(activeVessel))
                    {
                        vesselValid = false;
                        issues.Add(v);
                    }
                }
            }
        }

        /// <summary>"Vessel" recovery only: the wreck itself has to have made it here.
        ///
        /// Matched by part flightID, which KSP assigns once and then preserves through
        /// export, import, docking and undocking — so the wreck is recognisable whether
        /// it was towed, docked into the rescue ship, or refuelled and flown home under
        /// its own power. A share of the parts is enough (see WreckCoverageRequired):
        /// demanding all of them would fail a tow that shed an antenna.</summary>
        private void ValidateWreckAboard(List<string> issues)
        {
            if (!rescueTarget.RequiresWreck) return;

            // No part list means the server had nothing to give us — an older contract,
            // or a wreck node it couldn't read. Don't invent a failure the player has no
            // way to fix; the server re-checks this authoritatively on submission.
            if (rescueTarget.wreckParts == null || rescueTarget.wreckParts.Count == 0) return;

            var here = new HashSet<uint>();
            var parts = FlightGlobals.ActiveVessel?.parts;
            if (parts != null)
                foreach (Part p in parts)
                    if (p != null) here.Add(p.flightID);

            int found = 0;
            foreach (uint id in rescueTarget.wreckParts)
                if (here.Contains(id)) found++;

            int needed = (int)Math.Ceiling(rescueTarget.wreckParts.Count * ContractCreation.WreckCoverageRequired);
            if (needed < 1) needed = 1;
            if (found >= needed) return;

            vesselValid = false;
            issues.Add($"❌ The stranded vessel isn't here: {found} of {rescueTarget.wreckParts.Count} " +
                       $"of its parts aboard, need at least {needed}. This contract wants the wreck " +
                       $"brought home, not just its crew.");
        }

        /// <summary>Δv floor on whatever is carrying the crew, so they're left somewhere
        /// they can leave from. Skipped when the issuer set none, or when the stock Δv
        /// readout can't give us a number (CraftDeltaV returns -1) — failing on a value
        /// we couldn't read would block a valid submission.</summary>
        private void ValidateRescueDeltaV(List<string> issues)
        {
            if (rescueTarget.minDv <= 0) return;

            double dv = CraftDeltaV.TotalVacuum();
            if (dv < 0)
            {
                issues.Add($"⚠ Δv unreadable. This rescue needs ≥{rescueTarget.minDv:F0} m/s left. " +
                           "Turn on the stock Δv readout to check it here; the server checks it either way.");
                return;
            }

            // Same 0.5% slack the mission-limit Δv check allows, so a craft sitting right
            // on the number isn't failed by rounding.
            if (dv >= rescueTarget.minDv * 0.995) return;

            vesselValid = false;
            issues.Add($"❌ Not enough Δv left: {dv:F0} m/s, need ≥{rescueTarget.minDv:F0} m/s " +
                       "so the crew can get home from here.");
        }

        public void Draw()
        {
            if (!IsVisible) return;

            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
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

            // Layout pass only — see RefreshRenderStale.
            if (Event.current.type == EventType.Layout)
                RefreshRenderStale();

            GUILayout.BeginVertical(windowStyle);

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("📤 Submit Contract", headerStyle);
            if (GUILayout.Button("✕", GUILayout.Width(25), GUILayout.Height(25)))
                CloseWindow();
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
                CloseWindow();
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

                    // Auto-save the editor craft to disk so the .craft file we look
                    // up below is current — the player no longer has to save manually
                    // before submitting.
                    if (editorPartCount > 0)
                    {
                        try { ShipConstruction.SaveShip(editorCraftName); }
                        catch (Exception saveEx)
                        {
                            Debug.LogWarning($"[GeneKerman] Could not auto-save craft: {saveEx.Message}");
                        }
                    }

                    if (ship.parts != null)
                    {
                        foreach (var part in ship.parts)
                        {
                            editorCraftMass += part.mass + part.GetResourceMass();
                            // Full funds cost (dry + module modifiers incl. TweakScale + fuel).
                            editorCraftCost += VesselDataCollector.GetPartCost(part);
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
            BuildNearbyEntries();
        }

        /// <summary>Pause PRE, wait for out-of-range craft to unload, then capture. Keeps
        /// the "Stabilizing physics range…" notice up while the bubble collapses so the
        /// neighbour list reflects the stock range, not PRE's inflated one.</summary>
        private IEnumerator PreparePhysicsThenCapture()
        {
            physicsStabilizing = true;
            statusMsg = "Stabilizing physics range...";

            // Rescue is strictly single-vessel — leave PRE alone for it.
            preDisabledByUs = !IsRescue && PhysicsRangeManager.TryDisable();
            if (preDisabledByUs)
            {
                // Give KSP a few frames + a beat to drop now-out-of-range vessels.
                for (int i = 0; i < 5; i++) yield return new WaitForEndOfFrame();
                yield return new WaitForSeconds(0.5f);
            }

            CaptureFlightData();
            ValidateVesselState();

            physicsStabilizing = false;
            if (statusMsg == "Stabilizing physics range...") statusMsg = "";
        }

        /// <summary>(Re)build the in-range extra-craft list, preserving the player's
        /// existing on/off choices for vessels that are still loaded.</summary>
        private void BuildNearbyEntries()
        {
            var prevSelected = new Dictionary<Guid, bool>();
            if (nearbyEntries != null)
                foreach (var e in nearbyEntries)
                    if (e.vessel != null) prevSelected[e.vessel.id] = e.selected;

            nearbyEntries = new List<NearbyEntry>();

            var active = FlightGlobals.ActiveVessel;
            if (active == null) return;
            Vector3d aPos = active.GetWorldPos3D();

            foreach (var v in VesselDataCollector.GetNearbyVessels(active))
            {
                var entry = new NearbyEntry
                {
                    vessel = v,
                    snap = VesselDataCollector.CaptureVessel(v),
                    distance = Vector3d.Distance(aPos, v.GetWorldPos3D()),
                };
                bool wasSelected;
                if (prevSelected.TryGetValue(v.id, out wasSelected)) entry.selected = wasSelected;
                nearbyEntries.Add(entry);
            }

            nearbyEntries.Sort((a, b) => a.distance.CompareTo(b.distance));
        }

        private void SetAllSelected(bool value)
        {
            if (nearbyEntries == null) return;
            foreach (var e in nearbyEntries) e.selected = value;
        }

        private void SelectWithinRange()
        {
            double km;
            if (!double.TryParse(rangeFilterKm, out km)) return;
            double metres = km * 1000.0;
            foreach (var e in nearbyEntries) e.selected = e.distance <= metres;
        }

        private static string FormatDistance(double metres)
        {
            return metres >= 1000.0 ? $"{metres / 1000.0:F2} km" : $"{metres:F0} m";
        }

        private void DrawFlightMode()
        {
            if (physicsStabilizing)
            {
                GUILayout.BeginVertical(boxStyle);
                GUILayout.Label("⏳ Stabilizing physics range…", valueStyle);
                GUILayout.Label("Pausing Physics Range Extender and letting distant craft unload.", labelStyle);
                GUILayout.EndVertical();
                return;
            }

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

            // The orbit this contract asks for, drawn whether or not the craft is in it.
            // A requirement only ever printed as a failure is one the player meets by
            // accident or not at all — and the vessel readout below prints the live
            // inclination/eccentricity right underneath, so the two can be compared.
            string orbitReq = DescribeOrbitRequirement();
            if (!string.IsNullOrEmpty(orbitReq))
            {
                GUILayout.Label("🛰 Required orbit: " + orbitReq, labelStyle);
                GUILayout.Space(3);
            }

            // Active vessel info
            GUILayout.Label("Active Vessel", headerStyle);
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label($"🚀 {activeVessel.vesselName}", valueStyle);
            GUILayout.Label($"📍 {activeVessel.body} · {activeVessel.situation}", labelStyle);
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

            // Multi-craft sending is for ordinary active-vessel contracts only.
            if (!IsRescue)
                DrawNearbySection();

            GUILayout.Space(10);
            DrawScreenshotSection();
        }

        // ── Extra Crafts (multi-vessel submission) ──────────────────────────

        private void DrawNearbySection()
        {
            GUILayout.Space(8);
            GUILayout.Label("Send Extra Crafts in Range", headerStyle);
            GUILayout.BeginVertical(boxStyle);

            if (preDisabledByUs)
                GUILayout.Label("ℹ️ Physics Range Extender paused; showing stock-range craft.", labelStyle);

            if (nearbyEntries == null || nearbyEntries.Count == 0)
            {
                GUILayout.Label("No other vessels in physics range.", labelStyle);
                if (GUILayout.Button("🔄 Rescan Range", GUILayout.Height(24)))
                    CaptureFlightData();
                GUILayout.EndVertical();
                return;
            }

            int selCount = 0;
            foreach (var e in nearbyEntries) if (e.selected) selCount++;
            GUILayout.Label($"{selCount}/{nearbyEntries.Count} selected, packed and sent with this submission.", labelStyle);

            // Batch selectors
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All", GUILayout.Height(22))) SetAllSelected(true);
            if (GUILayout.Button("Select None", GUILayout.Height(22))) SetAllSelected(false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("In range ≤", labelStyle, GUILayout.Width(62));
            rangeFilterKm = GUILayout.TextField(rangeFilterKm ?? "", GUILayout.Width(50));
            GUILayout.Label("km", labelStyle, GUILayout.Width(22));
            if (GUILayout.Button("Apply", GUILayout.Height(22), GUILayout.Width(60))) SelectWithinRange();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            nearbyScroll = GUILayout.BeginScrollView(nearbyScroll, GUILayout.Height(130));
            foreach (var e in nearbyEntries)
            {
                e.selected = GUILayout.Toggle(e.selected,
                    $"{e.snap.vesselName}  ·  {FormatDistance(e.distance)}", checkboxStyle);
                GUILayout.Label(
                    $"      {e.snap.vesselType} · {e.snap.body} {e.snap.situation} · " +
                    $"{e.snap.partCount} parts · {e.snap.crewCount} crew", labelStyle);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            if (GUILayout.Button("🔄 Rescan Range", GUILayout.Height(24)))
                CaptureFlightData();

            GUILayout.EndVertical();
        }

        // ── Screenshot ──────────────────────────────────────────────────────

        private void DrawScreenshotSection()
        {
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("📸 Vessel Render", valueStyle);

            int extras = CountSelectedExtras();
            if (extras > 0)
                GUILayout.Label($"Renders the active craft + {extras} selected extra(s).", labelStyle);
            else
                GUILayout.Label("Orthographic vessel render will be captured automatically.", labelStyle);

            if (screenshotTaken && renderStale)
            {
                GUILayout.Label("⚠ The craft changed after these renders were taken.", errorStyle);
                GUILayout.Label(
                    HighLogic.LoadedSceneIsEditor
                        ? "Renders must show the craft you submit. Retake them to continue."
                        : "The vessel is no longer the one in these renders. Retake them to continue.",
                    labelStyle);
                if (GUILayout.Button("📸 Retake Renders", GUILayout.Height(30)))
                    TakeScreenshot();
            }
            else if (screenshotTaken)
            {
                GUILayout.Label($"✅ Captured {screenshotPaths.Count} render(s)", successStyle);
                if (GUILayout.Button("🔄 Retake Renders", GUILayout.Height(26)))
                    TakeScreenshot();
            }
            else
            {
                if (GUILayout.Button("📸 Capture Vessel Renders", GUILayout.Height(30)))
                    TakeScreenshot();
            }
            GUILayout.EndVertical();
        }

        /// <summary>Capture an orthographic render of the active (contract) craft, plus
        /// one for each selected extra craft so every submitted vessel has a blueprint
        /// image. Renders run synchronously — a deliberate one-tap action.</summary>
        private void TakeScreenshot()
        {
            // Re-capture before rendering, so the picture and the payload are taken from
            // the same craft. In the editor this re-saves the .craft to disk, which is
            // the file SubmitCoroutine uploads — without it a retake would produce a
            // fresh render of a craft still described on disk by a stale file.
            if (missionType == "craft_build")
            {
                CaptureEditorCraft();
            }
            else if (HighLogic.LoadedSceneIsFlight)
            {
                CaptureFlightData();
                ValidateVesselState();
            }

            screenshotPaths = new List<string>();

            Vessel active = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            string activePath = KVVIntegration.CaptureWithFallback(active);
            if (!string.IsNullOrEmpty(activePath)) screenshotPaths.Add(activePath);

            if (!IsRescue && nearbyEntries != null)
            {
                foreach (var e in nearbyEntries)
                {
                    if (!e.selected || e.vessel == null) continue;
                    string ep = KVVIntegration.CaptureWithFallback(e.vessel);
                    if (!string.IsNullOrEmpty(ep)) screenshotPaths.Add(ep);
                }
            }

            screenshotTaken = screenshotPaths.Count > 0;

            // Pin what these renders show, so any later change to the craft is caught.
            renderFingerprint = RenderSubjectFingerprint();
            renderStale = false;
            nextStaleCheck = Time.realtimeSinceStartup + 0.4f;

            statusMsg = screenshotPaths.Count > 1
                ? $"📸 Captured {screenshotPaths.Count} renders! (may take a moment to save)"
                : "📸 Captured! (may take a moment to save)";
        }

        // ── Render freshness ────────────────────────────────────────────────

        /// <summary>
        /// Re-check whether the captured renders still show the craft that would be
        /// submitted. Throttled, and only ever called from the Layout pass — flipping
        /// a flag that changes the window's contents between Layout and Repaint is what
        /// produces GUILayout mismatch errors.
        /// </summary>
        private void RefreshRenderStale()
        {
            if (!screenshotTaken || isSubmitting) return;

            float now = Time.realtimeSinceStartup;
            if (now < nextStaleCheck) return;
            nextStaleCheck = now + 0.4f;

            renderStale = RenderSubjectFingerprint() != renderFingerprint;
        }

        /// <summary>
        /// Signature of everything the renders depict: the craft being submitted plus
        /// each extra craft currently ticked, since TakeScreenshot captures one image
        /// per subject and changing the selection changes the image set.
        /// Summed rather than chained, because the neighbour list is sorted by distance
        /// and drifting craft reorder it without anything having actually changed.
        /// </summary>
        private int RenderSubjectFingerprint()
        {
            unchecked
            {
                int h = PartsFingerprint(GetSubmissionParts());
                if (!IsRescue && nearbyEntries != null)
                {
                    foreach (var e in nearbyEntries)
                    {
                        if (!e.selected || e.vessel == null) continue;
                        h += PartsFingerprint(e.vessel.parts) * 31 + 17;
                    }
                }
                return h;
            }
        }

        /// <summary>
        /// A cheap structural signature of one craft: which parts it has, and — in the
        /// editor only — where they sit and how big they are.
        ///
        /// Layout is deliberately excluded in flight. Nothing can be moved or rescaled
        /// on a flying craft, so the part set alone catches every real change (staging,
        /// docking, undocking, decoupling, a part being destroyed), while joints flex
        /// under physics and a position-sensitive signature would report a wobbling
        /// rocket as modified several times a second. Resource levels are excluded
        /// everywhere: a blueprint does not show them, and in flight they change
        /// continuously, which would make every render stale the moment it was taken.
        /// </summary>
        private static int PartsFingerprint(IEnumerable<Part> parts)
        {
            if (parts == null) return 0;

            bool includeLayout = HighLogic.LoadedSceneIsEditor;

            unchecked
            {
                int hash = 0;
                int count = 0;

                foreach (var p in parts)
                {
                    if (p == null) continue;
                    count++;

                    int h = 17;
                    if (p.partInfo != null && p.partInfo.name != null)
                        h = h * 31 + p.partInfo.name.GetHashCode();
                    // 0 for every part in the editor, unique and stable in flight.
                    h = h * 31 + (int)p.flightID;

                    if (includeLayout && p.transform != null)
                    {
                        Vector3 lp = p.transform.localPosition;
                        h = h * 31 + Mathf.RoundToInt(lp.x * 1000f);
                        h = h * 31 + Mathf.RoundToInt(lp.y * 1000f);
                        h = h * 31 + Mathf.RoundToInt(lp.z * 1000f);

                        Quaternion lr = p.transform.localRotation;
                        h = h * 31 + Mathf.RoundToInt(lr.x * 1000f);
                        h = h * 31 + Mathf.RoundToInt(lr.y * 1000f);
                        h = h * 31 + Mathf.RoundToInt(lr.z * 1000f);
                        h = h * 31 + Mathf.RoundToInt(lr.w * 1000f);

                        Vector3 ls = p.transform.localScale;
                        h = h * 31 + Mathf.RoundToInt(ls.x * 1000f);
                        h = h * 31 + Mathf.RoundToInt(ls.y * 1000f);
                        h = h * 31 + Mathf.RoundToInt(ls.z * 1000f);
                    }

                    hash += h;
                }

                return hash * 31 + count;
            }
        }

        private int CountSelectedExtras()
        {
            if (IsRescue || nearbyEntries == null) return 0;
            int n = 0;
            foreach (var e in nearbyEntries) if (e.selected && e.vessel != null) n++;
            return n;
        }

        // ── Submission ──────────────────────────────────────────────────────

        private bool CanSubmit()
        {
            if (!sceneValid) return false;
            if (!screenshotTaken) return false;
            // Renders that no longer match the craft are worse than none: the issuer
            // would review a picture of something that was never submitted.
            if (renderStale) return false;

            if (missionType == "craft_build")
                // vesselValid carries the part-legality result from CaptureEditorCraft();
                // without it an illegal part (e.g. a Squad expansion part on a restricted
                // contract) would pass the gate and submit anyway.
                return !string.IsNullOrEmpty(editorCraftPath) && vesselValid;

            // active_vessel: need vessel data AND matching state
            return activeVessel != null && vesselValid;
        }

        /// <summary>Hide the window and restore Physics Range Extender if we paused it.
        /// All close paths (✕, Cancel, successful/awaiting submit) route through here so
        /// PRE is never left disabled.</summary>
        private void CloseWindow()
        {
            if (preDisabledByUs)
            {
                PhysicsRangeManager.Reenable();
                preDisabledByUs = false;
            }
            IsVisible = false;
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

            // Last word on render freshness, unthrottled: the throttled Draw-time check
            // can be up to 0.4s behind, and this coroutine then waits another 1.5s
            // before reading anything — ample time to pull a part off.
            if (screenshotTaken && RenderSubjectFingerprint() != renderFingerprint)
            {
                renderStale = true;
                isSubmitting = false;
                statusMsg = "❌ The craft changed after the renders were taken.\n" +
                            "Retake the renders, then submit.";
                yield break;
            }

            // Re-save the live editor ship so the .craft we upload is the craft on
            // screen. The fingerprint above proves the shape still matches the renders,
            // but a tweak a blueprint cannot show — a fuel level, an action group — is
            // only on disk if the file was written after it.
            if (missionType == "craft_build")
            {
                CaptureEditorCraft();
                if (!vesselValid || string.IsNullOrEmpty(editorCraftPath))
                {
                    isSubmitting = false;
                    statusMsg = string.IsNullOrEmpty(validationMsg)
                        ? "❌ Could not read the craft. Save it and try again."
                        : validationMsg;
                    yield break;
                }
            }

            // Craft's vacuum Δv (editor = full-fuel design, flight = current). Read
            // once: used by the gate below and reported to the server for the
            // authoritative Δv check. -1 when unavailable (Δv limit then skipped).
            double deltaVVac = CraftDeltaV.TotalVacuum();

            // Mission-limit gate: reject before uploading anything if the craft
            // breaks a part-usage constraint. The server re-checks authoritatively,
            // but failing here is instant and explains exactly what's wrong.
            if (partLimits != null && !partLimits.IsEmpty)
            {
                // Crew aboard for the crew-count and per-profession limits (flight only;
                // -1 / null elsewhere so they're skipped, matching the server, which
                // reads crew from the submitted telemetry).
                bool inFlight = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null;
                int crewAboard = inFlight
                    ? VesselTransfer.CrewCountOf(FlightGlobals.ActiveVessel) : -1;
                var crewTraits = inFlight
                    ? ContractConstraints.CountCrewTraits(VesselTransfer.CrewOf(FlightGlobals.ActiveVessel))
                    : null;
                var violations = partLimits.CheckCraft(GetSubmissionParts(), deltaVVac, crewAboard, crewTraits);
                if (violations.Count > 0)
                {
                    isSubmitting = false;
                    statusMsg = "❌ Mission limits not met:\n- " + string.Join("\n- ", violations.ToArray());
                    yield break;
                }
            }

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

                // Bake the live editor parts' TweakScale-computed scale into the blueprint
                // so it reconstructs identically for every receiver, regardless of their
                // TweakScale version (see ScaleBridge).
                var editorParts = EditorLogic.fetch?.ship?.parts;
                if (craftData != null && editorParts != null)
                    craftData = ScaleBridge.SnapshotIntoCraftBytes(craftData, editorParts);
            }
            else if (activeVessel != null) // active_vessel or rescue — both submit from flight
            {
                // Active vessel: send craft file + loadmeta + telemetry + full vessel state
                var submission = new Dictionary<string, object>
                {
                    { "contract_id", ContractId },
                    { "active_vessel", activeVessel.ToDict() },
                };

                // Extra crafts the player toggled on (never for rescue — that flow is
                // strictly single-vessel and tracks the handed-over craft by pid).
                var selectedExtras = new List<Vessel>();
                if (!IsRescue && nearbyEntries != null)
                    foreach (var e in nearbyEntries)
                        if (e.selected && e.vessel != null) selectedExtras.Add(e.vessel);

                // Telemetry for the extras actually being sent (informational context
                // for the issuer's review).
                if (selectedExtras.Count > 0)
                {
                    var sentList = new List<object>();
                    foreach (var e in nearbyEntries)
                    {
                        if (!e.selected || e.vessel == null) continue;
                        var d = e.snap.ToDict();
                        d["sent"] = true;
                        sentList.Add(d);
                    }
                    submission["sent_vessels"] = sentList;
                }

                vesselDataJson = MiniJSON.Serialize(submission);

                // Also try to get the craft file for the active vessel
                string craftPath = VesselDataCollector.FindCraftFile(activeVessel.vesselName);
                if (!string.IsNullOrEmpty(craftPath))
                {
                    craftData = VesselDataCollector.ReadCraftFile(craftPath);
                    craftName = Path.GetFileName(craftPath);
                    loadmeta = VesselDataCollector.ReadLoadmeta(craftPath);

                    // Bake scale into the editable blueprint too, matching parts by craftID
                    // against the live flight vessel (the proto VESSEL node is handled
                    // separately via SnapshotIntoVesselNode).
                    if (craftData != null && FlightGlobals.ActiveVessel?.parts != null)
                        craftData = ScaleBridge.SnapshotIntoCraftBytes(craftData, FlightGlobals.ActiveVessel.parts);
                }

                // Export full vessel state for transfer, embedding the crew roster so the
                // importing save recreates each kerbal with their real attributes
                // (gender / profession / courage / stupidity) and owner tag. When extras
                // are selected this becomes a GKFLEET bundle carrying every craft's state,
                // flags and blueprint; otherwise it's a single VESSEL node as before.
                vesselNodeData = selectedExtras.Count > 0
                    ? VesselTransfer.ExportFleet(FlightGlobals.ActiveVessel, selectedExtras, true)
                    : VesselTransfer.ExportActiveVessel(true);
            }

            // Read every captured render (active + selected extras).
            var screenshots = new List<byte[]>();
            var ssNames = new List<string>();

            if (screenshotTaken && screenshotPaths != null)
            {
                foreach (var sp in screenshotPaths)
                {
                    if (string.IsNullOrEmpty(sp)) continue;
                    byte[] ssData = VesselDataCollector.ReadScreenshot(sp);
                    if (ssData != null)
                    {
                        screenshots.Add(ssData);
                        ssNames.Add(Path.GetFileName(sp));
                    }
                }
            }

            // The contractor's full installed modlist is always attached — it's
            // informational context for the issuer and no longer optional.
            string modlist;
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

            // Per-part summary for the server's authoritative mission-limit re-check.
            string usedParts = CollectUsedPartsJson();

            // Vacuum Δv (m/s) for the server's Δv-limit re-check, invariant-formatted
            // so the decimal point survives locales. null when unavailable.
            string deltaVField = deltaVVac >= 0
                ? deltaVVac.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // For rescue, remember which craft we handed over so it can be removed
            // from our save once the issuer approves and it's delivered to them.
            string submittedPid = (IsRescue && FlightGlobals.ActiveVessel != null)
                ? FlightGlobals.ActiveVessel.id.ToString() : null;

            // Carry the craft's custom mission flags so the receiving player sees
            // them instead of a missing decal (the live vessel node embeds its own).
            if (craftData != null)
                craftData = FlagTransfer.EmbedFlagsInCraft(craftData);

            // Record our TweakScale version so the recipient is warned if theirs is
            // missing/different (a scaled craft only rebuilds correctly on a matching
            // TweakScale). Appended AFTER flags so the block sits last in the file.
            if (craftData != null)
                craftData = TweakScaleGuard.EmbedVersionInCraft(craftData);

            // Carry the Textures Unlimited paint job: which recolour packs it needs, so
            // the recipient can be told (and a receiver without TU gets a clean, stock-
            // coloured load rather than orphan recolour modules). After GKTSVER, before
            // GKMODS — the strip order on the other side is the exact reverse.
            if (craftData != null)
                craftData = TextureTransfer.EmbedInCraft(craftData);

            // Record the craft's mods so a recipient missing any gets a CKAN modpack.
            // Appended after flags/TweakScale so the GKMODS block stays a clean strip.
            if (craftData != null)
                craftData = CkanGenerator.EmbedModsInCraft(craftData);

            // Embed an NW-view thumbnail rendered from the live editor craft, so the
            // recipient's craft browser shows it on import instead of KSP's green
            // placeholder. Appended LAST (stripped first on import).
            if (craftData != null)
                craftData = CraftThumb.EmbedThumbForCurrentCraft(craftData);

            // Life-support flag of the submitted craft (which LS mod, per-kerbal
            // endurance, crew capacity) — shown on the contract's review embed.
            LifeSupportInfo ls = LifeSupportScan.Scan(new List<Part>(GetSubmissionParts()));

            yield return GeneKermanMod.Instance.Api.SubmitContract(
                ContractId, craftData, craftName, loadmeta, vesselDataJson,
                vesselNodeData, screenshots, ssNames, modlist, usedModlist, usedParts,
                deltaVField,
                ls.ModKey, ls.EnduranceDaysPerKerbal, ls.CrewCapacity,
                (ok, resp, status) =>
                {
                    isSubmitting = false;
                    if (ok && !string.IsNullOrEmpty(resp))
                    {
                        var result = MiniJSON.DeserializeDict(resp);
                        string reviewStatus = MiniJSON.GetString(result, "review_status", "");
                        string message = MiniJSON.GetString(result, "message", "Submitted!");

                        if (!string.IsNullOrEmpty(submittedPid))
                            GeneKermanMod.Instance.RecordRescueSubmission(ContractId, submittedPid);

                        if (reviewStatus == "approved")
                        {
                            int xp = MiniJSON.GetInt(result, "xp_awarded");
                            int coins = MiniJSON.GetInt(result, "coins_awarded");
                            GeneKermanMod.Instance.ShowNotification("✅ Mission Approved!", $"+{coins} KCoins, +{xp} XP");
                            EditorPartEnforcer.Instance?.StopEnforcing();
                            CloseWindow();
                            GeneKermanMod.Instance.RefreshContracts();
                        }
                        else if (reviewStatus == "refused")
                        {
                            statusMsg = $"❌ Refused: {MiniJSON.GetString(result, "reason", "")}";
                        }
                        else
                        {
                            // Submitted and awaiting review — clear enforcer so VAB is unlocked
                            EditorPartEnforcer.Instance?.StopEnforcing();
                            CloseWindow();
                            GeneKermanMod.Instance.RefreshContracts();
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
                if (info == null) continue;

                if (!IsPartAllowed(info.partUrl))
                {
                    string label = $"• {info.title} (from {GetModFolder(info.partUrl)})";
                    if (seen.Add(label)) bad.Add(label);
                }
                // Scaled parts are no longer flagged: GeneKermanScale bakes the final scale
                // into the submitted craft (see ScaleBridge) and re-applies it on the receiver
                // without TweakScale, so a scaled craft no longer depends on the recipient
                // having (a matching) TweakScale.
            }

            if (bad.Count == 0) return null;
            return "❌ Illegal parts for this contract:\n" + string.Join("\n", bad.ToArray());
        }

        // The parts of the craft being submitted: editor ship for craft builds,
        // active vessel otherwise. Shared by the modlist, mission-limit and
        // used-parts collectors so they all judge the same set of parts.
        private System.Collections.Generic.IEnumerable<Part> GetSubmissionParts()
        {
            if (missionType == "craft_build")
                return EditorLogic.fetch?.ship?.parts;
            return FlightGlobals.ActiveVessel?.parts;
        }

        // Per-part classification of the submitted craft (title, propellants,
        // engine/part categories) as a JSON array, so the server can re-verify
        // the contract's mission limits independently of the client gate.
        private string CollectUsedPartsJson()
        {
            var parts = GetSubmissionParts();
            if (parts == null) return null;

            var list = new List<object>();
            foreach (var part in parts)
            {
                if (part == null) continue;
                list.Add(PartClassifier.Classify(part).ToDict());
            }
            return list.Count > 0 ? MiniJSON.Serialize(list) : null;
        }

        // Distinct top-level mod folders used by the craft being submitted (editor ship
        // for craft builds, active vessel otherwise). Sent to the server for validation.
        private string CollectUsedModFolders()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            System.Collections.Generic.IEnumerable<Part> parts = GetSubmissionParts();
            if (parts == null) return null;

            foreach (var part in parts)
            {
                string url = part?.partInfo?.partUrl;
                if (!string.IsNullOrEmpty(url))
                    folders.Add(url.Split('/')[0]);

                // TweakScale is intentionally NOT reported as a dependency: GeneKermanScale
                // bakes the scale into the delivered craft and applies it without TweakScale
                // (see ScaleBridge), so a scaled craft does not require the recipient to have
                // TweakScale installed.
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
