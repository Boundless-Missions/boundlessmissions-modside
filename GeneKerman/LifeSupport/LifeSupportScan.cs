/*
 * LifeSupportScan.cs – Work out which life-support mod a craft is provisioned for and
 * how long it can sustain its crew, for the marketplace/contract LS flag.
 *
 * "Provisioned for" = which installed consumption mod's resources the craft actually
 * carries (Supplies → USI, Food/Water/Oxygen → TAC/Kerbalism, Snacks → Snacks). A craft
 * with no life-support resources is tagged "none". Endurance is reported per kerbal; the
 * display side derives the min/max range for 1..CrewCapacity kerbals.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public struct LifeSupportInfo
    {
        public string ModKey;                 // usi|tac|snacks|kerbalism|none
        public double EnduranceDaysPerKerbal; // days one kerbal survives on what's aboard (0 = none/unknown)
        public int CrewCapacity;              // total seats (for the min/max range)
        public bool HasLifeSupport;           // craft carries LS resources

        public static LifeSupportInfo None => new LifeSupportInfo { ModKey = "none" };
    }

    public static class LifeSupportScan
    {
        /// <summary>Scan the craft currently open in the editor (VAB/SPH).</summary>
        public static LifeSupportInfo FromEditor()
        {
            try
            {
                var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                if (ship != null && ship.parts != null)
                    return Scan(ship.parts);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] LifeSupportScan.FromEditor failed: {ex.Message}");
            }
            return LifeSupportInfo.None;
        }

        /// <summary>Scan an arbitrary part list (editor ship or flight vessel).</summary>
        public static LifeSupportInfo Scan(IList<Part> parts)
        {
            var info = LifeSupportInfo.None;
            if (parts == null) return info;

            // Total seats — needed for the min/max endurance range regardless of LS.
            int capacity = 0;
            foreach (var p in parts)
                if (p != null) capacity += p.CrewCapacity;
            info.CrewCapacity = capacity;

            // Pick the installed consumption mod whose resources the craft actually carries.
            // Prefer the install's primary LS mod when several match.
            var adapters = LifeSupportRegistry.InstalledConsumption;
            if (adapters == null || adapters.Count == 0) return info;

            ILifeSupportAdapter chosen = null;
            Dictionary<string, double> chosenAmounts = null;

            var primary = LifeSupportRegistry.PrimaryConsumptionLs;
            foreach (var adapter in OrderPrimaryFirst(adapters, primary))
            {
                var amounts = SumResources(parts, adapter.ResourceNames);
                if (amounts.Count > 0)
                {
                    chosen = adapter;
                    chosenAmounts = amounts;
                    break;
                }
            }

            if (chosen == null) return info; // craft carries no LS resources → "none"

            info.ModKey = chosen.ModKey;
            info.HasLifeSupport = true;
            info.EnduranceDaysPerKerbal = chosen.EnduranceDaysPerKerbal(chosenAmounts);
            return info;
        }

        private static IEnumerable<ILifeSupportAdapter> OrderPrimaryFirst(
            IList<ILifeSupportAdapter> adapters, ILifeSupportAdapter primary)
        {
            if (primary != null) yield return primary;
            foreach (var a in adapters)
                if (a != primary) yield return a;
        }

        /// <summary>Sum onboard amounts for the named resources across all parts.
        /// Only resources actually present (amount &gt; 0) are returned.</summary>
        private static Dictionary<string, double> SumResources(IList<Part> parts, string[] resourceNames)
        {
            var totals = new Dictionary<string, double>();
            if (resourceNames == null) return totals;
            var wanted = new HashSet<string>(resourceNames);

            foreach (var p in parts)
            {
                if (p?.Resources == null) continue;
                foreach (PartResource r in p.Resources)
                {
                    if (r == null || !wanted.Contains(r.resourceName)) continue;
                    double cur;
                    totals.TryGetValue(r.resourceName, out cur);
                    totals[r.resourceName] = cur + r.amount;
                }
            }
            // Drop zero-amount entries so an empty tank doesn't count as "provisioned".
            var keys = new List<string>(totals.Keys);
            foreach (var k in keys)
                if (totals[k] <= 0) totals.Remove(k);
            return totals;
        }
    }
}
