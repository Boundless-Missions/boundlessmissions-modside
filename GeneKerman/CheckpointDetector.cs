/*
 * CheckpointDetector.cs – Notices flight milestones worth photographing.
 *
 * Ticked each frame while in flight. Watches the active vessel for:
 *   - rendezvous : another crewed/probe vessel comes within close range
 *   - asteroid   : an asteroid or comet (SpaceObject) comes within close range
 *   - flyby      : the vessel enters a new (non-home) body's sphere of influence
 *
 * When one fires it raises a single prompt (debounced globally and per target),
 * leaving the actual confirm/capture to the caller. Detection is intentionally
 * cheap — a short-interval distance scan over loaded vessels plus an SOI check.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>A milestone worth offering a hero shot for.</summary>
    public struct Checkpoint
    {
        public string kind;             // "rendezvous" | "asteroid" | "flyby"
        public string title;            // prompt heading
        public string message;          // prompt body
        public string label;            // filename-safe tag for the saved image
        public Vessel targetVessel;     // object of interest (object mode), or null
        public CelestialBody targetBody;// object of interest (backdrop mode), or null
    }

    public class CheckpointDetector
    {
        // ── Tuning ──────────────────────────────────────────────────────────
        const float RendezvousDist = 2200f;   // m — "close approach" threshold
        const float AsteroidDist   = 2200f;   // m
        const float ScanInterval   = 2f;       // s between distance scans
        const float GlobalCooldown = 45f;      // s — min gap between any two prompts
        const float PerKeyCooldown = 900f;     // s — don't re-offer the same subject

        private readonly System.Action<Checkpoint> onCheckpoint;

        private float lastScan;
        private float lastPromptTime = -9999f;
        private CelestialBody lastMainBody;
        private readonly Dictionary<string, float> firedKeys = new Dictionary<string, float>();

        /// <summary>Set by the host while a prompt or capture is in flight to pause detection.</summary>
        public bool Suspended { get; set; }

        /// <summary>
        /// Host gate — returns true when checkpoint capture is enabled (mirrors the
        /// setting the host checks before calling <see cref="Tick"/>). Consulted by
        /// the event-driven detectors, which fire outside the Tick loop.
        /// </summary>
        public System.Func<bool> IsCaptureEnabled { get; set; }

        public CheckpointDetector(System.Action<Checkpoint> onCheckpoint)
        {
            this.onCheckpoint = onCheckpoint;
        }

        // ── Event-driven milestones ─────────────────────────────────────────
        //
        // Proximity/SOI milestones are polled in Tick(); these are discrete game
        // events (EVA, staging, situation changes) that we hook directly. The host
        // calls Register()/Unregister() alongside its own GameEvents wiring.

        public void Register()
        {
            GameEvents.onCrewOnEva.Add(OnCrewEva);
            GameEvents.onStageActivate.Add(OnStage);
            GameEvents.onVesselSituationChange.Add(OnSituationChange);
        }

        public void Unregister()
        {
            GameEvents.onCrewOnEva.Remove(OnCrewEva);
            GameEvents.onStageActivate.Remove(OnStage);
            GameEvents.onVesselSituationChange.Remove(OnSituationChange);
        }

        /// <summary>Clear per-scene state. Call on scene changes so SOI/keys don't leak.</summary>
        public void Reset()
        {
            lastMainBody = null;
            firedKeys.Clear();
        }

        /// <summary>Shared pre-flight gate for the event-driven detectors.</summary>
        private bool CanFire()
        {
            if (Suspended) return false;
            if (!HighLogic.LoadedSceneIsFlight) return false;
            if (IsCaptureEnabled != null && !IsCaptureEnabled()) return false;
            return true;
        }

        private void OnCrewEva(GameEvents.FromToAction<Part, Part> data)
        {
            if (!CanFire()) return;

            Vessel ship = data.from != null ? data.from.vessel : null;     // craft left behind
            Vessel kerbal = data.to != null ? data.to.vessel : null;       // the new EVA vessel
            string who = kerbal != null ? kerbal.vesselName : "A kerbal";

            Fire(new Checkpoint
            {
                kind = "eva",
                title = "🚶 EVA",
                message = $"{who} stepped outside. Capture the spacewalk?",
                label = "eva_" + who,
                // Frame the spacewalker against the craft they left (when it isn't the
                // active vessel already); otherwise a lone portrait of the kerbal.
                targetVessel = (ship != null && ship != FlightGlobals.ActiveVessel) ? ship : null,
            }, key: "eva:" + who);
        }

        private void OnStage(int stage)
        {
            if (!CanFire()) return;

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            // Skip staging on the pad / sitting on a surface — only flight stagings
            // (booster sep, fairing/jettison during ascent, etc.) are worth a shot.
            if (!IsInSpace(vessel)) return;

            Fire(new Checkpoint
            {
                kind = "staging",
                title = "🔥 Staging",
                message = $"Stage {stage} separation. Capture the burn?",
                label = $"staging_s{stage}",
                // Portrait of the vessel — no paired subject.
            }, key: $"staging:{stage}");
        }

        private void OnSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (!CanFire()) return;
            if (data.host == null || data.host != FlightGlobals.ActiveVessel) return;

            var vessel = data.host;
            CelestialBody body = vessel.mainBody;
            string bodyName = body != null ? body.bodyName : "space";

            switch (data.to)
            {
                case Vessel.Situations.ORBITING:
                    Fire(new Checkpoint
                    {
                        kind = "orbit",
                        title = "🛰 Orbit achieved",
                        message = $"You've reached orbit around {bodyName}. Capture it with the body in frame?",
                        label = "orbit_" + bodyName,
                        targetBody = body,   // backdrop the body you're now circling
                    }, key: "orbit:" + bodyName);
                    break;

                case Vessel.Situations.LANDED:
                    Fire(new Checkpoint
                    {
                        kind = "landing",
                        title = "🛬 Touchdown",
                        message = $"Touchdown on {bodyName}. Capture the landing?",
                        label = "landing_" + bodyName,
                    }, key: "landing:" + bodyName);
                    break;

                case Vessel.Situations.SPLASHED:
                    Fire(new Checkpoint
                    {
                        kind = "splashdown",
                        title = "🌊 Splashdown",
                        message = $"Splashdown on {bodyName}. Capture the recovery?",
                        label = "splashdown_" + bodyName,
                    }, key: "splashdown:" + bodyName);
                    break;
            }
        }

        public void Tick()
        {
            if (Suspended) return;
            if (!HighLogic.LoadedSceneIsFlight) return;

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;

            // SOI tracking has to run every frame to catch the transition; the rest of
            // the (heavier) proximity scan is throttled.
            if (CheckFlyby(vessel)) return;

            float now = Time.realtimeSinceStartup;
            if (now - lastScan < ScanInterval) return;
            lastScan = now;

            if (CheckAsteroid(vessel)) return;
            if (CheckRendezvous(vessel)) return;
        }

        // ── Detectors ───────────────────────────────────────────────────────

        private bool CheckFlyby(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            if (lastMainBody == null) { lastMainBody = body; return false; }
            if (body == lastMainBody) return false;

            CelestialBody entered = body;
            lastMainBody = body;

            if (entered == null || entered.isHomeWorld) return false;
            // Only when actually flying/orbiting through the SOI, not sitting on a surface.
            if (!IsInSpace(vessel)) return false;

            string bodyName = entered.bodyName;
            return Fire(new Checkpoint
            {
                kind = "flyby",
                title = "🛰 " + bodyName + " flyby",
                message = $"You've reached {bodyName}. Capture a hero shot with it in the backdrop?",
                label = "flyby_" + bodyName,
                targetBody = entered,
            }, key: "flyby:" + bodyName);
        }

        private bool CheckAsteroid(Vessel vessel)
        {
            Vessel nearest = NearestLoaded(vessel, AsteroidDist,
                o => o.vesselType == VesselType.SpaceObject);
            if (nearest == null) return false;

            // Comets carry a "ModuleComet" PartModule; everything else of this type is
            // an asteroid. Matched by module name to avoid a hard type dependency that
            // would break on KSP versions without comets.
            string what = HasModule(nearest, "ModuleComet") ? "comet" : "asteroid";

            return Fire(new Checkpoint
            {
                kind = "asteroid",
                title = "☄ " + char.ToUpper(what[0]) + what.Substring(1) + " nearby",
                message = $"A {what} ({nearest.vesselName}) is in range. Capture it with your vessel?",
                label = what + "_" + nearest.vesselName,
                targetVessel = nearest,
            }, key: "asteroid:" + nearest.id);
        }

        private bool CheckRendezvous(Vessel vessel)
        {
            Vessel nearest = NearestLoaded(vessel, RendezvousDist, IsRendezvousTarget);
            if (nearest == null) return false;

            return Fire(new Checkpoint
            {
                kind = "rendezvous",
                title = "🤝 Rendezvous",
                message = $"Close approach with {nearest.vesselName}. Capture the moment together?",
                label = "rendezvous_" + nearest.vesselName,
                targetVessel = nearest,
            }, key: "rendezvous:" + nearest.id);
        }

        // ── Shared helpers ──────────────────────────────────────────────────

        private static bool IsRendezvousTarget(Vessel o)
        {
            switch (o.vesselType)
            {
                case VesselType.Debris:
                case VesselType.Unknown:
                case VesselType.SpaceObject:
                case VesselType.Flag:
                case VesselType.EVA:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>True if any part on the vessel carries a PartModule with the given name.</summary>
        private static bool HasModule(Vessel vessel, string moduleName)
        {
            if (vessel?.parts == null) return false;
            foreach (var part in vessel.parts)
            {
                if (part?.Modules == null) continue;
                foreach (var m in part.Modules)
                {
                    if (m != null && m.moduleName == moduleName) return true;
                }
            }
            return false;
        }

        private static bool IsInSpace(Vessel vessel)
        {
            switch (vessel.situation)
            {
                case Vessel.Situations.ORBITING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                case Vessel.Situations.FLYING:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Nearest loaded vessel (other than the active one) within range matching a filter.</summary>
        private static Vessel NearestLoaded(Vessel self, float maxDist, System.Func<Vessel, bool> filter)
        {
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return null;

            Vessel best = null;
            float bestDist = maxDist;
            Vector3 selfPos = self.CoM;

            foreach (var o in loaded)
            {
                if (o == null || o == self || !o.loaded) continue;
                if (!filter(o)) continue;

                float d = Vector3.Distance(selfPos, o.CoM);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = o;
                }
            }
            return best;
        }

        /// <summary>Raise a checkpoint if cooldowns allow. Returns true when it fired.</summary>
        private bool Fire(Checkpoint cp, string key)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastPromptTime < GlobalCooldown) return false;
            if (firedKeys.TryGetValue(key, out float t) && now - t < PerKeyCooldown) return false;

            firedKeys[key] = now;
            lastPromptTime = now;
            onCheckpoint?.Invoke(cp);
            return true;
        }
    }
}
