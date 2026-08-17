/*
 * RescueImmunityGuardian.cs – The emergency freeze: keeps stranded rescue kerbals alive
 * until rescued, however long that takes and whichever life-support mod either player
 * runs. Three parts, all of which have to hold for a cross-mod rescue to work:
 *
 *   • Freeze (on spawn): each non-frozen rescue kerbal is removed from the wreck —
 *     remembering which part/seat they were in — and parked (rosterStatus = Dead, so
 *     KSP's respawn timer can't revive them behind our back). A kerbal that isn't aboard
 *     any vessel is consumed by nothing, so this holds uniformly for USI-LS, TAC-LS,
 *     Snacks and Kerbalism with no per-mod hacks. LsFreeze additionally tells each
 *     installed LS mod to let go of them. Crew already frozen in a real DeepFreeze
 *     cryopod are left alone — they're inert and the player thaws them at the pod.
 *   • Rations (on spawn): the wreck is stocked with a few days of the RESCUER's
 *     life-support resources (LsRations), because a craft provisioned for someone else's
 *     LS mod carries nothing this save recognises.
 *   • Thaw (on contact / button): when the active vessel comes within ReviveRadiusMeters
 *     the crew go back into their parts, marked Assigned, and LsFreeze hands them to the
 *     local LS mod with a clean slate — without that reset, a mod that reconstructs
 *     hunger from a "last meal" timestamp kills them the instant they board. We act while
 *     the wreck is still UNLOADED whenever possible (editing the ProtoVessel), so KSP
 *     seats them naturally when it loads; a loaded re-seat is the fallback.
 *
 * Records persist in GKContractScenario so a freeze survives a restart mid-rescue. Every
 * path that drops a record thaws first, including the one where the wreck is already
 * gone: a kerbal frozen and never thawed would stay exempt from life support forever.
 * Everything is best-effort and guarded — a failure never throws into KSP.
 */

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>One stranded kerbal held in stasis: name + the part (by flightID) it must
    /// be returned to when revived.</summary>
    public class StasisCrew
    {
        public string Name;
        public uint PartFlightId;
    }

    /// <summary>One spawned wreck's frozen crew, persisted in the contract scenario.</summary>
    public class RescueImmunityRecord
    {
        public string ContractId;
        public string VesselPid;          // GUID string of the spawned wreck
        public List<StasisCrew> Crew = new List<StasisCrew>();

        /// <summary>LS mod the wreck was built/provisioned for on the issuer's client
        /// (usi|tac|snacks|kerbalism|none) — shown to the rescuer so a mismatch with their
        /// own install is visible before they set off.</summary>
        public string BuiltWithLs = "none";

        public void Save(ConfigNode node)
        {
            node.AddValue("contractId", ContractId ?? "");
            node.AddValue("vesselPid", VesselPid ?? "");
            node.AddValue("builtWithLs", BuiltWithLs ?? "none");
            foreach (var c in (Crew ?? new List<StasisCrew>()))
            {
                ConfigNode cn = node.AddNode("CREW");
                cn.AddValue("name", c.Name ?? "");
                cn.AddValue("partFlightId", c.PartFlightId);
            }
        }

        public static RescueImmunityRecord FromNode(ConfigNode node)
        {
            if (node == null) return null;
            var r = new RescueImmunityRecord
            {
                ContractId = node.GetValue("contractId") ?? "",
                VesselPid = node.GetValue("vesselPid") ?? "",
                // Absent in records written before the LS flag existed.
                BuiltWithLs = node.GetValue("builtWithLs") ?? "none",
            };
            foreach (ConfigNode cn in node.GetNodes("CREW"))
            {
                string name = cn.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                uint fid = 0;
                uint.TryParse(cn.GetValue("partFlightId"), out fid);
                r.Crew.Add(new StasisCrew { Name = name, PartFlightId = fid });
            }
            return r;
        }
    }

    public static class RescueImmunityGuardian
    {
        // Revive while the rescuer is still well outside load range (~2.25 km), so the
        // wreck is unloaded and we can edit its ProtoVessel and let KSP seat the crew on
        // load. Generous so there's always a wide unloaded window to act in.
        private const double ReviveRadiusMeters = 10000.0;

        private const float TickInterval = 1.0f;
        private static float _lastTick;

        // ── Freeze (on spawn) ────────────────────────────────────────────────

        /// <summary>Put a freshly spawned wreck's crew into emergency freeze and stow an
        /// emergency ration kit aboard. Already-frozen crew (DeepFreeze cryopod) are left
        /// alone. No-op if the player turned the freeze off, or no crew need freezing.</summary>
        public static void Register(string contractId, string vesselPid,
                                    IEnumerable<string> kerbalNames, string builtWithLs = "none")
        {
            var names = (kerbalNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            if (names.Count == 0) return;

            Vessel wreck = FindVesselByPid(vesselPid);
            if (wreck == null)
            {
                Debug.LogWarning($"[GeneKerman] RescueFreeze: wreck {vesselPid} not found — cannot freeze crew.");
                return;
            }

            // Rations are stowed whether or not the freeze itself runs: a player who
            // turned the freeze off still gets a craft carrying an LS mod they may not run.
            string rations = LsRations.Provision(wreck, names.Count);

            if (!FreezeEnabled)
            {
                Debug.Log("[GeneKerman] RescueFreeze: disabled in settings — crew left aboard the wreck.");
                if (rations != null) PersistIfPossible(wreck);
                return;
            }

            var frozen = new List<StasisCrew>();
            foreach (var name in names)
            {
                if (LifeSupportRegistry.IsFrozen(name))
                {
                    Debug.Log($"[GeneKerman] RescueFreeze: {name} is cryo-frozen — left as-is.");
                    continue;
                }
                uint partId;
                if (RemoveCrewFromWreck(wreck, name, out partId))
                    frozen.Add(new StasisCrew { Name = name, PartFlightId = partId });
            }

            if (frozen.Count == 0)
            {
                // Nobody needed freezing (all already in cryopods) — but a ration kit may
                // still have been stowed, and that change deserves the same save.
                if (rations != null) PersistIfPossible(wreck);
                return;
            }

            // Out of the simulation AND released by every installed LS mod — the second
            // half is what stops a mod aging them from a stored timestamp.
            LsFreeze.Freeze(frozen.Select(c => c.Name));

            RefreshLoadedCrew(wreck);
            PersistIfPossible(wreck);

            GKContractScenario.Instance?.AddImmunity(new RescueImmunityRecord
            {
                ContractId = contractId, VesselPid = vesselPid, Crew = frozen,
                BuiltWithLs = string.IsNullOrEmpty(builtWithLs) ? "none" : builtWithLs.ToLowerInvariant(),
            });
            Debug.Log($"[GeneKerman] RescueFreeze: {frozen.Count} kerbal(s) frozen for contract {contractId} " +
                      $"(built for {builtWithLs}, this install runs {LsFreeze.LocalLsKey}).");
        }

        /// <summary>Player-facing kill switch (settings.cfg `enableEmergencyFreeze`). With
        /// it off the crew stay seated and their life support runs normally.</summary>
        private static bool FreezeEnabled =>
            GeneKermanMod.Instance?.Api == null || GeneKermanMod.Instance.Api.EmergencyFreezeEnabled;

        // ── Revive ───────────────────────────────────────────────────────────

        /// <summary>Manually thaw a contract's frozen crew (the in-UI button). Returns
        /// true if a record was found and thawed.</summary>
        public static bool ReviveContract(string contractId)
        {
            var scenario = GKContractScenario.Instance;
            var rec = scenario?.Immunities?.FirstOrDefault(r => r.ContractId == contractId);
            if (rec == null) return false;
            Revive(rec, manual: true);
            return true;
        }

        /// <summary>True when a contract still has crew waiting in emergency freeze.</summary>
        public static bool HasStasisCrew(string contractId)
        {
            var scenario = GKContractScenario.Instance;
            return scenario?.Immunities?.Any(r => r.ContractId == contractId) ?? false;
        }

        /// <summary>The frozen-crew record for a contract, for UI that wants to describe it
        /// (how many, what LS the wreck was built for). Null when nobody is frozen.</summary>
        public static RescueImmunityRecord GetRecord(string contractId)
        {
            var scenario = GKContractScenario.Instance;
            return scenario?.Immunities?.FirstOrDefault(r => r.ContractId == contractId);
        }

        private static void Revive(RescueImmunityRecord rec, bool manual = false)
        {
            var names = rec.Crew.Select(c => c.Name).ToList();
            Vessel wreck = FindVesselByPid(rec.VesselPid);
            if (wreck == null)
            {
                // Wreck is gone (recovered/destroyed) — nothing to board them onto. Thaw
                // them anyway before dropping the record: leaving a kerbal suspended in an
                // LS mod's books would exempt them from life support for the rest of the
                // save. Then free the record; the rescue can no longer complete.
                LsFreeze.Thaw(names);
                ReleaseParked(names);
                GKContractScenario.Instance?.RemoveImmunity(rec.ContractId);
                return;
            }

            int revived = 0;
            var stranded = new List<string>();
            foreach (var c in rec.Crew)
            {
                if (AddCrewToWreck(wreck, c.Name, c.PartFlightId)) revived++;
                else stranded.Add(c.Name);
            }
            // Anyone we couldn't seat is still holding the parked state this record was
            // the only remaining note of. Release them here or they keep it forever.
            ReleaseParked(stranded);

            // Hand them back to the local LS mod with a clean slate, and make sure the
            // wreck is carrying enough of that mod's resources to keep them alive while
            // the rescuer closes the remaining distance.
            LsFreeze.Thaw(names);
            string rations = LsRations.Provision(wreck, rec.Crew.Count);

            RefreshLoadedCrew(wreck);
            PersistIfPossible(wreck);
            GKContractScenario.Instance?.RemoveImmunity(rec.ContractId);
            Debug.Log($"[GeneKerman] RescueFreeze: thawed {revived}/{rec.Crew.Count} kerbal(s) " +
                      $"for contract {rec.ContractId}.");

            if (revived > 0 && !manual) Announce(revived, rations);
        }

        /// <summary>Tell the player the crew woke up — an automatic thaw happens while they
        /// are flying an approach and would otherwise be invisible.</summary>
        private static void Announce(int revived, string rations)
        {
            try
            {
                string body = rations == null
                    ? $"{revived} kerbal(s) are back aboard the stranded craft."
                    : $"{revived} kerbal(s) are back aboard, with {rations} stowed aboard.";
                GeneKermanMod.Instance?.ShowNotification("🧊 Rescue crew thawed", body);
            }
            catch { /* a notification is never worth an exception */ }
        }

        // ── Tick (approach detection) ────────────────────────────────────────

        public static void Tick()
        {
            var scenario = GKContractScenario.Instance;
            var records = scenario?.Immunities;
            if (records == null || records.Count == 0) return;
            if (Time.realtimeSinceStartup - _lastTick < TickInterval) return;
            _lastTick = Time.realtimeSinceStartup;

            foreach (var rec in records.ToList())
            {
                Vessel wreck = FindVesselByPid(rec.VesselPid);
                // Both branches go through Revive: a vanished wreck still has to release
                // its crew from every LS mod's books before the record is dropped.
                if (wreck == null || RescuerInRange(wreck)) Revive(rec);
            }
        }

        /// <summary>True once the active vessel (the rescuer) is within revive range of the
        /// wreck — works whether the wreck is loaded or still unloaded.</summary>
        private static bool RescuerInRange(Vessel wreck)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active == wreck) return false;
            if (active.vesselType == VesselType.Debris || active.vesselType == VesselType.Flag) return false;
            try { return Vector3d.Distance(active.GetWorldPos3D(), wreck.GetWorldPos3D()) <= ReviveRadiusMeters; }
            catch { return false; }
        }

        // ── Crew manipulation ────────────────────────────────────────────────

        /// <summary>Remove a kerbal from the wreck (proto if unloaded, live if loaded),
        /// park it (rosterStatus = Dead), and report which part it was in.</summary>
        private static bool RemoveCrewFromWreck(Vessel wreck, string kerbalName, out uint partFlightId)
        {
            partFlightId = 0;
            try
            {
                if (wreck.loaded)
                {
                    foreach (Part p in wreck.parts)
                    {
                        if (p?.protoModuleCrew == null) continue;
                        ProtoCrewMember pcm = p.protoModuleCrew.FirstOrDefault(c => c != null && c.name == kerbalName);
                        if (pcm == null) continue;
                        partFlightId = p.flightID;
                        p.RemoveCrewmember(pcm);
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                        return true;
                    }
                }
                else if (wreck.protoVessel?.protoPartSnapshots != null)
                {
                    foreach (var pps in wreck.protoVessel.protoPartSnapshots)
                    {
                        if (pps?.protoModuleCrew == null) continue;
                        ProtoCrewMember pcm = pps.protoModuleCrew.FirstOrDefault(c => c != null && c.name == kerbalName);
                        if (pcm == null) continue;
                        partFlightId = pps.flightID;
                        // Keep the part's two crew lists in sync, and drop from the vessel
                        // crew list, so the wreck loads with the seat genuinely empty.
                        pps.protoModuleCrew.Remove(pcm);
                        pps.protoCrewNames?.Remove(pcm.name);
                        try { wreck.protoVessel.RemoveCrew(pcm); } catch { /* best-effort */ }
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RescueStasis: failed to stash {kerbalName}: {ex.Message}");
            }
            return false;
        }

        /// <summary>Return a parked kerbal to its part on the wreck and mark it Assigned.</summary>
        private static bool AddCrewToWreck(Vessel wreck, string kerbalName, uint partFlightId)
        {
            try
            {
                ProtoCrewMember pcm = FindRosterCrew(kerbalName);
                if (pcm == null) return false;
                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;

                if (wreck.loaded)
                {
                    Part p = wreck.parts.FirstOrDefault(x => x != null && x.flightID == partFlightId)
                             ?? wreck.parts.FirstOrDefault(x => x != null && x.CrewCapacity > x.protoModuleCrew.Count);
                    if (p == null) return false;
                    p.AddCrewmember(pcm);
                    return true;
                }
                else if (wreck.protoVessel?.protoPartSnapshots != null)
                {
                    var pps = wreck.protoVessel.protoPartSnapshots.FirstOrDefault(x => x != null && x.flightID == partFlightId)
                              ?? wreck.protoVessel.protoPartSnapshots.FirstOrDefault(
                                  x => x != null && x.partInfo != null && x.partInfo.partPrefab != null
                                       && x.partInfo.partPrefab.CrewCapacity > x.protoModuleCrew.Count);
                    if (pps == null) return false;
                    // ProtoPartSnapshot has no AddCrew — add to its crew lists directly and
                    // register on the vessel so KSP seats the kerbal when the wreck loads.
                    if (!pps.protoModuleCrew.Contains(pcm)) pps.protoModuleCrew.Add(pcm);
                    if (pps.protoCrewNames != null && !pps.protoCrewNames.Contains(pcm.name))
                        pps.protoCrewNames.Add(pcm.name);
                    try { wreck.protoVessel.AddCrew(pcm); } catch { /* best-effort */ }
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RescueStasis: failed to revive {kerbalName}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Take kerbals out of the parked (Dead) state when there is no wreck left to put
        /// them back aboard. The freeze parks them as Dead so KSP's respawn timer can't
        /// revive them behind our back; that is only safe while a record exists saying so,
        /// so every path that drops a record without seating them has to come through here
        /// — otherwise they stay KIA for the rest of the save, out of the Astronaut
        /// Complex's Available list and still counted against the hire limit.
        ///
        /// Ours go back to Available (they were never lost, their ride was). Borrowed
        /// kerbals leave the roster: they belong to another save, their craft isn't here,
        /// and keeping them only pollutes the roster KSP generates new names against.
        /// </summary>
        private static void ReleaseParked(IEnumerable<string> names)
        {
            if (names == null) return;
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    ProtoCrewMember pcm = FindRosterCrew(name);
                    if (pcm == null) continue;
                    if (VesselTransfer.IsBorrowedCrewName(name))
                    {
                        roster.Remove(pcm);
                        Debug.Log($"[GeneKerman] RescueFreeze: {name} was borrowed and their craft " +
                                  "is gone — dropped from the roster.");
                    }
                    else
                    {
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        Debug.Log($"[GeneKerman] RescueFreeze: {name} released to the roster.");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] RescueFreeze: could not release {name}: {ex.Message}");
                }
            }
        }

        /// <summary>Rebuild a loaded vessel's crew/portraits after a change (no-op when
        /// the wreck is unloaded — KSP seats from the proto on load).</summary>
        private static void RefreshLoadedCrew(Vessel wreck)
        {
            if (wreck == null || !wreck.loaded) return;
            try { wreck.DespawnCrew(); wreck.SpawnCrew(); wreck.RebuildCrewList(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RescueStasis: crew refresh failed: {ex.Message}");
            }
        }

        /// <summary>Persist the change so stasis survives a quit. Only saves outside flight
        /// (the same constraint VesselTransfer uses) — in flight, the next autosave covers it.</summary>
        private static void PersistIfPossible(Vessel wreck)
        {
            if (HighLogic.LoadedSceneIsFlight) return;
            try { GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RescueStasis: save failed: {ex.Message}");
            }
        }

        // ── Lookups ──────────────────────────────────────────────────────────

        private static ProtoCrewMember FindRosterCrew(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName) || HighLogic.CurrentGame == null) return null;
            try
            {
                // Search every roster status so our parked "Dead" kerbals are found too
                // (the active crew list alone wouldn't include them).
                var statuses = new[]
                {
                    ProtoCrewMember.RosterStatus.Assigned,
                    ProtoCrewMember.RosterStatus.Available,
                    ProtoCrewMember.RosterStatus.Dead,
                    ProtoCrewMember.RosterStatus.Missing,
                };
                foreach (var pcm in HighLogic.CurrentGame.CrewRoster.Kerbals(statuses))
                    if (pcm != null && pcm.name == kerbalName) return pcm;
            }
            catch { /* fall through */ }
            return LsReflect.FindCrew(kerbalName);
        }

        private static Vessel FindVesselByPid(string pid)
        {
            if (string.IsNullOrEmpty(pid) || FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id.ToString() == pid) return v;
            return null;
        }
    }
}
