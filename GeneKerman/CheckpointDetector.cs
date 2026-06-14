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

        public CheckpointDetector(System.Action<Checkpoint> onCheckpoint)
        {
            this.onCheckpoint = onCheckpoint;
        }

        /// <summary>Clear per-scene state. Call on scene changes so SOI/keys don't leak.</summary>
        public void Reset()
        {
            lastMainBody = null;
            firedKeys.Clear();
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
