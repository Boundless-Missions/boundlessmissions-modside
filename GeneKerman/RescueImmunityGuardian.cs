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
 *     Putting a kerbal back is three things, not one, and skipping any of them leaves a
 *     wreck that looks crewed and behaves as if it weren't: it goes back in the PART it
 *     came from (ModuleCommand counts pilots per part, so a pilot in the wrong one reads
 *     as partial control with no SAS); in a SEAT of its own, with the pre-freeze seat and
 *     Kerbal references dropped first (KSP honours a stale seatIdx without checking
 *     whether that chair is taken, and the loser of the collision spawns no portrait);
 *     and the change is ANNOUNCED (Vessel.CrewWasModified + onVesselWasModified), since
 *     seating by hand updates one list and tells nothing else. See AddCrewToWreck and
 *     SpawnMissingIvas for the details of each.
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
    /// <summary>One stranded kerbal held in stasis: name + the part (by flightID) and the
    /// seat within it they must be returned to when revived.
    ///
    /// The seat is recorded, not re-derived: KSP's <c>InternalModel.AssignToSeat</c> writes
    /// <c>seats[pcm.seatIdx]</c> without checking whether that chair is already taken, so two
    /// kerbals carrying the same index end up fighting over one seat and only the last one
    /// spawns a <c>Kerbal</c> — which is the object a portrait is drawn from. Putting each
    /// one back where it sat keeps the indices distinct by construction.</summary>
    public class StasisCrew
    {
        public string Name;
        public uint PartFlightId;

        /// <summary>Seat index within that part, or -1 when it wasn't known (records
        /// written before the seat was carried, or a kerbal frozen out of an unloaded
        /// wreck that had never been seated). -1 means "any free seat".</summary>
        public int SeatIdx = -1;
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
                cn.AddValue("seatIdx", c.SeatIdx);
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
                // Absent in records written before the seat was carried: -1 reads as
                // "any free seat", which is what those records always meant.
                int seat;
                if (!int.TryParse(cn.GetValue("seatIdx"), out seat)) seat = -1;
                r.Crew.Add(new StasisCrew { Name = name, PartFlightId = fid, SeatIdx = seat });
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
                int seatIdx;
                if (RemoveCrewFromWreck(wreck, name, out partId, out seatIdx))
                    frozen.Add(new StasisCrew { Name = name, PartFlightId = partId, SeatIdx = seatIdx });
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

            NotifyCrewChanged(wreck);
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
            var seated = new List<Part>();
            foreach (var c in rec.Crew)
            {
                Part into;
                if (AddCrewToWreck(wreck, c, out into))
                {
                    revived++;
                    if (into != null && !seated.Contains(into)) seated.Add(into);
                }
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

            SpawnMissingIvas(wreck, seated);
            NotifyCrewChanged(wreck);
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
                GeneKermanMod.Instance?.ShowNotification("Rescue crew defrozen", body);
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
        /// park it (rosterStatus = Dead), and report which part and seat it was in.</summary>
        private static bool RemoveCrewFromWreck(Vessel wreck, string kerbalName,
                                                out uint partFlightId, out int seatIdx)
        {
            partFlightId = 0;
            seatIdx = -1;
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
                        seatIdx = pcm.seatIdx;
                        p.RemoveCrewmember(pcm);
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                        ClearSeatRefs(pcm);
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
                        seatIdx = pcm.seatIdx;
                        // Keep the part's two crew lists in sync, and drop from the vessel
                        // crew list, so the wreck loads with the seat genuinely empty.
                        pps.protoModuleCrew.Remove(pcm);
                        pps.protoCrewNames?.Remove(pcm.name);
                        try { wreck.protoVessel.RemoveCrew(pcm); } catch { /* best-effort */ }
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                        ClearSeatRefs(pcm);
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

        /// <summary>
        /// Return a parked kerbal to its part on the wreck, in its own seat, and mark it
        /// Assigned. Reports the part it went into (null when the wreck is unloaded — there
        /// are no Parts yet, only snapshots).
        ///
        /// Two things beyond "add it to the crew list" have to be right, and both were the
        /// thaw's doing rather than KSP's:
        ///
        ///   • It must land in the part it was recorded in. A kerbal that ends up in a
        ///     passenger cabin instead of the pod leaves the pod crewed but pilotless, and
        ///     <c>ModuleCommand</c> counts pilots per part — the vessel then flies at
        ///     PARTIAL_MANNED, which is exactly "some control, no SAS".
        ///   • Its seat/IVA references have to be dropped first. They describe a vessel
        ///     state that no longer exists: <c>InternalSeat</c> and <c>Kerbal</c> objects
        ///     from before the freeze are long destroyed, and <c>AssignToSeat</c> honours a
        ///     stale <c>seatIdx</c> without checking whether that chair is taken.
        /// </summary>
        private static bool AddCrewToWreck(Vessel wreck, StasisCrew c, out Part seatedIn)
        {
            seatedIn = null;
            try
            {
                ProtoCrewMember pcm = FindRosterCrew(c.Name);
                if (pcm == null) return false;
                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                // Nothing from the pre-freeze vessel may carry over; then ask for the seat
                // this kerbal actually sat in.
                ClearSeatRefs(pcm);
                pcm.seatIdx = c.SeatIdx;

                if (wreck.loaded)
                {
                    Part p = FindCrewablePart(wreck, c);
                    if (p == null) return false;

                    bool ok = SeatAtOrAnywhere(p, pcm, c.SeatIdx);
                    if (!ok) return false;

                    // AddCrewmember* sits the kerbal in the IVA but never spawns the model
                    // the portrait is rendered from — Vessel.SpawnCrew does that, and it
                    // only runs on a vessel switch. Without this the crew are aboard and
                    // controllable with no portraits in the corner.
                    if (pcm.seat != null && pcm.KerbalRef == null)
                    {
                        try { pcm.seat.SpawnCrew(); }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[GeneKerman] RescueStasis: portrait spawn failed " +
                                             $"for {c.Name}: {ex.Message}");
                        }
                    }

                    seatedIn = p;
                    Debug.Log($"[GeneKerman] RescueFreeze: {c.Name} back aboard " +
                              $"'{PartLabel(p)}' (seat {pcm.seatIdx}).");
                    return true;
                }
                else if (wreck.protoVessel?.protoPartSnapshots != null)
                {
                    var pps = FindCrewableSnapshot(wreck, c);
                    if (pps == null) return false;
                    // ProtoPartSnapshot has no AddCrew — add to its crew lists directly and
                    // register on the vessel so KSP seats the kerbal when the wreck loads.
                    if (!pps.protoModuleCrew.Contains(pcm)) pps.protoModuleCrew.Add(pcm);
                    if (pps.protoCrewNames != null && !pps.protoCrewNames.Contains(pcm.name))
                        pps.protoCrewNames.Add(pcm.name);
                    try { wreck.protoVessel.AddCrew(pcm); } catch { /* best-effort */ }
                    Debug.Log($"[GeneKerman] RescueFreeze: {c.Name} queued back into " +
                              $"'{SnapshotLabel(pps)}' (seat {pcm.seatIdx}) — the wreck is " +
                              "unloaded, KSP seats them on load.");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RescueStasis: failed to revive {c.Name}: {ex.Message}");
            }
            return false;
        }

        /// <summary>Forget where a kerbal used to sit. <c>InternalSeat</c> and <c>Kerbal</c>
        /// are scene objects: once the vessel they belonged to is gone (or its IVA was torn
        /// down) these fields point at destroyed objects, and KSP dereferences both without
        /// checking.</summary>
        private static void ClearSeatRefs(ProtoCrewMember pcm)
        {
            if (pcm == null) return;
            pcm.seat = null;
            pcm.seatIdx = -1;
            pcm.KerbalRef = null;
        }

        /// <summary>Seat a kerbal in its own chair when that chair exists and is free, and
        /// in any free one otherwise. Both KSP calls add to <c>protoModuleCrew</c> — the
        /// difference is only which seat, and <c>AddCrewmemberAt</c> would silently leave
        /// the kerbal unseated if the index were taken or out of range.</summary>
        private static bool SeatAtOrAnywhere(Part p, ProtoCrewMember pcm, int seatIdx)
        {
            var model = p.internalModel;
            bool seatUsable = model != null && model.seats != null
                              && seatIdx >= 0 && seatIdx < model.seats.Count
                              && model.seats[seatIdx] != null && !model.seats[seatIdx].taken;
            return seatUsable ? p.AddCrewmemberAt(pcm, seatIdx) : p.AddCrewmember(pcm);
        }

        /// <summary>The loaded part a frozen kerbal belongs in: the one it was taken from,
        /// or — when that part is gone or already full — a free crewable one, preferring a
        /// command part so a thawed pilot still gives the wreck full control.</summary>
        private static Part FindCrewablePart(Vessel wreck, StasisCrew c)
        {
            Part recorded = wreck.parts.FirstOrDefault(x => x != null && x.flightID == c.PartFlightId);
            if (recorded != null && recorded.protoModuleCrew.Count < recorded.CrewCapacity)
                return recorded;

            var free = wreck.parts.Where(x => x != null && x.CrewCapacity > x.protoModuleCrew.Count).ToList();
            Part pick = free.FirstOrDefault(x => x.FindModuleImplementing<ModuleCommand>() != null)
                        ?? free.FirstOrDefault();
            Debug.LogWarning($"[GeneKerman] RescueFreeze: {c.Name}'s part (flightID {c.PartFlightId}) is " +
                             (recorded == null ? "not on the wreck" : "full") +
                             $" — seating them in '{(pick == null ? "nothing (no free seat)" : PartLabel(pick))}' instead.");
            return pick;
        }

        /// <summary>The unloaded equivalent of <see cref="FindCrewablePart"/>.</summary>
        private static ProtoPartSnapshot FindCrewableSnapshot(Vessel wreck, StasisCrew c)
        {
            var snaps = wreck.protoVessel.protoPartSnapshots;
            ProtoPartSnapshot recorded = snaps.FirstOrDefault(x => x != null && x.flightID == c.PartFlightId);
            if (recorded != null && SnapshotCapacity(recorded) > recorded.protoModuleCrew.Count)
                return recorded;

            var free = snaps.Where(x => x != null && SnapshotCapacity(x) > x.protoModuleCrew.Count).ToList();
            ProtoPartSnapshot pick = free.FirstOrDefault(x => x.FindModule("ModuleCommand") != null)
                                     ?? free.FirstOrDefault();
            Debug.LogWarning($"[GeneKerman] RescueFreeze: {c.Name}'s part (flightID {c.PartFlightId}) is " +
                             (recorded == null ? "not on the wreck" : "full") +
                             $" — seating them in '{(pick == null ? "nothing (no free seat)" : SnapshotLabel(pick))}' instead.");
            return pick;
        }

        private static int SnapshotCapacity(ProtoPartSnapshot pps)
        {
            Part prefab = pps.partInfo != null ? pps.partInfo.partPrefab : null;
            return prefab != null ? prefab.CrewCapacity : 0;
        }

        private static string PartLabel(Part p) =>
            p == null ? "?" : (p.partInfo != null ? p.partInfo.title : p.name);

        private static string SnapshotLabel(ProtoPartSnapshot pps) =>
            pps == null ? "?" : (pps.partInfo != null ? pps.partInfo.title : pps.partName);

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
                        // They are aboard nothing, so they hold no seat either — a leftover
                        // index would follow them into whatever they are next assigned to.
                        ClearSeatRefs(pcm);
                        Debug.Log($"[GeneKerman] RescueFreeze: {name} released to the roster.");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] RescueFreeze: could not release {name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Build the IVA for a part we just seated someone into, when it hasn't got one.
        ///
        /// KSP only spawns interiors for the vessel the player is flying, so this is scoped
        /// to that — every other vessel gets its IVA when the player switches to it
        /// (<c>Vessel.MakeActive</c> → <c>SpawnCrew</c>), which is where the portraits come
        /// from too.
        ///
        /// What this deliberately does NOT do is the obvious-looking
        /// <c>DespawnCrew(); SpawnCrew();</c> pair. <c>Part.DespawnIVA</c> destroys the
        /// interior with <c>Object.Destroy</c> — which completes at the END of the frame —
        /// and never nulls <c>part.internalModel</c>. <c>SpawnIVA</c>'s null check therefore
        /// still passes in the same frame, so it re-seats the crew and spawns their
        /// <c>Kerbal</c>s into a model that is destroyed moments later: the part ends up
        /// with no interior at all, every portrait it registered is unregistered again as
        /// the objects die, and nothing rebuilds it until the player switches vessels. That
        /// pair is why a thaw left the wreck without the IVA view in the corner.
        /// </summary>
        private static void SpawnMissingIvas(Vessel wreck, List<Part> parts)
        {
            if (wreck == null || !wreck.loaded || parts == null || parts.Count == 0) return;
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel != wreck) return;

            foreach (Part p in parts)
            {
                if (p == null || p.internalModel != null) continue;
                try { p.SpawnIVA(); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] RescueStasis: IVA spawn failed for " +
                                     $"'{PartLabel(p)}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Tell KSP the vessel's crew changed. Seating a kerbal by hand updates the part's
        /// own list and nothing else: <c>Vessel.CrewWasModified</c> rebuilds the vessel's
        /// cached crew list and fires <c>onVesselCrewWasModified</c>, and the portrait
        /// gallery listens to <c>onVesselWasModified</c> (not the crew one) to rebuild the
        /// row of portraits for the active vessel. No-op on an unloaded wreck — there is no
        /// live vessel to describe, and KSP rebuilds all of this when it loads.
        /// </summary>
        private static void NotifyCrewChanged(Vessel wreck)
        {
            if (wreck == null || !wreck.loaded) return;
            try
            {
                Vessel.CrewWasModified(wreck);
                GameEvents.onVesselWasModified.Fire(wreck);
            }
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
