/*
 * VesselTransfer.cs – Export and import live vessels between KSP saves.
 *
 * Export: Serializes the active vessel's ProtoVessel into a ConfigNode string.
 * Import: Deserializes a ConfigNode string, randomizes crew names to avoid
 *         duplicates, adds crew to roster, and spawns the vessel in-game.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
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

        /// <summary>The pid (GUID string) of the most recently spawned vessel, set by
        /// SpawnInnerNode. Lets the rescue-immunity guardian pin the exact wreck it just
        /// imported. Best-effort single-shot state — read it right after an import call.</summary>
        public static string LastSpawnedPid { get; private set; }

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

            return ExportVesselNode(vessel, embedRoster);
        }

        /// <summary>
        /// Serialize an arbitrary loaded vessel into a "VESSEL" ConfigNode (flags +
        /// optional crew roster embedded). Works for any vessel in physics range, not
        /// just the active one, so multiple crafts can be packed into one submission.
        /// Returns null on failure.
        /// </summary>
        public static ConfigNode ExportVesselNode(Vessel vessel, bool embedRoster = false)
        {
            if (vessel == null) return null;

            try
            {
                // Force all parts to update their state before backup
                vessel.protoVessel = vessel.BackupVessel();

                ConfigNode vesselNode = new ConfigNode("VESSEL");
                vessel.protoVessel.Save(vesselNode);

                // The crew that the node above actually carries — read back off the
                // snapshot rather than off the vessel, so the roster we embed and the
                // count we log can never disagree with what was serialized. See CrewOf.
                List<ProtoCrewMember> crew = CrewOf(vessel.protoVessel);

                // Embed each crew member's full roster definition so the receiving
                // save can recreate them faithfully (gender, profession/trait,
                // courage, stupidity) instead of generating a random kerbal.
                if (embedRoster)
                    EmbedRosterData(vesselNode, crew);

                // Carry any custom mission flags the parts use so the receiving
                // save renders them instead of a missing decal.
                FlagTransfer.EmbedFlagsInNode(vesselNode);

                // Record which non-stock mods this vessel's parts come from so a
                // recipient missing them gets a CKAN modpack to install them.
                CkanGenerator.EmbedModsInNode(vesselNode, vessel);

                // Carry the Textures Unlimited paint job the same way: the recolour data
                // is already in the parts' modules, but which recolour PACK defines the
                // sets they name is only knowable here, on the sender's install.
                TextureTransfer.EmbedInNode(vesselNode);

                // And the RealFuels/RO fuel-and-engine configuration: also already in the
                // parts' modules, also from a mod no part walk can see. Which pack
                // defines each tank type is likewise only knowable on the sender's install.
                RealFuelsTransfer.EmbedInNode(vesselNode);

                // Snapshot the final values of any TweakScale-rescaled parts (absolute
                // model scale / mass / stats) into each part's GeneKermanScale module, so
                // the craft reconstructs identically for every receiver regardless of
                // their TweakScale version. Reads the live parts here while they exist.
                ScaleBridge.SnapshotIntoVesselNode(vessel, vesselNode);

                Debug.Log($"[GeneKerman] Exported vessel '{vessel.vesselName}': " +
                          $"{vessel.parts.Count} parts, {crew.Count} crew" +
                          (crew.Count > 0 ? $" ({string.Join(", ", crew.ConvertAll(p => p.name).ToArray())})" : ""));

                // KSP's own cached crew list is what most callers read; if it has drifted
                // from the parts, say so here rather than let a later "0 crew" report
                // send someone hunting through the transfer path for a lost kerbal.
                if (vessel.loaded && vessel.GetCrewCount() != crew.Count)
                    Debug.LogWarning($"[GeneKerman] '{vessel.vesselName}': KSP's cached crew list says " +
                                     $"{vessel.GetCrewCount()}, the parts say {crew.Count} — exporting the parts.");
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

        // ── Fleet Export (active + selected nearby vessels) ──────────────────

        /// <summary>
        /// Pack the active vessel plus any selected nearby vessels into a single
        /// "GKFLEET" container so a submission can deliver multiple crafts at once.
        /// Each extra vessel carries its own flags, crew roster, and (when found) its
        /// editor blueprint embedded as a GKCRAFT child. With no extras this returns a
        /// plain "VESSEL" node string, identical to <see cref="ExportActiveVessel"/>,
        /// so the legacy single-vessel path stays byte-for-byte compatible.
        /// </summary>
        public static string ExportFleet(Vessel active, List<Vessel> extras, bool embedRoster = false)
        {
            ConfigNode activeNode = (active != null)
                ? ExportVesselNode(active, embedRoster)
                : ExportActiveVesselNode(embedRoster);
            if (activeNode == null) return null;

            // No extras → keep the historical single-VESSEL payload (back-compat).
            if (extras == null || extras.Count == 0)
                return activeNode.ToString();

            ConfigNode fleet = new ConfigNode("GKFLEET");
            fleet.AddNode(activeNode);   // primary (contract) vessel goes first

            int packed = 1;
            foreach (var v in extras)
            {
                if (v == null || v == active) continue;
                ConfigNode vn = ExportVesselNode(v, embedRoster);
                if (vn == null) continue;
                EmbedCraftBlueprint(vn, v);
                fleet.AddNode(vn);
                packed++;
            }

            Debug.Log($"[GeneKerman] Exported fleet: {packed} vessels.");
            return fleet.ToString();
        }

        /// <summary>Attach a vessel's editor blueprint (.craft + loadmeta, flags
        /// embedded) to its VESSEL node as a base64 "GKCRAFT" child so the recipient
        /// can re-edit it in the VAB/SPH. Silently skips if no blueprint is found.</summary>
        private static void EmbedCraftBlueprint(ConfigNode vesselNode, Vessel v)
        {
            try
            {
                string path = VesselDataCollector.FindCraftFile(v.vesselName);
                if (string.IsNullOrEmpty(path))
                {
                    // Matched strictly on "<vesselName>.craft", so a vessel renamed in
                    // flight — or one that arrived from another player and was never a
                    // blueprint here — has none to carry. Say so: the recipient gets the
                    // vessel but nothing in their VAB/SPH, which otherwise looks like a bug.
                    Debug.Log($"[GeneKerman] EmbedCraftBlueprint: no .craft named '{v.vesselName}' "
                              + "on this install — sending the vessel without a blueprint.");
                    return;
                }

                byte[] craftBytes = System.IO.File.ReadAllBytes(path);
                // Bake the scale into the blueprint as well. The VESSEL node beside it is
                // already baked (SnapshotIntoVesselNode, above), so without this the
                // recipient gets a correct flying ship and a broken re-editable copy of the
                // same ship — the worst possible split. Matched against the live vessel by
                // craftID, exactly as the submission path does for a flight craft.
                if (v.parts != null && v.parts.Count > 0)
                    craftBytes = ScaleBridge.SnapshotIntoCraftBytes(craftBytes, v.parts);
                // Carry custom mission flags inside the blueprint too.
                craftBytes = FlagTransfer.EmbedFlagsInCraft(craftBytes);
                // …a TweakScale-version backstop, in case the bake above found nothing…
                craftBytes = TweakScaleGuard.EmbedVersionInCraft(craftBytes);
                // …the Textures Unlimited paint job (which recolour packs it needs — no
                // part walk can find a mod that adds no parts)…
                craftBytes = TextureTransfer.EmbedInCraft(craftBytes);
                // …the RealFuels/RO fuel-and-engine configuration (same blind spot)…
                craftBytes = RealFuelsTransfer.EmbedInCraft(craftBytes);
                // …the mod list…
                craftBytes = CkanGenerator.EmbedModsInCraft(craftBytes);
                // …and an NW-view thumbnail rendered from this specific vessel (appended
                // last so every strip stays a clean cut on import).
                craftBytes = CraftThumb.EmbedThumbForVessel(craftBytes, v, path);

                ConfigNode cn = vesselNode.AddNode("GKCRAFT");
                cn.AddValue("name", System.IO.Path.GetFileName(path));
                cn.AddValue("data", Convert.ToBase64String(craftBytes));

                string loadmeta = VesselDataCollector.ReadLoadmeta(path);
                if (!string.IsNullOrEmpty(loadmeta))
                    cn.AddValue("loadmeta",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(loadmeta)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] EmbedCraftBlueprint failed for '{v.vesselName}': {ex.Message}");
            }
        }

        /// <summary>Save each crew member's full ProtoCrewMember into a GKCREW child
        /// node so a different save can rebuild them exactly (KSP ignores unknown
        /// node names on load).</summary>
        private static void EmbedRosterData(ConfigNode vesselNode, List<ProtoCrewMember> crew)
        {
            if (crew == null) return;
            foreach (var pcm in crew)
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

        // ── Who is actually aboard ───────────────────────────────────────────
        //
        // Vessel.GetCrewCount() is `Vessel.crew.Count` and Vessel.GetVesselCrew() returns
        // that same list, refreshed only when the part count happens to have changed.
        // It is a cache, rebuilt by RebuildCrewList() / the onVesselCrewWasModified event
        // — so anything that seats or unseats a kerbal without firing that event leaves
        // it stale, and a stale-empty cache reads as "nobody aboard".
        //
        // That matters here beyond a wrong number: the crew embedded as GKCREW came off
        // the cache while the `crew = <name>` refs in each PART node come off
        // Part.protoModuleCrew, so a stale cache would ship the names with no roster
        // definitions and the recipient would rebuild each kerbal with a random gender,
        // trait and courage. Every crew read in the mod goes through the helpers below,
        // which read the same field KSP itself serializes.

        /// <summary>Crew a snapshot will actually write out. ProtoPartSnapshot.Save emits
        /// one `crew = name` per entry in protoCrewNames, filled from Part.protoModuleCrew
        /// when the snapshot was taken — so this is exactly the payload's crew.</summary>
        public static List<ProtoCrewMember> CrewOf(ProtoVessel pv)
        {
            var crew = new List<ProtoCrewMember>();
            if (pv?.protoPartSnapshots == null) return crew;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var roster = HighLogic.CurrentGame?.CrewRoster;

            foreach (ProtoPartSnapshot pps in pv.protoPartSnapshots)
            {
                if (pps == null) continue;

                // Resolved crew objects when the snapshot has them…
                if (pps.protoModuleCrew != null)
                    foreach (var pcm in pps.protoModuleCrew)
                        if (pcm != null && seen.Add(pcm.name)) crew.Add(pcm);

                // …and the names otherwise, which is what a snapshot read back from a
                // ConfigNode carries before KSP resolves it. Names alone can't build a
                // GKCREW node, so look each one up in the roster.
                if (pps.protoCrewNames == null || roster == null) continue;
                foreach (string name in pps.protoCrewNames)
                {
                    if (string.IsNullOrEmpty(name) || seen.Contains(name)) continue;
                    ProtoCrewMember pcm = roster[name];
                    if (pcm == null) continue;
                    seen.Add(name);
                    crew.Add(pcm);
                }
            }
            return crew;
        }

        /// <summary>Crew aboard a vessel right now, read from the parts (or from its
        /// snapshot when it isn't loaded) rather than from KSP's cached crew list.</summary>
        public static List<ProtoCrewMember> CrewOf(Vessel vessel)
        {
            if (vessel == null) return new List<ProtoCrewMember>();
            if (!vessel.loaded || vessel.parts == null) return CrewOf(vessel.protoVessel);

            var crew = new List<ProtoCrewMember>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Part p in vessel.parts)
            {
                if (p?.protoModuleCrew == null) continue;
                foreach (var pcm in p.protoModuleCrew)
                    if (pcm != null && seen.Add(pcm.name)) crew.Add(pcm);
            }
            return crew;
        }

        /// <summary>Number of kerbals aboard, counted off the parts. See <see cref="CrewOf(Vessel)"/>.</summary>
        public static int CrewCountOf(Vessel vessel) => CrewOf(vessel).Count;

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

        /// <summary>True when a roster name still carries someone else's ownership tag,
        /// i.e. the kerbal is only on loan to this save. Our own kerbals are never tagged
        /// here — <see cref="ApplyOwnershipTag"/> strips the tag the moment they come
        /// home — so this is the test for "not mine, do not keep".</summary>
        public static bool IsBorrowedCrewName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   name.IndexOf("'s ", StringComparison.Ordinal) > 0;
        }

        /// <summary>"{owner}'s {Name}" → "Name"; an untagged name unchanged. For reading
        /// a contract's tagged kerbal list against the *issuer's own* roster, where the
        /// same kerbals live under their bare names.</summary>
        public static string StripOwnershipTag(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int idx = name.IndexOf("'s ", StringComparison.Ordinal);
            return idx > 0 ? name.Substring(idx + 3) : name;
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
        /// Import a delivered payload that may be a single "VESSEL" node (legacy) or a
        /// "GKFLEET" container holding several. Every vessel is spawned and any embedded
        /// GKCRAFT blueprint installed. Returns the number of vessels imported.
        /// </summary>
        public static int ImportFleet(string vesselNodeStr, string ownerName, string myName)
        {
            if (!CanImport()) return 0;

            try
            {
                ConfigNode root = LoadRootNode(vesselNodeStr);
                if (root == null) return 0;

                // ConfigNode.Load wraps the file's top-level node(s) under an unnamed
                // root, so the GKFLEET / VESSEL node may be `root` itself or a child.
                ConfigNode fleet = (root.name == "GKFLEET") ? root : root.GetNode("GKFLEET");

                // Fleet container → import each VESSEL child.
                if (fleet != null)
                {
                    int count = 0;
                    foreach (ConfigNode vNode in fleet.GetNodes("VESSEL"))
                        if (ImportOneInner(vNode, ownerName, myName)) count++;
                    Debug.Log($"[GeneKerman] ImportFleet: imported {count} vessels.");
                    return count;
                }

                // Single vessel (root is VESSEL, or a wrapper around one).
                ConfigNode inner = (root.name == "VESSEL") ? root : root.GetNode("VESSEL");
                if (inner == null && root.CountNodes > 0) inner = root.nodes[0];
                if (inner == null)
                {
                    Debug.LogError("[GeneKerman] ImportFleet: no VESSEL node found.");
                    return 0;
                }
                return ImportOneInner(inner, ownerName, myName) ? 1 : 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] ImportFleet failed: {ex}");
                return 0;
            }
        }

        /// <summary>Run the full per-vessel import pipeline on an already-parsed inner
        /// VESSEL node: install its embedded blueprint, freshen ids/flags, reset
        /// controls, tag crew, pin its orbit epoch, and spawn it. Returns false on
        /// failure (so one bad vessel doesn't abort the rest of a fleet).</summary>
        private static bool ImportOneInner(ConfigNode innerNode, string ownerName, string myName)
        {
            try
            {
                InstallEmbeddedCraft(innerNode);   // pulls + strips any GKCRAFT children
                PrepareInnerNode(innerNode);       // fresh pid + install GKFLAG textures
                ResetControls(innerNode);
                TagCrew(innerNode, ownerName, myName);
                FreezeOrbitEpochToNow(innerNode);
                SpawnInnerNode(innerNode);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] ImportOneInner failed: {ex}");
                return false;
            }
        }

        /// <summary>Install any GKCRAFT blueprint(s) embedded in a VESSEL node into the
        /// save's Ships directory, then strip them so the ProtoVessel build never sees
        /// them.</summary>
        private static void InstallEmbeddedCraft(ConfigNode vesselNode)
        {
            foreach (ConfigNode cn in vesselNode.GetNodes("GKCRAFT"))
            {
                try
                {
                    string b64 = cn.GetValue("data");
                    if (string.IsNullOrEmpty(b64)) continue;
                    byte[] craftBytes = Convert.FromBase64String(b64);
                    string name = cn.GetValue("name") ?? "received.craft";

                    string loadmeta = null;
                    string lmB64 = cn.GetValue("loadmeta");
                    if (!string.IsNullOrEmpty(lmB64))
                        loadmeta = Encoding.UTF8.GetString(Convert.FromBase64String(lmB64));

                    CraftInstaller.Install(craftBytes, name, loadmeta);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] InstallEmbeddedCraft failed: {ex.Message}");
                }
            }
            while (vesselNode.GetNode("GKCRAFT") != null)
                vesselNode.RemoveNode("GKCRAFT");
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

        /// <summary>pid assigned to the most recently imported vessel (PrepareInnerNode
        /// mints a fresh one per import). For callers that must re-key bookkeeping to
        /// the spawned copy — a restored rescue submission has to update the
        /// contract→pid record or a later approval's removal targets the old, dead pid.</summary>
        public static string LastImportedPid { get; private set; }

        /// <summary>Parse a vessel string to its inner VESSEL node and assign a fresh
        /// pid/persistentId so it can't collide with an existing vessel.</summary>
        private static ConfigNode LoadInnerVesselNode(string vesselNodeStr)
        {
            ConfigNode fileNode = LoadRootNode(vesselNodeStr);
            if (fileNode == null) return null;

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

            PrepareInnerNode(innerNode);
            return innerNode;
        }

        /// <summary>Write a ConfigNode string to a temp file and load it back — the
        /// reliable way to parse a serialized node (ConfigNode.Parse() is flaky). Returns
        /// the root node (which may be a bare VESSEL or a GKFLEET container).</summary>
        private static ConfigNode LoadRootNode(string vesselNodeStr)
        {
            if (string.IsNullOrEmpty(vesselNodeStr))
            {
                Debug.LogWarning("[GeneKerman] Import: Empty vessel data.");
                return null;
            }

            string tempPath = System.IO.Path.Combine(
                KSPUtil.ApplicationRootPath, "PluginData", "GeneKerman_vessel_import.cfg");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempPath));
            System.IO.File.WriteAllText(tempPath, vesselNodeStr, Encoding.UTF8);
            ConfigNode fileNode = ConfigNode.Load(tempPath);
            try { System.IO.File.Delete(tempPath); } catch { }

            if (fileNode == null)
                Debug.LogError("[GeneKerman] Import: ConfigNode.Load returned null.");
            return fileNode;
        }

        /// <summary>Freshen an inner VESSEL node so it can be spawned without colliding
        /// with an existing vessel: assign a new pid/persistentId and install (then
        /// strip) any custom mission flags it carries, so parts resolve textures.</summary>
        private static void PrepareInnerNode(ConfigNode innerNode)
        {
            Guid newGuid = Guid.NewGuid();
            innerNode.SetValue("pid", newGuid.ToString("D"), true);
            innerNode.SetValue("persistentId", ((uint)rng.Next(100000, int.MaxValue)).ToString(), true);
            LastImportedPid = newGuid.ToString("D");

            // Install any custom mission flags this vessel carried (and strip the
            // GKFLAG nodes) before the ProtoVessel is built, so its parts resolve
            // the textures on spawn.
            FlagTransfer.ExtractAndInstallFlags(innerNode);

            // Read + strip the carried mod list; write a CKAN modpack for any mod the
            // recipient is missing (so they can install what this vessel needs).
            CkanGenerator.ExtractCheckAndStripMods(innerNode);

            // Then the finer-grained pass: a part this install lacks under one name may
            // be installed under another (a DLC part vs its ReStock+ stand-in), which the
            // mod-folder check above cannot see. Swap those before the ProtoVessel is
            // built, or the spawn drops the part.
            PartAliases.ApplyToVesselNode(innerNode, innerNode.GetValue("name"));

            // With the parts settled, reconcile the paint job: read + strip the GKTU node
            // and drop the recolour modules this install's prefabs can't accept, so a
            // vessel painted with a pack the recipient hasn't got spawns in stock colours
            // instead of dragging orphan modules into a live ProtoVessel.
            TextureTransfer.ExtractCheckAndStripFromNode(innerNode, innerNode.GetValue("name"));

            // Likewise the fuel/engine configuration: read + strip the GKRF node, check
            // tank types / engine configs / the RO environment against this install, and
            // for a recipient without RealFuels drop the RF modules and any propellant
            // this install doesn't define, so the spawned vessel carries local fuels
            // instead of resources KSP has no definition for.
            RealFuelsTransfer.ExtractCheckAndStripFromNode(innerNode, innerNode.GetValue("name"));

            // For any part carrying a GeneKermanScale snapshot, strip its TweakScale
            // module so the receiver's TweakScale (if any) stays at 1× and our applicator
            // is the sole authority — making the scaled craft deterministic across versions.
            ScaleBridge.NeutralizeTweakScaleForImport(innerNode);
        }

        /// <summary>Register an inner VESSEL node into the running universe (after
        /// any crew/placement edits) and persist. Returns the vessel name.</summary>
        private static string SpawnInnerNode(ConfigNode innerNode)
        {
            string vesselName = innerNode.GetValue("name") ?? "Imported Vessel";
            // Remember the fresh pid assigned in PrepareInnerNode so the caller (e.g. the
            // rescue-immunity guardian) can identify exactly the vessel we just spawned
            // without guessing by name. Reset each spawn.
            LastSpawnedPid = innerNode.GetValue("pid");

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

            // If we're in the Tracking Station, its vessel list was built on scene
            // entry and won't show the new craft until a scene reload. Rebuild it in
            // place so the import is immediately selectable.
            RefreshTrackingStation();

            Debug.Log($"[GeneKerman] (Ok) Spawned vessel '{vesselName}'");
            return vesselName;
        }

        /// <summary>Rebuild the Tracking Station's vessel list so a just-imported vessel
        /// appears without leaving and re-entering the scene. No-op outside the Tracking
        /// Station. SpaceTracking.buildVesselsList() is a private instance method that
        /// repopulates the vessel widgets from the live vessel list, so we call it via
        /// reflection (SpaceTracking.Instance itself is public).</summary>
        private static void RefreshTrackingStation()
        {
            if (HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
            try
            {
                var st = KSP.UI.Screens.SpaceTracking.Instance;
                if (st == null) return;

                var build = typeof(KSP.UI.Screens.SpaceTracking).GetMethod(
                    "buildVesselsList", BindingFlags.Instance | BindingFlags.NonPublic);
                if (build == null)
                {
                    Debug.LogWarning("[GeneKerman] Tracking Station refresh: buildVesselsList not found — KSP API may have changed.");
                    return;
                }

                build.Invoke(st, null);
                Debug.Log("[GeneKerman] Tracking Station vessel list rebuilt after import.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Tracking Station refresh failed: {ex.Message}");
            }
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
        /// Friendly name of a vessel by pid GUID, looked up while it still exists.
        /// Used for player-facing removal notices so the message can name the craft
        /// even after it's been destroyed. Falls back to a generic label.
        /// </summary>
        /// <summary>The live vessel with this pid, or null. For callers that need more
        /// than existence — e.g. "is it loaded right now?".</summary>
        public static Vessel FindVessel(string pid)
        {
            Guid g;
            if (string.IsNullOrEmpty(pid) || !Guid.TryParse(pid, out g)) return null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id == g) return v;
            return null;
        }

        public static string GetVesselName(string pid)
        {
            Guid g;
            if (string.IsNullOrEmpty(pid) || !Guid.TryParse(pid, out g))
                return "Your craft";
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id == g)
                    return string.IsNullOrEmpty(v.vesselName) ? "Your craft" : v.vesselName;
            return "Your craft";
        }

        /// <summary>
        /// Whether a vessel with this pid is still in the current save. Lets a caller
        /// ask "did that removal actually happen?" without going near Die() — the
        /// reconciler uses it so it can be run repeatedly and stay a no-op once the
        /// craft is gone. A vessel already dying this frame is treated as gone.
        /// </summary>
        public static bool VesselExists(string pid)
        {
            Guid g;
            if (string.IsNullOrEmpty(pid) || !Guid.TryParse(pid, out g)) return false;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id == g && v.state != Vessel.State.DEAD)
                    return true;
            return false;
        }

        /// <summary>Write the save out now. Never call this in flight — KSP would
        /// serialize a half-torn-down vessel.</summary>
        public static void SaveNow()
        {
            try { GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE); }
            catch (Exception saveEx) { Debug.LogWarning($"[GeneKerman] Save failed: {saveEx.Message}"); }
        }

        /// <summary>Outcome of a <see cref="RemoveVesselFromSave"/> call, so the caller
        /// can tell a terminal result (the vessel is gone — stop trying) from a
        /// retry-later one (we're in flight and must defer to a safe scene).</summary>
        public enum RemovalResult
        {
            Removed,   // we deleted it from this save
            NotFound,  // not in this save — already gone, nothing to do (terminal)
            Deferred,  // it's the focused flight vessel — retry from a non-flight scene
            Failed,    // bad pid or an exception while removing
        }

        /// <summary>What becomes of the crew aboard a vessel this save is giving up.
        /// Removing the ship kills whoever is aboard (KSP's own Die()), so every value
        /// here is also a decision about kerbals the player did not agree to lose.</summary>
        public enum CrewFate
        {
            /// <summary>Everyone aboard goes with the craft. The issuer side of a rescue:
            /// the stranded crew are the point of the contract, and they come back later
            /// as an import with their tag stripped, not by staying here.</summary>
            LeavesWithCraft,

            /// <summary>Only borrowed kerbals ("{owner}'s {name}") go; our own are handed
            /// back to the roster as Available. The rescuer side: the delivery ship is
            /// normally flown by the player's own pilots, and handing the craft over must
            /// not quietly cost them a crew they never sent anywhere.</summary>
            BorrowedOnly,

            /// <summary>Nobody leaves the roster; the craft alone is removed.</summary>
            StaysInRoster,
        }

        /// <summary>
        /// Remove a vessel (by pid GUID) from the current save, disposing of its crew
        /// per <paramref name="crewFate"/> so the same kerbals don't exist in two saves.
        /// Refuses to remove the focused active vessel in flight (caller must defer to a
        /// Space Center / Tracking Station scene).
        ///
        /// <paramref name="persist"/> false leaves the save file alone — for a caller
        /// removing several vessels, which wants one save at the end and, more to the
        /// point, wants its own bookkeeping to be up to date before that save runs.
        /// The removal itself is complete either way; only the write to disk is deferred.
        /// </summary>
        public static RemovalResult RemoveVesselFromSave(string pid,
                                                         CrewFate crewFate = CrewFate.LeavesWithCraft,
                                                         bool persist = true)
        {
            if (string.IsNullOrEmpty(pid))
            {
                Debug.LogWarning("[GeneKerman] RemoveVessel: no pid given.");
                return RemovalResult.Failed;
            }
            Guid g;
            if (!Guid.TryParse(pid, out g))
            {
                Debug.LogWarning($"[GeneKerman] RemoveVessel: pid '{pid}' is not a GUID.");
                return RemovalResult.Failed;
            }

            Vessel target = null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id == g) { target = v; break; }

            if (target == null)
            {
                // Not in this save — either already removed or it belongs to a different
                // save. Either way there's nothing to remove, so this is terminal: the
                // caller must stop retrying (otherwise it spins once per frame forever).
                Debug.Log($"[GeneKerman] RemoveVessel: no vessel with pid {pid} in this save — already gone.");
                return RemovalResult.NotFound;
            }

            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel == target)
            {
                Debug.LogWarning("[GeneKerman] RemoveVessel: target is the active vessel — defer to a non-flight scene.");
                return RemovalResult.Deferred;
            }

            try
            {
                // Snapshot the crew before destroying the hull — Die() unassigns them, so
                // there is nobody aboard to read afterwards. Always read them, whatever
                // their fate: Die() marks everyone aboard Missing (or KIA), so even the
                // crew we are keeping have to be found again and put back.
                var crew = CrewOf(target);

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

                // Same null-tolerance as flightState above: the vessel is already gone by
                // this point, so a missing game must not turn a completed removal into a
                // Failed the caller retries forever.
                var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
                var kept = new List<string>();
                var dropped = new List<string>();
                foreach (var pcm in (roster != null ? crew : new List<ProtoCrewMember>()))
                {
                    if (pcm == null) continue;
                    try
                    {
                        if (KeepsRoster(crewFate, pcm.name))
                        {
                            // They were never lost — the craft was. Undo the death Die()
                            // just handed them, or they sit in the Astronaut Complex's
                            // Lost tab (its Available tab lists Available crew only) and
                            // still count against the hireable-crew limit.
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            kept.Add(pcm.name);
                            continue;
                        }
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                        // Remove is keyed by name and answers whether it found anything.
                        // A false here is how a kerbal ends up parked as Dead forever, so
                        // say so rather than reporting a drop that didn't happen.
                        if (roster.Remove(pcm)) dropped.Add(pcm.name);
                        else Debug.LogWarning($"[GeneKerman] RemoveVessel: {pcm.name} was not in " +
                                              "the roster to drop.");
                    }
                    catch (Exception cex)
                    {
                        Debug.LogWarning($"[GeneKerman] RemoveVessel: could not settle crew {pcm.name}: {cex.Message}");
                    }
                }
                if (kept.Count > 0 || dropped.Count > 0)
                    Debug.Log($"[GeneKerman] RemoveVessel: crew fate {crewFate} — " +
                              $"kept [{string.Join(", ", kept.ToArray())}], " +
                              $"dropped [{string.Join(", ", dropped.ToArray())}].");

                if (persist && !HighLogic.LoadedSceneIsFlight)
                    SaveNow();

                Debug.Log($"[GeneKerman] (Ok) Removed vessel pid {pid} from save.");
                return RemovalResult.Removed;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] RemoveVessel failed: {ex}");
                return RemovalResult.Failed;
            }
        }

        /// <summary>
        /// Remove the crew a contract hands over *by name*, wherever they are now.
        ///
        /// The vessel removal above settles whoever is aboard the recorded hull — and
        /// only them. A kerbal who stepped off before it ran (EVA'd away, boarded a
        /// different pod, was even the "vessel" a crew-only delivery was submitted as)
        /// used to survive the removal while their copy was delivered to the other
        /// player — a duplicate the server has no way to see. This walks the contract's
        /// own crew list against the whole save: still-present names are lifted out of
        /// whatever vessel holds them (loaded part or proto snapshot, the same two
        /// shapes the emergency freeze edits) and dropped from the roster.
        ///
        /// Returns true when every listed kerbal is settled (or was already gone).
        /// False means at least one could not be settled yet — a kerbal currently ON
        /// EVA as a loaded vessel of their own is deferred rather than killed under
        /// the player — and the caller must keep its queue entry so a later pass
        /// (Space Center at the latest) finishes the job.
        /// </summary>
        public static bool RemoveContractCrew(List<string> names, CrewFate fate)
        {
            if (names == null || names.Count == 0 || fate == CrewFate.StaysInRoster) return true;
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return false;

            bool allSettled = true;
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (fate == CrewFate.BorrowedOnly && !IsBorrowedCrewName(name)) continue;

                try
                {
                    ProtoCrewMember pcm = FindRosterMember(roster, name);
                    if (pcm == null) continue;  // already gone — the normal case

                    // Where are they? A kerbal on EVA *is* a vessel; one aboard a ship
                    // is a crew entry on it; one in the roster alone is neither.
                    Vessel host = null;
                    bool isEvaVessel = false;
                    foreach (var v in FlightGlobals.Vessels)
                    {
                        if (v == null) continue;
                        if (v.isEVA && VesselHoldsCrew(v, name)) { host = v; isEvaVessel = true; break; }
                        if (VesselHoldsCrew(v, name)) { host = v; break; }
                    }

                    if (isEvaVessel)
                    {
                        if (host.loaded)
                        {
                            // Killing a loaded EVA kerbal detonates them in front of the
                            // player; wait for a pass where they're aboard something or
                            // out of range.
                            Debug.LogWarning($"[GeneKerman] RemoveContractCrew: {name} is on EVA " +
                                             "nearby — deferring until they board or leave range.");
                            allSettled = false;
                            continue;
                        }
                        host.Die();
                        var fs = HighLogic.CurrentGame.flightState;
                        if (fs != null && fs.protoVessels != null)
                        {
                            Guid gid = host.id;
                            fs.protoVessels.RemoveAll(pv => pv != null && pv.vesselID == gid);
                        }
                    }
                    else if (host != null)
                    {
                        RemoveCrewFromVessel(host, name);
                    }

                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                    if (!roster.Remove(pcm))
                        Debug.LogWarning($"[GeneKerman] RemoveContractCrew: {name} was not in the roster to drop.");
                    Debug.Log($"[GeneKerman] RemoveContractCrew: {name} left with the contract" +
                              (host != null ? $" (was {(isEvaVessel ? "on EVA" : "aboard '" + host.vesselName + "'")})." : "."));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] RemoveContractCrew: could not settle {name}: {ex.Message}");
                    allSettled = false;
                }
            }
            return allSettled;
        }

        /// <summary>Is this kerbal aboard the vessel, loaded or proto?</summary>
        private static bool VesselHoldsCrew(Vessel v, string name)
        {
            if (v.loaded)
            {
                if (v.parts == null) return false;
                foreach (var p in v.parts)
                    if (p?.protoModuleCrew != null &&
                        p.protoModuleCrew.Exists(c => c != null && c.name == name))
                        return true;
                return false;
            }
            var snaps = v.protoVessel?.protoPartSnapshots;
            if (snaps == null) return false;
            foreach (var pps in snaps)
                if (pps?.protoModuleCrew != null &&
                    pps.protoModuleCrew.Exists(c => c != null && c.name == name))
                    return true;
            return false;
        }

        /// <summary>Lift a kerbal out of a vessel in place — the loaded and proto
        /// shapes both, mirroring the emergency freeze's removal.</summary>
        private static void RemoveCrewFromVessel(Vessel v, string name)
        {
            if (v.loaded)
            {
                foreach (var p in v.parts)
                {
                    if (p?.protoModuleCrew == null) continue;
                    var pcm = p.protoModuleCrew.Find(c => c != null && c.name == name);
                    if (pcm == null) continue;
                    p.RemoveCrewmember(pcm);
                    Vessel.CrewWasModified(v);
                    GameEvents.onVesselWasModified.Fire(v);
                    return;
                }
                return;
            }
            var snaps = v.protoVessel?.protoPartSnapshots;
            if (snaps == null) return;
            foreach (var pps in snaps)
            {
                if (pps?.protoModuleCrew == null) continue;
                var pcm = pps.protoModuleCrew.Find(c => c != null && c.name == name);
                if (pcm == null) continue;
                pps.protoModuleCrew.Remove(pcm);
                pps.protoCrewNames?.Remove(name);
                try { v.protoVessel.RemoveCrew(pcm); } catch { /* best-effort */ }
                return;
            }
        }

        private static ProtoCrewMember FindRosterMember(KerbalRoster roster, string name)
        {
            var statuses = new[]
            {
                ProtoCrewMember.RosterStatus.Assigned,
                ProtoCrewMember.RosterStatus.Available,
                ProtoCrewMember.RosterStatus.Dead,
                ProtoCrewMember.RosterStatus.Missing,
            };
            foreach (var pcm in roster.Kerbals(statuses))
                if (pcm != null && pcm.name == name) return pcm;
            return null;
        }

        /// <summary>Does this kerbal stay in the roster when their ship is given up?</summary>
        private static bool KeepsRoster(CrewFate fate, string kerbalName)
        {
            switch (fate)
            {
                case CrewFate.StaysInRoster: return true;
                case CrewFate.BorrowedOnly:  return !IsBorrowedCrewName(kerbalName);
                default:                     return false;
            }
        }

        /// <summary>
        /// Drop borrowed kerbals ("{owner}'s {name}") that this save has left dead or
        /// missing — the residue of a craft that left without a clean hand-over (the
        /// common case: the ship was already gone by the time its removal ran, so
        /// nothing ever settled its crew).
        ///
        /// They belong to another player and their ship isn't here, so they can do
        /// nothing but harm: KSP counts Missing crew against the astronaut-complex
        /// hire limit, and its applicant generator refuses any new name that appears
        /// as a substring of an existing roster name — silently, so a polluted roster
        /// shows up as an empty applicant list rather than an error. Anything the
        /// emergency freeze is deliberately holding is left alone; it parks its crew
        /// as Dead on purpose and thaws them itself.
        ///
        /// Returns how many were dropped. Nothing here is lost for good — a re-import
        /// of their craft rebuilds them from its GKCREW nodes.
        /// </summary>
        public static int PurgeBorrowedGhostCrew()
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return 0;

            try
            {
                var frozen = new HashSet<string>(StringComparer.Ordinal);
                var records = GKContractScenario.Instance != null
                    ? GKContractScenario.Instance.Immunities : null;
                if (records != null)
                    foreach (var rec in records)
                    {
                        if (rec == null || rec.Crew == null) continue;
                        foreach (var c in rec.Crew)
                            if (c != null && !string.IsNullOrEmpty(c.Name)) frozen.Add(c.Name);
                    }

                // Materialise before removing: the roster is being iterated.
                var statuses = new[]
                {
                    ProtoCrewMember.RosterStatus.Dead,
                    ProtoCrewMember.RosterStatus.Missing,
                };
                var ghosts = new List<ProtoCrewMember>();
                foreach (var pcm in roster.Kerbals(statuses))
                {
                    if (pcm == null || !IsBorrowedCrewName(pcm.name)) continue;
                    if (frozen.Contains(pcm.name)) continue;
                    ghosts.Add(pcm);
                }

                int removed = 0;
                foreach (var pcm in ghosts)
                {
                    try { if (roster.Remove(pcm)) removed++; }
                    catch (Exception rex)
                    {
                        Debug.LogWarning($"[GeneKerman] RosterSweep: could not drop {pcm.name}: {rex.Message}");
                    }
                }
                if (removed > 0)
                    Debug.Log($"[GeneKerman] RosterSweep: dropped {removed} borrowed kerbal(s) " +
                              "left behind by craft that are no longer in this save.");
                return removed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RosterSweep failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Kerbals in this save whose profession no installed mod defines, as
        /// "Name (trait)" — the labels for the warning. This call is read-only: the trait
        /// string is exactly what lets them resolve again if the mod that defines it comes
        /// back, so nothing rewrites it behind the player's back. <see cref="TraitRepair"/>
        /// is the deliberate, recorded, reversible way to overwrite one, and it only runs
        /// from the button on that warning.
        ///
        /// Reporting it is worth doing at all because the way KSP fails here names
        /// nothing: one of these anywhere in the roster throws a NullReference part-way
        /// through building the Astronaut Complex (see <see cref="ApplyTrait"/>), and
        /// what the player sees is a screen with one half-drawn applicant who cannot be
        /// hired and three empty tabs.
        /// </summary>
        public static List<string> FindUnresolvableTraitCrew()
        {
            var found = new List<string>();
            foreach (var pcm in TraitRepair.BrokenCrew())
                found.Add($"{pcm.name} ({pcm.trait})");
            return found;
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
            // Collect the professions this install has to refuse while the import runs
            // and report them once at the end — one message per craft, not one per kerbal
            // (the same shape as PartAliases' Report). This is the only entry point into
            // ApplyTrait, so the accumulator's lifetime is exactly one import; the finally
            // matters because a throw part-way through still leaves downgraded crew behind.
            traitDowngrades = new List<TraitRepair.Downgrade>();
            try { AddCrewToRosterInner(vesselNode); }
            finally
            {
                var downgraded = traitDowngrades;
                traitDowngrades = null;
                // Write the originals down before saying anything about them: the message
                // promises they come back if the mod is installed, and TraitRepair's record
                // file is what makes that true.
                TraitRepair.RememberDowngrades(downgraded);
                // Read the name defensively: this runs on the way out of a throw too,
                // and an NRE here would replace the real exception with a useless one.
                PostTraitDowngrades(downgraded,
                    vesselNode != null ? vesselNode.GetValue("name") : null);
            }
        }

        private static void AddCrewToRosterInner(ConfigNode vesselNode)
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

        // Downgrades collected across one import, or null outside one. See
        // AddCrewToRoster, which owns its lifetime. The record type is TraitRepair's
        // because that is where they are persisted and undone from.
        private static List<TraitRepair.Downgrade> traitDowngrades;

        /// <summary>
        /// Give an incoming kerbal the sender's profession, but only one this install
        /// actually defines.
        ///
        /// `KerbalRoster.SetExperienceTrait` does **not** validate: an unknown name is
        /// written straight into `pcm.trait`, and since no `EXPERIENCE_TRAIT` matches it,
        /// `experienceTrait` is left null. That combination is a landmine for every stock
        /// screen built out of `CrewListItem` — the Astronaut Complex and the crew
        /// assignment dialog — because `SetXP` reads `pcm.experienceTrait.Title`. Its one
        /// self-repair (`SetExperienceTrait(pcm, null)`) can't help: the fallback that
        /// picks a valid trait only fires when `pcm.trait` is *empty*, and this one is
        /// full of a name that will never resolve. The result is a NullReference thrown
        /// mid-build, which takes out the rest of the list, the other three lists, and
        /// leaves a half-drawn row that cannot be clicked — with nothing in the log
        /// tying it to the kerbal that caused it.
        ///
        /// The sender's own trait is not lost by refusing it here: it stays in the GKCREW
        /// node that travels with the craft, so the same kerbal resolves correctly again
        /// in any save that has the mod defining it.
        /// </summary>
        private static void ApplyTrait(ProtoCrewMember pcm, string trait)
        {
            if (pcm == null || string.IsNullOrEmpty(trait)) return;

            bool known;
            try
            {
                var configs = GameDatabase.Instance != null
                    ? GameDatabase.Instance.ExperienceConfigs : null;
                known = configs != null && configs.GetExperienceTraitConfig(trait) != null;
            }
            catch { known = false; }

            if (!known)
            {
                // Leave whatever GetNewKerbal generated — a real local profession.
                Debug.LogWarning($"[GeneKerman] Crew import: '{trait}' is not a profession this " +
                                 $"install defines — {pcm.name} keeps {pcm.trait} instead. " +
                                 "(Install the mod that adds it to get the original back.)");
                if (traitDowngrades != null)
                    traitDowngrades.Add(new TraitRepair.Downgrade
                    { Name = pcm.name, Original = trait, Given = pcm.trait });
                return;
            }
            KerbalRoster.SetExperienceTrait(pcm, trait);
        }

        /// <summary>
        /// Say out loud which incoming kerbals lost their profession, once per import.
        ///
        /// <see cref="ApplyTrait"/> refuses a trait this install can't define, which keeps
        /// the roster safe but silently changes someone's job: a player who was told they
        /// were getting an engineer finds a pilot, with nothing but a log line to explain
        /// it. Every other import-side substitution reports itself (PartAliases, GKMODS,
        /// GKTU) and this one has the same shape, so it says the same kind of thing.
        ///
        /// The original is not lost twice over: the *craft* keeps it in its GKCREW node,
        /// and <see cref="TraitRepair.RememberDowngrades"/> keeps it for these roster
        /// entries — so installing the mod later does hand these kerbals their job back,
        /// on the next visit to the Space Center.
        /// </summary>
        private static void PostTraitDowngrades(List<TraitRepair.Downgrade> downgrades, string vesselName)
        {
            if (downgrades == null || downgrades.Count == 0) return;

            string what = string.IsNullOrEmpty(vesselName) ? "this craft" : "'" + vesselName + "'";

            // Who changed, and which mods would have covered them — deduped, because the
            // point of the message is what to install and two Kolonists are one install.
            var lines = new List<string>();
            var mods = new List<string>();
            foreach (var d in downgrades)
            {
                lines.Add($"{d.Name} ({d.Original} → {d.Given})");
                string mod = ContractConstraints.TraitMod(d.Original);
                if (mod != null && !mods.Contains(mod)) mods.Add(mod);
            }

            var sb = new StringBuilder();
            sb.Append($"{downgrades.Count} kerbal(s) aboard {what} have a profession no installed ")
              .Append("mod defines, and were given a local one instead: ")
              .Append(string.Join("; ", lines.ToArray())).Append(". ");
            if (mods.Count > 0)
                sb.Append($"Those come from {string.Join(" / ", mods.ToArray())}. ");
            sb.Append("Their original professions are remembered: install the mod that defines ")
              .Append("them and they are handed back automatically.");

            string title = "Crew arrived without their profession";
            string body = sb.ToString();
            Debug.LogWarning($"[GeneKerman] {title} — {body}");

            var gk = GeneKermanMod.Instance;
            if (gk != null)
            {
                try { gk.ShowNotification(title, body); return; }
                catch (Exception ex)
                {
                    Debug.LogWarning("[GeneKerman] Crew-trait notification failed, falling back " +
                                     $"to screen message: {ex.Message}");
                }
            }

            try { ScreenMessages.PostScreenMessage($"{title}: {body}", 12f, ScreenMessageStyle.UPPER_CENTER); }
            catch { /* no screen (headless) — the log line is enough */ }
        }

        /// <summary>Copy gender, profession/trait, courage and stupidity from a saved
        /// ProtoCrewMember node (KSP stores courage as "brave", stupidity as "dull").</summary>
        private static void ApplyKerbalAttributes(ProtoCrewMember pcm, ConfigNode kn)
        {
            ApplyTrait(pcm, kn.GetValue("trait"));

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
            ApplyTrait(newCrew, trait);

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
    /// What a rescuer has to deliver, and where.
    ///
    /// `mode` is the DELIVERY destination — NOT where the wreck spawns (the wreck keeps
    /// its own snapshot orbit so there's an actual rescue to fly). Orbit mode uses Ap/Pe
    /// (altitudes above the surface); surface mode uses Lat/Lon. Margins are the
    /// issuer-set tolerances.
    ///
    /// `recovery` is the separate question of *what* has to arrive:
    ///   "crew"   — the stranded kerbals, aboard whatever ship brought them. The wreck
    ///              may be stripped, abandoned or destroyed.
    ///   "vessel" — the crew AND the wreck itself, towed/flown home. Checked by part
    ///              flightID: KSP keeps a part's uid across export, import, docking and
    ///              undocking, so the wreck stays identifiable however it gets home.
    /// `minDv` is a floor on the delivering craft's remaining vacuum Δv, so the crew are
    /// dropped somewhere they can actually leave from. 0 means no requirement.
    /// </summary>
    public class RescueTargetSpec
    {
        public string body;
        public string mode = "orbit";       // "orbit" | "surface"
        public double ap;
        public double pe;
        public double lat;
        public double lon;
        public double marginAlt;
        public double marginPos;

        public string recovery = "crew";    // "crew" | "vessel"
        public double minDv;                // m/s, 0 = no requirement

        // Orbit mode only: the plane and the regime the delivery orbit has to be in.
        // Ap/Pe say nothing about either, so without these a craft in an equatorial
        // orbit satisfies a rescue from a polar one. marginIncl <= 0 == any plane,
        // no orbit types == any regime — which is every rescue issued before this.
        public double incl;                 // target inclination, degrees (0..180)
        public double marginIncl;           // ± degrees; <= 0 = no plane requirement
        public List<string> orbitTypes = new List<string>();

        /// <summary>The named-regime half of the requirement, as the shared checker
        /// wants it. Empty when the issuer named no regime.</summary>
        public OrbitConstraint OrbitTypeConstraint()
        {
            var o = new OrbitConstraint();
            if (orbitTypes != null) o.Requirements.AddRange(orbitTypes);
            return o;
        }

        /// <summary>One-line summary of the orbit requirement, or "" when there is none.</summary>
        public string DescribeOrbitRequirement()
        {
            var bits = new List<string>();
            var types = OrbitTypeConstraint();
            if (!types.IsEmpty) bits.Add(types.LabelList());
            if (marginIncl > 0) bits.Add($"inclination {incl:F1}° (±{marginIncl:F1}°)");
            return string.Join(" · ", bits.ToArray());
        }

        /// <summary>flightIDs of the wreck's parts as it was handed over. Only the
        /// rescuer's client is sent these, and only on a "vessel" recovery — nobody
        /// else has anything to check them against.</summary>
        public List<uint> wreckParts = new List<uint>();

        public bool RequiresWreck => string.Equals(recovery, "vessel", StringComparison.OrdinalIgnoreCase);

        public static RescueTargetSpec FromDict(System.Collections.Generic.Dictionary<string, object> d)
        {
            if (d == null) return null;
            var spec = new RescueTargetSpec
            {
                body = MiniJSON.GetString(d, "body", ""),
                mode = MiniJSON.GetString(d, "mode", "orbit"),
                ap = MiniJSON.GetDouble(d, "ap", 0),
                pe = MiniJSON.GetDouble(d, "pe", 0),
                lat = MiniJSON.GetDouble(d, "lat", 0),
                lon = MiniJSON.GetDouble(d, "lon", 0),
                marginAlt = MiniJSON.GetDouble(d, "margin_alt", 0),
                marginPos = MiniJSON.GetDouble(d, "margin_pos", 0),
                // Absent on every rescue issued before the two modes existed, which were
                // all crew-only with no Δv floor — exactly what these defaults mean.
                recovery = MiniJSON.GetString(d, "recovery", "crew"),
                minDv = MiniJSON.GetDouble(d, "min_dv", 0),
                // Absent on every rescue issued before the plane could be constrained;
                // a 0 margin reads as "any plane", which is what those all meant.
                incl = MiniJSON.GetDouble(d, "inc", 0),
                marginIncl = MiniJSON.GetDouble(d, "margin_inc", 0),
            };

            var types = MiniJSON.GetList(d, "orbit_types");
            if (types != null)
                foreach (var o in types)
                {
                    string t = o == null ? null : o.ToString().Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(t)) spec.orbitTypes.Add(t);
                }

            var parts = MiniJSON.GetList(d, "wreck_parts");
            if (parts != null)
                foreach (var o in parts)
                {
                    if (o == null) continue;
                    uint id;
                    if (uint.TryParse(o.ToString(), out id) && id != 0) spec.wreckParts.Add(id);
                }

            return spec;
        }
    }
}
