/*
 * VesselTransfer.cs – Export and import live vessels between KSP saves.
 *
 * Export: Serializes the active vessel's ProtoVessel into a ConfigNode string.
 * Import: Deserializes a ConfigNode string, randomizes crew names to avoid
 *         duplicates, adds crew to roster, and spawns the vessel in-game.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
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

                // And the cheat mark, if this vessel carries one. Taint is keyed on
                // persistentId and every import mints a fresh one, so without this a
                // copy of a cheated vessel arrives clean — see CheatDetection's carry
                // section. Written here because this is the one choke point every
                // live-vessel export goes through.
                CheatDetection.EmbedInNode(vesselNode, vessel);

                // Snapshot the final values of any TweakScale-rescaled parts (absolute
                // model scale / mass / stats) into each part's GeneKermanScale module, so
                // the craft reconstructs identically for every receiver regardless of
                // their TweakScale version. Reads the live parts here while they exist.
                ScaleBridge.SnapshotIntoVesselNode(vessel, vesselNode);

                // And where the ground was under it, if it is sitting on any. A landed
                // craft's `alt` is an altitude above SEA level, so it only means anything
                // against the terrain that produced it; the recipient's install cannot
                // re-derive the sender's ground level, so it is carried. See
                // SurfacePlacement.
                SurfacePlacement.EmbedInNode(vesselNode, vessel);

                Debug.Log($"[GeneKerman] Exported vessel '{vessel.vesselName}': " +
                          $"{vessel.parts.Count} parts, {crew.Count} crew" +
                          (crew.Count > 0 ? $" ({string.Join(", ", crew.ConvertAll(p => p.name).ToArray())})" : ""));

                // KSP's own cached crew list is what most callers read; if it has drifted
                // from the parts, say so here rather than let a later "0 crew" report
                // send someone hunting through the transfer path for a lost kerbal.
                if (vessel.loaded && vessel.GetCrewCount() != crew.Count)
                    Debug.LogWarning($"[GeneKerman] '{vessel.vesselName}': KSP's cached crew list says " +
                                     $"{vessel.GetCrewCount()}, the parts say {crew.Count}; exporting the parts.");
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
            List<string> ignored;
            return ExportFleet(active, extras, embedRoster, out ignored);
        }

        /// <summary>
        /// <see cref="ExportFleet"/>, additionally reporting the pid of every vessel it
        /// actually packed, primary first.
        ///
        /// A caller that records what it handed over must record exactly this and not
        /// the list it asked for. An extra whose node export fails is dropped here in
        /// silence — quite deliberately, since one unreadable craft must not fail a whole
        /// submission — and a recipient never receives it. Recording it anyway would
        /// queue that craft for deletion out of the sender's own save when the contract
        /// is approved: a ship destroyed at one end and never created at the other.
        /// </summary>
        public static string ExportFleet(Vessel active, List<Vessel> extras, bool embedRoster,
                                         out List<string> packedPids)
        {
            packedPids = new List<string>();

            Vessel primary = active ?? FlightGlobals.ActiveVessel;
            ConfigNode activeNode = (active != null)
                ? ExportVesselNode(active, embedRoster)
                : ExportActiveVesselNode(embedRoster);
            if (activeNode == null) return null;
            if (primary != null) packedPids.Add(primary.id.ToString());

            // No extras → keep the historical single-VESSEL payload (back-compat).
            if (extras == null || extras.Count == 0)
                return activeNode.ToString();

            ConfigNode fleet = new ConfigNode("GKFLEET");

            // Name the primary explicitly as well as writing it first. Ordering is the
            // contract every reader may rely on — the server's _primary_vessel_values
            // scopes its rescue rechecks to the FIRST VESSEL node and deliberately does
            // not read this value, because a marker in a client-supplied payload could be
            // pointed anywhere. On our own import side the two agree and the pid is the
            // stronger match: it survives the node list being reordered by anything
            // between here and there, and a reader that cannot find it falls back to the
            // first node. Written on the GKFLEET wrapper, which is discarded before any
            // ProtoVessel is built, so KSP never sees an unknown VESSEL field.
            string primaryPid = activeNode.GetValue("pid");
            if (!string.IsNullOrEmpty(primaryPid)) fleet.AddValue("primaryPid", primaryPid);

            fleet.AddNode(activeNode);   // primary (contract) vessel goes first

            int packed = 1;
            foreach (var v in extras)
            {
                if (v == null || v == primary) continue;
                ConfigNode vn = ExportVesselNode(v, embedRoster);
                if (vn == null)
                {
                    Debug.LogWarning($"[GeneKerman] ExportFleet: could not export '{v.vesselName}', " +
                                     "leaving it out of this delivery.");
                    continue;
                }
                EmbedCraftBlueprint(vn, v);
                fleet.AddNode(vn);
                packedPids.Add(v.id.ToString());
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
                              + "on this install; sending the vessel without a blueprint.");
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

        /// <summary>The owner a tag names when the server told us nothing. Not a
        /// username and not meant to be one: it exists so an incoming kerbal whose owner
        /// is unknown still reads as borrowed. Left bare — which is what an empty
        /// owner_name used to produce, and the bot sends one on any rescue whose stored
        /// contractor/issuer name is missing — a stranger's crew are adopted as this
        /// save's own: never swept, and counted against the astronaut-complex hire limit
        /// for the life of the save.</summary>
        public const string UnknownOwnerTag = "someone else";

        /// <summary>
        /// <see cref="ApplyOwnershipTag"/> for a name arriving from *another player's*
        /// node, where the one thing the name may never do is claim to be ours.
        ///
        /// A vessel node is written entirely by whoever sent it, so an embedded
        /// "{X}'s {core}" prefix is a claim, not a fact. Honoured blindly (as the plain
        /// tagger does, and must, for names already sitting in our own roster) it is a
        /// capture primitive: a sender who knows the recipient's Boundless username —
        /// it is printed in every player picker — writes their crew as
        /// "{Victim}'s Jebediah Kerman", the tag is stripped as "coming home", and the
        /// incoming vessel keys onto the recipient's *own* Jeb. That kerbal then reads
        /// as un-borrowed, so a later hand-over deletes them from the roster for good.
        ///
        /// So only <paramref name="ownerName"/> — which comes from the server with the
        /// import entry, not from the payload — may decide that these crew are ours.
        /// An embedded tag naming somebody else is still carried, because that is the
        /// honest multi-hop case (A → B → C keeps A's name on A's kerbal); an embedded
        /// tag naming *us* is refused and the whole incoming name becomes the core, so
        /// the forgery arrives visibly as "{Sender}'s {Victim}'s Jebediah Kerman"
        /// rather than silently as one of ours.
        ///
        /// <paramref name="homebound"/> is what the first version of this got wrong, and
        /// it got it wrong in the direction that destroys things. Refusing *every*
        /// embedded tag that names us also refuses the honest rescue return leg, which
        /// has exactly the forgery's shape: A issues a rescue, B's save holds the
        /// stranded crew as "A's Jeb", and the craft comes home to A tagged by B. With no
        /// allow-list those kerbals arrived as "B's A's Jeb" — which
        /// <see cref="IsBorrowedCrewName"/> reads as borrowed, so
        /// <see cref="PurgeBorrowedGhostCrew"/> and the BorrowedOnly hand-over swept A's
        /// own crew out of A's roster permanently, and the tag doubled again on every
        /// further round trip ("C's B's A's Jeb").
        ///
        /// The honest return and the forgery are the same string, so the name alone
        /// cannot tell them apart and no rule over it ever will. The allow-list carries
        /// the evidence the name cannot — in both cases something the server wrote down
        /// before the returning payload existed: the <c>rescue_kerbals</c> of a contract
        /// *this player issued*, tagged by their own client when they handed the wreck
        /// over, and the crew ledger's record of which of this save's own kerbals went
        /// out to *this particular friend* on an earlier quicksend. A name on that list
        /// is one this save is owed back and may strip; everything else keeps the
        /// refusal.
        ///
        /// The quicksend half was missing at first, and its absence was not cosmetic:
        /// an honest round trip between friends returned a player's own crew
        /// double-tagged and <c>borrowed</c>, which made them eligible for
        /// <see cref="PurgeBorrowedGhostCrew"/> to delete. Lending a crewed ship cost
        /// you the crew.
        /// </summary>
        public static string ApplyIncomingOwnershipTag(string name, string ownerName, string myName)
        {
            return ApplyIncomingOwnershipTag(name, ownerName, myName, null);
        }

        /// <param name="homebound">The names the server attests are this save's own crew
        /// on their way back — see the remarks on the three-argument overload. Null on
        /// every path but the two the server keeps a record for: the delivery of a rescue
        /// this player issued (`rescue_kerbals`), and a friend returning a live vessel
        /// this save lent them (`data/crew_ledger.py`). Both attest with something written
        /// down before the returning payload existed, which is the only kind of evidence
        /// that can survive the sender controlling every name in it.</param>
        /// <inheritdoc cref="ApplyIncomingOwnershipTag(string,string,string)"/>
        public static string ApplyIncomingOwnershipTag(
            string name, string ownerName, string myName, ICollection<string> homebound)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Attested → the plain tagger, which strips "{us}'s Jeb" back to "Jeb". This
            // is the only route from an incoming node back to a bare name, and it is
            // reachable only for a name the server is holding on this save's behalf.
            if (homebound != null && homebound.Contains(name))
                return ApplyOwnershipTag(name, ownerName, myName);

            // No attested owner is not a reason to let a name in untagged: nobody to tag
            // it to is not nobody to protect it from. See UnknownOwnerTag.
            string owner = string.IsNullOrEmpty(ownerName) ? UnknownOwnerTag : ownerName;

            int idx = name.IndexOf("'s ", StringComparison.Ordinal);
            bool claimsUs = idx > 0 && !string.IsNullOrEmpty(myName) &&
                            name.Substring(0, idx).Equals(myName, StringComparison.OrdinalIgnoreCase);
            if (claimsUs)
            {
                // Refused: re-tag the whole incoming name under whoever actually handed
                // the craft over, so the forgery arrives visibly rather than silently,
                // and the roster-collision rename in TagCrew keeps it off one of ours.
                return owner + "'s " + name;
            }
            // Everything else is the plain tagger, which is already right: a bare name
            // belongs to the attested owner (stripped when that owner is us — the craft
            // coming home), and a third party's tag rides along untouched, whether it is
            // an honest multi-hop or a borrowed passenger on a ship of ours coming back.
            return ApplyOwnershipTag(name, owner, myName);
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

        /// <summary>
        /// Is this incoming craft's owner <i>us</i> — i.e. may its crew take their
        /// bare names back and key onto the roster entries already here?
        ///
        /// The answer used to be <c>ownerName == myName</c>, and a display name cannot
        /// carry it. A Discord display name is self-chosen, changeable and not unique
        /// (the bot sends <c>interaction.user.display_name</c>), so an attacker set
        /// theirs to a victim's, re-linked, and quicksent a vessel whose crew carried
        /// plain names: nothing in the payload claimed to be the victim, so the forgery
        /// check in <see cref="ApplyIncomingOwnershipTag"/> saw nothing to refuse, the
        /// names arrived bare, and "coming home" adopted the victim's own kerbals onto
        /// the arriving hull — where the next hand-over deleted them. Two players who
        /// merely share a nickname did the same thing to each other by accident.
        ///
        /// So the decision moves to the account id, which is issued by the server, is
        /// immutable, and is the same key the wallet and the contracts are stored under.
        /// The <i>tag text</i> stays on the display name: "A's Jeb" is written for a
        /// human to read, and an account id in a roster entry would be unreadable.
        ///
        /// <paramref name="byName"/> reports that the id comparison could not be made
        /// and the old name comparison answered instead. That happens for a queue entry
        /// written before the server carried <c>owner_id</c> (they drain within days),
        /// for a contract fetched from a server that predates <c>issuer_id</c>/
        /// <c>contractor_id</c> on the contract list, for a client whose profile has not
        /// landed yet, and for the one caller that has no id to pass: the browser UI's
        /// craft-download bridge, whose page hands it <c>owner_name</c> alone (see
        /// <c>CraftDelivery.Deliver</c>). It deliberately does <b>not</b> fail closed: refusing to
        /// strip makes an honest returning kerbal read as borrowed, and
        /// <see cref="PurgeBorrowedGhostCrew"/> then deletes it — the destruction this
        /// whole area exists to prevent, reached through the honest path. The name
        /// comparison is no worse than what shipped, and the liveness check in
        /// <see cref="ResolveIncomingCrewName"/> blunts it: a spoof can no longer land
        /// on a kerbal who is actually crewing something.
        /// </summary>
        public static bool DecideComingHome(string ownerId, string myId, string ownerName,
                                            string myName, out bool byName)
        {
            byName = false;
            // Trimmed before the emptiness test, so a whitespace-only id falls back to
            // the name rather than comparing as a value. The only direction that matters:
            // a spurious *mismatch* is the costly one here, because it takes an honest
            // return off the adoption path.
            ownerId = ownerId == null ? "" : ownerId.Trim();
            myId = myId == null ? "" : myId.Trim();
            // Ordinal, never IgnoreCase: an account id is an opaque key (a Discord
            // snowflake or a generated one), not a name, and case-folding one would only
            // ever widen the match.
            if (ownerId.Length > 0 && myId.Length > 0)
                return string.Equals(ownerId, myId, StringComparison.Ordinal);

            byName = true;
            return !string.IsNullOrEmpty(myName) && !string.IsNullOrEmpty(ownerName) &&
                   ownerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>This client's own account id, or "" before the link/profile response
        /// carrying it has landed. Read here rather than threaded from the callers
        /// because every one of them already takes <c>myName</c> from this same
        /// singleton, and a second parameter saying the same thing is a second thing to
        /// forget.</summary>
        private static string LocalAccountId()
        {
            try
            {
                var gk = GeneKermanMod.Instance;
                return gk != null ? (gk.LinkedAccountId ?? "") : "";
            }
            catch { return ""; }
        }

        private static bool warnedNameFallback;

        /// <summary>Say once per session that an import decided ownership on the display
        /// name. Once, because it is a property of the client/server pair rather than of
        /// the payload, and a line per arriving kerbal would bury the log it belongs in.</summary>
        private static void WarnNameFallbackOnce(string ownerName, string ownerId)
        {
            if (warnedNameFallback) return;
            warnedNameFallback = true;
            Debug.LogWarning("[GeneKerman] Import: no account id on this transfer " +
                             $"(owner_id='{ownerId}', mine='{LocalAccountId()}'), so crew " +
                             $"ownership was decided on the display name ('{ownerName}'). " +
                             "Expected for entries queued before the server carried the id, " +
                             "and for a craft download started from the browser UI; " +
                             "it will not repeat this session.");
        }

        // ── Import ───────────────────────────────────────────────────────────

        /// <summary>
        /// Import a vessel into the current save, tagging its crew with the owner's
        /// name (or stripping the tag if they're coming home). See
        /// <see cref="ApplyOwnershipTag"/>. Returns the new vessel name, or null.
        /// </summary>
        public static string ImportVessel(string vesselNodeStr, string ownerName, string myName,
                                          IEnumerable<string> homebound = null,
                                          string ownerId = null)
        {
            return ImportVesselAtTarget(vesselNodeStr, null, ownerName, myName, homebound, ownerId);
        }

        /// <summary>
        /// Import a delivered payload that may be a single "VESSEL" node (legacy) or a
        /// "GKFLEET" container holding several. Every vessel is spawned and any embedded
        /// GKCRAFT blueprint installed. Returns the number of vessels imported.
        /// </summary>
        public static int ImportFleet(string vesselNodeStr, string ownerName, string myName,
                                      IEnumerable<string> homebound = null,
                                      string ownerId = null)
        {
            BeginImport();
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
                    var fleetNodes = BoundedFleet(OrderPrimaryFirst(fleet), "ImportFleet");
                    if (fleetNodes == null) return 0;

                    int count = 0;
                    for (int i = 0; i < fleetNodes.Count; i++)
                        if (ImportOneInner(fleetNodes[i], ownerName, myName, homebound, ownerId,
                                           persist: false) != null) count++;
                    // One save for the whole fleet rather than one per vessel: the write
                    // is the entire persistent.sfs, so per-vessel made the cost quadratic
                    // in the number of vessels. Only after the loop, so a throw part-way
                    // through does not persist a half-imported fleet either.
                    if (count > 0) PersistAfterImport();
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
                return ImportOneInner(inner, ownerName, myName, homebound, ownerId) != null ? 1 : 0;
            }
            catch (Exception ex)
            {
                // Persist whatever did land. ImportOneInner swallows its own failures, so
                // reaching here means something outside the per-vessel pipeline threw —
                // and a vessel that is in the running game but not on disk is the one
                // outcome the hoisted save could introduce that the per-vessel save
                // could not.
                PersistAfterImport();
                Debug.LogError($"[GeneKerman] ImportFleet failed: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Import a delivered payload (single "VESSEL" node or a "GKFLEET" container) and
        /// report the pid of every vessel actually spawned, <b>primary first</b>.
        ///
        /// This is <see cref="ImportFleet"/> for the paths that have to keep bookkeeping
        /// against what they spawned — a rescue hand-over, and the restore of a
        /// contractor's own submission — where the alternative, <see cref="LastImportedPid"/>,
        /// names only the last vessel of a fleet and would re-key a contract to an extra.
        /// A later approval's removal would then delete the wrong hull.
        ///
        /// The primary is identified twice over: by the "primaryPid" value ExportFleet
        /// writes on the container, and — when that is absent (an older client's payload)
        /// or matches nothing — by node order, which ExportFleet also guarantees. Both,
        /// rather than either, because the pid survives a reordering and the ordering
        /// survives a stripped value.
        ///
        /// <paramref name="primaryName"/> is the display name of the first vessel
        /// spawned, or null when nothing spawned at all.
        /// </summary>
        public static List<string> ImportDeliveredFleet(string vesselNodeStr, string ownerName,
                                                        string myName, out string primaryName,
                                                        IEnumerable<string> homebound = null,
                                                        string ownerId = null)
        {
            primaryName = null;
            var pids = new List<string>();
            BeginImport();
            if (!CanImport()) return pids;

            try
            {
                ConfigNode root = LoadRootNode(vesselNodeStr);
                if (root == null) return pids;

                ConfigNode fleet = (root.name == "GKFLEET") ? root : root.GetNode("GKFLEET");

                var nodes = new List<ConfigNode>();
                if (fleet != null)
                {
                    var fleetNodes = BoundedFleet(OrderPrimaryFirst(fleet), "ImportDeliveredFleet");
                    if (fleetNodes == null) return pids;
                    nodes.AddRange(fleetNodes);
                }
                else
                {
                    ConfigNode inner = (root.name == "VESSEL") ? root : root.GetNode("VESSEL");
                    if (inner == null && root.CountNodes > 0) inner = root.nodes[0];
                    if (inner == null)
                    {
                        Debug.LogError("[GeneKerman] ImportDeliveredFleet: no VESSEL node found.");
                        return pids;
                    }
                    nodes.Add(inner);
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    // Read the pid off the node rather than LastSpawnedPid: same value,
                    // but taken from the thing that was spawned instead of from a static
                    // another import could have moved on in between.
                    string name = ImportOneInner(nodes[i], ownerName, myName, homebound, ownerId,
                                                 persist: false);

                    if (name == null)
                    {
                        // An extra that will not build is skipped — one bad vessel must
                        // not cost the delivery. The PRIMARY is different: it is the
                        // craft the contract is about, and letting an extra take its
                        // place at the head of the list would re-key the contract to the
                        // wrong hull (see the ordering contract above). Spawning its
                        // escorts without it helps nobody and would duplicate them on
                        // the retry, so nothing is spawned at all and the caller is told
                        // the import failed — the queue entry stays and comes round again.
                        if (i == 0)
                        {
                            Debug.LogError("[GeneKerman] ImportDeliveredFleet: the primary " +
                                           "vessel would not spawn; leaving the whole " +
                                           "delivery for another attempt.");
                            pids.Clear();
                            primaryName = null;
                            return pids;
                        }
                        continue;
                    }

                    string pid = nodes[i].GetValue("pid");
                    if (!string.IsNullOrEmpty(pid)) pids.Add(pid);
                    if (primaryName == null) primaryName = name;
                }

                // See ImportFleet: one save for the delivery, not one per hull.
                if (primaryName != null) PersistAfterImport();
                Debug.Log($"[GeneKerman] ImportDeliveredFleet: spawned {pids.Count} of {nodes.Count} vessel(s).");
                return pids;
            }
            catch (Exception ex)
            {
                PersistAfterImport();   // see ImportFleet's catch
                Debug.LogError($"[GeneKerman] ImportDeliveredFleet failed: {ex}");
                return pids;
            }
        }

        /// <summary>The VESSEL children of a GKFLEET container, primary first. See
        /// <see cref="ImportDeliveredFleet"/> for why the marker and the ordering are
        /// both consulted.</summary>
        private static List<ConfigNode> OrderPrimaryFirst(ConfigNode fleet)
        {
            var nodes = new List<ConfigNode>(fleet.GetNodes("VESSEL"));
            string primaryPid = fleet.GetValue("primaryPid");
            if (string.IsNullOrEmpty(primaryPid) || nodes.Count < 2) return nodes;

            int idx = nodes.FindIndex(n => n != null &&
                string.Equals(n.GetValue("pid"), primaryPid, StringComparison.OrdinalIgnoreCase));
            if (idx <= 0) return nodes;   // -1 = no match (keep the ordering), 0 = already first

            Debug.LogWarning($"[GeneKerman] GKFLEET: primaryPid names node {idx}, not the first; " +
                             "reordering so the contract vessel leads.");
            var primary = nodes[idx];
            nodes.RemoveAt(idx);
            nodes.Insert(0, primary);
            return nodes;
        }

        /// <summary>Run the full per-vessel import pipeline on an already-parsed inner
        /// VESSEL node: install its embedded blueprint, freshen ids/flags, reset
        /// controls, tag crew, pin its orbit epoch, and spawn it. Returns the spawned
        /// vessel's name, or null on failure (so one bad vessel doesn't abort the rest
        /// of a fleet). On success the node carries the fresh pid it was spawned under,
        /// which is what <see cref="ImportDeliveredFleet"/> reads back.</summary>
        private static string ImportOneInner(ConfigNode innerNode, string ownerName, string myName,
                                             IEnumerable<string> homebound = null,
                                             string ownerId = null, bool persist = true)
        {
            try
            {
                // Before anything with a side effect: a vessel whose orbit is NaN or
                // Infinity is refused rather than half-imported (its flags installed,
                // its blueprint written into Ships/) and then spawned.
                if (!IncomingOrbitIsFinite(innerNode)) return null;
                // Likewise the part count, and for the same reason it is checked HERE
                // rather than after InstallEmbeddedCraft: every step below is a side
                // effect (files written into Ships/ and GameData/, roster entries
                // created), and the walks themselves are the cost being bounded.
                if (!PartCountIsSane(innerNode, "ImportOneInner")) return null;

                InstallEmbeddedCraft(innerNode);   // pulls + strips any GKCRAFT children
                PrepareInnerNode(innerNode);       // fresh pid + install GKFLAG textures
                ResetControls(innerNode);
                TagCrew(innerNode, ownerName, myName, homebound, ownerId);
                FreezeOrbitEpochToNow(innerNode);
                return SpawnInnerNode(innerNode, persist);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] ImportOneInner failed: {ex}");
                return null;
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
            string vesselNodeStr, RescueTargetSpec target, string ownerName, string myName,
            IEnumerable<string> homebound = null, string ownerId = null)
        {
            Debug.Log($"[GeneKerman] VesselTransfer.Import: starting ({vesselNodeStr?.Length ?? 0} chars), owner='{ownerName}', me='{myName}'");
            BeginImport();
            if (!CanImport()) return null;

            try
            {
                ConfigNode innerNode = LoadInnerVesselNode(vesselNodeStr);
                if (innerNode == null) return null;
                if (!IncomingOrbitIsFinite(innerNode)) return null;
                // Same bound, same reason, same place in the sequence as ImportOneInner:
                // this is the OTHER entry point that takes a peer-supplied VESSEL node —
                // the rescue-wreck spawn, whose bytes are the issuer's uploaded snapshot —
                // and it reaches none of ImportOneInner's three guards. It has to sit
                // after LoadInnerVesselNode (which is where the node comes from) and
                // before PlaceAtTarget/SpawnInnerNode, because every walk below is the
                // cost being bounded and SpawnInnerNode is the side effect.
                if (!PartCountIsSane(innerNode, "ImportVesselAtTarget")) return null;

                ResetControls(innerNode);
                TagCrew(innerNode, ownerName, myName, homebound, ownerId);

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

        /// <summary>Every crew rename the last import performed, tagged name → the name
        /// the kerbal actually arrived under, empty when nothing was renamed.
        ///
        /// Accumulated across the vessels of one fleet and reset by each public import
        /// entry point, so it is read immediately after the call that produced it — the
        /// same contract <see cref="LastImportedPid"/> and <see cref="LastSpawnedPid"/>
        /// keep. It exists because a rename is not cosmetic: everything downstream of a
        /// wreck spawn (the emergency freeze, the contract's crew list, the hand-over
        /// that settles it) addresses those kerbals *by name*, and a caller still holding
        /// the pre-rename names silently targets kerbals that no longer exist — the
        /// stranded crew are then never lifted out of the life-support simulation, which
        /// is the one thing the freeze is for.</summary>
        public static Dictionary<string, string> LastCrewRenames { get; private set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── Node-count bounds on an imported payload ─────────────────────────
        //
        // The only ceiling anywhere on the import path was a BYTE ceiling
        // (CraftInstaller.MaxDecompressedBytes 50 MB, ApiClient.MaxDownloadBytes 32 MB).
        // Nothing counted nodes, and a minimal buildable VESSEL node is on the order of
        // a hundred bytes of highly repetitive text — which gzips far below the server's
        // own upload cap. So a delivery the server happily stores expands to hundreds of
        // thousands of vessels here, each running the full PrepareInnerNode chain and
        // (outside flight) its own full-save write: quadratic in the payload, not linear.
        //
        // The crew half is the half that cannot be undone. KSP has no UI that deletes a
        // roster entry, PurgeBorrowedGhostCrew only sweeps borrowed crew whose vessel is
        // gone, and a polluted roster counts against the astronaut-complex hire limit
        // while making the applicant generator refuse any name that is a substring of an
        // existing one — i.e. the Astronaut Complex stops being able to hire, for the
        // life of the save.
        //
        // These are bounds on the honest producer, not guesses at an attacker's budget.
        // ExportFleet sends the active vessel plus the extras the player ticked; a
        // crewed vessel holds as many kerbals as it has seats.

        /// <summary>Most VESSEL children one GKFLEET container may spawn.</summary>
        public const int MaxFleetVessels = 32;

        /// <summary>Most roster entries one arriving vessel may create.
        ///
        /// Was 64, which an honest craft reaches: a stock Mk3 Passenger Module seats 16,
        /// so five of them is 80, and an SSPX habitat stack or a gifted station passes it
        /// without trying. The cap counts CREATIONS, so it bites hardest on exactly the
        /// case it should not — a large station going to a new owner, where every kerbal
        /// aboard is new to that roster. 200 is still a bound worth having (with
        /// <see cref="MaxFleetVessels"/> it caps a payload's roster cost) while sitting
        /// far above any crew a real vessel carries.
        ///
        /// It is no longer load-bearing on its own: <see cref="StripUnfulfilledCrewRefs"/>
        /// takes the names it dropped out of the node, so hitting it costs empty seats
        /// rather than a broken save. Raising it alone would not have closed that.</summary>
        public const int MaxCrewPerVessel = 200;

        /// <summary>Most PART children one arriving VESSEL node may carry.
        ///
        /// The third attacker-chosen count on this path, and the one the earlier
        /// bounds missed: MaxFleetVessels caps how many hulls spawn and
        /// MaxCrewPerVessel caps how many roster entries one hull creates, but
        /// nothing counted parts. A minimal PART node is ~100 bytes of highly
        /// repetitive text — precisely what gzips to nothing under the server's
        /// 25 MB upload ceiling — so a payload the server happily stores expands to
        /// hundreds of thousands of parts inside ONE VESSEL node, under both existing
        /// caps. Each is then walked by PartAliases, TextureTransfer,
        /// ReforgedTransfer, RealFuelsTransfer, ScaleBridge and MapCrewNames, and
        /// handed to ProtoPartSnapshot construction on the main thread; the import
        /// runs synchronously inside the download callback, so the game freezes or
        /// dies allocating. Worse, dying before the /done ack leaves the entry queued,
        /// so it is re-fetched and re-applied on the next launch: a crash loop from
        /// one accepted delivery.
        ///
        /// A bound on the honest producer, like the two above it. CraftInstaller's own
        /// note measures the largest real craft on this machine at 563 parts / 3.02 MB;
        /// KSP itself becomes unplayable long before 2000. Anything past this is not a
        /// craft.</summary>
        public const int MaxPartsPerVessel = 2000;

        /// <summary>True when this VESSEL node's PART count is within
        /// <see cref="MaxPartsPerVessel"/>; records the refusal when it is not.
        ///
        /// Recorded through LastImportRefusal so the existing "refused, left queued,
        /// NOT acked" path in ClientState handles it — the same treatment
        /// BoundedFleet gets, and for the same reason: a client-policy refusal must
        /// not ack, because on a live quicksend the server's copy is the only one
        /// left once the sender's client has removed the ship.</summary>
        private static bool PartCountIsSane(ConfigNode innerNode, string where)
        {
            if (innerNode == null) return true;
            int n = innerNode.GetNodes("PART").Length;
            if (n <= MaxPartsPerVessel) return true;
            LastImportRefusal =
                $"one of its vessels has {n} parts, past the {MaxPartsPerVessel} this can import";
            Debug.LogError($"[GeneKerman] {where}: refusing a VESSEL node carrying {n} PART " +
                           $"nodes (cap {MaxPartsPerVessel}). Nothing was spawned.");
            return false;
        }

        /// <summary>Why the last import refused the whole payload, or null when it did
        /// not. Read straight after the call, exactly like <see cref="LastImportedPid"/>.
        ///
        /// It exists because "nothing spawned" and "this will never spawn here" want
        /// opposite handling upstream, which is the same distinction
        /// CraftDelivery.DecompressToString draws with its <c>refused</c> flag: leaving a
        /// queue entry unacked asks for the same bytes again, which is right for a
        /// download that failed and a permanent loop for a payload that is refused — it
        /// is re-fetched and re-refused on every launch for the life of the save, with
        /// nothing ever said to the player.</summary>
        public static string LastImportRefusal { get; private set; }

        /// <summary>Start a fresh import: forget the previous one's renames so a caller
        /// cannot read another payload's map, and clear the refusal so a stale one
        /// cannot be read as this payload's.</summary>
        /// <summary>Reset the per-import statics. Runs BEFORE the scene check, not after.
        ///
        /// `LastImportRefusal` is sticky — only this clears it — and all three entry
        /// points used to return on `!CanImport()` without calling it. That made an
        /// early scene bail indistinguishable from a refusal of the CURRENT payload,
        /// and `ClientState` reads the static to decide whether to ack: so one earlier
        /// refusal (an over-size fleet, a non-finite orbit) left the string set, and
        /// the next perfectly ordinary quicksend that happened to arrive while the
        /// player was walking into the VAB was reported "refused" with that stale
        /// reason and left unacked in `processingImports` — stuck until a restart.
        ///
        /// The download between the scene check in `ClientState` and the call here is
        /// what opens that window, so it is not a rare interleaving.
        ///
        /// Clearing first is safe: both statics describe the import about to run, and
        /// a caller that then bails has simply performed no import — which is exactly
        /// what a null refusal and an empty rename map say.</summary>
        private static void BeginImport()
        {
            LastCrewRenames = new Dictionary<string, string>(StringComparer.Ordinal);
            LastImportRefusal = null;
        }

        /// <summary>Bound a GKFLEET's VESSEL list, recording the refusal when it is over.
        /// Returns null when the payload is refused outright.</summary>
        private static List<ConfigNode> BoundedFleet(List<ConfigNode> nodes, string where)
        {
            if (nodes == null || nodes.Count <= MaxFleetVessels) return nodes;
            LastImportRefusal =
                $"it carries {nodes.Count} vessels, past the {MaxFleetVessels} this can import";
            Debug.LogError($"[GeneKerman] {where}: refusing a payload of {nodes.Count} VESSEL " +
                           $"nodes (cap {MaxFleetVessels}). Nothing was spawned — spawning the " +
                           "first few would leave the rest queued and the same payload would " +
                           "arrive again on the next launch.");
            return null;
        }

        /// <summary>Both spellings of every attested name, because the two rescue legs
        /// write the same kerbal differently: a craft coming home carries "{us}'s Jeb"
        /// (tagged while it lived in the rescuer's save), while a cancelled rescue's own
        /// wreck comes back holding the bare "Jeb" it was sent out with. The server's
        /// list is tagged, so matching on that form alone would leave the restore path
        /// unattested — and an unattested "Jeb" is one the recipient's own save adopts
        /// under a stranger's tag. Null in, null out: no attestation is not an empty
        /// one.</summary>
        public static HashSet<string> HomeboundSet(IEnumerable<string> homebound)
        {
            if (homebound == null) return null;
            HashSet<string> set = null;
            foreach (var n in homebound)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (set == null) set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(n);
                set.Add(StripOwnershipTag(n));
            }
            return set;
        }

        /// <summary>Tag every crew reference (PART crew refs + embedded GKCREW roster
        /// nodes) for the importing save via <see cref="ApplyIncomingOwnershipTag"/>,
        /// then move any name that would land on a kerbal already in this roster out of
        /// the way — see <see cref="ResolveIncomingCrewName"/>. Returns the renames it
        /// made (tagged name → arrival name), which is also merged into
        /// <see cref="LastCrewRenames"/> for callers that hold names of their own.</summary>
        private static Dictionary<string, string> TagCrew(
            ConfigNode innerNode, string ownerName, string myName, IEnumerable<string> homebound,
            string ownerId = null)
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            // The server said these crew are ours, so a roster name they match is the
            // same kerbal coming home and AddCrewToRoster's adopt-by-name is correct.
            // Decided on the *account id* wherever both ends have one — see
            // DecideComingHome for why the display name cannot carry this.
            bool byName;
            bool comingHome = DecideComingHome(ownerId, LocalAccountId(), ownerName, myName,
                                               out byName);
            if (byName) WarnNameFallbackOnce(ownerName, ownerId);
            // Names already crewing something in this save. Adoption-by-name is only
            // ever safe for a roster entry that is not aboard anything — see
            // ResolveIncomingCrewName.
            var crewedNow = CrewedNames();
            var attested = HomeboundSet(homebound);
            // Names this import has already claimed: the roster only learns about them
            // in AddCrewToRoster, which runs after every rename here is settled.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renamed = new List<string>();
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);

            Func<string, string> resolve = old =>
            {
                string tagged = ApplyIncomingOwnershipTag(old, ownerName, myName, attested);
                // An attested name whose tag actually came off is this save's own kerbal
                // on the way back, so the roster entry it lands on is that same kerbal and
                // adopt-by-name is what is wanted — renaming it around the entry would
                // spawn a duplicate and leave the original a ghost. Per name, not per
                // import: the rescue craft carries the rescuer's own pilots as well, and
                // those are not ours. The "tag came off" half matters because the set
                // carries stripped forms too, so a rescuer's own pilot who happens to
                // share a bare name with a rescued kerbal matches it — and comes out
                // tagged, which is a borrowed arrival and must still be moved aside.
                bool home = comingHome ||
                            (attested != null && attested.Contains(old) && !IsBorrowedCrewName(tagged));
                string final = ResolveIncomingCrewName(tagged, roster, claimed, home, crewedNow);
                if (!string.Equals(tagged, final, StringComparison.Ordinal))
                {
                    renamed.Add($"{tagged} → {final}");
                    renames[tagged] = final;
                }
                return final;
            };

            var map = MapCrewNames(innerNode, resolve);
            foreach (ConfigNode kn in innerNode.GetNodes("GKCREW"))
            {
                string nm = kn.GetValue("name");
                if (string.IsNullOrEmpty(nm)) continue;
                string nn;
                if (!map.TryGetValue(nm, out nn))
                {
                    nn = resolve(nm);
                    map[nm] = nn;   // a GKCREW entry with no part reference still owns its name
                }
                kn.SetValue("name", nn, true);
            }

            foreach (var kvp in renames) LastCrewRenames[kvp.Key] = kvp.Value;

            // Say so. A kerbal arriving under a different name than the sender wrote is
            // exactly the kind of quiet change that otherwise turns into a bug report —
            // and when it happens because a name was already in this roster, the player
            // is the only one who can tell whether that was a coincidence.
            if (renamed.Count > 0)
                Announce("Arriving crew were renamed",
                    $"{renamed.Count} kerbal(s) arrived under a name already in your roster " +
                    "and were renamed, so nobody of yours was written over: " +
                    string.Join("; ", renamed.ToArray()) + ".");
            return renames;
        }

        /// <summary>
        /// Pick the name an incoming kerbal takes in this save, given the one already
        /// here is not theirs to have.
        ///
        /// <see cref="AddCrewToRosterInner"/> skips creation when the roster already
        /// holds the name, which is right for a kerbal coming home and wrong for
        /// everything else: it silently keys the incoming vessel onto the *recipient's*
        /// ProtoCrewMember, and since that entry carries no ownership tag,
        /// <see cref="KeepsRoster"/> reads it as ours until a hand-over under
        /// <see cref="CrewFate.LeavesWithCraft"/> removes it from the roster for good.
        /// A sender picks these names, so a collision is as likely deliberate as not.
        ///
        /// The rename keeps the ownership prefix — a borrowed kerbal renamed to a bare
        /// name would read as one of ours and never be swept — and reuses
        /// <see cref="GenerateKerbalName"/>, the same source of names the randomized
        /// import path uses.
        ///
        /// <paramref name="comingHome"/> buys an exemption from that rename, and only
        /// for a roster entry that is <i>free</i>: see the gate at the top of the body
        /// for the honest case where it is not, which is a rescue delivery landing while
        /// the issuer still has the original stranded vessel.
        /// </summary>
        /// <remarks>Internal rather than private so the debug build's self-test panel
        /// can drive the table above without a save loaded; nothing outside this assembly
        /// can reach it either way.</remarks>
        internal static string ResolveIncomingCrewName(
            string name, KerbalRoster roster, HashSet<string> claimed, bool comingHome,
            HashSet<string> crewedNow)
        {
            if (string.IsNullOrEmpty(name)) return name;
            // Coming home is permission to *reuse* a roster entry, not permission to
            // reuse one that is busy. The honest return normally finds no entry at all
            // (the kerbal left the roster when the craft was handed over) and falls
            // straight through this to the adoption below. But the issuer of a rescue
            // can still be holding the original stranded vessel when the delivery lands
            // — its removal deferred because they were flying it, rolled back by a
            // quickload, or still settling EVA'd crew — and _deliver_rescue_craft sends
            // no vessel_pid, so the VesselExists/HasQueuedRemoval guard upstream does
            // not cover that leg. Adopting there would leave two ProtoVessels pointing
            // at one ProtoCrewMember: desynced seats and crew counts, and recovering
            // either takes the kerbal out of the other. So the busy case is renamed
            // aside exactly as any other collision is, which costs a name and a line in
            // the "arriving crew were renamed" notice instead of a broken save.
            if (comingHome && !IsCrewingSomething(name, roster, crewedNow))
            {
                claimed.Add(name);
                return name;
            }

            Func<string, bool> taken = n =>
            {
                if (claimed.Contains(n)) return true;
                // A name crewing something is taken, whatever the roster says about it.
                // Belt and braces for the gate above: the two sets are the same names in
                // practice (CrewedNames resolves through the roster), and this is what
                // makes the fall-through from that gate land on the rename rather than
                // back on the very name it just refused.
                if (crewedNow != null && crewedNow.Contains(n)) return true;
                try { return roster != null && roster[n] != null; }
                catch { return false; }   // an odd name the roster indexer dislikes is not a collision
            };

            if (!taken(name)) { claimed.Add(name); return name; }

            // The replacement must carry an ownership prefix or the rename undoes itself:
            // a bare arrival reads as one of ours, is never swept, and sits against the
            // astronaut-complex hire limit forever. An unprefixed name should no longer
            // reach here — ApplyIncomingOwnershipTag substitutes UnknownOwnerTag when the
            // server named no owner, which is where the bare ones used to come from — but
            // this is the last place that can still refuse to adopt one, so it does.
            int idx = name.IndexOf("'s ", StringComparison.Ordinal);
            string prefix = idx > 0 ? name.Substring(0, idx + 3) : UnknownOwnerTag + "'s ";
            for (int i = 0; i < 64; i++)
            {
                string candidate = prefix + GenerateKerbalName();
                if (!taken(candidate))
                {
                    claimed.Add(candidate);
                    Debug.LogWarning($"[GeneKerman] Import: incoming crew '{name}' is already in " +
                                     $"this roster; the arrival is '{candidate}' instead. An " +
                                     "incoming node cannot lay claim to a kerbal already here.");
                    return candidate;
                }
            }
            // 64 random names all taken is not a real save; fall back to something that
            // cannot collide rather than letting the adoption happen after all.
            string unique = prefix + Guid.NewGuid().ToString("N").Substring(0, 8) + " Kerman";
            claimed.Add(unique);
            return unique;
        }

        /// <summary>Every kerbal name currently crewing something in this save, read off
        /// the vessels rather than off the roster because the roster does not always say
        /// so: the emergency freeze parks a stranded crew at <c>rosterStatus = Dead</c>
        /// while they are still aboard the wreck, and a proto vessel read back from a
        /// ConfigNode carries names before KSP resolves them to roster entries.
        ///
        /// Built once per vessel imported, not once per kerbal, and it deliberately
        /// picks up the vessels earlier members of the same GKFLEET already spawned.</summary>
        private static HashSet<string> CrewedNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            List<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch { return names; }
            if (vessels == null) return names;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;
                try
                {
                    foreach (ProtoCrewMember pcm in CrewOf(v))
                        if (pcm != null && !string.IsNullOrEmpty(pcm.name)) names.Add(pcm.name);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[GeneKerman] Import: could not read a vessel's crew " +
                                     "while checking arrivals: " + ex.Message);
                }
            }
            return names;
        }

        /// <summary>Is the roster entry under this name busy — aboard a vessel, or marked
        /// Assigned? Only a free entry may be adopted by an arriving kerbal of the same
        /// name; see <see cref="ResolveIncomingCrewName"/>.</summary>
        private static bool IsCrewingSomething(string name, KerbalRoster roster,
                                               HashSet<string> crewedNow)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (crewedNow != null && crewedNow.Contains(name)) return true;
            if (roster == null) return false;

            ProtoCrewMember pcm;
            // An odd name the roster indexer dislikes is not a kerbal aboard anything —
            // the same reading the collision test takes of the same throw.
            try { pcm = roster[name]; }
            catch { return false; }
            return pcm != null && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned;
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
            uint newPersistentId = (uint)rng.Next(100000, int.MaxValue);
            innerNode.SetValue("pid", newGuid.ToString("D"), true);
            innerNode.SetValue("persistentId", newPersistentId.ToString(), true);
            LastImportedPid = newGuid.ToString("D");

            // Re-apply any cheat mark the sender carried. This has to happen HERE, at
            // the line that mints the new persistentId, because that re-mint is exactly
            // what used to lose it: a cheated craft sent to a friend and back came home
            // clean. Before the flag stripping below, so a truncated or hand-edited
            // node still fails closed.
            CheatDetection.ExtractAndApply(innerNode, newPersistentId);

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

            // Same again for Reforged Materials Redux, the other recolour mod: drop the
            // ModuleReforged nodes this install's prefabs can't accept before the
            // ProtoVessel is built. Nothing to strip first — Reforged rides entirely in
            // the part modules and has no side-channel block.
            ReforgedTransfer.ReconcileNode(innerNode, innerNode.GetValue("name"));

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

            // Last, because it is about where the finished craft goes rather than what it
            // is made of: read + strip the GKLAND block, point the vessel at the body its
            // name says (an index into FlightGlobals.Bodies means a different world once a
            // planet pack is installed), re-derive a landed altitude against THIS install's
            // terrain, and arm KSP's own ground seating so the craft is put on the surface
            // it finds rather than the one it left.
            SurfacePlacement.ExtractAndReseat(innerNode, innerNode.GetValue("name"));
        }

        /// <summary>Register an inner VESSEL node into the running universe (after
        /// any crew/placement edits) and persist. Returns the vessel name.</summary>
        private static string SpawnInnerNode(ConfigNode innerNode, bool persist = true)
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

            // A fleet import passes persist:false and saves once after the whole loop —
            // this write is the entire persistent.sfs, so doing it per vessel made the
            // cost of a payload quadratic in the number of vessels it carried.
            if (persist) PersistAfterImport();

            // If we're in the Tracking Station, its vessel list was built on scene
            // entry and won't show the new craft until a scene reload. Rebuild it in
            // place so the import is immediately selectable.
            RefreshTrackingStation();

            Debug.Log($"[GeneKerman] (Ok) Spawned vessel '{vesselName}'");
            return vesselName;
        }

        /// <summary>Write the save once the import (or the whole fleet import) is done.
        /// No-op in flight, where KSP owns the save.</summary>
        private static void PersistAfterImport()
        {
            if (HighLogic.LoadedSceneIsFlight) return;
            try
            {
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            }
            catch (Exception saveEx)
            {
                Debug.LogWarning($"[GeneKerman] Post-import save failed: {saveEx.Message}");
            }
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
                    Debug.LogWarning("[GeneKerman] Tracking Station refresh: buildVesselsList not found, KSP API may have changed.");
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

        /// <summary>Metres above the sampled ground a surface-target wreck is written in
        /// at. Deliberately a clearance and not a landing: the sample is the PQS
        /// heightmap, the craft comes to rest on a collider, and the two differ wherever
        /// terrain is tessellated — so the number only has to be small enough not to be a
        /// fall and large enough for KSP's ground seating to raycast down onto the real
        /// surface.</summary>
        private const double SPAWN_CLEARANCE = 5.0;

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
                Debug.LogWarning($"[GeneKerman] PlaceAtTarget: body '{target.body}' not found, leaving original orbit.");
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
                // allowNegative: the ground really is below sea level in places (a basin
                // floor on Duna, any dry sea bed), and the clamped overload reports those
                // as 0 — which would drop the wreck in from however far up that is.
                try { terrain = body.TerrainAltitude(lat, lon, true); } catch { }
                double alt = terrain + SPAWN_CLEARANCE;

                innerNode.SetValue("sit", "LANDED", true);
                innerNode.SetValue("landed", "True", true);
                innerNode.SetValue("splashed", "False", true);
                innerNode.SetValue("landedAt", body.bodyName, true);
                innerNode.SetValue("lat", lat.ToString("G17"), true);
                innerNode.SetValue("lon", lon.ToString("G17"), true);
                innerNode.SetValue("alt", alt.ToString("G17"), true);
                innerNode.SetValue("hgt", SPAWN_CLEARANCE.ToString("G9"), true);

                // The clearance above only has to be enough for KSP's own seating to find
                // the ground and drop the wreck onto it — the terrain sample is the PQS
                // heightmap, and the collider a craft actually rests on can be metres off
                // it wherever a terrain mod tessellates or scatters. See SurfacePlacement.
                SurfacePlacement.ForceGroundReseat(innerNode);

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

                // Clear the surface fields as well, and this is not tidiness. They are
                // whatever the SOURCE vessel had, and a craft snapshotted on the ground
                // carries a runway lat/lon, an `alt` of a few dozen metres and an `hgt`
                // above terrain. Written into a node that also claims ORBITING, KSP
                // resolves the contradiction in favour of the numbers: the vessel comes
                // out `FLYING` rather than `ORBITING`, and an unloaded vessel that KSP
                // believes is flying inside an atmosphere is deleted on the next scene
                // change — the craft simply vanishes, with nothing logged.
                //
                // Measured, not theorised: cloning a craft parked on the runway into a
                // 150×160 km orbit produced exactly that, twice, and the vessel was gone
                // from persistent.sfs by the following save.
                //
                // `alt` is given the orbital altitude rather than zeroed, since it is the
                // one of these four that still means something in orbit.
                innerNode.SetValue("lat", "0", true);
                innerNode.SetValue("lon", "0", true);
                innerNode.SetValue("alt", (sma - body.Radius).ToString("G17"), true);
                innerNode.SetValue("hgt", "0", true);
                // The snapshot may have arrived landed (and so armed for ground seating by
                // SurfacePlacement); it is going into orbit instead.
                SurfacePlacement.ClearGroundReseat(innerNode);

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

        /// <summary>Finite in the sense that matters here: a value KSP can build an orbit
        /// from. .NET 4.7.2 has no double.IsFinite.</summary>
        private static bool IsFiniteOrbitValue(double v) =>
            !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>Every element of an incoming ORBIT block is finite — unless the
        /// vessel is on a surface, where a non-finite element is KSP's own storage.
        ///
        /// <see cref="WriteOrbit"/> guards the orbits this mod COMPUTES, and says at
        /// length why. The ordinary import path never went through it: FreezeOrbitEpochToNow
        /// only rewrites EPH, and the peer's SMA/ECC/INC/… went straight into
        /// `new ProtoVessel(innerNode, …)` and from there into persistent.sfs. So the
        /// mod guarded its own writes and not the counterparty's, which is the wrong way
        /// round — one of them is arithmetic we control and the other is a stranger's file.
        ///
        /// The asymmetry is closed rather than the loss demonstrated: 0109's in-game
        /// verification RETRACTED the "a NaN orbit wedges the game" claim after finding a
        /// `SMA = NaN` sitting in a working save, so this is not asserted to break
        /// anything. It costs seven TryParse calls per imported vessel.
        ///
        /// That retraction is the whole reason a SURFACE vessel is exempt rather than
        /// merely forgiven. `SMA = NaN` is what KSP ITSELF writes for a craft that is not
        /// in an orbit: it sits in a stock-shipped scenario (the Philae probe in Squad's
        /// Transmissions.sfs, `sit = LANDED`) and on splashed hulls in this project's own
        /// test saves, and ExportVesselNode serialises `BackupVessel()` verbatim — so it
        /// ships as-is on every ordinary landed or splashed hand-over. Refusing that was
        /// destructive, not merely wrong: ToolActions.Quicksend removes the sender's ship
        /// and crew the moment the server answers ok, and a recipient-side refusal that
        /// ACKS the queue entry drops the server's stored snapshot — the ship then exists
        /// in neither save. The decline/return leg fails the same way.
        ///
        /// Nothing is rewritten for it either, and that is deliberate rather than lazy.
        /// The node is byte-for-byte what KSP would have written on the recipient's own
        /// machine; a surface craft is placed by its lat/lon plus SurfacePlacement's
        /// ground reseat rather than by its conic; and PlaceAtTarget overwrites every
        /// element through <see cref="WriteOrbit"/> on the one path that turns a surface
        /// snapshot into an orbit. Dropping the element — or the ORBIT node — would be a
        /// mutation KSP's own reader has never been asked to survive, bought with nothing.
        ///
        /// What is left is the guard's real case: a genuinely broken conic on a craft
        /// whose conic is what places it, which is exactly WriteOrbit's observed wedge.
        ///
        /// Unparseable is deliberately NOT refused: a value KSP's own reader will reject
        /// or default is its business, and refusing it here would turn a cosmetic
        /// difference in number formatting into a lost delivery. Only a value that
        /// parses cleanly to NaN or Infinity — which `double.TryParse` accepts for the
        /// literals "NaN" and "Infinity" — is one we know to be poison.</summary>
        private static bool IncomingOrbitIsFinite(ConfigNode innerNode)
        {
            ConfigNode orbit = innerNode != null ? innerNode.GetNode("ORBIT") : null;
            if (orbit == null) return true;   // landed/splashed nodes need not carry one

            for (int i = 0; i < OrbitElementKeys.Length; i++)
            {
                string key = OrbitElementKeys[i];
                string raw = orbit.GetValue(key);
                if (string.IsNullOrEmpty(raw)) continue;
                double v;
                // Invariant, because that is the culture ConfigNode writes in ("G17").
                // Parsing under the player's own made the guard a silent no-op on every
                // comma-decimal locale: each value failed to parse and each was skipped.
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    continue;
                if (IsFiniteOrbitValue(v)) continue;

                if (SurfacePlacement.IsOnSurface(innerNode))
                {
                    Debug.LogWarning($"[GeneKerman] Import: ORBIT {key}={raw} on a vessel that is " +
                                     "on a surface. That is KSP's own storage for a craft with no " +
                                     "orbit, not corruption — importing it exactly as it arrived.");
                    return true;
                }

                LastImportRefusal = $"its orbit is not a number ({key}={raw})";
                Debug.LogError($"[GeneKerman] Import: refusing a vessel whose ORBIT carries a " +
                               $"non-finite {key} ({raw}). This value would be written into " +
                               "persistent.sfs as-is, and the save carries it forward.");
                return false;
            }
            return true;
        }

        private static readonly string[] OrbitElementKeys =
            { "SMA", "ECC", "INC", "LPE", "LAN", "MNA", "EPH" };

        /// <summary>
        /// Write an orbit, refusing to write a non-finite one.
        ///
        /// The guard is not defensive tidiness. A NaN in SMA makes KSP's own
        /// OrbitRendererBase.LateUpdate throw a NullReferenceException *every frame*,
        /// which stops the scene camera being positioned and leaves the game apparently
        /// hung — observed: a save that loaded to the Space Center with the camera
        /// stranded over the ocean and an exception storm, recoverable only from a
        /// backup. The value is also persisted, so the save carries the wedge forward
        /// and reloading does not clear it.
        ///
        /// Every input here is derived arithmetic — body.Radius plus altitudes, or a
        /// terrain sample — and `body.TerrainAltitude` returns a double, so a NaN
        /// arrives as a value rather than as an exception and the callers' try/catch
        /// cannot see it. Refusing leaves whatever orbit the node already had, which is
        /// always a better answer than a poisoned one: a craft in the wrong place is a
        /// bug, a craft with a NaN orbit is an unplayable save.
        ///
        /// Returns false when it refused, so a caller can report rather than assume.
        /// </summary>
        private static bool WriteOrbit(ConfigNode orbit, int refIdx,
            double sma, double ecc, double inc, double lpe, double lan, double mna, double eph)
        {
            if (!IsFiniteOrbitValue(sma) || !IsFiniteOrbitValue(ecc) ||
                !IsFiniteOrbitValue(inc) || !IsFiniteOrbitValue(lpe) ||
                !IsFiniteOrbitValue(lan) || !IsFiniteOrbitValue(mna) ||
                !IsFiniteOrbitValue(eph))
            {
                Debug.LogError("[GeneKerman] Refusing to write a non-finite orbit " +
                               $"(SMA={sma} ECC={ecc} INC={inc} LPE={lpe} LAN={lan} " +
                               $"MNA={mna} EPH={eph}). The node keeps its previous orbit. " +
                               "A NaN here wedges KSP: OrbitRendererBase NullReferences " +
                               "every frame and the scene never finishes loading.");
                return false;
            }

            orbit.SetValue("SMA", sma.ToString("G17"), true);
            orbit.SetValue("ECC", ecc.ToString("G17"), true);
            orbit.SetValue("INC", inc.ToString("G17"), true);
            orbit.SetValue("LPE", lpe.ToString("G17"), true);
            orbit.SetValue("LAN", lan.ToString("G17"), true);
            orbit.SetValue("MNA", mna.ToString("G17"), true);
            orbit.SetValue("EPH", eph.ToString("G17"), true);
            orbit.SetValue("REF", refIdx.ToString(), true);
            return true;
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

        /// <summary>Every part flightID aboard a vessel, by pid. Empty when the vessel
        /// isn't in this save.
        ///
        /// flightID is the identity that survives export, import, docking and undocking
        /// — the same one the server pins a rescue's wreck_parts from — which is why it,
        /// and not the vessel pid, is what a wreck is recognised by later. Reads the
        /// proto snapshots for an unloaded vessel and the live parts for a loaded one,
        /// since a freshly spawned wreck is normally neither loaded nor near the player.
        /// </summary>
        public static List<uint> PartFlightIdsOf(string pid)
        {
            var ids = new List<uint>();
            Vessel v = FindVessel(pid);
            if (v == null) return ids;

            try
            {
                if (v.loaded && v.parts != null)
                {
                    foreach (Part p in v.parts)
                        if (p != null && p.flightID != 0 && !ids.Contains(p.flightID)) ids.Add(p.flightID);
                }
                var snaps = v.protoVessel?.protoPartSnapshots;
                if (snaps != null)
                {
                    foreach (var pps in snaps)
                        if (pps != null && pps.flightID != 0 && !ids.Contains(pps.flightID)) ids.Add(pps.flightID);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PartFlightIdsOf({pid}) failed: {ex.Message}");
            }
            return ids;
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
                Debug.Log($"[GeneKerman] RemoveVessel: no vessel with pid {pid} in this save, already gone.");
                return RemovalResult.NotFound;
            }

            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel == target)
            {
                Debug.LogWarning("[GeneKerman] RemoveVessel: target is the active vessel, defer to a non-flight scene.");
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
                    Debug.Log($"[GeneKerman] RemoveVessel: crew fate {crewFate}, " +
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
                                             "nearby, deferring until they board or leave range.");
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
        /// A kerbal any vessel still <b>names</b> is left alone too, whatever their
        /// status, and that guard is load-bearing rather than defensive: removing a
        /// roster entry a `crew = ` line still points at leaves a seat referring to
        /// nobody, and a single dangling reference like that **silently disables
        /// Astronaut Complex hiring** — the applicant rows lose their `Selectable`, the
        /// Hire button does nothing, and KSP logs not one line about it (measured
        /// 2026-09-01; removing the reference restored hiring immediately). Dead or
        /// Missing does not by itself prove the craft is gone, so it cannot stand in for
        /// this check. The cost of the guard is a residue entry that lingers while
        /// something still claims it, which is the cheaper failure by a wide margin.
        ///
        /// Returns how many were dropped. Nothing here is lost for good — a re-import
        /// of their craft rebuilds them from its GKCREW nodes.
        /// </summary>
        /// <summary>
        /// Borrowed kerbals this save is holding that nothing crews and no live contract
        /// accounts for — orphans in <b>any</b> roster status, which is what separates
        /// this from <see cref="PurgeBorrowedGhostCrew"/>.
        ///
        /// That sweep collects only Dead/Missing, and deliberately: those are the residue
        /// of a craft that vanished before its removal ran, and a kerbal in that state is
        /// doing nothing but harm. The harm, however, does not depend on the status. KSP
        /// counts every roster entry against the astronaut-complex hire limit, and its
        /// applicant generator silently refuses any new name that appears as a substring
        /// of an existing one — so "A's Cergar Kerman" sitting at Available blocks
        /// "Cergar Kerman" from ever being offered, for the life of the save, and shows
        /// up as an Astronaut Complex with nobody to hire rather than as an error.
        /// Measured 2026-09-01: 14 such orphans across three test saves, 13 of them
        /// Available and therefore permanently out of the sweep's reach.
        ///
        /// This is a <b>read</b>. It is deliberately not wired into
        /// <c>SweepRosterOnce</c>, because widening the automatic sweep to cover them has
        /// a failure worse than the one it fixes, and it is reachable in ordinary play:
        /// a rescuer who collects the stranded crew and <i>recovers the vessel at the
        /// KSC</i> puts them in the roster as Available, crewing nothing, with the
        /// emergency-freeze record already dropped (the thaw at 10 km drops it). At that
        /// moment they are borrowed, orphaned and unfrozen — and an automatic purge would
        /// delete them on the next Space Center visit, destroying the rescue before it
        /// could be submitted. So the decision goes to the player, the way
        /// <see cref="TraitRepair"/> does: report, and act on a button.
        ///
        /// Three exclusions, and the third is why this can be trusted at all:
        ///   * anything crewing a vessel is not an orphan — they are a passenger, and a
        ///     recovered-and-re-crewed borrowed kerbal is a legitimate roster entry;
        ///   * anything an emergency-freeze record holds is left alone, exactly as the
        ///     Dead/Missing sweep leaves it;
        ///   * anything named by a live contract is left alone, and if the contract list
        ///     has <b>not been fetched</b> this returns nothing at all rather than
        ///     guessing. That is the same "empty is not unknown" distinction
        ///     <c>ClientState.HomeboundCrewFor</c> draws, and here the cost of guessing
        ///     is somebody's crew.
        /// </summary>
        public static List<string> OrphanedBorrowedCrew()
        {
            var result = new List<string>();
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return result;

            try
            {
                // Fail closed on an unread contract list — see the remarks.
                var spoken = LiveContractCrewNames();
                if (spoken == null) return result;

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

                var crewedNow = CrewedNames();
                var statuses = new[]
                {
                    ProtoCrewMember.RosterStatus.Assigned,
                    ProtoCrewMember.RosterStatus.Available,
                    ProtoCrewMember.RosterStatus.Dead,
                    ProtoCrewMember.RosterStatus.Missing,
                };
                foreach (var pcm in roster.Kerbals(statuses))
                {
                    if (pcm == null || !IsBorrowedCrewName(pcm.name)) continue;
                    if (frozen.Contains(pcm.name)) continue;
                    if (crewedNow != null && crewedNow.Contains(pcm.name)) continue;
                    if (spoken.Contains(pcm.name)) continue;
                    result.Add(pcm.name);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] OrphanedBorrowedCrew failed: {ex.Message}");
                return new List<string>();
            }
            return result;
        }

        /// <summary>Every crew name any non-terminal contract still speaks for, or
        /// <c>null</c> when the contract list has never been fetched this session.
        ///
        /// Null is the whole point: "no contracts" and "I have not looked" must not read
        /// the same, because the only caller uses this to decide whether a kerbal may be
        /// deleted. A rescue that is accepted but not yet submitted is exactly the case
        /// that must survive, and it is invisible until the list is loaded.</summary>
        private static HashSet<string> LiveContractCrewNames()
        {
            var mod = GeneKermanMod.Instance;
            var state = mod != null ? mod.State : null;
            var list = state != null ? state.ContractList : null;
            if (list == null) return null;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in list)
            {
                var c = o as Dictionary<string, object>;
                if (c == null) continue;
                string status = MiniJSON.GetString(c, "status", "");
                if (status == "completed" || status == "cancelled" ||
                    status == "rejected" || status == "expired")
                    continue;
                foreach (var k in MiniJSON.GetList(c, "rescue_kerbals"))
                {
                    if (k == null) continue;
                    string n = k.ToString();
                    if (string.IsNullOrEmpty(n)) continue;
                    names.Add(n);
                    // The list is stored tagged ("{issuer}'s Jeb"), which is how it
                    // arrives in the rescuer's roster; the issuer's own copy is bare.
                    // Both spellings are protected for the same reason HomeboundSet
                    // carries both — one save's tagged name is another's plain one.
                    names.Add(StripOwnershipTag(n));
                }
            }
            return names;
        }

        /// <summary>Drop the orphans <see cref="OrphanedBorrowedCrew"/> reports, and say
        /// what happened. The deliberate write, run only from a button — never from
        /// <c>SweepRosterOnce</c>. Nothing is lost for good: a re-import of their craft
        /// rebuilds them from its GKCREW nodes.</summary>
        public static string PurgeOrphanedBorrowedCrew()
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return "No save is loaded.";

            var names = OrphanedBorrowedCrew();
            if (names.Count == 0)
                return "No borrowed kerbals are stranded in your roster.";

            int removed = 0;
            var failed = new List<string>();
            foreach (var name in names)
            {
                try
                {
                    var pcm = FindRosterMember(roster, name);
                    if (pcm != null && roster.Remove(pcm)) removed++;
                    else failed.Add(name);
                }
                catch (Exception ex)
                {
                    failed.Add(name);
                    Debug.LogWarning($"[GeneKerman] Could not drop orphan {name}: {ex.Message}");
                }
            }
            if (removed > 0)
            {
                Debug.Log($"[GeneKerman] RosterSweep: dropped {removed} orphaned borrowed " +
                          "kerbal(s) on request.");
                SaveNow();
            }
            if (failed.Count > 0)
                return $"Released {removed}, but could not drop {failed.Count} " +
                       $"({string.Join(", ", failed.ToArray())}). See KSP.log.";
            return $"Released {removed} borrowed kerbal(s) who were stuck in your roster. " +
                   "The names they were blocking are available for hiring again.";
        }

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
                // Names any vessel in this save still claims — see the remarks. Read
                // once: it walks every protovessel, and the answer cannot change while
                // this loop runs.
                var crewedNow = CrewedNames();
                var ghosts = new List<ProtoCrewMember>();
                foreach (var pcm in roster.Kerbals(statuses))
                {
                    if (pcm == null || !IsBorrowedCrewName(pcm.name)) continue;
                    if (frozen.Contains(pcm.name)) continue;
                    if (crewedNow != null && crewedNow.Contains(pcm.name)) continue;
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

            // Which names the parts of this vessel actually seat. A GKCREW node is a
            // roster *definition* that rides along so a transferred kerbal keeps their
            // gender, profession and stats — it is not, by itself, a reason to mint a
            // kerbal. Creating one per node regardless was the unbounded half: a peer
            // chooses how many GKCREW blocks a payload holds, KSP has no UI that deletes
            // a roster entry, and PurgeBorrowedGhostCrew only sweeps borrowed crew whose
            // vessel is gone — so unreferenced arrivals sit in the roster for the life of
            // the save, counting against the hire limit and poisoning the applicant
            // generator (which refuses any name that is a substring of an existing one).
            var seated = CrewNamesReferencedBy(vesselNode);

            int created = 0;

            // Prefer the full roster definitions embedded as GKCREW — these preserve
            // gender / profession / courage / stupidity across the save transfer.
            foreach (ConfigNode kn in vesselNode.GetNodes("GKCREW"))
            {
                string name = kn.GetValue("name");
                if (string.IsNullOrEmpty(name) || addedNames.Contains(name))
                    continue;
                if (!seated.Contains(name))
                {
                    // Nothing on this craft has a seat for them. The definition is still
                    // stripped with the rest of the node; it just does not become a
                    // person in this save.
                    Debug.LogWarning($"[GeneKerman] Import: GKCREW '{name}' is not seated by any " +
                                     "part of this vessel — not adding them to the roster.");
                    continue;
                }
                if (created >= MaxCrewPerVessel)
                {
                    Debug.LogError($"[GeneKerman] Import: refusing to create more than " +
                                   $"{MaxCrewPerVessel} crew for one vessel; '{name}' and any " +
                                   "after it were dropped.");
                    break;
                }
                addedNames.Add(name);
                // Already here → the same kerbal coming home, and only that: TagCrew
                // has renamed any incoming name that would otherwise land on one of
                // ours (see ResolveIncomingCrewName), so an adoption here is one the
                // server attested — on an account id, not on a display name anybody can
                // copy — and one whose roster entry is free, never one that is currently
                // crewing something. It costs no roster entry, so it is not counted
                // against the cap: the cap bounds what this import CREATES.
                if (roster[name] != null) continue;

                created++;
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
                    if (AddKerbalToRoster(name, trait, roster, addedNames, created)) created++;
                }

                foreach (string crewVal in partNode.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(crewVal) && !int.TryParse(crewVal, out _))
                    {
                        if (AddKerbalToRoster(crewVal, null, roster, addedNames, created)) created++;
                    }
                }
            }

            // Whatever the two passes above did not create must not still be referenced.
            StripUnfulfilledCrewRefs(vesselNode, roster, addedNames);
        }

        /// <summary>Delete every crew reference this vessel's parts still make to a
        /// kerbal that is not in the roster.
        ///
        /// An empty seat is a valid save. A seat naming a kerbal who does not exist is
        /// not, and it is not a loud failure either: per this project's own note
        /// (memory: "dangling crew ref breaks hiring", confirmed in-game) ONE `crew = `
        /// naming a missing roster entry breaks Astronaut Complex hiring for the life of
        /// the save — the applicant rows draw with no enabled Selectable and the Hire
        /// button does nothing, with nothing written to KSP.log to say why.
        ///
        /// The caps above (<see cref="MaxCrewPerVessel"/>) stop CREATING and used to
        /// leave the names behind, and <see cref="AddCrewToRosterInner"/> runs
        /// immediately before `new ProtoVessel(innerNode, …)` — so the vessel was
        /// registered naming crew nobody had made. That is the half raising the cap does
        /// not fix, since a hostile payload trips any cap there is.
        ///
        /// "Fulfilled" is asked two ways and a name only has to satisfy one, because the
        /// cost of the two mistakes is not symmetric: a name left in when it should have
        /// gone is the bug above, but a name taken out when the kerbal really is there
        /// unseats a legitimate crew member. `addedNames` is every name this import
        /// handled (created, or found already present); the roster lookup catches the
        /// ones the fallback pass skipped because they were already there.</summary>
        private static void StripUnfulfilledCrewRefs(ConfigNode vesselNode, KerbalRoster roster,
                                                     HashSet<string> addedNames)
        {
            if (vesselNode == null || roster == null) return;

            Func<string, bool> fulfilled = n =>
                !string.IsNullOrEmpty(n) &&
                ((addedNames != null && addedNames.Contains(n)) || roster[n] != null);

            int dropped = 0;
            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                // CREW child nodes: collect first, remove after — RemoveNode mutates the
                // collection GetNodes hands back.
                // A nameless CREW node is left alone: it names nobody, so it cannot
                // dangle, and this pass only removes what it understands.
                List<ConfigNode> stale = null;
                foreach (ConfigNode crewNode in partNode.GetNodes("CREW"))
                    if (!string.IsNullOrEmpty(crewNode.GetValue("name")) &&
                        !fulfilled(crewNode.GetValue("name")))
                    {
                        if (stale == null) stale = new List<ConfigNode>();
                        stale.Add(crewNode);
                    }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                    {
                        partNode.RemoveNode(stale[i]);
                        dropped++;
                    }

                // Bare `crew = ` values: rewritten as a set, the same shape RenameCrew
                // uses, since ConfigNode can only remove them by key. An index-form value
                // is not a name and is put back untouched.
                string[] crewValues = partNode.GetValues("crew");
                bool anyStale = false;
                for (int i = 0; i < crewValues.Length && !anyStale; i++)
                {
                    int ignored;
                    string v = crewValues[i];
                    if (string.IsNullOrEmpty(v) || int.TryParse(v, out ignored)) continue;
                    anyStale = !fulfilled(v);
                }
                if (!anyStale) continue;

                partNode.RemoveValues("crew");
                for (int i = 0; i < crewValues.Length; i++)
                {
                    int ignored;
                    string v = crewValues[i];
                    if (!string.IsNullOrEmpty(v) && !int.TryParse(v, out ignored) && !fulfilled(v))
                    {
                        dropped++;
                        continue;
                    }
                    partNode.AddValue("crew", v);
                }
            }

            if (dropped > 0)
                Debug.LogError($"[GeneKerman] Import: {dropped} crew reference(s) on " +
                               $"'{vesselNode.GetValue("name")}' named a kerbal this import did " +
                               "not create (the per-vessel crew cap, or a name that could not be " +
                               "made). They have been removed from the vessel — the seats arrive " +
                               "empty. A reference left dangling would have broken Astronaut " +
                               "Complex hiring for this save.");
        }

        /// <summary>Every crew name the parts of this vessel node reference — the CREW
        /// child nodes and the bare `crew = ` values, both spellings KSP uses. Index-form
        /// crew values are not names and are skipped, matching AddCrewToRosterInner's own
        /// fallback pass.</summary>
        private static HashSet<string> CrewNamesReferencedBy(ConfigNode vesselNode)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (vesselNode == null) return names;
            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                foreach (ConfigNode crewNode in partNode.GetNodes("CREW"))
                {
                    string n = crewNode.GetValue("name");
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                foreach (string crewVal in partNode.GetValues("crew"))
                {
                    int ignored;
                    if (!string.IsNullOrEmpty(crewVal) && !int.TryParse(crewVal, out ignored))
                        names.Add(crewVal);
                }
            }
            return names;
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
                                 $"install defines; {pcm.name} keeps {pcm.trait} instead. " +
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

            Announce("Crew arrived without their profession", sb.ToString());
        }

        /// <summary>Tell the player something about an import, once, wherever they can
        /// see it: the notification feed if the mod is up, a screen message if not, the
        /// log either way. Shared by every "the craft is not quite what was sent"
        /// report — a silent change is the one that arrives as a bug report.</summary>
        private static void Announce(string title, string body)
        {
            Debug.LogWarning($"[GeneKerman] {title}: {body}");

            var gk = GeneKermanMod.Instance;
            if (gk != null)
            {
                try { gk.ShowNotification(title, body); return; }
                catch (Exception ex)
                {
                    Debug.LogWarning("[GeneKerman] Import notification failed, falling back " +
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

        /// <summary>Returns true when a roster entry was actually created, so the caller
        /// can keep the per-vessel count that bounds it (see MaxCrewPerVessel).</summary>
        private static bool AddKerbalToRoster(string name, string trait, KerbalRoster roster,
                                              HashSet<string> addedNames, int created)
        {
            if (string.IsNullOrEmpty(name) || addedNames.Contains(name))
                return false;

            // Check if already in roster. As above, incoming names that collided with
            // this save's own crew were renamed in TagCrew before we got here.
            if (roster[name] != null)
                return false;

            if (created >= MaxCrewPerVessel)
            {
                Debug.LogError($"[GeneKerman] Import: refusing to create more than " +
                               $"{MaxCrewPerVessel} crew for one vessel; '{name}' was dropped.");
                return false;
            }

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
            return true;
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

#if GK_DEBUG_PANEL
        // ── Dev-only accessors, for Web/DebugBridge ──────────────────────────
        //
        // COMPILED OUT of production builds, so the visibility of these helpers is
        // unchanged in a shipped DLL. They exist because DebugTestPanel's crew rows
        // say what they cannot cover: "whether CrewedNames actually sees a
        // deferred-removal wreck's crew in a real save is a live test, not this one."
        // That test has to call THIS function against a real FlightGlobals — a copy of
        // the logic in the test harness would drift from the code it is asserting on,
        // and would then pass while the real one was broken, which is the one failure
        // mode a harness must not have.

        /// <summary>The real crewed-now set, over the live vessel list.</summary>
        internal static HashSet<string> DebugCrewedNames() => CrewedNames();

        /// <summary>The real busy predicate, against the live roster and crewed set.</summary>
        /// <summary>The real trait writer, with its refusal to write a profession this
        /// install cannot define — the guard T7 is about. A fixture builder must go
        /// through it rather than setting pcm.trait, or the harness would create exactly
        /// the corrupt roster the production code exists to prevent.</summary>
        internal static void DebugApplyTrait(ProtoCrewMember pcm, string trait)
            => ApplyTrait(pcm, trait);

        internal static bool DebugIsCrewingSomething(string name)
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            return IsCrewingSomething(name, roster, CrewedNames());
        }
#endif
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

        // Whether there is a target at all, or only a body to be at. Absent Ap/Pe means
        // any orbit of the body; absent lat/lon means anywhere on its surface. Both
        // default true: every rescue issued before the issuer could switch them off
        // carried a real target, and a spec built in code rather than parsed means the
        // same. The situation is required either way — an "any orbit" rescue still has
        // to be delivered in orbit.
        public bool hasAlt = true;
        public bool hasPos = true;

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
                // Presence, not value: a null Ap is "any orbit", which an Ap of 0 is
                // not. Both halves of a pair have to be there — half a coordinate is
                // not a place — and an old contract always carries both.
                hasAlt = MiniJSON.Has(d, "ap") && MiniJSON.Has(d, "pe"),
                hasPos = MiniJSON.Has(d, "lat") && MiniJSON.Has(d, "lon"),
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
