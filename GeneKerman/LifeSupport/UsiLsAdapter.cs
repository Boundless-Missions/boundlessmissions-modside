/*
 * UsiLsAdapter.cs – USI Life Support (assembly "USILifeSupport").  [CONFIRMED]
 *
 * Detection + endurance only. Verified against the installed USILifeSupport.dll
 * (see INTEGRATION_NOTES.md): LifeSupport.LifeSupportManager.Instance exists. Rescue
 * immunity is handled by RescueImmunityGuardian (stasis), not here.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class UsiLsAdapter : ILifeSupportAdapter
    {
        public string ModKey => "usi";
        public string DisplayName => "USI-LS";
        public bool IsConsumptionLs => true;
        public string[] ResourceNames => new[] { "Supplies" };

        // USI default: a kerbal consumes Supplies at 0.00005 units/second. Centralised
        // here (per INTEGRATION_NOTES) so endurance tuning lives in one place.
        private const double SuppliesPerSecond = 0.00005;

        private bool _checked;
        private bool _ok;

        public bool IsInstalled
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    var mgrType = LsReflect.FindType("USILifeSupport", "LifeSupport.LifeSupportManager");
                    _ok = LsReflect.GetStatic(mgrType, "Instance") != null;
                    if (_ok) Debug.Log("[GeneKerman] USI-LS detected.");
                }
                return _ok;
            }
        }

        public double EnduranceDaysPerKerbal(IDictionary<string, double> amounts)
        {
            if (amounts == null) return 0;
            double supplies;
            if (!amounts.TryGetValue("Supplies", out supplies) || supplies <= 0) return 0;
            double secondsPerDay = LsEndurance.SecondsPerDay();
            double perDay = SuppliesPerSecond * secondsPerDay;
            return perDay > 0 ? supplies / perDay : 0;
        }
    }
}
