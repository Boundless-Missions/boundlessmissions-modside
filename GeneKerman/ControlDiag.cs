/*
 * ControlDiag.cs – One-shot dump of everything that decides whether a vessel has SAS.
 *
 * Exists because the thawed-rescue-crew SAS bug has now survived two fixes that were
 * each aimed at a gate static analysis said could stick (trait registration, the
 * persisted `inactive` flag) while every other link in the chain provably re-derives
 * itself per frame. Whatever is actually broken is only observable in the running
 * game, so the thaw instruments itself: a dump right after seating, another five
 * seconds later (after ModuleCommand's FixedUpdate and CommNet have cycled), and one
 * more the moment the player switches to the wreck — the moment they discover SAS is
 * missing. One bug report with a KSP.log then names the broken link outright.
 *
 * Read the dump against the chain it walks:
 *   SAS gate  = APSkillExtensions.AvailableAtLevel → VesselValues.*Skill (per-frame max
 *               over parts) ← PartValues.*Skill (delegates registered by each crew
 *               member's ExperienceTrait on its part)
 *   Control   = Vessel.CurrentControlLevel ← CommNetVessel.GetControlLevel (CommNet on)
 *               ← max over ModuleCommand.GetControlSourceState() ← live crew count per
 *               part, pilots counted via HasEffect<FullVesselControlSkill> && !inactive
 *
 * Pure reads, every section individually guarded — a diagnostic that can break a thaw
 * would be worse than the bug.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class ControlDiag
    {
        // The wreck to dump once more when the player switches to it, and until when.
        private static string watchPid;
        private static float watchUntil;
        private static bool hooked;

        /// <summary>Dump now, again in five seconds, and once more when the player
        /// switches to this vessel within the next 15 minutes.</summary>
        public static void Arm(Vessel v, string label)
        {
            if (v == null) return;
            Dump(v, label);

            watchPid = v.id.ToString();
            watchUntil = Time.realtimeSinceStartup + 900f;
            if (!hooked)
            {
                hooked = true;
                GameEvents.onVesselChange.Add(OnVesselChange);
            }

            var mod = GeneKermanMod.Instance;
            if (mod != null) mod.RunCoroutine(DelayedDump(watchPid, label + "+5s"));
        }

        private static IEnumerator DelayedDump(string pid, string label)
        {
            yield return new WaitForSeconds(5f);
            Dump(FindByPid(pid), label);
        }

        private static void OnVesselChange(Vessel v)
        {
            try
            {
                if (v == null || watchPid == null) return;
                if (Time.realtimeSinceStartup > watchUntil) { watchPid = null; return; }
                if (v.id.ToString() != watchPid) return;
                watchPid = null;   // once — the switch under test, not every switch after
                Dump(v, "switched-to-wreck");
            }
            catch (Exception) { /* never into GameEvents */ }
        }

        private static Vessel FindByPid(string pid)
        {
            if (string.IsNullOrEmpty(pid) || FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.id.ToString() == pid) return v;
            return null;
        }

        /// <summary>Write the vessel's whole control chain into KSP.log as one block.</summary>
        public static void Dump(Vessel v, string label)
        {
            try
            {
                var sb = new StringBuilder(2048);
                sb.Append("[GeneKerman] ControlDiag (").Append(label).Append(")\n");
                if (v == null) { sb.Append("  vessel: null (gone)\n"); Debug.Log(sb.ToString()); return; }

                sb.Append($"  vessel '{v.vesselName}' loaded={v.loaded} packed={v.packed} " +
                          $"situation={v.situation} type={v.vesselType}\n");

                try { sb.Append($"  CurrentControlLevel={v.CurrentControlLevel}\n"); }
                catch (Exception ex) { sb.Append($"  CurrentControlLevel: threw {ex.GetType().Name}\n"); }

                DumpCommNet(v, sb);
                DumpCommandParts(v, sb);
                DumpSkillValues(v, sb);
                DumpVesselCrew(v, sb);
                DumpSasState(v, sb);
                DumpInputLocks(sb);

                Debug.Log(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ControlDiag failed: {ex.Message}");
            }
        }

        private static void DumpCommNet(Vessel v, StringBuilder sb)
        {
            try
            {
                bool scenario = CommNet.CommNetScenario.Instance != null;
                bool enabled = scenario && CommNet.CommNetScenario.CommNetEnabled;
                var conn = v.Connection;
                sb.Append($"  CommNet: scenario={scenario} enabled={enabled} connection={(conn == null ? "null" : "present")}");
                if (conn != null)
                    sb.Append($" ControlState={conn.ControlState} GetControlLevel={conn.GetControlLevel()}");
                sb.Append('\n');
            }
            catch (Exception ex) { sb.Append($"  CommNet: threw {ex.GetType().Name}: {ex.Message}\n"); }
        }

        private static void DumpCommandParts(Vessel v, StringBuilder sb)
        {
            try
            {
                if (!v.loaded || v.parts == null)
                {
                    sb.Append("  parts: vessel unloaded — no live modules to inspect\n");
                    return;
                }
                foreach (var p in v.parts)
                {
                    if (p == null) continue;
                    var mc = p.FindModuleImplementing<ModuleCommand>();
                    bool crewable = p.CrewCapacity > 0;
                    if (mc == null && !crewable && (p.protoModuleCrew == null || p.protoModuleCrew.Count == 0))
                        continue;

                    sb.Append($"  part '{p.partInfo?.title ?? p.name}' cap={p.CrewCapacity} " +
                              $"isControlSource={p.isControlSource}");
                    if (mc != null)
                    {
                        try
                        {
                            sb.Append($" mc.minimumCrew={mc.minimumCrew} " +
                                      $"mc.state={((CommNet.ICommNetControlSource)mc).GetControlSourceState()} " +
                                      $"hibernating={mc.IsHibernating}");
                        }
                        catch (Exception ex) { sb.Append($" mc: threw {ex.GetType().Name}"); }
                    }
                    try
                    {
                        sb.Append($" PartValues[APSkill={p.PartValues.AutopilotSkill.value} " +
                                  $"APKerbal={p.PartValues.AutopilotKerbalSkill.value} " +
                                  $"APSAS={p.PartValues.AutopilotSASSkill.value}]");
                    }
                    catch (Exception ex) { sb.Append($" PartValues: threw {ex.GetType().Name}"); }
                    sb.Append('\n');

                    if (p.protoModuleCrew != null)
                        foreach (var pcm in p.protoModuleCrew)
                            DumpCrewMember(pcm, sb);
                }
            }
            catch (Exception ex) { sb.Append($"  parts: threw {ex.GetType().Name}: {ex.Message}\n"); }
        }

        private static void DumpCrewMember(ProtoCrewMember pcm, StringBuilder sb)
        {
            try
            {
                if (pcm == null) { sb.Append("    crew: null entry in protoModuleCrew!\n"); return; }
                bool traitNull = pcm.experienceTrait == null;
                bool pilotEffect = false;
                string traitType = "null";
                if (!traitNull)
                {
                    traitType = pcm.experienceTrait.TypeName ?? "?";
                    try { pilotEffect = pcm.HasEffect<Experience.Effects.FullVesselControlSkill>(); }
                    catch (Exception) { /* leaves false, and the null flag says why */ }
                }
                sb.Append($"    crew '{pcm.name}' trait={pcm.trait} expTrait={(traitNull ? "NULL" : traitType)} " +
                          $"fullControlEffect={pilotEffect} level={pcm.experienceLevel} type={pcm.type} " +
                          $"status={pcm.rosterStatus} inactive={pcm.inactive} outG={pcm.outDueToG} " +
                          $"seatIdx={pcm.seatIdx} seat={(pcm.seat == null ? "null" : "set")} " +
                          $"kerbalRef={(pcm.KerbalRef == null ? "null" : "set")}\n");
            }
            catch (Exception ex) { sb.Append($"    crew: threw {ex.GetType().Name}\n"); }
        }

        private static void DumpSkillValues(Vessel v, StringBuilder sb)
        {
            try
            {
                var vv = v.VesselValues;
                sb.Append($"  VesselValues: APSkill={vv.AutopilotSkill.value} " +
                          $"APKerbal={vv.AutopilotKerbalSkill.value} APSAS={vv.AutopilotSASSkill.value} " +
                          $"Repair={vv.RepairSkill.value} Science={vv.ScienceSkill.value}\n");
            }
            catch (Exception ex) { sb.Append($"  VesselValues: threw {ex.GetType().Name}: {ex.Message}\n"); }
        }

        private static void DumpVesselCrew(Vessel v, StringBuilder sb)
        {
            try
            {
                var crew = v.GetVesselCrew();
                var names = new List<string>();
                if (crew != null)
                    foreach (var c in crew)
                        if (c != null) names.Add(c.name);
                sb.Append($"  GetVesselCrew ({names.Count}): {string.Join(", ", names.ToArray())}\n");
            }
            catch (Exception ex) { sb.Append($"  GetVesselCrew: threw {ex.GetType().Name}\n"); }
        }

        private static void DumpSasState(Vessel v, StringBuilder sb)
        {
            try
            {
                bool sasOn = v.ActionGroups != null && v.ActionGroups[KSPActionGroup.SAS];
                sb.Append($"  SAS group={(sasOn ? "on" : "off")}");
                try { sb.Append($" actionBlocked(SAS)={v.ActionControlBlocked(KSPActionGroup.SAS)}"); }
                catch (Exception) { sb.Append(" actionBlocked(SAS)=?"); }
                try
                {
                    sb.Append($" CanEngageSAS={APSkillExtensions.AvailableAtLevel(VesselAutopilot.AutopilotMode.StabilityAssist, v)}");
                }
                catch (Exception ex) { sb.Append($" CanEngageSAS: threw {ex.GetType().Name}"); }
                sb.Append('\n');
            }
            catch (Exception ex) { sb.Append($"  SAS state: threw {ex.GetType().Name}\n"); }
        }

        private static void DumpInputLocks(StringBuilder sb)
        {
            try
            {
                var stack = InputLockManager.lockStack;
                if (stack == null || stack.Count == 0) { sb.Append("  input locks: none\n"); return; }
                sb.Append("  input locks:");
                foreach (var kvp in stack)
                    sb.Append($" {kvp.Key}=0x{kvp.Value:X}");
                sb.Append('\n');
            }
            catch (Exception ex) { sb.Append($"  input locks: threw {ex.GetType().Name}\n"); }
        }
    }
}
