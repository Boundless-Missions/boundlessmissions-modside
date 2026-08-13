/*
 * KerbalismAdapter.cs – Kerbalism (assembly "Kerbalism"). DETECT-ONLY.
 *
 * Kerbalism runs its own background sim, but rescue immunity no longer needs to pause it:
 * RescueImmunityGuardian removes the stranded crew from the simulation entirely (stasis),
 * which defeats every LS mod including Kerbalism. This adapter only reports IsInstalled +
 * the LS resources it adds, so a craft can be tagged "built with Kerbalism".
 *
 * Endurance is left unknown (0) on purpose: Kerbalism's rates are profile-driven and
 * vary per install, so a guessed number would mislead. Detection + tagging only.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class KerbalismAdapter : ILifeSupportAdapter
    {
        public string ModKey => "kerbalism";
        public string DisplayName => "Kerbalism";
        public bool IsConsumptionLs => true;
        public string[] ResourceNames => new[] { "Food", "Water", "Oxygen" };

        private bool _checked;
        private bool _ok;

        public bool IsInstalled
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    // Kerbalism ships as KerbalismBootstrap.dll, which loads the real
                    // "Kerbalism" assembly from a .kbin during KSP startup. Match either so
                    // detection is robust regardless of when our check runs.
                    _ok = LsReflect.HasAssembly("Kerbalism") || LsReflect.HasAssembly("KerbalismBootstrap");
                    if (_ok) Debug.Log("[GeneKerman] Kerbalism detected.");
                }
                return _ok;
            }
        }

        // Profile-driven rates vary per install; report unknown rather than guess.
        public double EnduranceDaysPerKerbal(IDictionary<string, double> amounts) => 0;
    }
}
