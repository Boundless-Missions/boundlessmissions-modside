/*
 * TacLsAdapter.cs – TAC Life Support (assembly "TacLifeSupport", namespace "Tac").
 *
 * Detection + endurance only (CONFIRMED against the installed TacLifeSupport.dll —
 * Tac.TacLifeSupport.Instance exists). Rescue immunity is handled by
 * RescueImmunityGuardian (stasis), not here.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class TacLsAdapter : ILifeSupportAdapter
    {
        public string ModKey => "tac";
        public string DisplayName => "TAC-LS";
        public bool IsConsumptionLs => true;
        public string[] ResourceNames => new[] { "Food", "Water", "Oxygen" };

        // Canonical TAC defaults, units/second per kerbal (independent of day length).
        private const double FoodPerSecond = 0.000016927083;
        private const double WaterPerSecond = 0.000011188078;
        private const double OxygenPerSecond = 0.001713537562;

        private bool _checked;
        private bool _ok;

        public bool IsInstalled
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _ok = LsReflect.GetStatic(
                        LsReflect.FindType("TacLifeSupport", "Tac.TacLifeSupport"), "Instance") != null;
                    if (_ok) Debug.Log("[GeneKerman] TAC-LS detected.");
                }
                return _ok;
            }
        }

        public double EnduranceDaysPerKerbal(IDictionary<string, double> amounts)
        {
            if (amounts == null) return 0;
            double best = double.PositiveInfinity;
            best = Limit(best, amounts, "Food", FoodPerSecond);
            best = Limit(best, amounts, "Water", WaterPerSecond);
            best = Limit(best, amounts, "Oxygen", OxygenPerSecond);
            if (double.IsInfinity(best)) return 0;
            return best / LsEndurance.SecondsPerDay();
        }

        // The limiting resource sets endurance: min seconds across the three.
        private static double Limit(double best, IDictionary<string, double> amounts, string res, double perSec)
        {
            double amt;
            if (perSec <= 0 || !amounts.TryGetValue(res, out amt) || amt <= 0) return best;
            double secs = amt / perSec;
            return secs < best ? secs : best;
        }
    }
}
