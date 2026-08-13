/*
 * SnacksAdapter.cs – Snacks! continued (assembly "SnacksUtils", namespace "Snacks").
 *
 * Detection + endurance only (CONFIRMED against the installed SnacksUtils.dll — note the
 * assembly is "SnacksUtils", not "Snacks"; Snacks.SnacksScenario.Instance exists).
 * Rescue immunity is handled by RescueImmunityGuardian (stasis), not here.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class SnacksAdapter : ILifeSupportAdapter
    {
        public string ModKey => "snacks";
        public string DisplayName => "Snacks";
        public bool IsConsumptionLs => true;
        public string[] ResourceNames => new[] { "Snacks" };

        // Snacks default: 1 snack/meal × 3 meals/day = 3 snacks per kerbal per day.
        private const double SnacksPerDay = 3.0;

        private bool _checked;
        private bool _ok;

        public bool IsInstalled
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    var t = LsReflect.FindType("SnacksUtils", "Snacks.SnacksScenario");
                    _ok = LsReflect.GetStatic(t, "Instance") != null;
                    if (_ok) Debug.Log("[GeneKerman] Snacks detected.");
                }
                return _ok;
            }
        }

        public double EnduranceDaysPerKerbal(IDictionary<string, double> amounts)
        {
            if (amounts == null) return 0;
            double snacks;
            if (!amounts.TryGetValue("Snacks", out snacks) || snacks <= 0) return 0;
            return SnacksPerDay > 0 ? snacks / SnacksPerDay : 0;
        }
    }
}
