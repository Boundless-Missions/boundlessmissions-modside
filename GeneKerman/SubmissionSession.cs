/*
 * SubmissionSession.cs – The contract submission flow, with no UI attached.
 *
 * This is the whole of the old UI/SubmitWindow.cs except its drawing: the
 * classification rules a mission is submitted under, the scene/vessel validation
 * that enforces them client-side, the render capture and its freshness check, and
 * the coroutine that packs and uploads everything. The IMGUI window that used to
 * own all of it has been replaced by the draggable uGUI window in
 * UI/Gui/Panels/SubmitPanel.cs, and the split is the same one CraftDelivery and
 * SubmissionPreview were pulled out for: state and rules here, pixels there.
 *
 * Mission types (AI-classified, cached on server):
 *   - "craft_build": Must submit from VAB/SPH. Sends: craft file + KVV/screenshot.
 *   - "active_vessel": Must submit from Flight. Sends: craft + loadmeta + telemetry
 *     + screenshot, and the vessel's situation and body must match.
 *   - "rescue": active_vessel, plus the stranded crew (and sometimes the wreck).
 *
 * The server tells us what type + requirements via the contract data. We enforce
 * it here so players get immediate feedback, not a server rejection.
 *
 * A view watches <see cref="Changed"/> rather than polling: nothing here runs on a
 * draw pass any more, so a state change has to announce itself. <see cref="Closed"/>
 * is the other direction of the same wire — the submit coroutine decides the flow is
 * over (approved, or filed for review) and the window that is showing it closes.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>One other craft inside physics range, offered as an extra to pack
    /// into this submission.</summary>
    public sealed class NearbyCraft
    {
        public Vessel Vessel;
        public VesselDataCollector.VesselSnapshot Snap;
        public bool Selected;
        public double Distance;   // metres from the active vessel
    }

    public sealed class SubmissionSession
    {
        /// <summary>Anything a view draws has changed. Fired on the main thread.</summary>
        public event Action Changed;

        /// <summary>The flow is finished and whatever is showing it should go away.</summary>
        public event Action Closed;

        // ── Contract ────────────────────────────────────────────────────────

        public string ContractId { get; private set; }
        public string ContractMission { get; private set; }

        /// <summary>"craft_build", "active_vessel" or "rescue".</summary>
        public string MissionType { get; private set; } = "active_vessel";

        public string RequiredSituation { get; private set; } = "";
        public string RequiredBody { get; private set; } = "";
        public bool IsRescue => MissionType == "rescue";

        private RescueTargetSpec rescueTarget;
        private List<string> rescueKerbals;
        private ContractConstraints partLimits;   // mission part-usage limits (may be null)

        /// <summary>The contract's part limits, or null when it sets none.</summary>
        public ContractConstraints PartLimits => partLimits;

        // ── State a view draws ──────────────────────────────────────────────

        public bool SceneValid { get; private set; }       // right scene for this mission type?
        public bool VesselValid { get; private set; }      // situation/body/limits satisfied?
        public string ValidationMsg { get; private set; } = "";
        public string StatusMsg { get; private set; } = "";

        /// <summary>Whether <see cref="StatusMsg"/> is a failure — the view colours it.
        /// Carried as a flag because the message itself no longer starts with an emoji
        /// the caller could read it back out of.</summary>
        public bool StatusIsError { get; private set; }
        public bool IsSubmitting { get; private set; }
        public bool PhysicsStabilizing { get; private set; }
        public bool PreDisabledByUs { get; private set; }

        // Editor mode data
        public string EditorCraftName { get; private set; } = "";
        public string EditorCraftPath { get; private set; } = "";
        public string EditorCraftType { get; private set; } = "";
        public int EditorPartCount { get; private set; }
        public float EditorCraftMass { get; private set; }
        public float EditorCraftCost { get; private set; }

        // Flight data
        public VesselDataCollector.VesselSnapshot ActiveVessel { get; private set; }

        private List<NearbyCraft> nearbyEntries = new List<NearbyCraft>();
        public IList<NearbyCraft> Nearby => nearbyEntries;

        /// <summary>The "select everything within N km" box, kept as typed text so a
        /// half-entered number survives a rebuild.</summary>
        public string RangeFilterKm { get; set; } = "2.5";

        // Renders
        private List<string> screenshotPaths = new List<string>();
        public bool ScreenshotTaken { get; private set; }
        public int RenderCount => screenshotPaths?.Count ?? 0;

        // Render freshness. A render is a picture of a craft at one moment; the craft
        // can be changed afterwards and the submission would then carry an image of
        // something that was never sent. renderFingerprint is the structural signature
        // of everything the renders show, taken at capture time; RenderStale is set
        // when the live craft no longer matches it, which blocks Submit until the
        // player retakes them. nextStaleCheck throttles the comparison — it walks the
        // part list, and the view asks once a frame.
        public bool RenderStale { get; private set; }
        private int renderFingerprint;
        private float nextStaleCheck;

        // Part restriction
        private string requiredModlist;
        private HashSet<string> allowedMods;
        private List<string> excludePaths;

        // ── Opening ─────────────────────────────────────────────────────────

        public void Open(string contractId, string mission,
            string type = "active_vessel", string situation = "", string body = "", string modlist = "",
            RescueTargetSpec rescueTargetSpec = null, List<string> rescueKerbalNames = null,
            ContractConstraints constraints = null)
        {
            ContractId = contractId;
            ContractMission = mission;
            MissionType = type ?? "active_vessel";
            RequiredSituation = situation ?? "";
            RequiredBody = body ?? "";
            rescueTarget = rescueTargetSpec;
            rescueKerbals = rescueKerbalNames;
            partLimits = (constraints != null && !constraints.IsEmpty) ? constraints : null;
            // For rescue, derive the body the rescuer must reach from the target.
            if (IsRescue && rescueTarget != null && string.IsNullOrEmpty(RequiredBody))
                RequiredBody = rescueTarget.body;

            IsSubmitting = false;
            StatusMsg = "";
            StatusIsError = false;
            ScreenshotTaken = false;
            screenshotPaths = new List<string>();
            RenderStale = false;
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

        /// <summary>
        /// A scene load has been *requested* — everything below is still alive, and the
        /// scene we are about to be in cannot be asked about yet. So this is only the
        /// letting go: hand PRE back, and drop the renders and the vessel readout,
        /// which describe a scene that is about to stop existing. Revalidating is
        /// <see cref="Revalidate"/>, and it has to wait until the load has landed.
        /// </summary>
        public void LeavingScene()
        {
            if (PreDisabledByUs)
            {
                PhysicsRangeManager.Reenable();
                PreDisabledByUs = false;
            }

            // Renders are of a craft in a scene that is going away. Keeping them would
            // let a player submit a picture taken before whatever they just did.
            ScreenshotTaken = false;
            screenshotPaths = new List<string>();
            RenderStale = false;
            renderFingerprint = 0;

            ActiveVessel = null;
            nearbyEntries = new List<NearbyCraft>();

            SceneValid = false;
            VesselValid = false;
            ValidationMsg = "";

            Touch();
        }

        /// <summary>
        /// Re-run validation for the scene we are in now. The window survives scene
        /// loads, and a submission that is *waiting* for the player to reach the VAB or
        /// launch the craft is the normal case — without this it would keep telling
        /// them to go where they already are until they closed and reopened it.
        /// </summary>
        public void Revalidate() => Validate();

        private void Validate()
        {
            SceneValid = false;
            VesselValid = false;
            ValidationMsg = "";

            if (MissionType == "craft_build")
            {
                // Craft build: must be in VAB/SPH
                if (HighLogic.LoadedSceneIsEditor)
                {
                    SceneValid = true;
                    VesselValid = true; // No vessel situation needed for craft builds
                    CaptureEditorCraft();
                }
                else
                {
                    ValidationMsg = "This is a craft build mission.\nGo to the VAB or SPH to submit.";
                }
            }
            else // active_vessel / rescue
            {
                if (HighLogic.LoadedSceneIsFlight)
                {
                    SceneValid = true;
                    // Pause PRE (if any), let far craft unload, then capture the
                    // active vessel + the stock-range neighbours offered as extras.
                    GeneKermanMod.Instance.RunCoroutine(PreparePhysicsThenCapture());
                }
                else
                {
                    ValidationMsg = "This is an active vessel mission.\nLaunch your craft and fly to the target to submit.";
                }
            }

            Touch();
        }

        private void Touch() => Changed?.Invoke();

        // ── Validation ──────────────────────────────────────────────────────

        private void ValidateVesselState()
        {
            if (ActiveVessel == null)
            {
                VesselValid = false;
                ValidationMsg = "No active vessel found.";
                return;
            }

            VesselValid = true;
            var issues = new List<string>();

            // Check body requirement
            if (!string.IsNullOrEmpty(RequiredBody))
            {
                if (!string.Equals(ActiveVessel.body, RequiredBody, StringComparison.OrdinalIgnoreCase))
                {
                    VesselValid = false;
                    issues.Add($"Body mismatch: at {ActiveVessel.body}, need {RequiredBody}");
                }
            }

            // Check situation requirement
            if (!string.IsNullOrEmpty(RequiredSituation))
            {
                if (!string.Equals(ActiveVessel.situation, RequiredSituation, StringComparison.OrdinalIgnoreCase))
                {
                    VesselValid = false;
                    issues.Add($"Situation mismatch: {ActiveVessel.situation}, need {RequiredSituation}");
                }
            }

            // Orbit-type requirement (polar/equatorial/keostationary/…): the craft must
            // be in the specific orbit the mission names. The server re-checks this
            // authoritatively from the submitted telemetry.
            if (partLimits?.Orbit != null && !partLimits.Orbit.IsEmpty)
            {
                foreach (var v in partLimits.Orbit.CheckOrbit(ActiveVessel))
                {
                    VesselValid = false;
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
                VesselValid = false;
                issues.Add(illegalParts);
            }

            if (issues.Count > 0)
                ValidationMsg = string.Join("\n", issues.ToArray());
        }

        /// <summary>
        /// Every orbit requirement on this contract in one line, or "" when it names no
        /// particular orbit. Two sources, because there are two ways to ask: the regime
        /// parsed out of the mission text (any contract), and the plane/regime an issuer
        /// picked for a rescue target.
        /// </summary>
        public string DescribeOrbitRequirement()
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
                    VesselValid = false;
                    issues.Add($"Missing kerbals ({missing.Count}): {string.Join(", ", missing.ToArray())}");
                }
            }

            if (rescueTarget == null) return;

            ValidateWreckAboard(issues);
            ValidateRescueDeltaV(issues);

            bool surface = (rescueTarget.mode ?? "orbit").ToLower() == "surface";
            if (surface)
            {
                if (!(ActiveVessel.situation == "LANDED" || ActiveVessel.situation == "SPLASHED"))
                {
                    VesselValid = false;
                    issues.Add($"Must be landed at {rescueTarget.body} (currently {ActiveVessel.situation}).");
                }
                else
                {
                    double dLat = Math.Abs(ActiveVessel.latitude - rescueTarget.lat);
                    double dLon = Math.Abs(ActiveVessel.longitude - rescueTarget.lon);
                    if (dLon > 180) dLon = 360 - dLon; // wrap
                    double margin = Math.Max(rescueTarget.marginPos, 0.01);
                    if (dLat > margin || dLon > margin)
                    {
                        VesselValid = false;
                        issues.Add($"Off target: at {ActiveVessel.latitude:F2}°,{ActiveVessel.longitude:F2}°, " +
                                   $"need {rescueTarget.lat:F2}°,{rescueTarget.lon:F2}° (±{margin:F2}°).");
                    }
                }
            }
            else
            {
                if (ActiveVessel.situation != "ORBITING")
                {
                    VesselValid = false;
                    issues.Add($"Must be orbiting {rescueTarget.body} (currently {ActiveVessel.situation}).");
                }
                else
                {
                    double margin = Math.Max(rescueTarget.marginAlt, 1.0); // metres
                    double dAp = Math.Abs(ActiveVessel.apoapsis - rescueTarget.ap);
                    double dPe = Math.Abs(ActiveVessel.periapsis - rescueTarget.pe);
                    if (dAp > margin || dPe > margin)
                    {
                        VesselValid = false;
                        issues.Add($"Orbit off target: Ap {ActiveVessel.apoapsis / 1000:F0}km / " +
                                   $"Pe {ActiveVessel.periapsis / 1000:F0}km, need " +
                                   $"Ap {rescueTarget.ap / 1000:F0}km / Pe {rescueTarget.pe / 1000:F0}km (±{margin / 1000:F0}km).");
                    }

                    // Plane and regime, when the issuer asked for them. Ap/Pe are the
                    // cheap half of a rendezvous — the plane is the half that costs
                    // delta-v, so a rescue that names one is a materially different job.
                    string plane = OrbitConstraint.CheckInclination(
                        rescueTarget.incl, rescueTarget.marginIncl, ActiveVessel.inclination);
                    if (plane != null)
                    {
                        VesselValid = false;
                        issues.Add(plane);
                    }
                    foreach (var v in rescueTarget.OrbitTypeConstraint().CheckOrbit(ActiveVessel))
                    {
                        VesselValid = false;
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

            VesselValid = false;
            issues.Add($"The stranded vessel isn't here: {found} of {rescueTarget.wreckParts.Count} " +
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
                issues.Add($"Δv unreadable. This rescue needs ≥{rescueTarget.minDv:F0} m/s left. " +
                           "Turn on the stock Δv readout to check it here; the server checks it either way.");
                return;
            }

            // Same 0.5% slack the mission-limit Δv check allows, so a craft sitting right
            // on the number isn't failed by rounding.
            if (dv >= rescueTarget.minDv * 0.995) return;

            VesselValid = false;
            issues.Add($"Not enough Δv left: {dv:F0} m/s, need ≥{rescueTarget.minDv:F0} m/s " +
                       "so the crew can get home from here.");
        }

        // ── Editor mode (craft_build) ───────────────────────────────────────

        /// <summary>Re-read the craft on the editor's build stage, saving it to disk so
        /// the .craft we would upload is the one on screen.</summary>
        public void CaptureEditorCraft()
        {
            EditorCraftName = "";
            EditorCraftPath = "";
            EditorPartCount = 0;
            EditorCraftMass = 0;
            EditorCraftCost = 0;

            // Re-scan part legality from scratch each capture. This method is also called
            // standalone via the "Refresh craft data" button (not just through Validate),
            // so reset validity here or a fixed craft would stay flagged.
            if (allowedMods != null)
            {
                VesselValid = true;
                if (ValidationMsg.StartsWith("Illegal part")) ValidationMsg = "";
            }

            try
            {
                var ship = EditorLogic.fetch?.ship;
                if (ship != null)
                {
                    EditorCraftName = ship.shipName ?? "Untitled";
                    EditorPartCount = ship.parts?.Count ?? 0;

                    // Auto-save the editor craft to disk so the .craft file we look
                    // up below is current — the player no longer has to save manually
                    // before submitting.
                    if (EditorPartCount > 0)
                    {
                        try { ShipConstruction.SaveShip(EditorCraftName); }
                        catch (Exception saveEx)
                        {
                            Debug.LogWarning($"[GeneKerman] Could not auto-save craft: {saveEx.Message}");
                        }
                    }

                    if (ship.parts != null)
                    {
                        float mass = 0f, cost = 0f;
                        foreach (var part in ship.parts)
                        {
                            mass += part.mass + part.GetResourceMass();
                            // Full funds cost (dry + module modifiers incl. TweakScale + fuel).
                            cost += VesselDataCollector.GetPartCost(part);
                        }
                        EditorCraftMass = mass;
                        EditorCraftCost = cost;

                        // List every part that violates the contract's restriction.
                        string illegalParts = FindIllegalParts(ship.parts);
                        if (illegalParts != null)
                        {
                            VesselValid = false;
                            ValidationMsg = illegalParts;
                        }
                    }

                    EditorCraftType = EditorDriver.editorFacility == EditorFacility.VAB ? "VAB" : "SPH";

                    string saveFolder = HighLogic.SaveFolder ?? "default";
                    string shipDir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder,
                        "Ships", EditorCraftType);
                    string craftFile = Path.Combine(shipDir, EditorCraftName + ".craft");

                    if (File.Exists(craftFile))
                        EditorCraftPath = craftFile;
                    else
                    {
                        string rootDir = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", EditorCraftType);
                        craftFile = Path.Combine(rootDir, EditorCraftName + ".craft");
                        if (File.Exists(craftFile))
                            EditorCraftPath = craftFile;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Error reading editor craft: {ex.Message}");
            }

            Touch();
        }

        // ── Flight mode (active_vessel / rescue) ────────────────────────────

        /// <summary>Re-read the active vessel and the craft around it, then re-check the
        /// contract's requirements against them.</summary>
        public void CaptureFlightData()
        {
            ActiveVessel = VesselDataCollector.CaptureActiveVessel();
            BuildNearbyEntries();
            ValidateVesselState();
            Touch();
        }

        /// <summary>Pause PRE, wait for out-of-range craft to unload, then capture. Keeps
        /// the "Stabilizing physics range…" notice up while the bubble collapses so the
        /// neighbour list reflects the stock range, not PRE's inflated one.</summary>
        private IEnumerator PreparePhysicsThenCapture()
        {
            PhysicsStabilizing = true;
            StatusMsg = "Stabilizing physics range...";
            StatusIsError = false;
            Touch();

            // Rescue is strictly single-vessel — leave PRE alone for it.
            PreDisabledByUs = !IsRescue && PhysicsRangeManager.TryDisable();
            if (PreDisabledByUs)
            {
                // Give KSP a few frames + a beat to drop now-out-of-range vessels.
                for (int i = 0; i < 5; i++) yield return new WaitForEndOfFrame();
                yield return new WaitForSeconds(0.5f);
            }

            // The player can leave flight while we wait; capturing then would read a
            // vessel that is no longer loaded and overwrite the scene message.
            if (!HighLogic.LoadedSceneIsFlight)
            {
                PhysicsStabilizing = false;
                Touch();
                yield break;
            }

            ActiveVessel = VesselDataCollector.CaptureActiveVessel();
            BuildNearbyEntries();
            ValidateVesselState();

            PhysicsStabilizing = false;
            if (StatusMsg == "Stabilizing physics range...") StatusMsg = "";
            Touch();
        }

        /// <summary>(Re)build the in-range extra-craft list, preserving the player's
        /// existing on/off choices for vessels that are still loaded.</summary>
        private void BuildNearbyEntries()
        {
            var prevSelected = new Dictionary<Guid, bool>();
            if (nearbyEntries != null)
                foreach (var e in nearbyEntries)
                    if (e.Vessel != null) prevSelected[e.Vessel.id] = e.Selected;

            nearbyEntries = new List<NearbyCraft>();

            var active = FlightGlobals.ActiveVessel;
            if (active == null) return;
            Vector3d aPos = active.GetWorldPos3D();

            foreach (var v in VesselDataCollector.GetNearbyVessels(active))
            {
                var entry = new NearbyCraft
                {
                    Vessel = v,
                    Snap = VesselDataCollector.CaptureVessel(v),
                    Distance = Vector3d.Distance(aPos, v.GetWorldPos3D()),
                };
                bool wasSelected;
                if (prevSelected.TryGetValue(v.id, out wasSelected)) entry.Selected = wasSelected;
                nearbyEntries.Add(entry);
            }

            nearbyEntries.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        public void SetAllSelected(bool value)
        {
            if (nearbyEntries == null) return;
            foreach (var e in nearbyEntries) e.Selected = value;
            Touch();
        }

        public void SetSelected(NearbyCraft entry, bool value)
        {
            if (entry == null) return;
            entry.Selected = value;
            Touch();
        }

        public void SelectWithinRange()
        {
            double km;
            if (!double.TryParse(RangeFilterKm, out km)) return;
            double metres = km * 1000.0;
            foreach (var e in nearbyEntries) e.Selected = e.Distance <= metres;
            Touch();
        }

        public int SelectedExtras
        {
            get
            {
                if (IsRescue || nearbyEntries == null) return 0;
                int n = 0;
                foreach (var e in nearbyEntries) if (e.Selected && e.Vessel != null) n++;
                return n;
            }
        }

        // ── Renders ─────────────────────────────────────────────────────────

        /// <summary>Capture an orthographic render of the active (contract) craft, plus
        /// one for each selected extra craft so every submitted vessel has a blueprint
        /// image. Renders run synchronously — a deliberate one-tap action.</summary>
        public void TakeRenders()
        {
            // Re-capture before rendering, so the picture and the payload are taken from
            // the same craft. In the editor this re-saves the .craft to disk, which is
            // the file SubmitCoroutine uploads — without it a retake would produce a
            // fresh render of a craft still described on disk by a stale file.
            if (MissionType == "craft_build")
            {
                CaptureEditorCraft();
            }
            else if (HighLogic.LoadedSceneIsFlight)
            {
                ActiveVessel = VesselDataCollector.CaptureActiveVessel();
                BuildNearbyEntries();
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
                    if (!e.Selected || e.Vessel == null) continue;
                    string ep = KVVIntegration.CaptureWithFallback(e.Vessel);
                    if (!string.IsNullOrEmpty(ep)) screenshotPaths.Add(ep);
                }
            }

            ScreenshotTaken = screenshotPaths.Count > 0;

            // Pin what these renders show, so any later change to the craft is caught.
            renderFingerprint = RenderSubjectFingerprint();
            RenderStale = false;
            nextStaleCheck = Time.realtimeSinceStartup + 0.4f;

            StatusIsError = false;
            StatusMsg = screenshotPaths.Count > 1
                ? $"Captured {screenshotPaths.Count} renders. They may take a moment to save."
                : "Captured. It may take a moment to save.";

            Touch();
        }

        /// <summary>
        /// Re-check whether the captured renders still show the craft that would be
        /// submitted. Throttled — it walks the part list — and called once a frame by
        /// whatever is showing the session.
        /// </summary>
        public void TickRenderStale()
        {
            if (!ScreenshotTaken || IsSubmitting) return;

            float now = Time.realtimeSinceStartup;
            if (now < nextStaleCheck) return;
            nextStaleCheck = now + 0.4f;

            bool stale = RenderSubjectFingerprint() != renderFingerprint;
            if (stale == RenderStale) return;

            RenderStale = stale;
            Touch();
        }

        /// <summary>
        /// Signature of everything the renders depict: the craft being submitted plus
        /// each extra craft currently ticked, since TakeRenders captures one image
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
                        if (!e.Selected || e.Vessel == null) continue;
                        h += PartsFingerprint(e.Vessel.parts) * 31 + 17;
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

        // ── Submission ──────────────────────────────────────────────────────

        public bool CanSubmit()
        {
            if (!SceneValid) return false;
            if (!ScreenshotTaken) return false;
            // Renders that no longer match the craft are worse than none: the issuer
            // would review a picture of something that was never submitted.
            if (RenderStale) return false;

            if (MissionType == "craft_build")
                // VesselValid carries the part-legality result from CaptureEditorCraft();
                // without it an illegal part (e.g. a Squad expansion part on a restricted
                // contract) would pass the gate and submit anyway.
                return !string.IsNullOrEmpty(EditorCraftPath) && VesselValid;

            // active_vessel: need vessel data AND matching state
            return ActiveVessel != null && VesselValid;
        }

        /// <summary>Restore Physics Range Extender if we paused it, and tell whatever is
        /// showing this session to go away. Every close path routes through here so PRE
        /// is never left disabled.</summary>
        public void Close()
        {
            if (PreDisabledByUs)
            {
                PhysicsRangeManager.Reenable();
                PreDisabledByUs = false;
            }
            Closed?.Invoke();
        }

        public void Submit()
        {
            if (IsSubmitting) return;
            IsSubmitting = true;
            StatusMsg = "Submitting...";
            StatusIsError = false;
            Touch();
            GeneKermanMod.Instance.RunCoroutine(SubmitCoroutine());
        }

        private void Fail(string message)
        {
            IsSubmitting = false;
            StatusMsg = message;
            StatusIsError = true;
            Touch();
        }

        private IEnumerator SubmitCoroutine()
        {
            yield return new WaitForSeconds(1.5f);

            // Last word on render freshness, unthrottled: the throttled per-frame check
            // can be up to 0.4s behind, and this coroutine then waits another 1.5s
            // before reading anything — ample time to pull a part off.
            if (ScreenshotTaken && RenderSubjectFingerprint() != renderFingerprint)
            {
                RenderStale = true;
                Fail("The craft changed after the renders were taken.\nRetake the renders, then submit.");
                yield break;
            }

            // Re-save the live editor ship so the .craft we upload is the craft on
            // screen. The fingerprint above proves the shape still matches the renders,
            // but a tweak a blueprint cannot show — a fuel level, an action group — is
            // only on disk if the file was written after it.
            if (MissionType == "craft_build")
            {
                CaptureEditorCraft();
                if (!VesselValid || string.IsNullOrEmpty(EditorCraftPath))
                {
                    Fail(string.IsNullOrEmpty(ValidationMsg)
                        ? "Could not read the craft. Save it and try again."
                        : ValidationMsg);
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
                    Fail("Mission limits not met:\n- " + string.Join("\n- ", violations.ToArray()));
                    yield break;
                }
            }

            byte[] craftData = null;
            string craftName = null;
            string loadmeta = null;
            string vesselDataJson = null;
            string vesselNodeData = null;
            string cheatReport = null;

            if (MissionType == "craft_build" && !string.IsNullOrEmpty(EditorCraftPath))
            {
                craftData = VesselDataCollector.ReadCraftFile(EditorCraftPath);
                craftName = Path.GetFileName(EditorCraftPath);
                // No loadmeta or vessel node for craft_build

                // Bake the live editor parts' TweakScale-computed scale into the blueprint
                // so it reconstructs identically for every receiver, regardless of their
                // TweakScale version (see ScaleBridge).
                var editorParts = EditorLogic.fetch?.ship?.parts;
                if (craftData != null && editorParts != null)
                    craftData = ScaleBridge.SnapshotIntoCraftBytes(craftData, editorParts);
            }
            else if (ActiveVessel != null) // active_vessel or rescue — both submit from flight
            {
                // Active vessel: send craft file + loadmeta + telemetry + full vessel state
                var submission = new Dictionary<string, object>
                {
                    { "contract_id", ContractId },
                    { "active_vessel", ActiveVessel.ToDict() },
                };

                // Extra crafts the player toggled on (never for rescue — that flow is
                // strictly single-vessel and tracks the handed-over craft by pid).
                var selectedExtras = new List<Vessel>();
                if (!IsRescue && nearbyEntries != null)
                    foreach (var e in nearbyEntries)
                        if (e.Selected && e.Vessel != null) selectedExtras.Add(e.Vessel);

                // Telemetry for the extras actually being sent (informational context
                // for the issuer's review).
                if (selectedExtras.Count > 0)
                {
                    var sentList = new List<object>();
                    foreach (var e in nearbyEntries)
                    {
                        if (!e.Selected || e.Vessel == null) continue;
                        var d = e.Snap.ToDict();
                        d["sent"] = true;
                        sentList.Add(d);
                    }
                    submission["sent_vessels"] = sentList;
                }

                vesselDataJson = MiniJSON.Serialize(submission);

                // Cheat report covering exactly the vessels this submission claims flew
                // here (active + selected extras). Editor builds carry none — a blueprint
                // has no flight state to have cheated. See CheatDetection for what
                // taints a vessel and why an explicit clean report is still sent.
                var judged = new List<Vessel> { FlightGlobals.ActiveVessel };
                judged.AddRange(selectedExtras);
                cheatReport = CheatDetection.BuildReportJson(judged);

                // Also try to get the craft file for the active vessel
                string craftPath = VesselDataCollector.FindCraftFile(ActiveVessel.vesselName);
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

            if (ScreenshotTaken && screenshotPaths != null)
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
                modlist = string.Join(",", new List<string>(folders).ToArray());
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

            // Carry the RealFuels/RO fuel-and-engine configuration manifest: which tank
            // packs the craft's config needs and whether it was built for RO physics —
            // knowable only on this install, invisible to every part walk. After GKTU,
            // before GKMODS.
            if (craftData != null)
                craftData = RealFuelsTransfer.EmbedInCraft(craftData);

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
                cheatReport,
                (ok, resp, status) =>
                {
                    IsSubmitting = false;
                    if (ok && !string.IsNullOrEmpty(resp))
                    {
                        var result = MiniJSON.DeserializeDict(resp);
                        string reviewStatus = MiniJSON.GetString(result, "review_status", "");

                        // A server-side gate can refuse with HTTP 200 + success:false
                        // (mission limits, illegal mods, cheat disqualification, …).
                        // That is a rejection, not "submitted": show the reason and
                        // record nothing — a refused rescue craft was NOT handed over,
                        // so it must not be queued for removal on approval.
                        if (!MiniJSON.GetBool(result, "success", true))
                        {
                            StatusMsg = MiniJSON.GetString(result, "message", "Submission refused.");
                            StatusIsError = true;
                            Touch();
                            return;
                        }

                        if (!string.IsNullOrEmpty(submittedPid))
                            GeneKermanMod.Instance.RecordRescueSubmission(ContractId, submittedPid);

                        if (reviewStatus == "approved")
                        {
                            int xp = MiniJSON.GetInt(result, "xp_awarded");
                            int coins = MiniJSON.GetInt(result, "coins_awarded");
                            GeneKermanMod.Instance.ShowNotification("Mission approved", $"+{coins} KCoins, +{xp} XP");
                            EditorPartEnforcer.Instance?.StopEnforcing();
                            Close();
                            GeneKermanMod.Instance.RefreshContracts();
                        }
                        else if (reviewStatus == "refused")
                        {
                            StatusMsg = $"Refused: {MiniJSON.GetString(result, "reason", "")}";
                            StatusIsError = true;
                        }
                        else
                        {
                            // Submitted and awaiting review — clear enforcer so VAB is unlocked
                            EditorPartEnforcer.Instance?.StopEnforcing();
                            Close();
                            GeneKermanMod.Instance.RefreshContracts();
                        }
                    }
                    else
                    {
                        StatusMsg = "Submission failed. Check connection and try again.";
                        StatusIsError = true;
                    }

                    Touch();
                }
            );
        }

        // ── Part legality ───────────────────────────────────────────────────

        private string GetModFolder(string partUrl)
        {
            if (string.IsNullOrEmpty(partUrl)) return null;
            string[] parts = partUrl.Split('/');
            if (parts.Length > 0) return parts[0];
            return null;
        }

        /// <summary>
        /// A user-facing list of every part that violates the contract's part
        /// restriction, so the player knows exactly why Submit is greyed out. Returns
        /// null when there's no restriction or all parts are allowed.
        /// </summary>
        private string FindIllegalParts(IEnumerable<Part> parts)
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
            return "Illegal parts for this contract:\n" + string.Join("\n", bad.ToArray());
        }

        /// <summary>
        /// The parts of the craft being submitted: editor ship for craft builds,
        /// active vessel otherwise. Shared by the modlist, mission-limit and
        /// used-parts collectors so they all judge the same set of parts.
        /// </summary>
        private IEnumerable<Part> GetSubmissionParts()
        {
            if (MissionType == "craft_build")
                return EditorLogic.fetch?.ship?.parts;
            return FlightGlobals.ActiveVessel?.parts;
        }

        /// <summary>
        /// Per-part classification of the submitted craft (title, propellants,
        /// engine/part categories) as a JSON array, so the server can re-verify
        /// the contract's mission limits independently of the client gate.
        /// </summary>
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

        /// <summary>
        /// Distinct top-level mod folders used by the craft being submitted (editor ship
        /// for craft builds, active vessel otherwise). Sent to the server for validation.
        /// </summary>
        private string CollectUsedModFolders()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<Part> parts = GetSubmissionParts();
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

            return folders.Count > 0 ? string.Join(",", new List<string>(folders).ToArray()) : null;
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
