/*
 * CheatDetection.cs – Notices when a vessel's flight state was cheated into place,
 * and remembers it for submission time.
 *
 * A contract submission claims "this craft flew here". HyperEdit, VesselMover and
 * the F12 cheat menu's Set Position / Set Orbit make that claim free, and nothing
 * in the submitted telemetry can tell — the numbers describe a perfectly
 * consistent orbit, because the vessel really is there. The server's telemetry
 * consistency check therefore cannot catch a teleported (as opposed to forged)
 * submission. Only the client can, by being present when it happens.
 *
 * Two detection channels, because the tools differ in how visible they are:
 *
 *   - A physics watchdog (CheatWatchdog, per flight scene): the active vessel's
 *     state must be continuous. On rails the orbital elements are mathematically
 *     constant; off rails the position must extrapolate from the last tick's
 *     position and velocity; a change of main body must happen at the SOI
 *     boundary. Anything that breaks those rules — Set Orbit, Set Position,
 *     HyperEdit's orbit editor and lander — is a teleport regardless of which
 *     tool did it, which is what makes this future-proof against tools we have
 *     never heard of.
 *
 *   - Direct reads where a tool is polite enough to be readable: the stock
 *     CheatOptions toggles (infinite propellant, no heating, …) are plain static
 *     bools, and VesselMover exposes its "currently moving this vessel" state,
 *     probed via reflection (no reference; absent mod = no-op) because its slow
 *     glide is deliberately physical-looking and slips under the watchdog.
 *
 * Every threshold is tuned to prefer FALSE NEGATIVES: a missed cheat still faces
 * the issuer's review and the server's other gates, but a false positive
 * disqualifies an honest player with no appeal. Hence the generous jump
 * allowances, the grace ticks around every event that legitimately perturbs
 * physics (scene load, vessel switch, dock/undock/staging, pack/unpack), and the
 * outright skip of the on-rails element check when a mod that legitimately moves
 * on-rails orbits (Principia, PersistentThrust) is installed.
 *
 * Grace is an amnesty for DERIVATIVES only, never for state. The stock Set
 * Orbit / Set Position cheats teleport by way of the very events grace
 * forgives — GoOnRails, write the new state, unpack — so a grace tick that
 * re-baselines blindly adopts the cheated state and the watchdog never sees a
 * delta (which is how the first version shipped, and why it caught nothing).
 * State continuity is therefore judged on EVERY tick, grace or not.
 *
 * The continuity invariant is POSITION VIA THE ORBIT: propagate last tick's
 * conic to now and compare where the vessel would be against where its orbit
 * says it is. That one comparison was chosen over per-element checks after
 * both of the latter's blind spots bit in testing: FlightGlobals.SetShipOrbit
 * clears vessel.Landed before our next tick, so any branch selected by the
 * landed flags is choosable by the cheat itself; and "Rendezvous Me" (or any
 * same-orbit teleport) changes no element at all — only the phase. Position-
 * at-now catches every flavour, works packed or unpacked (the orbit driver is
 * fresh even when the physics representation is stale), and is immune to
 * legitimate velocity discontinuities like touchdown, because both the old
 * and new conic pass through the point where they exchanged. While landed the
 * surface lat/lon stands in for it (a parked pseudo-conic does not rotate
 * with the planet, so propagating it across a warp tick diverges for
 * legitimate reasons), and thresholds stay loose enough that no single tick
 * of legitimate physics can cross them.
 *
 * A detection taints the VESSEL, keyed by persistentId (stable across save/load,
 * and what the docking GameEvents hand us), not the player or the session: the
 * position of that vessel is what the cheat corrupted, and reverting/quickloading
 * to a save from before the cheat legitimately clears it (the taint store is
 * persisted through GKContractScenario, so OnLoad rolls it back with the rest of
 * the save). Taint spreads with position: docking with, undocking from, or
 * transferring crew via EVA to/from a tainted vessel taints the counterpart,
 * because "teleport next to the target and EVA over" is the obvious laundering.
 *
 * Tool PRESENCE (HyperEdit installed) is reported separately and must never
 * disqualify by itself — plenty of people keep HyperEdit for sandbox testing and
 * fly missions clean. The server is told "installed" as context, "tainted" as
 * the verdict, and only the latter is ever acted on.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GeneKerman
{
    public static class CheatDetection
    {
        // ── Taint store ─────────────────────────────────────────────────────
        //
        // Static, single source of truth. GKContractScenario persists it
        // (SaveTo/LoadFrom below) so it rides quicksaves and rolls back with
        // quickloads; being static keeps writers working even in the window
        // where the scenario instance doesn't exist yet (EnsureExists heals it
        // on the next Update, and the next OnSave picks the records up).

        private class TaintRecord
        {
            public string Name;                 // last known vessel name, for the report
            public double FirstUt;              // when the first reason landed
            public readonly List<string> Reasons = new List<string>();
            public readonly HashSet<string> Keys = new HashSet<string>();
        }

        private static readonly Dictionary<uint, TaintRecord> taints =
            new Dictionary<uint, TaintRecord>();

        private const int MaxReasonsPerVessel = 6;

        public static bool IsTainted(Vessel v)
            => v != null && taints.ContainsKey(v.persistentId);

        public static bool IsTaintedPid(uint pid) => taints.ContainsKey(pid);

        public static List<string> ReasonsFor(Vessel v)
        {
            if (v != null && taints.TryGetValue(v.persistentId, out var rec))
                return new List<string>(rec.Reasons);
            return new List<string>();
        }

        /// <summary>Record a detection against a vessel. `key` dedups: the same kind of
        /// detection on the same vessel is stored (and logged) once, so a cheat toggle
        /// left on doesn't grow a reason per physics tick.</summary>
        internal static void Taint(Vessel v, string key, string reason)
        {
            if (v == null) return;
            TaintPid(v.persistentId, v.vesselName, key, reason);
        }

        internal static void TaintPid(uint pid, string name, string key, string reason)
        {
            if (!taints.TryGetValue(pid, out var rec))
            {
                rec = new TaintRecord { Name = name ?? "?", FirstUt = Planetarium.GetUniversalTime() };
                taints[pid] = rec;
            }
            if (!string.IsNullOrEmpty(name)) rec.Name = name;
            if (!rec.Keys.Add(key)) return;
            if (rec.Reasons.Count < MaxReasonsPerVessel) rec.Reasons.Add(reason);
            Debug.Log($"[GeneKerman] Cheat detection: vessel '{rec.Name}' ({pid}): {reason}");
        }

        /// <summary>Taint spread: `to` inherits every reason of `from` (prefixed so the
        /// report says how it got there), when `from` is tainted.</summary>
        internal static void Spread(uint fromPid, uint toPid, string toName, string how)
        {
            if (fromPid == toPid) return;
            if (!taints.TryGetValue(fromPid, out var src)) return;
            TaintPid(toPid, toName, "spread:" + fromPid, $"{how} '{src.Name}', which was flagged for: {string.Join("; ", src.Reasons.ToArray())}");
        }

        // ── Persistence (called by GKContractScenario) ──────────────────────

        public static void SaveTo(ConfigNode scenarioNode)
        {
            try
            {
                var node = scenarioNode.AddNode("CHEAT_TAINTS");
                foreach (var kvp in taints)
                {
                    var rec = node.AddNode("RECORD");
                    rec.AddValue("pid", kvp.Key);
                    rec.AddValue("name", kvp.Value.Name ?? "?");
                    rec.AddValue("ut", kvp.Value.FirstUt);
                    foreach (var r in kvp.Value.Reasons) rec.AddValue("reason", r);
                    foreach (var k in kvp.Value.Keys) rec.AddValue("key", k);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CheatDetection.SaveTo failed: {ex.Message}");
            }
        }

        /// <summary>Replace the store with what the save carries. Always clears first —
        /// a null/empty node means a save with no taints, and stale in-memory records
        /// from another save (or from after this quicksave) must not survive into it.</summary>
        public static void LoadFrom(ConfigNode scenarioNode)
        {
            taints.Clear();
            if (scenarioNode == null) return;
            try
            {
                var node = scenarioNode.GetNode("CHEAT_TAINTS");
                if (node == null) return;
                foreach (ConfigNode rec in node.GetNodes("RECORD"))
                {
                    if (!uint.TryParse(rec.GetValue("pid"), out uint pid)) continue;
                    var t = new TaintRecord { Name = rec.GetValue("name") ?? "?" };
                    double.TryParse(rec.GetValue("ut"), out double ut);
                    t.FirstUt = ut;
                    foreach (var r in rec.GetValues("reason"))
                        if (!string.IsNullOrEmpty(r) && t.Reasons.Count < MaxReasonsPerVessel)
                            t.Reasons.Add(r);
                    foreach (var k in rec.GetValues("key"))
                        if (!string.IsNullOrEmpty(k)) t.Keys.Add(k);
                    if (t.Reasons.Count > 0) taints[pid] = t;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CheatDetection.LoadFrom failed: {ex.Message}");
            }
        }

        // ── Orbit helpers (shared with DebugMenuCheatWarning) ───────────────

        /// <summary>Copy a vessel's conic into a reusable Orbit instance. Returns
        /// null when the source can't be snapshotted (no orbit / no body).</summary>
        internal static Orbit SnapshotOrbit(Orbit src, Orbit reuse)
        {
            if (src == null || src.referenceBody == null) return null;
            var o = reuse ?? new Orbit();
            o.SetOrbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                       src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            return o;
        }

        /// <summary>Metres between where a snapshotted conic says the vessel would
        /// be at `ut` and where the current conic says it is. Both orbits must be
        /// around the same body (callers check). This is the teleport invariant:
        /// any external write to the trajectory — elements OR phase — moves it,
        /// while every legitimate hand-over (pack/unpack, touchdown, docking)
        /// keeps the two conics passing through the same current point.</summary>
        internal static double OrbitPositionDelta(Orbit snap, Orbit current, double ut)
        {
            Vector3d expected = snap.getRelativePositionAtUT(ut);
            Vector3d actual   = current.getRelativePositionAtUT(ut);
            return (expected - actual).magnitude;
        }

        // ── Installed-tool scan (context, never a verdict) ──────────────────

        private static List<string> toolsCache;

        public static List<string> InstalledCheatTools()
        {
            if (toolsCache != null) return toolsCache;
            var found = new List<string>();
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    string n = la?.name?.ToLowerInvariant() ?? "";
                    if (n.Contains("hyperedit") && !found.Contains("HyperEdit")) found.Add("HyperEdit");
                    if (n.Contains("vesselmover") && !found.Contains("VesselMover")) found.Add("VesselMover");
                }
            }
            catch { /* leave whatever was found */ }
            toolsCache = found;
            return found;
        }

        // ── Submission report ───────────────────────────────────────────────

        /// <summary>
        /// The JSON the submission attaches as `cheat_report`. Covers exactly the
        /// vessels being submitted (active + selected extras); `tainted` is the only
        /// field the server's disqualification gate acts on, `tools_installed` is
        /// context for the reviewer. Never returns null — an explicit clean report
        /// lets the server distinguish "checked and clean" from "client too old to
        /// check" (which it must, and does, treat as clean rather than reject).
        /// </summary>
        public static string BuildReportJson(List<Vessel> submitted)
        {
            var vessels = new List<object>();
            bool any = false;
            if (submitted != null)
            {
                foreach (var v in submitted)
                {
                    if (v == null || !taints.TryGetValue(v.persistentId, out var rec)) continue;
                    any = true;
                    vessels.Add(new Dictionary<string, object>
                    {
                        { "name", rec.Name ?? v.vesselName },
                        { "reasons", new List<object>(rec.Reasons.ToArray()) },
                        { "first_ut", rec.FirstUt },
                    });
                }
            }
            return MiniJSON.Serialize(new Dictionary<string, object>
            {
                { "reported", true },
                { "tainted", any },
                { "vessels", vessels },
                { "tools_installed", new List<object>(InstalledCheatTools().ToArray()) },
            });
        }
    }

    /// <summary>
    /// Per-flight-scene watchdog. Its own addon rather than a hook in
    /// GeneKermanMod.Update on purpose: detection is purely local bookkeeping (like
    /// the rescue guards) and must run for unlinked players and under the
    /// data-sharing opt-out too — nothing is transmitted until the player submits,
    /// which has its own consent gates. A quickload reloads the flight scene and so
    /// rebuilds this component with a fresh baseline for free.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CheatWatchdog : MonoBehaviour
    {
        // ── Tuning ──────────────────────────────────────────────────────────
        const int    GraceTicks     = 15;     // physics ticks of derivative amnesty after any legit perturbation
        const double JumpFloorM     = 2000.0; // m — never flag a jump smaller than this
        const double JumpSpeedMult  = 6.0;    // allowance = max(floor, mult · speed · dt)
        const double RailsSmaRel    = 0.005;  // on-rails element drift beyond these = Set Orbit
        const double RailsEccAbs    = 0.005;
        const double RailsIncDeg    = 0.05;

        // Cross-tick state continuity, judged during grace and across pack/unpack:
        // the JumpFloorM/JumpSpeedMult allowance applied to the orbit-propagation
        // position delta. One tick of the hardest legitimate physics diverges by
        // metres; a teleport by kilometres at minimum.
        const double MixedMaxDt     = 1.0;    // s — a landed endpoint is only judged at physics rate

        // Baseline of the last accepted tick.
        private bool     hasBaseline;
        private Guid     baseVesselId;
        private CelestialBody baseBody;
        private bool     baseOnRails;
        private Vector3d baseRelPos;
        private Vector3d baseObtVel;
        private double   baseSpeed, baseSma, baseEcc, baseInc, baseUt;
        private bool     baseLanded;
        private double   baseLat, baseLon, baseAlt;
        private Orbit    baseOrbitSnap;   // reused clone of last tick's conic
        private bool     baseOrbitValid;

        private int grace = GraceTicks;

        // Mods that legitimately change on-rails orbits (n-body, background thrust):
        // with one installed, the element-constancy rule is simply not true, so that
        // channel is disabled rather than mistuned. Jump/body checks stay on.
        private bool railsMotionModPresent;

        // VesselMover reflection handles (null when absent/unrecognised).
        private Type vmType;
        private MemberInfo vmInstance;    // static Instance/instance/fetch
        private MemberInfo vmMovingVessel; // Vessel-typed "currently moving" member
        private MemberInfo vmIsMoving;     // bool-typed fallback

        void Awake()
        {
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    string n = la?.name?.ToLowerInvariant() ?? "";
                    if (n.Contains("principia") || n.Contains("persistentthrust"))
                        railsMotionModPresent = true;
                }
            }
            catch { }
            ResolveVesselMover();

            GameEvents.onFlightReady.Add(OnPerturb0);
            GameEvents.onVesselChange.Add(OnPerturb1);
            GameEvents.onVesselGoOnRails.Add(OnPerturb1);
            GameEvents.onVesselGoOffRails.Add(OnPerturb1);
            GameEvents.onVesselWasModified.Add(OnPerturb1);
            GameEvents.onVesselDocking.Add(OnDocking);
            GameEvents.onVesselsUndocking.Add(OnUndocking);
            GameEvents.onCrewOnEva.Add(OnCrewMove);
            GameEvents.onCrewBoardVessel.Add(OnCrewMove);
        }

        void OnDestroy()
        {
            GameEvents.onFlightReady.Remove(OnPerturb0);
            GameEvents.onVesselChange.Remove(OnPerturb1);
            GameEvents.onVesselGoOnRails.Remove(OnPerturb1);
            GameEvents.onVesselGoOffRails.Remove(OnPerturb1);
            GameEvents.onVesselWasModified.Remove(OnPerturb1);
            GameEvents.onVesselDocking.Remove(OnDocking);
            GameEvents.onVesselsUndocking.Remove(OnUndocking);
            GameEvents.onCrewOnEva.Remove(OnCrewMove);
            GameEvents.onCrewBoardVessel.Remove(OnCrewMove);
        }

        // ── Legit perturbations: pause judgement, re-baseline ───────────────

        private void OnPerturb0() { grace = GraceTicks; }
        private void OnPerturb1(Vessel v)
        {
            // Only the vessel being judged matters; events for background vessels
            // (loading in/out of physics range) don't perturb the active one.
            if (v == null || v == FlightGlobals.ActiveVessel) grace = GraceTicks;
        }

        // ── Taint spread ────────────────────────────────────────────────────

        private void OnDocking(uint pid1, uint pid2)
        {
            // Fired before the merge with both persistent ids intact. Taint both
            // sides — whichever id the merged vessel keeps, it stays flagged.
            string n1 = NameOfPid(pid1), n2 = NameOfPid(pid2);
            CheatDetection.Spread(pid1, pid2, n2, "Docked with");
            CheatDetection.Spread(pid2, pid1, n1, "Docked with");
            grace = GraceTicks;
        }

        private void OnUndocking(Vessel oldV, Vessel newV)
        {
            if (oldV != null && newV != null)
                CheatDetection.Spread(oldV.persistentId, newV.persistentId, newV.vesselName, "Undocked from");
            grace = GraceTicks;
        }

        private void OnCrewMove(GameEvents.FromToAction<Part, Part> data)
        {
            // EVA out of / boarding into a tainted vessel carries the taint: the
            // kerbal's position is the cheat's product exactly as the hull's was.
            Vessel from = data.from?.vessel, to = data.to?.vessel;
            if (from != null && to != null && from != to)
                CheatDetection.Spread(from.persistentId, to.persistentId, to.vesselName, "Crew transferred from");
            grace = GraceTicks;
        }

        private static string NameOfPid(uint pid)
        {
            try
            {
                if (FlightGlobals.FindVessel(pid, out Vessel v) && v != null)
                    return v.vesselName;
            }
            catch { }
            return null;
        }

        // ── The watchdog ────────────────────────────────────────────────────

        void FixedUpdate()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null || !v.loaded) { hasBaseline = false; return; }

            PollStockCheats(v);
            PollVesselMover(v);
            PollSimulation(v);

            if (!hasBaseline || v.id != baseVesselId)
            {
                Rebaseline(v);
                grace = GraceTicks;
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            double dt = ut - baseUt;
            var body = v.mainBody;
            if (dt <= 0 || body == null) { Rebaseline(v); return; }

            if (grace > 0)
            {
                // Grace pauses the derivative rules below, not the state rules:
                // stock Set Orbit / Set Position pack the vessel first, so the
                // pack event's grace used to launder the teleport — the blind
                // re-baseline adopted the cheated state before judgement
                // resumed. Continuity is judged per tick, so the drift a legit
                // perturbation accumulates over the window never adds up into
                // one comparison, while a teleport is a single-tick jump.
                grace--;
                CheckStateContinuity(v, body, dt);
                Rebaseline(v);
                return;
            }

            if (body != baseBody)
            {
                CheckBodyJump(v, body, dt);
                Rebaseline(v);
                return;
            }

            bool onRails = v.packed;
            if (onRails != baseOnRails)
            {
                // Pack/unpack converts between orbit and physics state — a
                // change of representation, not of conic, so it is judged by
                // element continuity rather than waved through.
                CheckStateContinuity(v, body, dt);
                Rebaseline(v);
                return;
            }

            if (onRails)
            {
                if (!railsMotionModPresent) CheckRailsElements(v);
            }
            else
            {
                CheckPositionJump(v, body, dt);
            }
            Rebaseline(v);
        }

        private void Rebaseline(Vessel v)
        {
            hasBaseline  = true;
            baseVesselId = v.id;
            baseBody     = v.mainBody;
            baseOnRails  = v.packed;
            baseUt       = Planetarium.GetUniversalTime();
            if (v.mainBody != null)
                baseRelPos = v.CoMD - v.mainBody.position;
            baseObtVel = v.obt_velocity;
            baseSpeed  = baseObtVel.magnitude;
            if (v.orbit != null)
            {
                baseSma = v.orbit.semiMajorAxis;
                baseEcc = v.orbit.eccentricity;
                baseInc = v.orbit.inclination;
            }
            baseLanded = v.Landed || v.Splashed;
            baseLat    = v.latitude;
            baseLon    = v.longitude;
            baseAlt    = v.altitude;
            var snap = CheatDetection.SnapshotOrbit(v.orbit, baseOrbitSnap);
            if (snap != null) baseOrbitSnap = snap;
            baseOrbitValid = snap != null;
        }

        /// <summary>Judged on every tick the watchdog used to wave through — grace
        /// ticks and pack/unpack transitions. A legitimate perturbation (docking,
        /// staging, warp entry) changes the representation of the state or its
        /// derivatives, never the state itself: the vessel stays on the same conic,
        /// or at the same spot on the surface. Stock Set Orbit is precisely a
        /// one-tick state jump hidden inside a pack/unpack pair, which is why grace
        /// must pause the derivative rules and not this one.</summary>
        private void CheckStateContinuity(Vessel v, CelestialBody body, double dt)
        {
            // A body change during grace is still a body change — judged by the
            // same SOI-boundary rule (lenient about crossings near the edge).
            if (body != baseBody) { CheckBodyJump(v, body, dt); return; }

            bool landedNow = v.Landed || v.Splashed;
            if (landedNow && baseLanded)
            {
                // Surface frame: a landed vessel keeps its lat/lon through any
                // amount of warp; only driving moves it, at surface speed. This
                // is what catches Set Position relocating a landed vessel.
                double r     = Math.Max(1.0, body.Radius + Math.Max(0.0, (v.altitude + baseAlt) * 0.5));
                double horiz = SurfaceDistance(baseLat, baseLon, v.latitude, v.longitude, r);
                double vert  = Math.Abs(v.altitude - baseAlt);
                double err   = Math.Sqrt(horiz * horiz + vert * vert);
                double allow = Math.Max(JumpFloorM, JumpSpeedMult * Math.Max(v.srfSpeed, 1.0) * dt);
                if (err > allow)
                    CheatDetection.Taint(v, "teleport:surface",
                        $"Moved ~{err / 1000.0:F0} km across {body.bodyName}'s surface in one physics tick (Set Position, HyperEdit or a vessel mover)");
                return;
            }

            // A landed endpoint is only judged at physics rate: a parked
            // pseudo-conic doesn't rotate with the planet, so propagating it
            // across a long warp tick diverges for legitimate reasons.
            if ((landedNow || baseLanded) && dt > MixedMaxDt) return;

            // Under Principia/PersistentThrust a packed endpoint means the
            // conic may legitimately have been rewritten; skip, matching the
            // steady-state rails rule.
            if (railsMotionModPresent && (v.packed || baseOnRails)) return;
            if (!baseOrbitValid || v.orbit == null || v.orbit.referenceBody == null) return;

            // Position via the orbit, never via the physics state: the orbit
            // driver is fresh on the very tick a teleport lands (Set Orbit
            // writes it directly), while CoM/transform reads can be stale for
            // a freshly-packed vessel. Any trajectory write — elements or
            // phase alone ("Rendezvous Me") — moves position-at-now; every
            // legitimate hand-over (pack/unpack, touchdown, docking) keeps the
            // old and new conic passing through the same current point.
            double ut2   = Planetarium.GetUniversalTime();
            double err2  = CheatDetection.OrbitPositionDelta(baseOrbitSnap, v.orbit, ut2);
            double speed = Math.Max(v.obt_velocity.magnitude, baseSpeed);
            double allow2 = Math.Max(JumpFloorM, JumpSpeedMult * speed * dt);
            if (err2 > allow2)
                CheatDetection.Taint(v, "teleport:orbit",
                    $"Trajectory jumped ~{err2 / 1000.0:F0} km in one update (Set Orbit, Set Position, HyperEdit or similar)");
        }

        /// <summary>Great-circle distance (haversine) between two lat/lon pairs, in
        /// metres at the given radius. Handles longitude wrap-around.</summary>
        private static double SurfaceDistance(double lat1, double lon1, double lat2, double lon2, double radius)
        {
            const double D2R = Math.PI / 180.0;
            double dLat = (lat2 - lat1) * D2R;
            double dLon = (lon2 - lon1) * D2R;
            double sLat = Math.Sin(dLat / 2.0), sLon = Math.Sin(dLon / 2.0);
            double a = sLat * sLat + Math.Cos(lat1 * D2R) * Math.Cos(lat2 * D2R) * sLon * sLon;
            return radius * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
        }

        /// <summary>Off-rails: the position must extrapolate from last tick's position
        /// and velocity. Body-relative vectors so floating-origin/Krakensbane shifts
        /// cancel. The allowance scales with speed and dt (physical warp) and never
        /// drops below a floor no thrust or collision can cross in one tick — while a
        /// teleport is essentially never smaller than kilometres.</summary>
        private void CheckPositionJump(Vessel v, CelestialBody body, double dt)
        {
            Vector3d relPos = v.CoMD - body.position;
            Vector3d predicted = baseRelPos + baseObtVel * dt;
            double err = (relPos - predicted).magnitude;
            double speed = Math.Max(v.obt_velocity.magnitude, baseSpeed);
            double allow = Math.Max(JumpFloorM, JumpSpeedMult * speed * dt);
            if (err > allow)
                CheatDetection.Taint(v, "jump",
                    $"Position jumped ~{err / 1000.0:F0} km in one physics tick (Set Position, HyperEdit or a vessel mover)");
        }

        /// <summary>On rails the elements are mathematically constant — KSP moves the
        /// vessel *along* the conic, never off it — so any drift at all is an external
        /// write. Thresholds are still generous for conversion noise at pack time.</summary>
        private void CheckRailsElements(Vessel v)
        {
            var o = v.orbit;
            if (o == null) return;
            double smaScale = Math.Max(Math.Abs(baseSma), 1000.0);
            if (Math.Abs(o.semiMajorAxis - baseSma) / smaScale > RailsSmaRel ||
                Math.Abs(o.eccentricity - baseEcc) > RailsEccAbs ||
                Math.Abs(o.inclination - baseInc) > RailsIncDeg)
                CheatDetection.Taint(v, "rails",
                    "Orbit changed while on rails (Set Orbit, HyperEdit or similar)");
        }

        /// <summary>A real SOI transition hands the vessel over at the boundary; a
        /// teleport materialises it deep inside. The reachable-depth term keeps very
        /// high timewarp legit (one on-rails tick can legitimately travel far), and
        /// the Sun — whose SOI is infinite — is judged by where the vessel *left*
        /// the old body instead.</summary>
        private void CheckBodyJump(Vessel v, CelestialBody newBody, double dt)
        {
            double speed = Math.Max(v.obt_velocity.magnitude, baseSpeed);
            double soi = newBody.sphereOfInfluence;

            if (!double.IsInfinity(soi) && soi > 0)
            {
                double rNew = (v.CoMD - newBody.position).magnitude;
                double reachable = Math.Max(soi * 0.5, speed * dt * 2.0);
                if (rNew + reachable < soi)
                    CheatDetection.Taint(v, "body",
                        $"Appeared deep inside {newBody.bodyName}'s sphere of influence without crossing it (teleport)");
            }
            else if (baseBody != null && !double.IsInfinity(baseBody.sphereOfInfluence))
            {
                // Escaping to the Sun: must have happened near the old SOI's edge.
                double rOld = (v.CoMD - baseBody.position).magnitude;
                double escape = baseBody.sphereOfInfluence;
                double reachable = Math.Max(escape * 0.5, speed * dt * 2.0);
                if (rOld + reachable < escape)
                    CheatDetection.Taint(v, "body",
                        $"Left {baseBody.bodyName}'s sphere of influence without reaching its edge (teleport)");
            }
        }

        // ── Direct reads ────────────────────────────────────────────────────

        private void PollStockCheats(Vessel v)
        {
            // Plain static bools KSP itself exposes. Sampled every tick, deduped by
            // key in the store, so "left on for the whole flight" is one reason.
            if (CheatOptions.InfinitePropellant)
                CheatDetection.Taint(v, "cheat:fuel", "F12 cheat 'Infinite Propellant' enabled in flight");
            if (CheatOptions.InfiniteElectricity)
                CheatDetection.Taint(v, "cheat:ec", "F12 cheat 'Infinite Electricity' enabled in flight");
            if (CheatOptions.IgnoreMaxTemperature)
                CheatDetection.Taint(v, "cheat:heat", "F12 cheat 'Ignore Max Temperature' enabled in flight");
            if (CheatOptions.UnbreakableJoints)
                CheatDetection.Taint(v, "cheat:joints", "F12 cheat 'Unbreakable Joints' enabled in flight");
            if (CheatOptions.NoCrashDamage)
                CheatDetection.Taint(v, "cheat:crash", "F12 cheat 'No Crash Damage' enabled in flight");
        }

        // A simulated launch (RP-1/KCT's "Simulate", KRASH) is a flight that never
        // happened — free, instant, and reverted when it ends — so any vessel flown
        // during one is tainted. SubmissionSession refuses such a submit outright
        // with a friendlier message before upload; this taint is the backstop that
        // makes the server's gate refuse it too. The revert that ends every sim
        // reloads the pre-sim save, which rolls the taint store back with it, so a
        // sim leaves no mark on the real career.
        private void PollSimulation(Vessel v)
        {
            if (SimulationDetection.SimulationActive(out string tool))
                CheatDetection.Taint(v, "simulation",
                    $"Flown in a {tool} simulation - a simulated launch is not a real flight");
        }

        // VesselMover glides a vessel around at a chosen speed — per-tick deltas that
        // look physical, which is exactly why it gets its own channel instead of the
        // watchdog. All reflection, defensive-list style (see LsReflect): a fork that
        // renamed things degrades to "installed but not understood", never a break.
        private void ResolveVesselMover()
        {
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    if (!(la?.name?.ToLowerInvariant() ?? "").Contains("vesselmover")) continue;
                    foreach (var t in la.assembly.GetTypes())
                    {
                        if (t.Name != "VesselMove") continue;
                        var inst = (MemberInfo)t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                                ?? (MemberInfo)t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)
                                ?? (MemberInfo)t.GetField("instance", BindingFlags.Public | BindingFlags.Static)
                                ?? (MemberInfo)t.GetProperty("fetch", BindingFlags.Public | BindingFlags.Static);
                        if (inst == null) continue;
                        vmType = t;
                        vmInstance = inst;
                        vmMovingVessel =
                               (MemberInfo)FindMember(t, typeof(Vessel), "MovingVessel", "movingVessel");
                        vmIsMoving =
                               (MemberInfo)FindMember(t, typeof(bool), "IsMovingVessel", "isMovingVessel", "isMoving");
                        return;
                    }
                }
            }
            catch { vmType = null; }
        }

        private static MemberInfo FindMember(Type t, Type wanted, params string[] names)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var n in names)
            {
                var p = t.GetProperty(n, F);
                if (p != null && wanted.IsAssignableFrom(p.PropertyType)) return p;
                var f = t.GetField(n, F);
                if (f != null && wanted.IsAssignableFrom(f.FieldType)) return f;
            }
            return null;
        }

        private static object ReadMember(MemberInfo m, object target)
        {
            try
            {
                if (m is PropertyInfo p) return p.GetValue(target, null);
                if (m is FieldInfo f) return f.GetValue(target);
            }
            catch { }
            return null;
        }

        private void PollVesselMover(Vessel active)
        {
            if (vmType == null || vmInstance == null) return;
            object inst = ReadMember(vmInstance, null);
            if (inst == null) return;

            // Prefer the exact vessel being moved; fall back to "it says it's moving
            // something" against the active vessel (which is what VesselMover moves).
            if (vmMovingVessel != null)
            {
                if (ReadMember(vmMovingVessel, inst) is Vessel mv && mv != null)
                    CheatDetection.Taint(mv, "vesselmover", "Moved with VesselMover");
                return;
            }
            if (vmIsMoving != null && ReadMember(vmIsMoving, inst) is bool b && b)
                CheatDetection.Taint(active, "vesselmover", "Moved with VesselMover");
        }
    }
}
