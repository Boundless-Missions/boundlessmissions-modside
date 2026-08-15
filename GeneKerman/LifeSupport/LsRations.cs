/*
 * LsRations.cs – The emergency ration kit stowed aboard a rescue wreck.
 *
 * This is what actually makes a cross-mod rescue possible. The wreck was built and
 * provisioned by another player: a craft full of TAC-LS Food/Water/Oxygen arriving in a
 * USI-LS save carries nothing its new game recognises, so the moment its crew are thawed
 * they are aboard a ship with zero life support. Freezing them buys time; it doesn't
 * translate their supplies.
 *
 * So when a wreck is imported, we stow a small kit of the RESCUER's life-support
 * resources aboard — enough for `days × crew` at the local mod's own rates, read from the
 * same DailyNeedPerKerbal the endurance display uses. Notes:
 *
 *   • Idempotent and a top-up, never a refill: whatever the wreck already carries counts
 *     towards the target, so a craft that genuinely was provisioned for this LS mod gets
 *     nothing, and re-running on thaw adds nothing twice.
 *   • Deliberately small (settings.cfg `emergencyRationDays`, default 3, 0 disables). It
 *     is a rescue kit, not a resupply — the crew still have to be brought home.
 *   • Works loaded (live PartResource) and unloaded (ProtoPartResourceSnapshot), because
 *     a wreck is normally imported and thawed while it is nowhere near the active vessel.
 */

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public static class LsRations
    {
        /// <summary>Days of life support to stow per rescued kerbal. Read from
        /// settings.cfg; 0 turns emergency rations off entirely.</summary>
        public static int Days =>
            GeneKermanMod.Instance?.Api != null ? GeneKermanMod.Instance.Api.EmergencyRationDays : 3;

        /// <summary>Stock a wreck with the local life-support mod's resources for
        /// <paramref name="crewCount"/> kerbals. Returns a short human-readable summary of
        /// what was added ("3 d of Supplies"), or null when nothing was needed or possible.</summary>
        public static string Provision(Vessel wreck, int crewCount)
        {
            if (wreck == null || crewCount <= 0) return null;

            int days = Days;
            if (days <= 0) return null;

            var adapter = LifeSupportRegistry.PrimaryConsumptionLs;
            if (adapter == null) return null; // stock install — nothing to provision for

            var needs = adapter.DailyNeedPerKerbal;
            if (needs == null || needs.Count == 0) return null;

            var added = new List<string>();
            try
            {
                foreach (var need in needs)
                {
                    if (need.Value <= 0) continue;
                    if (!ResourceExists(need.Key)) continue;

                    double target = need.Value * days * crewCount;
                    double onboard = OnboardAmount(wreck, need.Key);
                    double deficit = target - onboard;
                    if (deficit <= 0.0001) continue;

                    if (AddResource(wreck, need.Key, deficit))
                        added.Add($"{deficit:F1} {need.Key}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] LsRations: provisioning {wreck.vesselName} failed: {ex.Message}");
            }

            if (added.Count == 0) return null;

            RefreshLoadedResources(wreck);
            string summary = $"{days} d of {string.Join(" + ", added)} ({adapter.DisplayName})";
            Debug.Log($"[GeneKerman] LsRations: stowed {summary} aboard {wreck.vesselName} " +
                      $"for {crewCount} kerbal(s).");
            return summary;
        }

        private static bool ResourceExists(string resourceName)
        {
            try
            {
                return PartResourceLibrary.Instance != null
                       && PartResourceLibrary.Instance.GetDefinition(resourceName) != null;
            }
            catch { return false; }
        }

        private static double OnboardAmount(Vessel wreck, string resourceName)
        {
            double total = 0;
            if (wreck.loaded)
            {
                foreach (Part p in wreck.parts)
                {
                    if (p?.Resources == null) continue;
                    foreach (PartResource r in p.Resources)
                        if (r != null && r.resourceName == resourceName) total += r.amount;
                }
            }
            else if (wreck.protoVessel?.protoPartSnapshots != null)
            {
                foreach (var pps in wreck.protoVessel.protoPartSnapshots)
                {
                    if (pps?.resources == null) continue;
                    foreach (var r in pps.resources)
                        if (r != null && r.resourceName == resourceName) total += r.amount;
                }
            }
            return total;
        }

        /// <summary>Add an amount of a resource to the best-placed part: one that already
        /// carries it, else one holding crew, else the first part we can reach.</summary>
        private static bool AddResource(Vessel wreck, string resourceName, double amount)
        {
            if (wreck.loaded)
            {
                Part part = wreck.parts.FirstOrDefault(p => p?.Resources != null && p.Resources.Contains(resourceName))
                            ?? wreck.parts.FirstOrDefault(p => p != null && p.CrewCapacity > 0)
                            ?? wreck.parts.FirstOrDefault(p => p != null);
                if (part == null) return false;

                PartResource existing = part.Resources.Get(resourceName);
                if (existing != null)
                {
                    existing.amount += amount;
                    if (existing.maxAmount < existing.amount) existing.maxAmount = existing.amount;
                }
                else
                {
                    // A part with no tank for this resource gets one sized to the kit —
                    // visible and tweakable, so the player can see what was stowed.
                    part.Resources.Add(resourceName, amount, amount, true, true, false, true,
                                       PartResource.FlowMode.Both);
                }
                return true;
            }

            var snapshots = wreck.protoVessel?.protoPartSnapshots;
            if (snapshots == null || snapshots.Count == 0) return false;

            var pps = snapshots.FirstOrDefault(
                          s => s?.resources != null && s.resources.Any(r => r?.resourceName == resourceName))
                      ?? snapshots.FirstOrDefault(s => s?.protoModuleCrew != null && s.protoModuleCrew.Count > 0)
                      ?? snapshots.FirstOrDefault(
                          s => s?.partInfo?.partPrefab != null && s.partInfo.partPrefab.CrewCapacity > 0)
                      ?? snapshots.FirstOrDefault(s => s != null);
            if (pps == null) return false;

            var snapshot = pps.resources?.FirstOrDefault(r => r?.resourceName == resourceName);
            if (snapshot != null)
            {
                snapshot.amount += amount;
                if (snapshot.maxAmount < snapshot.amount) snapshot.maxAmount = snapshot.amount;
                // The snapshot's own ConfigNode is what gets written to the save — the
                // fields alone would be discarded at the next persistence pass.
                snapshot.UpdateConfigNodeAmounts();
                return true;
            }

            var node = new ConfigNode("RESOURCE");
            node.AddValue("name", resourceName);
            node.AddValue("amount", amount);
            node.AddValue("maxAmount", amount);
            node.AddValue("flowState", true);
            if (pps.resources == null) return false;
            pps.resources.Add(new ProtoPartResourceSnapshot(node));
            return true;
        }

        /// <summary>Let a loaded vessel notice its new tanks (no-op when unloaded — the
        /// proto is read fresh on load).</summary>
        private static void RefreshLoadedResources(Vessel wreck)
        {
            if (wreck == null || !wreck.loaded) return;
            try { wreck.UpdateResourceSets(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] LsRations: resource refresh failed: {ex.Message}");
            }
        }
    }
}
