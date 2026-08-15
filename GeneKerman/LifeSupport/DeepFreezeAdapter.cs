/*
 * DeepFreezeAdapter.cs – DeepFreeze (assembly "DeepFreeze", author REPOSoftTech).
 *
 * CONFIRMED against the installed DeepFreeze.dll (see INTEGRATION_NOTES.md). The key
 * finding: freezing is **part-bound** — DF.DeepFreezer is a PartModule and DF.KerbalInfo
 * references a freezer part/seat (partID, seatIdx, vesselID). There is NO supported way
 * to freeze a kerbal that isn't sitting in a DeepFreezer cryopod, so we cannot put a
 * stranded rescue crew into a real cryopod on demand — which is why our emergency freeze
 * is stasis + per-mod suspension instead (see RescueImmunityGuardian and LsFreeze).
 * (DF.DFWrapper is a copy-into-your-mod helper in namespace MyPlugin_DFWrapper, not a
 * stable reflection target.)
 *
 * What we CAN do is *detect* already-frozen crew via:
 *   DF.DeepFreeze.Instance.FrozenKerbals : Dictionary<string, DF.KerbalInfo>  (keyed by name)
 *
 * Frozen kerbals consume no life support under ANY mod (including Kerbalism), so a rescue
 * wreck whose crew are frozen in a cryopod is already immune — the guardian leaves them
 * seated and the player thaws them at the pod on arrival. This adapter therefore never
 * freezes/thaws anyone; it only reports frozen state.
 */

namespace GeneKerman
{
    public class DeepFreezeAdapter : LsAdapterBase
    {
        public override string ModKey => "deepfreeze";
        public override string DisplayName => "DeepFreeze";
        public override bool IsConsumptionLs => false;
        public override string[] ResourceNames => new string[0];

        protected override bool Detect() =>
            LsReflect.FindType("DeepFreeze", "DF.DeepFreeze") != null;

        /// <summary>True if DeepFreeze currently has this kerbal frozen (cryopod). Frozen
        /// crew consume no life support, so they're immune even under Kerbalism.</summary>
        public bool IsKerbalFrozen(string kerbalName)
        {
            if (!IsInstalled || string.IsNullOrEmpty(kerbalName)) return false;
            var dfType = LsReflect.FindType("DeepFreeze", "DF.DeepFreeze");
            object instance = LsReflect.GetStatic(dfType, "Instance");
            object frozen = LsReflect.GetMember(instance, "FrozenKerbals");
            return LsReflect.ContainsKey(frozen, kerbalName);
        }
    }
}
