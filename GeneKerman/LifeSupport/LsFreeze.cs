/*
 * LsFreeze.cs – The mod-facing half of the emergency freeze.
 *
 * RescueImmunityGuardian owns the mod-agnostic half: stranded crew are lifted out of the
 * simulation entirely, which no life-support mod can consume through. That is airtight
 * while they are frozen — and not enough by itself when they come back, because most LS
 * mods reconstruct hunger from a stored timestamp. A kerbal thawed 200 days after being
 * frozen is, as far as USI-LS or TAC-LS is concerned, 200 days without a meal.
 *
 * So every freeze also tells each installed LS mod to let go of that kerbal, and every
 * thaw hands them back with a clean slate. Two consequences worth stating plainly:
 *
 *   • A thaw must run on EVERY exit path, including the one where the wreck was destroyed
 *     and nobody is coming. Kerbalism's disabled flag lives in the save; a kerbal we
 *     froze and never thawed would be permanently exempt from life support.
 *   • Freeze/thaw are per-kerbal and idempotent, so re-running either is harmless — which
 *     is what makes the manual "thaw now" button safe to press twice.
 *
 * Cross-mod rescues are the point of all this. The wreck was built by a player who may
 * run a different LS mod (or none); its crew arrive frozen, so nothing about the
 * mismatch matters until the rescuer is alongside — and LsRations stocks the wreck with
 * the *rescuer's* life support so they survive the thaw.
 */

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public static class LsFreeze
    {
        /// <summary>Life-support mod this install runs, for the "built for X, you run Y"
        /// comparison: usi|tac|snacks|kerbalism|none.</summary>
        public static string LocalLsKey => LifeSupportRegistry.PrimaryLsModKey;

        /// <summary>Display name for a stored mod key ("usi" → "USI-LS"), or "none".</summary>
        public static string DisplayNameFor(string modKey)
        {
            if (string.IsNullOrEmpty(modKey) || modKey == "none") return "none";
            var adapter = LifeSupportRegistry.ByKey(modKey);
            return adapter != null ? adapter.DisplayName : modKey;
        }

        /// <summary>True when a craft built for <paramref name="builtWithKey"/> lands in an
        /// install running something else — the case emergency rations exist for. A craft
        /// with no life support at all ("none") isn't a mismatch: nothing to translate.</summary>
        public static bool IsCrossMod(string builtWithKey)
        {
            string built = string.IsNullOrEmpty(builtWithKey) ? "none" : builtWithKey.ToLowerInvariant();
            string local = LocalLsKey;
            if (built == "none" || local == "none") return false;
            return built != local;
        }

        /// <summary>Suspend every installed LS mod's hold on these kerbals.</summary>
        public static void Freeze(IEnumerable<string> kerbalNames)
        {
            ForEachKerbal(kerbalNames, (adapter, name) => adapter.SuspendKerbal(name), "froze");
        }

        /// <summary>Hand these kerbals back to every installed LS mod with a clean slate.</summary>
        public static void Thaw(IEnumerable<string> kerbalNames)
        {
            ForEachKerbal(kerbalNames, (adapter, name) => adapter.ResumeKerbal(name), "thawed");
        }

        private static void ForEachKerbal(IEnumerable<string> kerbalNames,
                                          System.Action<ILifeSupportAdapter, string> action,
                                          string verb)
        {
            var names = (kerbalNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            if (names.Count == 0) return;

            var adapters = LifeSupportRegistry.All.Where(a => a.IsInstalled).ToList();
            if (adapters.Count == 0) return;

            foreach (var name in names)
                foreach (var adapter in adapters)
                {
                    // One misbehaving adapter must not strand the others mid-list.
                    try { action(adapter, name); }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] LsFreeze: {adapter.DisplayName} " +
                                         $"failed to {verb} {name}: {ex.Message}");
                    }
                }

            Debug.Log($"[GeneKerman] LsFreeze: {verb} {names.Count} kerbal(s) across " +
                      $"{string.Join(", ", adapters.Select(a => a.DisplayName))}.");
        }
    }
}
