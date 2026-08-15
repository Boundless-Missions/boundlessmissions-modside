/*
 * ILifeSupportAdapter.cs – One optional life-support mod, wrapped.
 *
 * Three jobs, all optional per mod:
 *
 *   • "Built-with" tagging + endurance: ResourceNames lists the life-support resources
 *     the mod adds and DailyNeedPerKerbal the rate it burns them, which together give
 *     the marketplace/contract LS flag ("USI-LS · ~180 d solo") and the size of an
 *     emergency ration kit (LsRations).
 *
 *   • Emergency freeze handoff: SuspendKerbal stops the mod tracking a kerbal;
 *     ResumeKerbal hands it back with a clean slate. RescueImmunityGuardian already
 *     removes stranded crew from the simulation, which no LS mod can consume — but
 *     that alone is not enough on the way *out*: USI-LS, TAC-LS and Snacks all decide
 *     how hungry a kerbal is from a stored "last fed" timestamp, so a kerbal thawed
 *     after 200 days of stasis is instantly 200 days starved unless someone resets it.
 *     ResumeKerbal is that someone. (Kerbalism keeps accumulated state instead of a
 *     timestamp and offers a real API for this — see KerbalismAdapter.)
 *
 * (DeepFreeze is not a consumption mod: it only detects crew already frozen in a
 * cryopod, who are inert under every LS mod and need no freeze of ours.)
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

        /// <summary>Units of each life-support resource one kerbal burns per in-game day.
        /// Empty when the mod's rates can't be read (endurance then reports "unknown").</summary>
        IDictionary<string, double> DailyNeedPerKerbal { get; }

        /// <summary>Days one kerbal survives on the given onboard resource amounts
        /// (keyed by resource name). Returns 0 when unknown/empty.</summary>
        double EnduranceDaysPerKerbal(IDictionary<string, double> amounts);

        /// <summary>Stop this mod consuming/penalising a kerbal — the mod-specific half of
        /// an emergency freeze. No-op when the mod has no such hook.</summary>
        void SuspendKerbal(string kerbalName);

        /// <summary>Hand a thawed kerbal back to this mod with a clean slate: no back-dated
        /// hunger, no accumulated deficit. Called on every thaw path.</summary>
        void ResumeKerbal(string kerbalName);
    }
}
