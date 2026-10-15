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
 *   - "craft_build": Must submit from VAB/SPH. Sends: craft file + render/screenshot.
 *   - "active_vessel": Must submit from Flight. Sends: craft + loadmeta + telemetry
 *     + screenshot, and the vessel's situation and body must match.
 *   - "constellation": active_vessel, plus a scan of every vessel in the target
 *     body's sphere of influence — counted and measured against the network the
 *     mission text describes. The scan is NOT bounded by physics range (see
 *     VesselDataCollector.CaptureBodyFleet); nothing is loaded and nothing is
 *     transferred, so unlike the extras list below it is evidence only.
 *   - "rescue": active_vessel, plus the stranded crew (and sometimes the wreck).
 *     Extras in range are handed over like any other submission's, provided this save
 *     can tell the stranded wreck apart from the craft the rescuer brought (see
 *     ExtrasAreHandedOver); when it cannot, they ride as renders + telemetry only.
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
    /// <summary>One other craft inside physics range, offered as an extra to send with
    /// this submission. A selected extra is packed into the delivered GKFLEET bundle as a
    /// live craft and leaves this save when the contract settles — except on a rescue
    /// whose wreck this save cannot identify, where it contributes its renders and
    /// telemetry only and stays put (see SubmissionSession.ExtrasAreHandedOver, and
    /// SubmitPanel for the wording that tells the player which of the two it is).</summary>
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
        public bool IsConstellation => MissionType == "constellation";

        /// <summary>Issued by Boundless Missions rather than by a player.</summary>
        public bool IsBotIssued { get; private set; }

        /// <summary>Whether this submission hands the network over, rather than only
        /// being counted.
        ///
        /// A bot-issued network is verified and left alone: there is no issuer save for
        /// satellites to arrive in, and taking a player's working relay constellation as
        /// payment for a weekly mission would be a strange thing to do to them. A
        /// player-issued one is a purchase — the buyer gets the network they paid for,
        /// which is the same thing every other contract type means by a hand-over.</summary>
        public bool HandoverApplies => IsConstellation && !IsBotIssued;

        // Which members the player has chosen to hand over, by `vesselId`. Empty until
        // they tick something: the set that leaves a save is never a default.
        private readonly HashSet<string> selectedMembers = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Problems with the SELECTED set, not the whole scan. Empty when no
        /// hand-over applies.</summary>
        public List<string> HandoverProblems { get; private set; } = new List<string>();

        public bool IsMemberSelected(string vesselId)
            => !string.IsNullOrEmpty(vesselId) && selectedMembers.Contains(vesselId);

        public int SelectedMemberCount => selectedMembers.Count;

        /// <summary>Tick or untick one member, then re-check what that set is worth.</summary>
        public void ToggleMember(string vesselId)
        {
            if (string.IsNullOrEmpty(vesselId)) return;
            if (!selectedMembers.Remove(vesselId)) selectedMembers.Add(vesselId);
            Revalidate();
        }

        /// <summary>Every vessel in the target body's SOI that counts toward the
        /// network, rescanned on each validation. Empty for every other mission
        /// type. Evidence only — nothing here is handed over.</summary>
        public List<VesselDataCollector.VesselSnapshot> FleetMembers { get; private set; }
            = new List<VesselDataCollector.VesselSnapshot>();

        /// <summary>Why the scanned network doesn't satisfy the contract yet, as
        /// player-facing sentences. Empty when it does (or when there is no network
        /// requirement).</summary>
        public List<string> FleetProblems { get; private set; } = new List<string>();

        /// <summary>The contract's network requirement, or null when it sets none.</summary>
        public FleetConstraint FleetLimits =>
            (partLimits != null && partLimits.Fleet != null && !partLimits.Fleet.IsEmpty)
                ? partLimits.Fleet : null;

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

        /// <summary>How many craft in physics range were left out of
        /// <see cref="Nearby"/> because they are part of this rescue itself — the
        /// stranded vessel, or one of the stranded crew standing on EVA. Offering
        /// either as an "extra" would be offering the issuer their own ship back a
        /// second time; the view says so rather than leaving them silently missing.</summary>
        public int RescueExcludedNearby { get; private set; }

        /// <summary>
        /// Whether ticking a nearby craft actually hands it over — packed into the
        /// delivered vessel node, and removed from this save when the contract settles.
        ///
        /// Always true off a rescue. On a rescue it is true only when this save can say
        /// which craft the stranded wreck is, because the extras list is a list of ships
        /// to give away and the wreck must never be one of them. Two sources answer
        /// that: the server's wreck_parts, which it only pins for a "vessel" recovery,
        /// and this save's own record of what it spawned (RescueWreckRecord), which a
        /// contract accepted before that record existed does not have.
        ///
        /// When it is false the section keeps working exactly as it did before extras
        /// were ever handed over on a rescue: a ticked craft is rendered and its
        /// telemetry sent, and it stays in the save. That is a real feature for the
        /// issuer, so it is kept rather than the list being hidden — but the copy in
        /// SubmitPanel has to say which of the two is happening, since one of them is
        /// permanent.
        /// </summary>
        public bool ExtrasAreHandedOver { get; private set; } = true;

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
            ContractConstraints constraints = null, bool botIssued = false)
        {
            ContractId = contractId;
            IsBotIssued = botIssued;
            selectedMembers.Clear();
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
            RescueExcludedNearby = 0;
            ExtrasAreHandedOver = true;

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
            else if (IsConstellation)
            {
                // A network is judged on the SOI scan, and that scan reads
                // `FlightGlobals.Vessels` — every vessel in the save, loaded or not,
                // with unloaded orbits kept on rails. So it is fully describable from
                // any scene that has a universe, and there is no reason to make the
                // player fly out to one satellite to press a button on the other N-1.
                // The server agrees: `_bot_mission_evidence_problem` judges a
                // constellation on the fleet and asks for no active vessel.
                if (HighLogic.LoadedSceneIsFlight)
                {
                    SceneValid = true;
                    GeneKermanMod.Instance.RunCoroutine(PreparePhysicsThenCapture());
                }
                else if (HighLogic.LoadedScene == GameScenes.TRACKSTATION
                         || HighLogic.LoadedScene == GameScenes.SPACECENTER)
                {
                    SceneValid = true;
                    ValidateNetworkOnly();
                }
                else
                {
                    ValidationMsg = "This is a satellite-network mission.\nSubmit from "
                                  + "the Tracking Station, the Space Centre, or flight.";
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
                    ValidationMsg = IsConstellation
                        ? "This is a satellite-network mission.\nFly to the target body, your whole network there is checked, not just this craft."
                        : "This is an active vessel mission.\nLaunch your craft and fly to the target to submit.";
                }
            }

            Touch();
        }

        private void Touch() => Changed?.Invoke();

        // ── Validation ──────────────────────────────────────────────────────

        /// <summary>
        /// Rescan the target body's sphere of influence and re-check the contract's
        /// network requirement against it. A no-op for every mission type that has
        /// none, so it is safe to call from the shared validation path.
        ///
        /// The body scanned is the contract's <see cref="RequiredBody"/>, not whatever
        /// the player is orbiting: the mission names where the network has to be, and
        /// the server counts against that same name. Falling back to the active
        /// vessel's body when the contract names none keeps a user-issued network
        /// contract (which may name no body at all) checkable rather than unsubmittable.
        /// </summary>
        private void CaptureConstellation()
        {
            FleetMembers = new List<VesselDataCollector.VesselSnapshot>();
            FleetProblems = new List<string>();

            if (!IsConstellation) return;
            // A constellation mission scans even when the contract carries no fleet
            // requirement — an empty constraint is a pure "is this orbiting" filter,
            // and the scan still has to be sent. Otherwise a server that classified
            // the mission but failed to attach the requirement would leave the client
            // sending no network at all, and the evidence gate would blame the mod.
            var want = FleetLimits ?? new FleetConstraint();

            CelestialBody body = null;
            if (!string.IsNullOrEmpty(RequiredBody))
            {
                foreach (var b in FlightGlobals.Bodies)
                    if (b != null && string.Equals(b.bodyName, RequiredBody,
                                                   StringComparison.OrdinalIgnoreCase))
                    { body = b; break; }
            }
            if (body == null) body = FlightGlobals.ActiveVessel?.mainBody;
            if (body == null) return;

            FleetMembers = VesselDataCollector.CaptureBodyFleet(body, want);
            FleetProblems = want.Check(FleetMembers, body.bodyName);

            // Drop ticks for members the scan no longer finds. A satellite can be
            // decoupled, deorbited or renamed between one rescan and the next, and a
            // stale id would otherwise sit in the set claiming to be part of a
            // hand-over that cannot include it.
            if (selectedMembers.Count > 0)
            {
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in FleetMembers)
                    if (!string.IsNullOrEmpty(m.vesselId)) present.Add(m.vesselId);
                selectedMembers.RemoveWhere(id => !present.Contains(id));
            }

            // The selected set is judged on its own terms, not as a subset of a set that
            // already passed. That is the whole point: a network of four bunched
            // satellites can satisfy "2 at 90 degrees" while the two the player picked
            // out of it sit 30 degrees apart, and handing those over would deliver
            // something the contract never asked for.
            HandoverProblems = new List<string>();
            if (HandoverApplies)
            {
                var chosen = SelectedMembers();
                HandoverProblems = chosen.Count == 0
                    ? new List<string> { "Pick the satellites to hand over." }
                    : want.Check(chosen, body.bodyName);
            }
        }

        /// <summary>The live vessels behind the ticked members, in scan order.
        ///
        /// Resolved by `vesselId` against `FlightGlobals.Vessels`, which lists every
        /// vessel in the save whether or not it is loaded — the same property the whole
        /// scan rests on. A member the save no longer has is skipped rather than
        /// substituted; the export's own out-parameter is what ends up recorded as handed
        /// over, so a gap here can only ever under-claim.</summary>
        private List<Vessel> SelectedMemberVessels()
        {
            var outList = new List<Vessel>();
            if (FlightGlobals.Vessels == null) return outList;
            foreach (var m in SelectedMembers())
            {
                if (m == null || string.IsNullOrEmpty(m.vesselId)) continue;
                foreach (var v in FlightGlobals.Vessels)
                {
                    if (v == null) continue;
                    if (v.id.ToString() == m.vesselId) { outList.Add(v); break; }
                }
            }
            return outList;
        }

        /// <summary>The scanned members the player has ticked, in scan order.</summary>
        public List<VesselDataCollector.VesselSnapshot> SelectedMembers()
        {
            var outList = new List<VesselDataCollector.VesselSnapshot>();
            foreach (var m in FleetMembers)
                if (m != null && IsMemberSelected(m.vesselId)) outList.Add(m);
            return outList;
        }

        /// <summary>Validate a constellation from a scene with no active vessel.
        ///
        /// The fleet is the whole subject here, so `FleetProblems` is the whole answer:
        /// there is no craft to compare a body or a situation against, and there should
        /// not be — `CaptureConstellation` scans the contract's own RequiredBody, and a
        /// qualifying member is orbiting by definition of qualifying.</summary>
        private void ValidateNetworkOnly()
        {
            ActiveVessel = null;
            CaptureConstellation();
            VesselValid = FleetProblems.Count == 0;
            ValidationMsg = VesselValid
                ? ""
                : string.Join("\n", FleetProblems.ToArray());
        }

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

            // The SOI scan lives here rather than beside BuildNearbyEntries because it
            // is not a capture of the scene the player is in — it reads the whole save,
            // costs no frames, and has to be fresh every time the button is re-derived
            // (a satellite launched a minute ago must count without reopening the
            // window). Every caller of this method has just re-read the active vessel,
            // so this is the one point where "what the button knows" is rebuilt.
            CaptureConstellation();
            foreach (var v in FleetProblems)
            {
                VesselValid = false;
                issues.Add(v);
            }

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
                else if (rescueTarget.hasPos)
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
                    // Only when the issuer asked for a particular orbit. Without one the
                    // requirement is the body and the situation above — any orbit of it
                    // counts — while the plane and regime below are still checked, since
                    // those can be asked for on their own.
                    if (rescueTarget.hasAlt)
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
                issues.Add($"Δv unreadable. This rescue needs at least {rescueTarget.minDv:F0} m/s left. " +
                           "Turn on the stock Δv readout to check it here; the server checks it either way.");
                return;
            }

            // Same 0.5% slack the mission-limit Δv check allows, so a craft sitting right
            // on the number isn't failed by rounding.
            if (dv >= rescueTarget.minDv * 0.995) return;

            VesselValid = false;
            issues.Add($"Not enough Δv left: {dv:F0} m/s, need at least {rescueTarget.minDv:F0} m/s " +
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

            // Every flight submission can now carry extra craft, rescue included, so
            // the neighbour list has to mean the same thing on both: what is in *stock*
            // physics range. PRE's inflated bubble would otherwise offer a rescuer a
            // craft 40 km away as "in range" while the "within N km" box measured
            // against a range the recipient's game does not have.
            PreDisabledByUs = PhysicsRangeManager.TryDisable();
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
            RescueExcludedNearby = 0;
            ExtrasAreHandedOver = true;

            var active = FlightGlobals.ActiveVessel;
            if (active == null) return;
            Vector3d aPos = active.GetWorldPos3D();

            // On a rescue, the things that must not be offered as extras (below).
            HashSet<uint> rescueHullParts = null;
            HashSet<string> strandedCrew = null;
            HashSet<string> wreckPids = null;
            if (IsRescue)
            {
                // The wreck, identified from both directions. The server pins
                // wreck_parts only for a "vessel" recovery, so on a crew-only rescue it
                // has nothing to say; this save's own record of every wreck it has
                // spawned covers that, and covers a second rescue's wreck parked in the
                // same place, which the contract in hand could never describe.
                rescueHullParts = new HashSet<uint>();
                if (rescueTarget != null && rescueTarget.wreckParts != null)
                    foreach (var fid in rescueTarget.wreckParts) rescueHullParts.Add(fid);

                var scenario = GKContractScenario.Instance;
                bool knowsThisWreck = rescueHullParts.Count > 0;
                if (scenario != null)
                {
                    foreach (var fid in scenario.AllRescueWreckPartIds()) rescueHullParts.Add(fid);
                    wreckPids = scenario.AllRescueWreckPids();
                    // Immunity records name a wreck whose crew are still frozen — a
                    // different contract's, at this point, since ours must be aboard to
                    // submit at all. Cheap, and it catches the case before the pid record
                    // existed for that contract.
                    if (scenario.Immunities != null)
                        foreach (var rec in scenario.Immunities)
                            if (rec != null && !string.IsNullOrEmpty(rec.VesselPid))
                                wreckPids.Add(rec.VesselPid);
                    if (scenario.KnowsRescueWreck(ContractId)) knowsThisWreck = true;
                }

                // Fail closed. Not knowing which craft the wreck is means every tick in
                // this list could be it, so nothing here is handed over — the section
                // falls back to the renders-and-telemetry behaviour it has always had.
                ExtrasAreHandedOver = knowsThisWreck;

                if (rescueKerbals != null && rescueKerbals.Count > 0)
                    strandedCrew = new HashSet<string>(rescueKerbals, StringComparer.Ordinal);
            }

            foreach (var v in VesselDataCollector.GetNearbyVessels(active))
            {
                if (IsRescue && IsPartOfThisRescue(v, rescueHullParts, strandedCrew, wreckPids))
                {
                    RescueExcludedNearby++;
                    continue;
                }

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

        /// <summary>
        /// Whether a neighbouring craft is part of a rescue rather than something the
        /// rescuer brought along, and so must never be offered as an extra.
        ///
        /// Three tests, in the order of how much they prove.
        ///
        /// <paramref name="hullParts"/> is part flightIDs — the same identity
        /// <see cref="ValidateWreckAboard"/> matches on, the one thing that survives the
        /// export/import that put the wreck in this save, and the one that survives it
        /// being docked to and released again. It is the union of what the server pinned
        /// for this contract (a "vessel" recovery only) and what this save recorded when
        /// it spawned any wreck of its own.
        ///
        /// <paramref name="wreckPids"/> is the same wrecks by vessel pid, plus any wreck
        /// still holding frozen crew. Weaker than flightIDs — a pid does not survive
        /// undocking — but it costs one comparison and covers a wreck recorded before
        /// its parts were.
        ///
        /// <paramref name="stranded"/>: a stranded kerbal standing on EVA is a vessel in
        /// their own right. Sending one as an "extra" would offer the issuer a second
        /// copy of a kerbal whose only correct route home is aboard the rescue craft,
        /// which is what <see cref="ValidateRescue"/> already insists on.
        ///
        /// The first two are deliberately not scoped to *this* contract: a rescuer
        /// running two rescues must not hand the second one's wreck to the first one's
        /// issuer. Over-excluding costs a craft that could have been sent; under-
        /// excluding gives away a hull carrying somebody else's contract.
        /// </summary>
        private bool IsPartOfThisRescue(Vessel v, HashSet<uint> hullParts,
                                        HashSet<string> stranded, HashSet<string> wreckPids)
        {
            if (v == null) return true;

            if (hullParts != null && hullParts.Count > 0)
            {
                if (v.parts != null)
                    foreach (Part p in v.parts)
                        if (p != null && hullParts.Contains(p.flightID)) return true;
                // Loaded vessels have live parts; read the snapshots too rather than
                // let a vessel mid-load answer "not the wreck" because its part list
                // hasn't been built yet.
                var snaps = v.protoVessel?.protoPartSnapshots;
                if (snaps != null)
                    foreach (var pps in snaps)
                        if (pps != null && hullParts.Contains(pps.flightID)) return true;
            }

            if (wreckPids != null && wreckPids.Count > 0 && wreckPids.Contains(v.id.ToString()))
                return true;

            if (stranded != null)
                foreach (var pcm in VesselTransfer.CrewOf(v))
                    if (pcm != null && stranded.Contains(pcm.name)) return true;

            return false;
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
                if (nearbyEntries == null) return 0;
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
            string activePath = VesselRenderer.CaptureVessel(active);
            if (!string.IsNullOrEmpty(activePath)) screenshotPaths.Add(activePath);

            if (nearbyEntries != null)
            {
                foreach (var e in nearbyEntries)
                {
                    if (!e.Selected || e.Vessel == null) continue;
                    string ep = VesselRenderer.CaptureVessel(e.Vessel);
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
                if (nearbyEntries != null)
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
            // A network submitted from the Tracking Station or the Space Centre has no
            // active vessel by design: the satellites are the submission. `VesselValid`
            // there is the fleet verdict from ValidateNetworkOnly, so it is the whole
            // gate. In flight it still carries the fleet verdict too (ValidateVesselState
            // folds FleetProblems in), so this stays correct in both scenes.
            // The hand-over is a second requirement, not a restatement of the first: the
            // network can match the contract while the satellites picked out of it do not.
            if (HandoverApplies && HandoverProblems.Count > 0) return false;
            if (IsConstellation) return VesselValid;
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
            // A simulated launch (RP-1/KCT's "Simulate", KRASH) is refused before
            // anything is read or uploaded: the flight is free, instant and about to
            // be reverted, so the claim "this craft flew here" is empty. Editor
            // builds are exempt — a blueprint has no flight to have simulated. The
            // watchdog taints sim-flown vessels too (the server-side backstop); this
            // check is the one that gets to explain itself. Checked again at submit
            // time rather than at panel-open, since a sim can't start mid-flight but
            // an old cached answer helps nobody.
            if (MissionType != "craft_build" && SimulationDetection.SimulationActive(out string simTool))
            {
                Fail($"This flight is a {simTool} simulation.\nSimulated launches can't be submitted; fly the mission for real.");
                yield break;
            }

            // A vessel already given away (quicksent, or issued as a rescue) and only
            // waiting for its deferred removal is not ours to hand over again.
            if (MissionType != "craft_build")
            {
                string sentRefusal = GeneKermanMod.Instance?.PendingRemovalRefusal(FlightGlobals.ActiveVessel);
                if (sentRefusal != null) { Fail(sentRefusal); yield break; }

                // Same question for every craft ticked alongside it, when ticking one
                // hands it over. A selected extra is as much a live vessel leaving this
                // save as the contract craft is, and a hull already given away and only
                // waiting for its deferred removal must not go to a second recipient —
                // that is the 2026-08-30 duplicate, reached through the extras list
                // instead of through the active vessel.
                if (ExtrasAreHandedOver && nearbyEntries != null)
                {
                    foreach (var e in nearbyEntries)
                    {
                        if (!e.Selected || e.Vessel == null) continue;
                        string extraRefusal = GeneKermanMod.Instance?.PendingRemovalRefusal(e.Vessel);
                        if (extraRefusal != null)
                        {
                            Fail($"\"{e.Vessel.vesselName}\" can't be sent with this submission.\n"
                                 + extraRefusal);
                            yield break;
                        }
                    }
                }

                // And for every satellite ticked on a network being bought. A relay is a
                // live vessel leaving this save exactly as a ticked extra is, so it is the
                // same 2026-08-30 duplicate reached through a third door: a satellite
                // already handed to one buyer, still waiting on its deferred removal,
                // must not be sold to a second.
                if (HandoverApplies)
                {
                    foreach (var v in SelectedMemberVessels())
                    {
                        string memberRefusal = GeneKermanMod.Instance?.PendingRemovalRefusal(v);
                        if (memberRefusal != null)
                        {
                            Fail($"\"{v.vesselName}\" can't be handed over with this network.\n"
                                 + memberRefusal);
                            yield break;
                        }
                    }
                }
            }

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
            // Declared out here because the rescue hand-over record below has to name
            // exactly the craft that were packed, and nothing else may.
            var selectedExtras = new List<Vessel>();
            List<string> handedOverPids = null;

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
                };
                // Absent on a network submitted from the Tracking Station: there is no
                // active vessel, and the server's gate no longer asks for one on a
                // constellation. Every other mission type still has one by the time it
                // reaches here, because its scene check demands flight.
                if (ActiveVessel != null)
                    submission["active_vessel"] = ActiveVessel.ToDict();

                // Extra crafts the player toggled on. Offered on a rescue too — a
                // rescuer who towed a stage out or flew a support craft alongside has
                // something to show for it — but what "sent" means differs, and the
                // difference is decided at the vessel-node export below, not here.
                if (nearbyEntries != null)
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

                // On a bought network the satellites ARE the sent vessels, so they go in
                // the key that already means exactly that. This is the one case where a
                // constellation member is not "telemetry and nothing else": it is leaving
                // the save, and the server needs its orbit to check that what is being
                // handed over meets the requirement on its own — not merely that the
                // scan it was picked from did.
                if (HandoverApplies)
                {
                    var handedList = new List<object>();
                    foreach (var m in SelectedMembers())
                    {
                        var d = m.ToDict();
                        d["sent"] = true;
                        handedList.Add(d);
                    }
                    if (handedList.Count > 0) submission["sent_vessels"] = handedList;
                }

                // The network this mission is judged on. Rescanned at pack time rather
                // than reused from validation: the two are seconds apart, but those are
                // seconds in which a satellite can be decoupled, and the payload has to
                // describe the save as it is when it is sent, not as the button saw it.
                //
                // Deliberately its own key rather than more `sent_vessels`. Those two
                // lists mean opposite things: an entry in sent_vessels is a live craft
                // LEAVING this save, and every one of them costs a render upload. A
                // constellation member is telemetry and nothing else — it stays put, it
                // is never rendered, and it is capped where a network is plausibly
                // capped rather than where an image budget is.
                if (IsConstellation)
                {
                    CaptureConstellation();
                    var fleetList = new List<object>();
                    foreach (var m in FleetMembers) fleetList.Add(m.ToDict());
                    submission["constellation"] = fleetList;
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
                //
                // On a rescue the extras are handed over too — but only when this save
                // can say which craft the stranded wreck is. ExtrasAreHandedOver is that
                // answer (see BuildNearbyEntries); false keeps the older behaviour, where
                // a ticked craft rides along as a render and telemetry and stays here.
                //
                // Everything the earlier note listed as a prerequisite is in place, and
                // each of them is load-bearing rather than tidy:
                //
                //  1. The delivery import is fleet-aware and reports what it spawned.
                //     ClientState's rescue_delivery / submission_restore handler now
                //     calls VesselTransfer.ImportDeliveredFleet, which understands a
                //     GKFLEET container, installs each vessel's embedded blueprint, and
                //     returns the spawned pids PRIMARY FIRST. LastImportedPid names the
                //     last vessel of a fleet, so the restore's re-key would otherwise
                //     pin an extra and a later approval would delete the wrong hull.
                //     The primary is identified by the "primaryPid" value ExportFleet
                //     writes on the container and by node order, which ExportFleet also
                //     guarantees — both, so neither being lost is fatal.
                //  2. GKContractScenario.rescueSubmittedPids holds a LIST per contract,
                //     primary first, and RecordRescueSubmission REPLACES it. The server
                //     overwrites delivered_vessel_node_url on every resubmission, so an
                //     appending record would queue the previous attempt's hulls for
                //     deletion when this one is approved. Its RECORD format survives a
                //     downgrade untouched: an older build reads pid with GetValue and
                //     gets the first value, which is the rescue craft, so it leaves the
                //     extras behind rather than deleting the wrong ship.
                //  3. Both removal triggers in GeneKermanMod.cs — MaybeHandleRescueRemoval
                //     and the authoritative ReconcileRescueVessels — go through
                //     QueueRescueHandover, which queues every recorded pid with crew fate
                //     BorrowedOnly (the rescuer's own pilots are not part of the
                //     hand-over) and attaches the contract's kerbal list to the FIRST
                //     entry only: RemoveContractCrew hunts those names save-wide, so an
                //     extra carrying them would kill the rescued crew off a rescue craft
                //     that may not have left yet. Not smuggled into
                //     ForgetRescueSubmission, which also runs on the cancelled-contract
                //     path where the craft stays.
                //  4. IsPartOfThisRescue no longer depends on the server having sent
                //     wreck flightIDs, which it does only for a "vessel" recovery. Every
                //     wreck this save spawns is recorded (pid + part flightIDs) at spawn,
                //     so an emptied stranded hull parked alongside on a crew-only rescue
                //     is recognised. A contract accepted before that record existed has
                //     neither source, and ExtrasAreHandedOver is false for it.
                //  5. A selected extra is now asked PendingRemovalRefusal too, at the top
                //     of this coroutine. It was only ever asked of the active vessel,
                //     which was sound while an extra never left the save; a hull already
                //     given away and waiting on its deferred removal would otherwise go
                //     to a second recipient through the extras list — the 2026-08-30
                //     duplicate by another door. And what is recorded as handed over is
                //     read back out of ExportFleet rather than rebuilt from the ticks,
                //     so an extra the export dropped is not deleted here for a delivery
                //     it was never in.
                //
                // The server half went first: _extract_crew_names and _extract_part_uids
                // in api_server.py read the primary VESSEL node only, so the rescue
                // rechecks cannot be satisfied by a craft riding alongside the one being
                // handed over. A "vessel" recovery therefore needs the wreck to arrive
                // docked, grabbed or towed as one vessel — ticking it as an extra does
                // not satisfy it, which is the semantics that check has always had.
                if (HandoverApplies)
                {
                    // A bought network is exported as a fleet of the ticked satellites and
                    // nothing else. Its primary is the first of them rather than the
                    // active vessel — there may not be one, since this can be submitted
                    // from the Tracking Station, and the craft the player happens to be
                    // flying is not part of what was bought either way.
                    //
                    // `handedOverPids` is read back out of the export, never rebuilt from
                    // the ticks: a satellite whose node export failed was not delivered,
                    // and recording it anyway would delete it here for a hand-over the
                    // recipient never received.
                    var chosen = SelectedMemberVessels();
                    if (chosen.Count > 0)
                    {
                        Vessel primary = chosen[0];
                        var rest = chosen.GetRange(1, chosen.Count - 1);
                        vesselNodeData = VesselTransfer.ExportFleet(
                            primary, rest, true, out handedOverPids);
                    }
                }
                else if (selectedExtras.Count > 0 && ExtrasAreHandedOver)
                {
                    vesselNodeData = VesselTransfer.ExportFleet(
                        FlightGlobals.ActiveVessel, selectedExtras, true, out handedOverPids);
                }
                else
                {
                    vesselNodeData = VesselTransfer.ExportActiveVessel(true);
                    if (FlightGlobals.ActiveVessel != null)
                        handedOverPids = new List<string>
                            { FlightGlobals.ActiveVessel.id.ToString() };
                }

                // The export is what leaves; the list is only a claim about it. An
                // export that produced nothing hands nothing over, and a craft recorded
                // as handed over is a craft deleted from this save on approval.
                if (string.IsNullOrEmpty(vesselNodeData)) handedOverPids = null;

                // On a bought network the export IS the delivery, so an empty one must
                // refuse rather than submit. Everywhere else an unreadable craft is a
                // poorer submission the issuer can still judge; here it is a purchase
                // with nothing in the box, and letting it through would approve a
                // payment for satellites that never left this save.
                if (HandoverApplies && string.IsNullOrEmpty(vesselNodeData))
                {
                    Fail("The satellites could not be packaged for hand-over. "
                         + "Nothing was submitted. Check KSP.log and try again.");
                    yield break;
                }
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

            // For rescue, remember every craft we handed over so each can be removed
            // from our save once the issuer approves and they are delivered.
            //
            // The ACTIVE vessel FIRST, then whatever was packed alongside it. That
            // order is not presentation: the removal path attaches the contract's
            // kerbal list to the first entry alone (RemoveContractCrew hunts those
            // names save-wide, so an extra carrying them would settle the rescued crew
            // on behalf of a hull that never held them), an older build reading this
            // record back takes the first value as the whole of it, and the delivered
            // node's own first VESSEL is what the server rechecks the rescue against.
            // Exactly the vessels ExportFleet packs, in the order it packs them.
            //
            // Taken from the export itself rather than rebuilt from what was ticked.
            // ExportFleet drops an extra it cannot serialise — one unreadable craft must
            // not fail a whole submission — so a list rebuilt from the ticks would name
            // a craft the issuer never receives, and approval would delete it here: a
            // ship destroyed at one end and never created at the other.
            // Recorded for a bought network too, not only a rescue. The record is
            // `contractId -> pids` and the removal it drives
            // (GeneKermanMod.MaybeHandleRescueRemoval -> PeekRescueSubmissions) queues
            // EVERY pid the contract recorded — which is why it, and not the per-vessel
            // gift echo, is the right mechanism for a handover of several satellites:
            // that echo carries a single `vessel_pid` and could only ever remove one of
            // them, leaving the rest behind as copies.
            //
            // Nothing is removed at submit. The server sends the removal notice only
            // after it has queued the network for the issuer, so the satellites leave
            // this save when the buyer actually has them and not before.
            List<string> submittedPids = (IsRescue || HandoverApplies) ? handedOverPids : null;

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
            // `GetSubmissionParts()` is null when there is no craft to read: it returns
            // `FlightGlobals.ActiveVessel?.parts`, and a network submitted from the
            // Tracking Station or the Space Centre has no active vessel by design. The
            // other two callers already allow for that — `PartsFingerprint` returns 0 and
            // `CheckCraft` returns no violations — and this one did not, so
            // `new List<Part>(null)` threw ArgumentNullException from inside the submit
            // coroutine. Unity logs the exception and stops the coroutine, which left the
            // button reading "Submitting…" for ever, sent no request, and put nothing in
            // the bot's log to explain it.
            //
            // An empty list is the right answer rather than a skip: a submission with no
            // craft genuinely has no life support to report, and the scan says so.
            var submissionParts = GetSubmissionParts();
            LifeSupportInfo ls = LifeSupportScan.Scan(
                submissionParts != null ? new List<Part>(submissionParts) : new List<Part>());

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

                        if (submittedPids != null && submittedPids.Count > 0)
                            GeneKermanMod.Instance.RecordRescueSubmission(ContractId, submittedPids);

                        if (reviewStatus == "approved")
                        {
                            int xp = MiniJSON.GetInt(result, "xp_awarded");
                            int coins = MiniJSON.GetInt(result, "coins_awarded");
                            GeneKermanMod.Instance.ShowNotification("Mission approved", $"+{coins} KCoins, +{xp} XP");
                            EditorPartEnforcer.Instance?.StopEnforcing();
                            Close();
                            GeneKermanMod.Instance.RefreshContracts();
                            // Out of flight the way the hand-over paths go. Nothing is
                            // waiting on it — see ReturnToSpaceCenterAfterSubmit — it is
                            // just that the mission is over and the flight is not.
                            GeneKermanMod.Instance.ReturnToSpaceCenterAfterSubmit();
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
                            // Same as the approved branch. Deliberately NOT on the
                            // "refused" branch above: a refusal is something the player
                            // may want to fix and resubmit from the flight they are in,
                            // and taking the scene away is the opposite of helpful.
                            GeneKermanMod.Instance.ReturnToSpaceCenterAfterSubmit();
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
