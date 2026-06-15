/*
 * VesselTransfer.cs – Export and import live vessels between KSP saves.
 *
 * Export: Serializes the active vessel's ProtoVessel into a ConfigNode string.
 * Import: Deserializes a ConfigNode string, randomizes crew names to avoid
 *         duplicates, adds crew to roster, and spawns the vessel in-game.
 */

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class VesselTransfer
    {
        // ── Random Kerbal First Names ────────────────────────────────────────

        private static readonly string[] FIRST_NAMES = {
            "Aldrin", "Tamara", "Rodney", "Sasha", "Kirby", "Miko", "Harlan",
            "Katya", "Booker", "Shira", "Obron", "Fenna", "Cyrus", "Leora",
            "Niles", "Zara", "Kelton", "Irma", "Destin", "Pella", "Sigmund",
            "Brynn", "Orlin", "Tessa", "Hatch", "Verna", "Rigel", "Cleo",
            "Doran", "Mavis", "Ender", "Liora", "Garvin", "Petra", "Thane",
            "Nella", "Rufus", "Delia", "Tycho", "Maren", "Castor", "Elke",
            "Dunbar", "Runa", "Corbin", "Ilsa", "Kepler", "Mira", "Vance",
            "Soleil", "Bardo", "Freya", "Colton", "Arwen", "Beckett", "Dagny",
        };

        private static System.Random rng = new System.Random();

        // ── Export ───────────────────────────────────────────────────────────

        /// <summary>
        /// Serialize the active vessel into a "VESSEL" ConfigNode. Returns null if
        /// no active vessel is available. Shared by the string export and the
        /// rescue-rename export so both work off the same proto snapshot.
        /// </summary>
        public static ConfigNode ExportActiveVesselNode(bool embedRoster = false)
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                Debug.LogWarning("[GeneKerman] VesselTransfer.Export: Not in flight scene.");
                return null;
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                Debug.LogWarning("[GeneKerman] VesselTransfer.Export: No active vessel.");
                return null;
            }

            try
            {
                // Force all parts to update their state before backup
                vessel.protoVessel = vessel.BackupVessel();

                ConfigNode vesselNode = new ConfigNode("VESSEL");
                vessel.protoVessel.Save(vesselNode);

                // Embed each crew member's full roster definition so the receiving
                // save can recreate them faithfully (gender, profession/trait,
                // courage, stupidity) instead of generating a random kerbal.
                if (embedRoster)
                    EmbedRosterData(vesselNode, vessel);

                // Carry any custom mission flags the parts use so the receiving
                // save renders them instead of a missing decal.
                FlagTransfer.EmbedFlagsInNode(vesselNode);

                Debug.Log($"[GeneKerman] Exported vessel '{vessel.vesselName}': " +
                          $"{vessel.parts.Count} parts, {vessel.GetCrewCount()} crew");
                return vesselNode;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] VesselTransfer.Export failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Serialize the active vessel into a ConfigNode string for transfer.
        /// Returns null if no active vessel is available.
        /// </summary>
        public static string ExportActiveVessel(bool embedRoster = false)
        {
            ConfigNode node = ExportActiveVesselNode(embedRoster);
            return node?.ToString();
        }

        /// <summary>Save each crew member's full ProtoCrewMember into a GKCREW child
        /// node so a different save can rebuild them exactly (KSP ignores unknown
        /// node names on load).</summary>
        private static void EmbedRosterData(ConfigNode vesselNode, Vessel vessel)
        {
            var roster = HighLogic.CurrentGame.CrewRoster;
            foreach (var pcm in vessel.GetVesselCrew())
            {
                if (pcm == null) continue;
                try
                {
                    ConfigNode kn = vesselNode.AddNode("GKCREW");
                    pcm.Save(kn);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] EmbedRosterData failed for {pcm.name}: {ex.Message}");
                }
            }
        }

        // ── Ownership name tagging ───────────────────────────────────────────
        //
        // Transferred kerbals are tagged with their owner's name as
        // "{owner}'s {OriginalName}" while they live in someone else's save, and the
        // tag is stripped when they come home. This is applied per-crew on import and
        // is fully reversible, so a kerbal can move between saves any number of times
        // without losing (or doubling up) the tag.

        /// <summary>
        /// Resolve a crew member's display name for the importing save. An untagged
        /// kerbal belongs to <paramref name="ownerName"/> (the source craft's owner);
        /// a "{X}'s {core}" kerbal already belongs to X. If the resolved owner is the
        /// importing user (<paramref name="myName"/>) the tag is stripped (home);
        /// otherwise it's tagged "{owner}'s {core}".
        /// </summary>
        public static string ApplyOwnershipTag(string name, string ownerName, string myName)
        {
            if (string.IsNullOrEmpty(name)) return name;

            string owner = null;
            string core = name;
            int idx = name.IndexOf("'s ", StringComparison.Ordinal);
            if (idx > 0)
            {
                owner = name.Substring(0, idx);
                core = name.Substring(idx + 3);
            }
            if (owner == null) owner = ownerName; // untagged → owned by the incoming craft's owner

            if (string.IsNullOrEmpty(owner)) return core;            // unknown owner → leave original
            if (!string.IsNullOrEmpty(myName) && owner.Equals(myName, StringComparison.OrdinalIgnoreCase))
                return core;                                          // coming home → strip tag
            return owner + "'s " + core;                              // someone else's → tagged
        }

        /// <summary>"{owner}'s {OriginalName}" — used when listing the names a rescuer
        /// will see for the stranded crew (owner = the issuer).</summary>
        public static string TagName(string ownerName, string originalName)
        {
            return ApplyOwnershipTag(originalName, ownerName, null);
        }

        // ── Import ───────────────────────────────────────────────────────────

        /// <summary>
        /// Import a vessel into the current save, tagging its crew with the owner's
        /// name (or stripping the tag if they're coming home). See
        /// <see cref="ApplyOwnershipTag"/>. Returns the new vessel name, or null.
        /// </summary>
        public static string ImportVessel(string vesselNodeStr, string ownerName, string myName)
        {
            return ImportVesselAtTarget(vesselNodeStr, null, ownerName, myName);
        }

        /// <summary>
        /// Import a vessel and optionally place it at a rescue target. Crew are tagged
        /// with their owner's name (stripped when they come home).
        /// </summary>
        public static string ImportVesselAtTarget(
            string vesselNodeStr, RescueTargetSpec target, string ownerName, string myName)
        {
            Debug.Log($"[GeneKerman] VesselTransfer.Import: starting ({vesselNodeStr?.Length ?? 0} chars), owner='{ownerName}', me='{myName}'");
            if (!CanImport()) return null;

            try
            {
                ConfigNode innerNode = LoadInnerVesselNode(vesselNodeStr);
                if (innerNode == null) return null;

                ResetControls(innerNode);
                TagCrew(innerNode, ownerName, myName);

                if (target != null)
                    PlaceAtTarget(innerNode, target);
                else
                    FreezeOrbitEpochToNow(innerNode);

                return SpawnInnerNode(innerNode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] VesselTransfer.Import failed: {ex}");
                return null;
            }
        }

        /// <summary>Tag every crew reference (PART crew refs + embedded GKCREW roster
        /// nodes) for the importing save via <see cref="ApplyOwnershipTag"/>.</summary>
        private static void TagCrew(ConfigNode innerNode, string ownerName, string myName)
        {
            var map = MapCrewNames(innerNode, old => ApplyOwnershipTag(old, ownerName, myName));
            foreach (ConfigNode kn in innerNode.GetNodes("GKCREW"))
            {
                string nm = kn.GetValue("name");
                if (string.IsNullOrEmpty(nm)) continue;
                string nn;
                if (!map.TryGetValue(nm, out nn))
                    nn = ApplyOwnershipTag(nm, ownerName, myName);
                kn.SetValue("name", nn, true);
            }
        }

        // ── Shared import helpers ────────────────────────────────────────────

        private static bool CanImport()
        {
            if (HighLogic.CurrentGame == null)
            {
                Debug.LogWarning("[GeneKerman] Import: No current game. You must be in a save.");
                return false;
            }
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            {
                Debug.LogWarning("[GeneKerman] Import: Must be in Flight, Space Center, or Tracking Station.");
                return false;
            }
            return true;
        }

        /// <summary>Parse a vessel string to its inner VESSEL node and assign a fresh
        /// pid/persistentId so it can't collide with an existing vessel.</summary>
        private static ConfigNode LoadInnerVesselNode(string vesselNodeStr)
        {
            if (string.IsNullOrEmpty(vesselNodeStr))
            {
                Debug.LogWarning("[GeneKerman] Import: Empty vessel data.");
                return null;
            }

            // Write to temp file and load — ConfigNode.Parse() is unreliable.
            string tempPath = System.IO.Path.Combine(
                KSPUtil.ApplicationRootPath, "PluginData", "GeneKerman_vessel_import.cfg");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempPath));
            System.IO.File.WriteAllText(tempPath, vesselNodeStr, Encoding.UTF8);
            ConfigNode fileNode = ConfigNode.Load(tempPath);
            try { System.IO.File.Delete(tempPath); } catch { }

            if (fileNode == null)
            {
                Debug.LogError("[GeneKerman] Import: ConfigNode.Load returned null.");
                return null;
            }

            ConfigNode innerNode = fileNode;
            if (fileNode.name != "VESSEL")
            {
                innerNode = fileNode.GetNode("VESSEL");
                if (innerNode == null)
                {
                    if (fileNode.CountNodes > 0) innerNode = fileNode.nodes[0];
                    else { Debug.LogError("[GeneKerman] Import: No VESSEL node found."); return null; }
                }
            }

            Guid newGuid = Guid.NewGuid();
            innerNode.SetValue("pid", newGuid.ToString("D"), true);
            innerNode.SetValue("persistentId", ((uint)rng.Next(100000, int.MaxValue)).ToString(), true);

            // Install any custom mission flags this vessel carried (and strip the
            // GKFLAG nodes) before the ProtoVessel is built, so its parts resolve
            // the textures on spawn.
            FlagTransfer.ExtractAndInstallFlags(innerNode);
            return innerNode;
        }

        /// <summary>Register an inner VESSEL node into the running universe (after
        /// any crew/placement edits) and persist. Returns the vessel name.</summary>
        private static string SpawnInnerNode(ConfigNode innerNode)
        {
            string vesselName = innerNode.GetValue("name") ?? "Imported Vessel";

            // Add all crew to roster first (before creating ProtoVessel).
            AddCrewToRoster(innerNode);

            ProtoVessel protoVessel = new ProtoVessel(innerNode, HighLogic.CurrentGame);
            // ProtoVessel.Load() registers the vessel with the running game (KSP uses
            // this same path for rescue-contract vessels and asteroids), so it shows
            // up in flight and the tracking station in every scene.
            protoVessel.Load(HighLogic.CurrentGame.flightState);

            if (!HighLogic.LoadedSceneIsFlight)
            {
                try
                {
                    GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                }
                catch (Exception saveEx)
                {
                    Debug.LogWarning($"[GeneKerman] Post-import save failed: {saveEx.Message}");
                }
            }

            Debug.Log($"[GeneKerman] (Ok) Spawned vessel '{vesselName}'");
            return vesselName;
        }

        // ── Rescue placement ─────────────────────────────────────────────────

        /// <summary>
        /// Rewrite a vessel node's ORBIT + situation so the wreck spawns at the
        /// rescue target — a circular-ish orbit (Ap/Pe) or a landed lat/lon spot.
        /// </summary>
        private static void PlaceAtTarget(ConfigNode innerNode, RescueTargetSpec target)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(target.body);
            if (body == null)
            {
                foreach (var b in FlightGlobals.Bodies)
                    if (b != null && string.Equals(b.bodyName, target.body, StringComparison.OrdinalIgnoreCase))
                    { body = b; break; }
            }
            if (body == null)
            {
                Debug.LogWarning($"[GeneKerman] PlaceAtTarget: body '{target.body}' not found — leaving original orbit.");
                return;
            }

            int refIdx = body.flightGlobalsIndex;
            ConfigNode orbit = innerNode.GetNode("ORBIT");
            if (orbit == null) orbit = innerNode.AddNode("ORBIT");

            double now = Planetarium.GetUniversalTime();

            if ((target.mode ?? "orbit").ToLower() == "surface")
            {
                // Landed: KSP recomputes the world position from lat/lon/alt on load.
                double lat = target.lat;
                double lon = target.lon;
                double terrain = 0.0;
                try { terrain = body.TerrainAltitude(lat, lon); } catch { }
                double alt = Math.Max(terrain, 0.0) + 2.0; // small offset above ground

                innerNode.SetValue("sit", "LANDED", true);
                innerNode.SetValue("landed", "True", true);
                innerNode.SetValue("splashed", "False", true);
                innerNode.SetValue("landedAt", body.bodyName, true);
                innerNode.SetValue("lat", lat.ToString("G17"), true);
                innerNode.SetValue("lon", lon.ToString("G17"), true);
                innerNode.SetValue("alt", alt.ToString("G17"), true);
                innerNode.SetValue("hgt", "1.0", true);

                // A landed vessel still needs a valid (surface-synchronous) orbit so
                // the body reference resolves without NaNs.
                double smaSurf = body.Radius + alt;
                WriteOrbit(orbit, refIdx, smaSurf, 0.0, 0.0, 0.0, lon, 0.0, now);
                Debug.Log($"[GeneKerman] PlaceAtTarget: landed at {body.bodyName} lat={lat:F2} lon={lon:F2} alt={alt:F0}");
            }
            else
            {
                // Orbit: derive SMA/ECC from Ap/Pe (altitudes above the surface).
                double ap = target.ap;
                double pe = target.pe;
                double rAp = body.Radius + Math.Max(ap, pe);
                double rPe = body.Radius + Math.Min(ap, pe);
                double sma = (rAp + rPe) / 2.0;
                double ecc = (rAp - rPe) / (rAp + rPe);
                if (double.IsNaN(ecc) || ecc < 0) ecc = 0;

                innerNode.SetValue("sit", "ORBITING", true);
                innerNode.SetValue("landed", "False", true);
                innerNode.SetValue("splashed", "False", true);
                innerNode.SetValue("landedAt", "", true);

                WriteOrbit(orbit, refIdx, sma, ecc, 0.0, 0.0, 0.0, 0.0, now);
                Debug.Log($"[GeneKerman] PlaceAtTarget: orbit {body.bodyName} ap={ap:F0} pe={pe:F0} sma={sma:F0} ecc={ecc:F4}");
            }
        }

        /// <summary>
        /// Rebase an orbiting vessel's epoch (EPH) to the current universe time while
        /// keeping its mean anomaly (MNA). The snapshot stores EPH as the absolute UT of
        /// the exporting save; loaded as-is, KSP propagates the orbit forward by
        /// (now - EPH) — which, across a time warp or a different save, drops the vessel
        /// wherever the source would be now rather than where it was snapshotted. Setting
        /// EPH=now with the same MNA pins it at the exact position it had at export.
        /// (Landed vessels resolve from lat/lon, so a stale orbit epoch is harmless there.)
        /// </summary>
        private static void FreezeOrbitEpochToNow(ConfigNode innerNode)
        {
            ConfigNode orbit = innerNode.GetNode("ORBIT");
            if (orbit == null) return;
            orbit.SetValue("EPH", Planetarium.GetUniversalTime().ToString("G17"), true);
        }

        private static void WriteOrbit(ConfigNode orbit, int refIdx,
            double sma, double ecc, double inc, double lpe, double lan, double mna, double eph)
        {
            orbit.SetValue("SMA", sma.ToString("G17"), true);
            orbit.SetValue("ECC", ecc.ToString("G17"), true);
            orbit.SetValue("INC", inc.ToString("G17"), true);
            orbit.SetValue("LPE", lpe.ToString("G17"), true);
            orbit.SetValue("LAN", lan.ToString("G17"), true);
            orbit.SetValue("MNA", mna.ToString("G17"), true);
            orbit.SetValue("EPH", eph.ToString("G17"), true);
            orbit.SetValue("REF", refIdx.ToString(), true);
        }

        // ── Remove a vessel from this save ───────────────────────────────────

        /// <summary>
        /// Remove a vessel (by pid GUID) from the current save and drop its crew
        /// from the roster, so the same kerbals don't exist in two saves. Refuses
        /// to remove the focused active vessel in flight (caller must defer to a
        /// Space Center / Tracking Station scene). Returns true if removed.
        /// </summary>
        public static bool RemoveVesselFromSave(string pid, bool dropCrew = true)
        {
            if (string.IsNullOrEmpty(pid))
            {
                Debug.LogWarning("[GeneKerman] RemoveVessel: no pid given.");
                return false;
            }
            Guid g;
            if (!Guid.TryParse(pid, out g))
            {
                Debug.LogWarning($"[GeneKerman] RemoveVessel: pid '{pid}' is not a GUID.");
                return false;
            }

            Vessel target = null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id == g) { target = v; break; }

            if (target == null)
            {
                Debug.LogWarning($"[GeneKerman] RemoveVessel: no vessel with pid {pid} found.");
                return false;
            }

            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel == target)
            {
                Debug.LogWarning("[GeneKerman] RemoveVessel: target is the active vessel — defer to a non-flight scene.");
                return false;
            }

            try
            {
                // Snapshot the crew before destroying the hull — Die() unassigns them, so
                // GetVesselCrew() returns nothing afterwards.
                var crew = new List<ProtoCrewMember>();
                if (dropCrew)
                    foreach (var pcm in target.GetVesselCrew())
                        if (pcm != null) crew.Add(pcm);

                // Destroy the hull FIRST. The old order removed crew from the roster
                // before Die(), which let KSP's own crew handling inside Die() trip over
                // kerbals that no longer existed — the crew vanished but the empty vessel
                // was left behind (the reported "only the kerbal gets removed" bug).
                target.Die();

                // Belt-and-suspenders: an unloaded vessel (removed from the Space Center /
                // Tracking Station) can linger as a ProtoVessel in the flight state even
                // after Die(), and reappear on the next load. Drop it explicitly.
                var flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
                if (flightState != null && flightState.protoVessels != null)
                    flightState.protoVessels.RemoveAll(pv => pv != null && pv.vesselID == g);

                if (dropCrew)
                {
                    var roster = HighLogic.CurrentGame.CrewRoster;
                    foreach (var pcm in crew)
                    {
                        try
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                            roster.Remove(pcm);
                        }
                        catch (Exception cex)
                        {
                            Debug.LogWarning($"[GeneKerman] RemoveVessel: could not drop crew {pcm.name}: {cex.Message}");
                        }
                    }
                }

                if (!HighLogic.LoadedSceneIsFlight)
                {
                    try { GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE); }
                    catch (Exception saveEx) { Debug.LogWarning($"[GeneKerman] RemoveVessel save failed: {saveEx.Message}"); }
                }

                Debug.Log($"[GeneKerman] (Ok) Removed vessel pid {pid} from save.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] RemoveVessel failed: {ex}");
                return false;
            }
        }

        // ── Crew Randomization ───────────────────────────────────────────────

        /// <summary>
        /// Walk all PART nodes in the vessel, find CREW subnodes,
        /// and replace crew names with random Kerman-style names.
        /// Also updates the crew manifest fields on each PART.
        /// </summary>
        private static void RandomizeCrewNames(ConfigNode vesselNode)
        {
            ResetControls(vesselNode);
            var nameMap = MapCrewNames(vesselNode, _ => GenerateKerbalName());
            Debug.Log($"[GeneKerman] Randomized {nameMap.Count} crew member names.");
        }

        /// <summary>
        /// Reset SAS/RCS/Brakes so a spawned ship isn't spinning or draining fuel.
        /// </summary>
        private static void ResetControls(ConfigNode vesselNode)
        {
            ConfigNode agNode = vesselNode.GetNode("ACTIONGROUPS");
            if (agNode != null)
            {
                agNode.SetValue("SAS", "False, 0", true);
                agNode.SetValue("RCS", "False, 0", true);
                agNode.SetValue("Brakes", "False, 0", true);
            }
            ConfigNode ctrlNode = vesselNode.GetNode("CTRLSTATE");
            if (ctrlNode != null)
            {
                ctrlNode.SetValue("SAS", "False", true);
                ctrlNode.SetValue("RCS", "False", true);
            }
        }

        /// <summary>
        /// Walk every crew reference in the vessel node (CREW subnodes and
        /// 'crew = name' fields) and rename each via <paramref name="mapper"/>.
        /// The mapper is called once per distinct original name; the result is
        /// reused so the same kerbal maps consistently. Returns original → new.
        /// </summary>
        private static Dictionary<string, string> MapCrewNames(ConfigNode vesselNode, Func<string, string> mapper)
        {
            var nameMap = new Dictionary<string, string>();

            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                foreach (ConfigNode crewNode in partNode.GetNodes("CREW"))
                {
                    string oldName = crewNode.GetValue("name");
                    if (string.IsNullOrEmpty(oldName)) continue;
                    if (!nameMap.ContainsKey(oldName))
                        nameMap[oldName] = mapper(oldName) ?? oldName;
                    crewNode.SetValue("name", nameMap[oldName]);
                }

                string[] crewValues = partNode.GetValues("crew");
                if (crewValues.Length > 0)
                {
                    partNode.RemoveValues("crew");
                    foreach (string crewVal in crewValues)
                    {
                        // Ignore empty or index-based crew (just in case)
                        if (string.IsNullOrEmpty(crewVal) || int.TryParse(crewVal, out _))
                        {
                            partNode.AddValue("crew", crewVal);
                            continue;
                        }
                        if (!nameMap.ContainsKey(crewVal))
                            nameMap[crewVal] = mapper(crewVal) ?? crewVal;
                        partNode.AddValue("crew", nameMap[crewVal]);
                    }
                }
            }

            return nameMap;
        }

        /// <summary>
        /// Add all crew members from the vessel to the game's crew roster
        /// so KSP doesn't throw errors about unknown crew.
        /// </summary>
        private static void AddCrewToRoster(ConfigNode vesselNode)
        {
            var roster = HighLogic.CurrentGame.CrewRoster;
            var addedNames = new HashSet<string>();

            // Prefer the full roster definitions embedded as GKCREW — these preserve
            // gender / profession / courage / stupidity across the save transfer.
            foreach (ConfigNode kn in vesselNode.GetNodes("GKCREW"))
            {
                string name = kn.GetValue("name");
                if (string.IsNullOrEmpty(name) || addedNames.Contains(name))
                    continue;
                addedNames.Add(name);
                if (roster[name] != null) continue;

                ProtoCrewMember pcm = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                pcm.ChangeName(name);
                ApplyKerbalAttributes(pcm, kn);
                pcm.type = ProtoCrewMember.KerbalType.Crew;
                Debug.Log($"[GeneKerman] Restored crew with attributes: {name} ({pcm.trait}, {pcm.gender})");
            }

            // Fallback: crew referenced by the parts but without embedded GKCREW data
            // (older nodes / non-rescue transfers) — created as a generic kerbal.
            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                foreach (ConfigNode crewNode in partNode.GetNodes("CREW"))
                {
                    string name = crewNode.GetValue("name");
                    string trait = crewNode.GetValue("trait");
                    AddKerbalToRoster(name, trait, roster, addedNames);
                }

                foreach (string crewVal in partNode.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(crewVal) && !int.TryParse(crewVal, out _))
                    {
                        AddKerbalToRoster(crewVal, null, roster, addedNames);
                    }
                }
            }
        }

        /// <summary>Copy gender, profession/trait, courage and stupidity from a saved
        /// ProtoCrewMember node (KSP stores courage as "brave", stupidity as "dull").</summary>
        private static void ApplyKerbalAttributes(ProtoCrewMember pcm, ConfigNode kn)
        {
            string trait = kn.GetValue("trait");
            if (!string.IsNullOrEmpty(trait))
                KerbalRoster.SetExperienceTrait(pcm, trait);

            string gender = kn.GetValue("gender");
            if (!string.IsNullOrEmpty(gender))
            {
                try { pcm.gender = (ProtoCrewMember.Gender)Enum.Parse(typeof(ProtoCrewMember.Gender), gender); }
                catch { }
            }

            float f;
            if (float.TryParse(kn.GetValue("brave"), out f)) pcm.courage = f;
            if (float.TryParse(kn.GetValue("dull"), out f)) pcm.stupidity = f;
            bool b;
            if (bool.TryParse(kn.GetValue("badS"), out b)) pcm.isBadass = b;
        }

        private static void AddKerbalToRoster(string name, string trait, KerbalRoster roster, HashSet<string> addedNames)
        {
            if (string.IsNullOrEmpty(name) || addedNames.Contains(name))
                return;

            // Check if already in roster
            if (roster[name] != null)
                return;

            // Create a new crew member
            ProtoCrewMember newCrew = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
            // GetNewKerbal auto-generates a name, override it
            newCrew.ChangeName(name);

            // Copy traits from the original node if available
            if (!string.IsNullOrEmpty(trait))
            {
                KerbalRoster.SetExperienceTrait(newCrew, trait);
            }

            // Set as assigned (in a vessel)
            newCrew.type = ProtoCrewMember.KerbalType.Crew;

            addedNames.Add(name);
            Debug.Log($"[GeneKerman] Added crew member to roster: {name} ({trait ?? "Random Trait"})");
        }

        /// <summary>
        /// Generate a random Kerbal-style name: "[FirstName] Kerman"
        /// </summary>
        private static string GenerateKerbalName()
        {
            string first = FIRST_NAMES[rng.Next(FIRST_NAMES.Length)];
            // Add a random number suffix if name is common to reduce collisions
            int suffix = rng.Next(10, 99);
            return $"{first}{suffix} Kerman";
        }
    }

    /// <summary>
    /// The DELIVERY destination a rescuer must bring the stranded kerbals to — NOT
    /// where the wreck spawns (the wreck keeps its own snapshot orbit so there's an
    /// actual rescue to fly). Orbit mode uses Ap/Pe (altitudes above the surface);
    /// surface mode uses Lat/Lon. Margins are the issuer-set tolerances.
    /// </summary>
    public class RescueTargetSpec
    {
        public string body;
        public string mode = "orbit"; // "orbit" | "surface"
        public double ap;
        public double pe;
        public double lat;
        public double lon;
        public double marginAlt;
        public double marginPos;

        public static RescueTargetSpec FromDict(System.Collections.Generic.Dictionary<string, object> d)
        {
            if (d == null) return null;
            return new RescueTargetSpec
            {
                body = MiniJSON.GetString(d, "body", ""),
                mode = MiniJSON.GetString(d, "mode", "orbit"),
                ap = MiniJSON.GetDouble(d, "ap", 0),
                pe = MiniJSON.GetDouble(d, "pe", 0),
                lat = MiniJSON.GetDouble(d, "lat", 0),
                lon = MiniJSON.GetDouble(d, "lon", 0),
                marginAlt = MiniJSON.GetDouble(d, "margin_alt", 0),
                marginPos = MiniJSON.GetDouble(d, "margin_pos", 0),
            };
        }
    }
}
