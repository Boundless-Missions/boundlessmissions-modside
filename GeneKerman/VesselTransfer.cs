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
        /// Serialize the active vessel into a ConfigNode string for transfer.
        /// Returns null if no active vessel is available.
        /// </summary>
        public static string ExportActiveVessel()
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

                // Store the vessel name as a top-level hint
                string result = vesselNode.ToString();

                Debug.Log($"[GeneKerman] Exported vessel '{vessel.vesselName}': {result.Length} chars, " +
                          $"{vessel.parts.Count} parts, {vessel.GetCrewCount()} crew");

                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] VesselTransfer.Export failed: {ex}");
                return null;
            }
        }

        // ── Import ───────────────────────────────────────────────────────────

        /// <summary>
        /// Import a vessel from a ConfigNode string into the current save.
        /// Crew names are randomized and added to the roster.
        /// Returns the new vessel name, or null on failure.
        /// </summary>
        public static string ImportVessel(string vesselNodeStr)
        {
            Debug.Log($"[GeneKerman] VesselTransfer.Import: Starting import ({vesselNodeStr?.Length ?? 0} chars)");

            if (string.IsNullOrEmpty(vesselNodeStr))
            {
                Debug.LogWarning("[GeneKerman] VesselTransfer.Import: Empty vessel data.");
                return null;
            }

            if (HighLogic.CurrentGame == null)
            {
                Debug.LogWarning("[GeneKerman] VesselTransfer.Import: No current game. You must be in a save.");
                return null;
            }

            if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            {
                Debug.LogWarning("[GeneKerman] VesselTransfer.Import: You must be in the Flight, Space Center, or Tracking Station scene to import a vessel.");
                return null;
            }

            try
            {
                // Write to temp file and load — ConfigNode.Parse() is unreliable
                string tempPath = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath, "PluginData", "GeneKerman_vessel_import.cfg");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempPath));
                System.IO.File.WriteAllText(tempPath, vesselNodeStr, Encoding.UTF8);
                Debug.Log($"[GeneKerman] Wrote temp vessel file: {tempPath}");

                ConfigNode fileNode = ConfigNode.Load(tempPath);

                // Clean up temp file
                try { System.IO.File.Delete(tempPath); } catch { }

                if (fileNode == null)
                {
                    Debug.LogError("[GeneKerman] VesselTransfer.Import: ConfigNode.Load returned null.");
                    return null;
                }

                Debug.Log($"[GeneKerman] Loaded ConfigNode: name='{fileNode.name}', " +
                          $"nodes={fileNode.CountNodes}, values={fileNode.CountValues}");

                // Find the VESSEL node — it may be the root or nested
                ConfigNode innerNode = fileNode;
                if (fileNode.name != "VESSEL")
                {
                    innerNode = fileNode.GetNode("VESSEL");
                    if (innerNode == null)
                    {
                        // Try first child node
                        if (fileNode.CountNodes > 0)
                        {
                            innerNode = fileNode.nodes[0];
                            Debug.Log($"[GeneKerman] Using first child node: '{innerNode.name}'");
                        }
                        else
                        {
                            Debug.LogError("[GeneKerman] VesselTransfer.Import: No VESSEL node found.");
                            return null;
                        }
                    }
                }

                string vesselName = innerNode.GetValue("name") ?? "Imported Vessel";
                Debug.Log($"[GeneKerman] Vessel node found: '{vesselName}', " +
                          $"parts={innerNode.GetNodes("PART").Length}");

                // Generate new persistent ID to avoid collisions
                Guid newGuid = Guid.NewGuid();
                string newPidStr = newGuid.ToString("N"); // 32 digits, no dashes (KSP format)
                
                // Set the pid as a proper GUID string (with dashes to be safe, KSP can parse both, but let's use the standard "D" format)
                // The error said "Guid should contain 32 digits with 4 dashes", which is the "D" format.
                string newPidDashed = newGuid.ToString("D");
                
                innerNode.SetValue("pid", newPidDashed, true);
                // persistentId is sometimes a uint in older KSP, but let's just generate a random uint for it
                uint newPersistentId = (uint)rng.Next(100000, int.MaxValue);
                innerNode.SetValue("persistentId", newPersistentId.ToString(), true);

                // Randomize crew names
                RandomizeCrewNames(innerNode);

                // Add all crew to roster first (before creating ProtoVessel)
                AddCrewToRoster(innerNode);

                // Create ProtoVessel and add to game
                Debug.Log("[GeneKerman] Creating ProtoVessel...");
                ProtoVessel protoVessel = new ProtoVessel(innerNode, HighLogic.CurrentGame);

                // ProtoVessel.Load() is what actually registers the vessel with the
                // running game: it instantiates the Vessel, adds it to
                // FlightGlobals.Vessels, and adds the proto to flightState.protoVessels.
                // Merely adding to the protoVessels list (as the old non-flight branch
                // did) leaves the vessel detached — it never shows up in flight OR the
                // tracking station until a save/reload rebuilds the universe. KSP itself
                // uses this same path to spawn rescue-contract vessels and asteroids that
                // appear in the tracking station, so it's safe in every scene.
                Debug.Log($"[GeneKerman] Registering ProtoVessel into the universe (scene={HighLogic.LoadedScene})...");
                protoVessel.Load(HighLogic.CurrentGame.flightState);

                // Persist immediately so the import survives even if the player quits
                // before the next autosave. The tracking station list is rebuilt from
                // FlightGlobals on scene (re)entry, so a freshly imported vessel shows up
                // after re-entering the tracking station (or right away in the space center).
                if (!HighLogic.LoadedSceneIsFlight)
                {
                    try
                    {
                        GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                        Debug.Log("[GeneKerman] Saved game after vessel import.");
                    }
                    catch (Exception saveEx)
                    {
                        Debug.LogWarning($"[GeneKerman] Post-import save failed: {saveEx.Message}");
                    }
                }

                Debug.Log($"[GeneKerman] (Ok) Imported vessel '{vesselName}' with pid {newPidDashed}");

                return vesselName;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] VesselTransfer.Import failed: {ex}");
                return null;
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
            var nameMap = new Dictionary<string, string>();

            // 1. Reset SAS and RCS so the ship isn't spinning or draining monopropellant
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

            // 2. Randomize crew
            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                // Process CREW subnodes (older KSP versions or specific mods)
                foreach (ConfigNode crewNode in partNode.GetNodes("CREW"))
                {
                    string oldName = crewNode.GetValue("name");
                    if (string.IsNullOrEmpty(oldName)) continue;

                    if (!nameMap.ContainsKey(oldName))
                        nameMap[oldName] = GenerateKerbalName();

                    crewNode.SetValue("name", nameMap[oldName]);
                }

                // Process 'crew = name' fields on the PART node itself (Standard KSP 1.x)
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
                            nameMap[crewVal] = GenerateKerbalName();

                        partNode.AddValue("crew", nameMap[crewVal]);
                    }
                }
            }

            Debug.Log($"[GeneKerman] Randomized {nameMap.Count} crew member names.");
        }

        /// <summary>
        /// Add all crew members from the vessel to the game's crew roster
        /// so KSP doesn't throw errors about unknown crew.
        /// </summary>
        private static void AddCrewToRoster(ConfigNode vesselNode)
        {
            var roster = HighLogic.CurrentGame.CrewRoster;
            var addedNames = new HashSet<string>();

            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                // Try from CREW subnodes first
                foreach (ConfigNode crewNode in partNode.GetNodes("CREW"))
                {
                    string name = crewNode.GetValue("name");
                    string trait = crewNode.GetValue("trait");
                    AddKerbalToRoster(name, trait, roster, addedNames);
                }

                // Also try from 'crew = name' on the PART node
                foreach (string crewVal in partNode.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(crewVal) && !int.TryParse(crewVal, out _))
                    {
                        AddKerbalToRoster(crewVal, null, roster, addedNames);
                    }
                }
            }
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
}
