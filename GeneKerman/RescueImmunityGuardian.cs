/*
 * RescueImmunityGuardian.cs – Keeps stranded rescue kerbals alive until rescued, by
 * putting them in "stasis": the crew are removed from the wreck (and the simulation)
 * while it drifts, then re-boarded when a rescuer approaches. Because the kerbals aren't
 * aboard any vessel while stranded, NO life-support mod consumes them — this works
 * uniformly for USI-LS, TAC-LS, Snacks and Kerbalism, with no per-mod hacks and no
 * DeepFreeze dependency.
 *
 *   • Stash (on spawn): remove each non-frozen rescue kerbal from the wreck, remembering
 *     which part/seat they were in, and park them (rosterStatus = Dead so KSP's respawn
 *     timer can't revive them behind our back). Crew already frozen in a real DeepFreeze
 *     cryopod are left alone — they're inert and the player thaws them at the pod.
 *   • Revive (on contact / button): when the active vessel comes within REVIVE_RADIUS of
 *     the wreck, put the crew back into their parts and mark them Assigned. We do this
 *     while the wreck is still UNLOADED whenever possible (editing the ProtoVessel), so
 *     KSP seats them naturally when it loads; a loaded re-seat is the fallback.
 *
 * Records persist in GKContractScenario so stasis survives a restart mid-rescue.
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

    /// <summary>One spawned wreck's stasis crew, persisted in the contract scenario.</summary>
    public class RescueImmunityRecord
    {
        public string ContractId;
        public string VesselPid;          // GUID string of the spawned wreck
        public List<StasisCrew> Crew = new List<StasisCrew>();

        public void Save(ConfigNode node)
        {
            node.AddValue("contractId", ContractId ?? "");
            node.AddValue("vesselPid", VesselPid ?? "");
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

        // ── Stash (on spawn) ─────────────────────────────────────────────────

        /// <summary>Put a freshly spawned wreck's crew into stasis. Already-frozen crew
        /// (DeepFreeze cryopod) are left alone. No-op if no crew need stashing.</summary>
        public static void Register(string contractId, string vesselPid, IEnumerable<string> kerbalNames)
        {
            var names = (kerbalNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            if (names.Count == 0) return;

            Vessel wreck = FindVesselByPid(vesselPid);
            if (wreck == null)
            {
                Debug.LogWarning($"[GeneKerman] RescueStasis: wreck {vesselPid} not found — cannot stash crew.");
                return;
            }

            var stashed = new List<StasisCrew>();
            foreach (var name in names)
            {
                if (LifeSupportRegistry.IsFrozen(name))
                {
                    Debug.Log($"[GeneKerman] RescueStasis: {name} is cryo-frozen — left as-is.");
                    continue;
                }
                uint partId;
                if (RemoveCrewFromWreck(wreck, name, out partId))
                    stashed.Add(new StasisCrew { Name = name, PartFlightId = partId });
            }

            if (stashed.Count == 0) return;

            RefreshLoadedCrew(wreck);
            PersistIfPossible(wreck);

            GKContractScenario.Instance?.AddImmunity(new RescueImmunityRecord
            {
                ContractId = contractId, VesselPid = vesselPid, Crew = stashed,
            });
            Debug.Log($"[GeneKerman] RescueStasis: {stashed.Count} kerbal(s) put in stasis for contract {contractId}.");
        }

        // ── Revive ───────────────────────────────────────────────────────────

        /// <summary>Manually revive a contract's stasis crew (the in-UI button). Returns
        /// true if a record was found and revived.</summary>
        public static bool ReviveContract(string contractId)
        {
            var scenario = GKContractScenario.Instance;
            var rec = scenario?.Immunities?.FirstOrDefault(r => r.ContractId == contractId);
            if (rec == null) return false;
            Revive(rec);
            return true;
        }

        /// <summary>True when a contract still has crew waiting in stasis.</summary>
        public static bool HasStasisCrew(string contractId)
        {
            var scenario = GKContractScenario.Instance;
            return scenario?.Immunities?.Any(r => r.ContractId == contractId) ?? false;
        }

        private static void Revive(RescueImmunityRecord rec)
        {
            Vessel wreck = FindVesselByPid(rec.VesselPid);
            if (wreck == null)
            {
                // Wreck is gone (recovered/destroyed) — nothing to board them onto. Free the
                // record; the kerbals stay parked (the rescue can no longer complete anyway).
                GKContractScenario.Instance?.RemoveImmunity(rec.ContractId);
                return;
            }

            int revived = 0;
            foreach (var c in rec.Crew)
                if (AddCrewToWreck(wreck, c.Name, c.PartFlightId)) revived++;

            RefreshLoadedCrew(wreck);
            PersistIfPossible(wreck);
            GKContractScenario.Instance?.RemoveImmunity(rec.ContractId);
            Debug.Log($"[GeneKerman] RescueStasis: revived {revived}/{rec.Crew.Count} kerbal(s) " +
                      $"for contract {rec.ContractId}.");
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
                if (wreck == null) { scenario.RemoveImmunity(rec.ContractId); continue; }
                if (RescuerInRange(wreck)) Revive(rec);
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
