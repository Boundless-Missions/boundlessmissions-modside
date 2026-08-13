/*
 * ILifeSupportAdapter.cs – One optional life-support mod, wrapped (detection only).
 *
 * Rescue-kerbal immunity is handled mod-agnostically by RescueImmunityGuardian (it
 * removes the stranded crew from the simulation entirely — "stasis" — so no LS mod can
 * consume them), so adapters no longer touch each mod's per-kerbal internals. What they
 * still provide is:
 *
 *   • "Built-with" tagging + endurance: ResourceNames lists the life-support resources
 *     the mod adds, and EnduranceDaysPerKerbal() converts onboard amounts into how many
 *     (in-game) days one kerbal could survive — used for the marketplace/contract LS flag.
 *
 * (DeepFreeze is handled separately by DeepFreezeAdapter, which only detects already-
 * frozen crew — it isn't a consumption mod and doesn't implement this interface's
 * endurance surface meaningfully.)
 *
 * An adapter whose mod isn't installed reports IsInstalled == false, so the rest of the
 * code never has to special-case absence.
 */

using System.Collections.Generic;

namespace GeneKerman
{
    public interface ILifeSupportAdapter
    {
        /// <summary>Stable key stored on listings/contracts: usi|tac|snacks|kerbalism|deepfreeze.</summary>
        string ModKey { get; }

        /// <summary>Human-readable name for logs/UI, e.g. "USI-LS".</summary>
        string DisplayName { get; }

        /// <summary>True when the mod's assembly + required members were resolved.</summary>
        bool IsInstalled { get; }

        /// <summary>True for consumption life-support mods that tag a "built-with" craft
        /// and have an endurance figure (USI/TAC/Snacks/Kerbalism). False for DeepFreeze.</summary>
        bool IsConsumptionLs { get; }

        /// <summary>Life-support resources this mod adds (for detection + endurance).</summary>
        string[] ResourceNames { get; }

        /// <summary>Days one kerbal survives on the given onboard resource amounts
        /// (keyed by resource name). Returns 0 when unknown/empty.</summary>
        double EnduranceDaysPerKerbal(IDictionary<string, double> amounts);
    }
}
