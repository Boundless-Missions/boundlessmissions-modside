/*
 * VesselDataCollector.cs – Extracts vessel information from KSP for submission.
 *
 * Collects data from:
 *   - FlightGlobals.ActiveVessel (current vessel in flight)
 *   - FlightGlobals.VesselsLoaded (all vessels in physics range)
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
            // Craft metadata
            public int partCount;
            public float totalMass;
            public float totalCost;
            public int crewCount;

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
                    { "part_count", partCount },
                    { "total_mass", (double)totalMass },
                    { "total_cost", (double)totalCost },
                    { "crew_count", crewCount },
                };
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
                crewCount = vessel.GetCrewCount(),
            };

            // Mass and cost
            if (vessel.parts != null)
            {
                float mass = 0f, cost = 0f;
                foreach (var part in vessel.parts)
                {
                    mass += part.mass + part.GetResourceMass();
                    cost += part.partInfo?.cost ?? 0f;
                }
                snap.totalMass = mass;
                snap.totalCost = cost;
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
            }
            if (vessel.mainBody != null)
            {
                snap.bodyRadius = vessel.mainBody.Radius;
            }

            return snap;
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
