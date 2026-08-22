/*
 * LifeSupportRegistry.cs – Central detection/selection for the life-support adapters.
 *
 * Named "Registry" (not "Manager") to avoid confusion with USI's own
 * LifeSupport.LifeSupportManager type that UsiLsAdapter reflects into.
 *
 * Holds one instance of each adapter, reports which mods are present on THIS (the
 * contractor / importing) client, and picks the consumption LS mod this install runs —
 * which is both the "built with" tag on anything listed from here and the mod an
 * emergency ration kit is sized for when a wreck arrives from a player running another.
 *
 * Freeze/thaw goes through every installed adapter rather than the primary one (see
 * LsFreeze): a save can have two LS mods loaded, and a kerbal only stays frozen if all
 * of them let go.
 */

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public static class LifeSupportRegistry
    {
        private static bool _scanned;
        private static List<ILifeSupportAdapter> _all;
        private static DeepFreezeAdapter _deepFreeze;

        // Consumption mods in selection priority (first installed wins as "primary").
        private static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;
            _deepFreeze = new DeepFreezeAdapter();
            _all = new List<ILifeSupportAdapter>
            {
                new UsiLsAdapter(),
                new TacLsAdapter(),
                new SnacksAdapter(),
                new KerbalismAdapter(),
                _deepFreeze,
            };
        }

        /// <summary>Every adapter (installed or not).</summary>
        public static IList<ILifeSupportAdapter> All { get { EnsureScanned(); return _all; } }

        /// <summary>Installed consumption life-support mods (USI/TAC/Snacks/Kerbalism).</summary>
        public static IList<ILifeSupportAdapter> InstalledConsumption =>
            All.Where(a => a.IsConsumptionLs && a.IsInstalled).ToList();

        public static DeepFreezeAdapter DeepFreeze { get { EnsureScanned(); return _deepFreeze; } }

        public static bool HasKerbalism =>
            All.Any(a => a.ModKey == "kerbalism" && a.IsInstalled);

        public static bool HasDeepFreeze => DeepFreeze.IsInstalled;

        /// <summary>True if DeepFreeze currently has this kerbal frozen — frozen crew
        /// consume no life support under any mod, so they need no stasis (the player thaws
        /// them at the cryopod). Rescue stasis skips these kerbals.</summary>
        public static bool IsFrozen(string kerbalName) => DeepFreeze.IsKerbalFrozen(kerbalName);

        /// <summary>The consumption LS mod this install runs (for "built-with" tagging),
        /// or null when none is installed.</summary>
        public static ILifeSupportAdapter PrimaryConsumptionLs => InstalledConsumption.FirstOrDefault();

        /// <summary>Mod key stored on listings/contracts: usi|tac|snacks|kerbalism|none.</summary>
        public static string PrimaryLsModKey => PrimaryConsumptionLs?.ModKey ?? "none";

        /// <summary>Find an adapter by its ModKey (installed or not), or null.</summary>
        public static ILifeSupportAdapter ByKey(string modKey) =>
            string.IsNullOrEmpty(modKey) ? null
            : All.FirstOrDefault(a => a.ModKey == modKey);

        /// <summary>Log what was detected, once, at startup.</summary>
        public static void LogDetected()
        {
            EnsureScanned();
            var present = All.Where(a => a.IsInstalled).Select(a => a.DisplayName).ToArray();
            Debug.Log(present.Length == 0
                ? "[GeneKerman] LifeSupport: no life-support / DeepFreeze mods detected."
                : $"[GeneKerman] LifeSupport: detected {string.Join(", ", present)}; " +
                  $"primary LS = {PrimaryLsModKey}, DeepFreeze = {HasDeepFreeze}, Kerbalism = {HasKerbalism}.");
        }
    }
}
