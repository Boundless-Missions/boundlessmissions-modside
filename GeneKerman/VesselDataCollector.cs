/*
 * VesselDataCollector.cs – Extracts vessel information from KSP for submission.
 *
 * Collects data from:
 *   - FlightGlobals.ActiveVessel (current vessel in flight)
 *   - FlightGlobals.VesselsLoaded (all vessels in physics range)
 *   - FlightGlobals.Vessels (EVERY vessel in the save, loaded or not — the whole-SOI
 *     scan a constellation mission is judged on; see CaptureBodyFleet)
 *   - .craft files from Ships/ or save Ships/ directories
 *   - .loadmeta files alongside craft files
 */

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class VesselDataCollector
    {
        // ── Vessel Snapshot ─────────────────────────────────────────────────

        public class VesselSnapshot
        {
            public string vesselName;
            public string vesselType;
            public string situation;
            public string body;
            public double latitude;
            public double longitude;
            public double altitude;
            // Orbital elements
            public double sma;
            public double eccentricity;
            public double inclination;
            public double apoapsis;     // altitude above the body's surface (m)
            public double periapsis;    // altitude above the body's surface (m)
            public double period;       // orbital period (s)
            public double bodyRadius;   // main body equatorial radius (m)
            public double rotationPeriod; // main body sidereal rotation period (s)
            // Orientation of the orbit in its plane, degrees. Only a constellation
            // check reads these — they answer "where around the body is this one",
            // which apoapsis and inclination cannot. LAN + argPe + meanAnomaly is the
            // phase angle you would draw on the map view, and unlike true anomaly it
            // advances uniformly, so members captured at one instant compare directly.
            // Summing is left to the two verifiers so they cannot derive it apart.
            public double lan;          // longitude of the ascending node (deg)
            public double argPe;        // argument of periapsis (deg)
            public double meanAnomaly;  // mean anomaly at this instant (deg)
            // Whether this craft can relay for others. Read from the part prefab
            // rather than from a live PartModule, because a constellation is mostly
            // unloaded and an unloaded part has no modules instantiated.
            public bool hasRelay;
            /// <summary>Strongest relay antenna's power, 0 when the craft cannot relay.
            /// Lets a contract ask for relays of a given strength without naming a part.</summary>
            public double relayPower;
            // The save's own id for this vessel. Sent so the server can see the same
            // craft submitted twice under two names — one satellite cannot be three.
            public string vesselId = "";
            // Craft metadata
            public int partCount;
            public float totalMass;
            public float totalCost;
            public int crewCount;
            // Crew aboard by profession ("Pilot" -> 2), for a contract that asks for
            // particular ones. Keyed by ProtoCrewMember.trait rather than by anything
            // this install resolved, so it still says "Kolonist" where the mod that
            // defines Kolonists isn't installed.
            public Dictionary<string, int> crewTraits = new Dictionary<string, int>();

            public Dictionary<string, object> ToDict()
            {
                return new Dictionary<string, object>
                {
                    { "vessel_name", vesselName },
                    { "vessel_type", vesselType },
                    { "situation", situation },
                    { "body", body },
                    { "latitude", latitude },
                    { "longitude", longitude },
                    { "altitude", altitude },
                    { "sma", sma },
                    { "eccentricity", eccentricity },
                    { "inclination", inclination },
                    { "apoapsis", apoapsis },
                    { "periapsis", periapsis },
                    { "period", period },
                    { "body_radius", bodyRadius },
                    { "rotation_period", rotationPeriod },
                    { "part_count", partCount },
                    { "total_mass", (double)totalMass },
                    { "total_cost", (double)totalCost },
                    { "crew_count", crewCount },
                    { "crew_traits", CrewTraitsForJson() },
                    { "lan", lan },
                    { "arg_pe", argPe },
                    { "mean_anomaly", meanAnomaly },
                    { "has_relay", hasRelay },
                    { "relay_power", relayPower },
                    { "vessel_id", vesselId },
                };
            }

            /// <summary>MiniJSON serialises Dictionary&lt;string, object&gt;, so the counts
            /// are boxed into one rather than handed over as ints.</summary>
            private Dictionary<string, object> CrewTraitsForJson()
            {
                var o = new Dictionary<string, object>();
                foreach (var kvp in (crewTraits ?? new Dictionary<string, int>()))
                    o[kvp.Key] = kvp.Value;
                return o;
            }
        }

        // ── Active Vessel ───────────────────────────────────────────────────

        public static VesselSnapshot CaptureActiveVessel()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return null;

            return CaptureVessel(vessel);
        }

        public static VesselSnapshot CaptureVessel(Vessel vessel)
        {
            var snap = new VesselSnapshot
            {
                vesselName = vessel.vesselName,
                vesselType = vessel.vesselType.ToString(),
                situation = vessel.situation.ToString(),
                body = vessel.mainBody?.bodyName ?? "Unknown",
                latitude = vessel.latitude,
                longitude = vessel.longitude,
                altitude = vessel.altitude,
                partCount = vessel.parts?.Count ?? 0,
                crewCount = VesselTransfer.CrewCountOf(vessel),
                crewTraits = ContractConstraints.CountCrewTraits(VesselTransfer.CrewOf(vessel)),
            };

            // Mass and cost
            if (vessel.parts != null)
            {
                float mass = 0f;
                foreach (var part in vessel.parts)
                    mass += part.mass + part.GetResourceMass();
                snap.totalMass = mass;
                snap.totalCost = GetVesselCost(vessel.parts);
            }

            // Orbital elements
            if (vessel.orbit != null)
            {
                snap.sma = vessel.orbit.semiMajorAxis;
                snap.eccentricity = vessel.orbit.eccentricity;
                snap.inclination = vessel.orbit.inclination;
                snap.apoapsis = vessel.orbit.ApA;   // altitude above surface
                snap.periapsis = vessel.orbit.PeA;  // altitude above surface
                snap.period = vessel.orbit.period;
                FillOrbitAngles(snap, vessel.orbit);
            }
            snap.vesselId = vessel.id.ToString();
            snap.relayPower = BestRelayPower(vessel);
            snap.hasRelay = snap.relayPower > 0.0;
            if (vessel.mainBody != null)
            {
                snap.bodyRadius = vessel.mainBody.Radius;
                snap.rotationPeriod = vessel.mainBody.rotationPeriod;
            }

            return snap;
        }

        // ── Cost ────────────────────────────────────────────────────────────

        /// <summary>
        /// Full KSP funds cost of a single part — the figure the game itself charges.
        /// <para/>
        /// <c>part.partInfo.cost</c> alone is only the PREFAB dry cost and is wrong for
        /// most real crafts: it omits (1) fuel/resource cost, and (2) every
        /// <c>IPartCostModifier</c> module — most importantly TweakScale, so a scaled-up
        /// part would otherwise report its small unscaled price. We add the module cost
        /// deltas (<c>GetModuleCosts</c>) and the loaded-resource cost to match KSP's own
        /// total. Works on editor and flight parts alike.
        /// </summary>
        public static float GetPartCost(Part part)
        {
            if (part?.partInfo == null) return 0f;

            float baseCost = part.partInfo.cost;
            // Module modifiers (TweakScale, variants, fuel switches, …). Pass the base
            // cost as KSP does so percentage-based modifiers resolve correctly.
            float modules = 0f;
            try { modules = part.GetModuleCosts(baseCost); } catch { /* prefab/compile — skip */ }

            // Loaded resources (fuel, monoprop, ablator, …): amount × per-unit cost.
            float resources = 0f;
            if (part.Resources != null)
                foreach (PartResource r in part.Resources)
                    if (r?.info != null) resources += (float)(r.amount * r.info.unitCost);

            return baseCost + modules + resources;
        }

        /// <summary>Full funds cost of an entire craft — sum of <see cref="GetPartCost"/>
        /// over every part. Accepts any part list (flight vessel or editor ship).</summary>
        public static float GetVesselCost(IList<Part> parts)
        {
            float total = 0f;
            if (parts != null)
                foreach (Part p in parts) total += GetPartCost(p);
            return total;
        }

        // ── All Loaded Vessels ──────────────────────────────────────────────

        public static List<VesselSnapshot> CaptureLoadedVessels()
        {
            var snapshots = new List<VesselSnapshot>();

            if (FlightGlobals.VesselsLoaded == null) return snapshots;

            foreach (var vessel in FlightGlobals.VesselsLoaded)
            {
                if (!IsTransferable(vessel)) continue;
                snapshots.Add(CaptureVessel(vessel));
            }

            return snapshots;
        }

        /// <summary>
        /// Real Vessel references for every transferable craft currently in physics
        /// range, excluding <paramref name="active"/>. Used by the submit window to
        /// offer extra crafts for multi-vessel submission (it needs the live Vessel,
        /// not just a snapshot, to export the full state).
        /// </summary>
        public static List<Vessel> GetNearbyVessels(Vessel active)
        {
            var result = new List<Vessel>();
            if (FlightGlobals.VesselsLoaded == null) return result;

            foreach (var vessel in FlightGlobals.VesselsLoaded)
            {
                if (vessel == null || vessel == active) continue;
                if (!IsTransferable(vessel)) continue;
                result.Add(vessel);
            }
            return result;
        }

        /// <summary>A vessel worth offering/transferring — excludes debris, asteroids
        /// and flags/EVA-less junk.</summary>
        private static bool IsTransferable(Vessel vessel)
        {
            if (vessel == null) return false;
            return vessel.vesselType != VesselType.Debris &&
                   vessel.vesselType != VesselType.SpaceObject &&
                   vessel.vesselType != VesselType.Unknown &&
                   vessel.vesselType != VesselType.Flag;
        }

        // ── Whole-SOI scan (constellation missions) ─────────────────────────

        /// <summary>
        /// Every vessel in <paramref name="body"/>'s sphere of influence that counts
        /// toward the network <paramref name="want"/> describes, loaded or not.
        ///
        /// Physics range is not the bound here, and that is the whole point. KSP only
        /// *loads* craft within ~2.5 km, so a relay network spread around the Mun is
        /// almost entirely unloaded — but <c>FlightGlobals.Vessels</c> lists every
        /// vessel in the save regardless, unloaded orbits are kept live on rails, and
        /// part lists survive as <c>protoPartSnapshots</c>. So a whole SOI can be read
        /// without loading anything, without touching PhysicsRangeManager, and without
        /// the frame cost that loading would bring: this walks an array that is already
        /// in memory.
        ///
        /// Filtered here rather than server-side-only so the player sees the count they
        /// will actually be graded on while they are still flying. The server re-runs
        /// the identical test on what arrives (fleet_constraints.is_member) — this is a
        /// pre-check, not the authority.
        /// </summary>
        public static List<VesselSnapshot> CaptureBodyFleet(CelestialBody body, FleetConstraint want)
        {
            var members = new List<VesselSnapshot>();
            if (body == null || FlightGlobals.Vessels == null) return members;

            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v.mainBody != body) continue;
                if (!IsTransferable(v)) continue;      // debris, flags, asteroids
                if (v.vesselType == VesselType.EVA) continue;

                VesselSnapshot snap;
                try { snap = v.loaded ? CaptureVessel(v) : CaptureUnloadedVessel(v); }
                catch (Exception ex)
                {
                    // One unreadable vessel must not cost the player the whole scan;
                    // a save can hold a wreck no part of this code can make sense of.
                    Debug.LogWarning($"[GeneKerman] Fleet scan skipped '{v.vesselName}': {ex.Message}");
                    continue;
                }

                if (want != null && !want.IsMember(snap)) continue;
                members.Add(snap);
                if (members.Count >= FleetConstraint.MaxMembers) break;
            }

            return members;
        }

        /// <summary>
        /// A snapshot of a vessel that is not loaded — read from its ProtoVessel.
        ///
        /// Everything a constellation is judged on survives unloading: the orbit is on
        /// rails and still exact, the part list is <c>protoPartSnapshots</c>, and the
        /// crew are on the proto. What does NOT survive is anything that needs live
        /// <c>Part</c> objects — <see cref="GetVesselCost"/> walks IPartCostModifiers
        /// and TweakScale, none of which exist here — so cost is left at zero rather
        /// than guessed from prefab prices. Nothing in a network check reads it.
        /// </summary>
        public static VesselSnapshot CaptureUnloadedVessel(Vessel vessel)
        {
            var pv = vessel.protoVessel;
            var snap = new VesselSnapshot
            {
                vesselName = vessel.vesselName,
                vesselType = vessel.vesselType.ToString(),
                situation = vessel.situation.ToString(),
                body = vessel.mainBody?.bodyName ?? "Unknown",
                latitude = vessel.latitude,
                longitude = vessel.longitude,
                altitude = vessel.altitude,
                vesselId = vessel.id.ToString(),
            };

            if (pv != null)
            {
                var parts = pv.protoPartSnapshots;
                snap.partCount = parts?.Count ?? 0;

                float mass = 0f;
                if (parts != null)
                    foreach (var pps in parts)
                    {
                        if (pps == null) continue;
                        try { mass += pps.mass + pps.moduleMass; } catch { /* older proto */ }
                    }
                snap.totalMass = mass;

                try
                {
                    var crew = pv.GetVesselCrew();
                    snap.crewCount = crew?.Count ?? 0;
                    snap.crewTraits = ContractConstraints.CountCrewTraits(crew);
                }
                catch { /* a proto whose crew can't be resolved counts as uncrewed */ }
            }

            if (vessel.orbit != null)
            {
                snap.sma = vessel.orbit.semiMajorAxis;
                snap.eccentricity = vessel.orbit.eccentricity;
                snap.inclination = vessel.orbit.inclination;
                snap.apoapsis = vessel.orbit.ApA;
                snap.periapsis = vessel.orbit.PeA;
                snap.period = vessel.orbit.period;
                FillOrbitAngles(snap, vessel.orbit);
            }
            if (vessel.mainBody != null)
            {
                snap.bodyRadius = vessel.mainBody.Radius;
                snap.rotationPeriod = vessel.mainBody.rotationPeriod;
            }

            snap.relayPower = BestRelayPower(vessel);
            snap.hasRelay = snap.relayPower > 0.0;
            return snap;
        }

        /// <summary>
        /// The three angles that say where around the body a craft is, in degrees.
        ///
        /// Mean anomaly is PROPAGATED to the current instant from the orbit's own epoch
        /// rather than read off <c>orbit.meanAnomaly</c>, because that field holds
        /// whatever it was when the orbit was last stepped — and an unloaded vessel is
        /// not stepped every frame. Comparing one member's fresh phase against another's
        /// stale one is exactly how a real, evenly spread network would read as bunched,
        /// which is the failure this method exists to avoid.
        ///
        /// M(t) = M0 + n·(t - epoch), n = 2π/T. Exact for the closed orbits a network
        /// member is required to be on, and it needs only fields KSP keeps on the orbit
        /// itself. Planetarium's clock is the right one to ask here: we are in the
        /// flight scene at submit time, not in an OnLoad where it lags behind the save.
        /// </summary>
        private static void FillOrbitAngles(VesselSnapshot snap, Orbit orbit)
        {
            const double Rad2Deg = 180.0 / Math.PI;
            snap.lan = orbit.LAN;
            snap.argPe = orbit.argumentOfPeriapsis;

            double ma = orbit.meanAnomaly;
            try
            {
                if (orbit.period > 0)
                {
                    double n = 2.0 * Math.PI / orbit.period;
                    ma = orbit.meanAnomalyAtEpoch
                         + n * (Planetarium.GetUniversalTime() - orbit.epoch);
                }
            }
            catch { /* fall back to whatever the orbit last cached */ }

            if (double.IsNaN(ma) || double.IsInfinity(ma)) ma = 0.0;
            // KSP keeps mean anomaly in radians while LAN and argPe are degrees.
            snap.meanAnomaly = ((ma * Rad2Deg) % 360.0 + 360.0) % 360.0;
        }

        /// <summary>
        /// Whether a craft can relay for others — a <c>ModuleDataTransmitter</c> whose
        /// antennaType is RELAY.
        ///
        /// Read from the part PREFAB, not from a live module, because a network is
        /// mostly unloaded and an unloaded part has no modules instantiated at all.
        /// antennaType is a config value, identical on prefab and instance, so the
        /// prefab is not an approximation here — it is the same number, available for
        /// craft the loaded path could never see.
        ///
        /// Returns false rather than throwing on anything unrecognisable: a part this
        /// install cannot resolve is one whose antenna we cannot vouch for, and a
        /// network check that fails closed refuses a submission, which is the right
        /// direction for a check the player can fix by looking at their own craft.
        /// </summary>
        public static bool HasRelayAntenna(Vessel vessel)
        {
            double ignored;
            return WalkRelayAntennas(vessel, out ignored);
        }

        /// <summary>The power of this craft's strongest relay antenna, or 0 when it has
        /// none. Reported alongside the boolean so a contract can ask for relays of a
        /// given strength ("four relays, each at least an RA-2") without naming a part.
        ///
        /// A number rather than a part name on purpose, and for the reason the rest of
        /// this check is geometric: a name is answerable only by the one mod that ships
        /// it, while antenna power is a figure every install has for every antenna,
        /// including the ones RealAntennas rewrites. The issuer picks a part they own,
        /// the form sends what it is worth, and the contractor satisfies it with
        /// whatever their own install can field.
        ///
        /// The strongest single antenna, not the combined total: KSP's own combining
        /// rule is a config-weighted average that mods change, and "each with an RA-2"
        /// is a statement about one antenna, not about a stack of them.
        ///
        /// Shares the walk with <see cref="HasRelayAntenna"/> rather than repeating it,
        /// so the two can never disagree about what counts as a relay.</summary>
        public static double BestRelayPower(Vessel vessel)
        {
            // -1, not 0: "this install cannot express antenna strength" is a different
            // statement from "this craft has no relay antenna", and a strength floor
            // must skip the first while still failing the second. RealAntennas deletes
            // antennaPower from every part, so there is no number here to compare and
            // pretending there is a 0 would disqualify every satellite on an RO install.
            if (!AntennaModel.ExpressesPower) return -1.0;
            double best;
            return WalkRelayAntennas(vessel, out best) ? best : 0.0;
        }

        /// <summary>One walk over a craft's relay antennas: whether it has any, and the
        /// strongest one's power. Shared so the flag and the number can never disagree
        /// about what counts as a relay.</summary>
        private static bool WalkRelayAntennas(Vessel vessel, out double best)
        {
            best = 0.0;
            if (vessel == null) return false;
            bool found = false;
            try
            {
                if (vessel.loaded && vessel.parts != null)
                {
                    foreach (var part in vessel.parts)
                    {
                        if (part?.Modules == null) continue;
                        // Every module rather than the first transmitter found: which
                        // module answers depends on the antenna mod, and on RealAntennas
                        // the one that counts is not a stock transmitter at all.
                        foreach (PartModule pm in part.Modules)
                        {
                            if (!AntennaModel.CountsAsRelay(pm)) continue;
                            found = true;
                            var mdt = pm as ModuleDataTransmitter;
                            if (mdt != null && mdt.antennaPower > best) best = mdt.antennaPower;
                        }
                    }
                    return found;
                }

                var parts = vessel.protoVessel?.protoPartSnapshots;
                if (parts == null) return false;
                foreach (var pps in parts)
                {
                    if (pps == null) continue;
                    var info = pps.partInfo ?? PartLoader.getPartInfoByName(pps.partName);
                    // Save files write '.' in a part name as '_'; partName is normally
                    // already clean, but a hand-edited or third-party save need not be.
                    if (info == null && !string.IsNullOrEmpty(pps.partName))
                        info = PartLoader.getPartInfoByName(pps.partName.Replace('_', '.'));
                    var prefab = info?.partPrefab;
                    if (prefab?.Modules == null) continue;
                    foreach (PartModule pm in prefab.Modules)
                    {
                        if (!AntennaModel.CountsAsRelay(pm)) continue;
                        found = true;
                        var mdt = pm as ModuleDataTransmitter;
                        if (mdt != null && mdt.antennaPower > best) best = mdt.antennaPower;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Relay check failed for '{vessel.vesselName}': {ex.Message}");
                best = 0.0;
                return false;
            }
            return found;
        }

        // ── Craft File Reading ──────────────────────────────────────────────

        public static string[] GetCraftFiles()
        {
            var files = new List<string>();

            string vabPath = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "VAB");
            string sphPath = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "SPH");

            if (Directory.Exists(vabPath))
                files.AddRange(Directory.GetFiles(vabPath, "*.craft"));
            if (Directory.Exists(sphPath))
                files.AddRange(Directory.GetFiles(sphPath, "*.craft"));

            // Also check save-specific ships
            string savePath = Path.Combine(KSPUtil.ApplicationRootPath, "saves",
                HighLogic.SaveFolder ?? "default");
            string saveVab = Path.Combine(savePath, "Ships", "VAB");
            string saveSph = Path.Combine(savePath, "Ships", "SPH");

            if (Directory.Exists(saveVab))
                files.AddRange(Directory.GetFiles(saveVab, "*.craft"));
            if (Directory.Exists(saveSph))
                files.AddRange(Directory.GetFiles(saveSph, "*.craft"));

            return files.ToArray();
        }

        public static byte[] ReadCraftFile(string path)
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
            return null;
        }

        // ── Loadmeta Reading ────────────────────────────────────────────────

        public static string ReadLoadmeta(string craftPath)
        {
            string metaPath = Path.ChangeExtension(craftPath, ".loadmeta");
            if (File.Exists(metaPath))
                return File.ReadAllText(metaPath);
            return null;
        }

        public static Dictionary<string, string> ParseLoadmeta(string content)
        {
            var meta = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(content)) return meta;

            foreach (var line in content.Split('\n'))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                int eq = trimmed.IndexOf('=');
                if (eq > 0)
                {
                    string key = trimmed.Substring(0, eq).Trim();
                    string val = trimmed.Substring(eq + 1).Trim();
                    // Loadmeta can have duplicate keys (partNames, partModules)
                    // Append with separator for those
                    if (meta.ContainsKey(key))
                        meta[key] = meta[key] + "|" + val;
                    else
                        meta[key] = val;
                }
            }
            return meta;
        }

        // ── Craft Info from Loadmeta ────────────────────────────────────────

        public struct CraftInfo
        {
            public string shipName;
            public int partCount;
            public int stageCount;
            public float totalCost;
            public float totalMass;
            public string type; // VAB or SPH
            public string filePath;
        }

        public static CraftInfo GetCraftInfo(string craftPath)
        {
            var info = new CraftInfo { filePath = craftPath };

            string meta = ReadLoadmeta(craftPath);
            if (meta != null)
            {
                var parsed = ParseLoadmeta(meta);
                info.shipName = parsed.ContainsKey("shipName") ? parsed["shipName"] : Path.GetFileNameWithoutExtension(craftPath);
                info.partCount = parsed.ContainsKey("partCount") ? int.Parse(parsed["partCount"]) : 0;
                info.stageCount = parsed.ContainsKey("stageCount") ? int.Parse(parsed["stageCount"]) : 0;

                float.TryParse(parsed.ContainsKey("totalCost") ? parsed["totalCost"] : "0", out info.totalCost);
                float.TryParse(parsed.ContainsKey("totalMass") ? parsed["totalMass"] : "0", out info.totalMass);
                info.type = parsed.ContainsKey("type") ? parsed["type"] : "VAB";
            }
            else
            {
                info.shipName = Path.GetFileNameWithoutExtension(craftPath);
                info.type = craftPath.Contains("SPH") ? "SPH" : "VAB";
            }

            return info;
        }

        // ── Screenshot Capture ──────────────────────────────────────────────

        public static string CaptureScreenshot()
        {
            string dir = Path.Combine(GeneKermanMod.PluginDataPath, "screenshots");
            Directory.CreateDirectory(dir);

            string filename = $"gk_capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string path = Path.Combine(dir, filename);

            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[GeneKerman] Screenshot saved to {path}");

            return path;
        }

        public static byte[] ReadScreenshot(string path)
        {
            // Unity captures async — need a small delay before reading
            if (File.Exists(path))
                return File.ReadAllBytes(path);
            return null;
        }

        // ── Craft File Finder ───────────────────────────────────────────────

        /// <summary>
        /// Search for a craft file by vessel name in save and root Ships directories.
        /// </summary>
        public static string FindCraftFile(string vesselName)
        {
            if (string.IsNullOrEmpty(vesselName)) return null;

            string[] editorTypes = { "VAB", "SPH" };
            string saveFolder = HighLogic.SaveFolder ?? "default";

            foreach (string type in editorTypes)
            {
                // Check save-specific first
                string savePath = Path.Combine(KSPUtil.ApplicationRootPath, "saves",
                    saveFolder, "Ships", type, vesselName + ".craft");
                if (File.Exists(savePath)) return savePath;

                // Check root Ships
                string rootPath = Path.Combine(KSPUtil.ApplicationRootPath, "Ships",
                    type, vesselName + ".craft");
                if (File.Exists(rootPath)) return rootPath;
            }

            return null;
        }
    }
}
