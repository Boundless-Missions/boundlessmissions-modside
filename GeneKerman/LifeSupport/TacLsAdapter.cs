/*
 * TacLsAdapter.cs – TAC Life Support (assembly "TacLifeSupport", namespace "Tac").
 *
 * CONFIRMED against the installed TacLifeSupport.dll (see INTEGRATION_NOTES.md):
 *   Tac.TacLifeSupport.Instance → gameSettings → knownCrew, a KSP
 *   DictionaryValueList<string, Tac.CrewMemberInfo> (not a plain IDictionary — read it
 *   through LsReflect.GetByKey), whose entries carry lastUpdate / lastFood / lastWater /
 *   lastO2 (not "lastOxygen") / lastEC.
 *
 * TAC decides how starved a kerbal is purely from the gap between those stamps and now,
 * so both freeze and thaw do the same thing: pin them to now. On freeze that neutralises
 * the record while the crew are out of the simulation; on thaw it erases the drift, which
 * is the difference between a rescued kerbal and one that dies as it boards.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class TacLsAdapter : LsAdapterBase
    {
        public override string ModKey => "tac";
        public override string DisplayName => "TAC-LS";
        public override string[] ResourceNames => new[] { "Food", "Water", "Oxygen" };

        // Canonical TAC defaults, units/second per kerbal (independent of day length).
        private const double FoodPerSecond = 0.000016927083;
        private const double WaterPerSecond = 0.000011188078;
        private const double OxygenPerSecond = 0.001713537562;

        public override IDictionary<string, double> DailyNeedPerKerbal
        {
            get
            {
                double day = LsEndurance.SecondsPerDay();
                return new Dictionary<string, double>
                {
                    { "Food", FoodPerSecond * day },
                    { "Water", WaterPerSecond * day },
                    { "Oxygen", OxygenPerSecond * day },
                };
            }
        }

        private static readonly string[] TimeStamps =
        {
            "lastUpdate", "lastFood", "lastWater", "lastO2", "lastEC",
        };

        private static object LifeSupport =>
            LsReflect.GetStatic(LsReflect.FindType("TacLifeSupport", "Tac.TacLifeSupport"), "Instance");

        private static object KnownCrew
        {
            get
            {
                object settings = LsReflect.GetMember(LifeSupport, "gameSettings");
                return LsReflect.GetMember(settings, "knownCrew");
            }
        }

        protected override bool Detect() => LifeSupport != null;

        public override void SuspendKerbal(string kerbalName) => PinToNow(kerbalName);

        public override void ResumeKerbal(string kerbalName)
        {
            if (PinToNow(kerbalName))
                Debug.Log($"[GeneKerman] TAC-LS: reset consumption stamps for {kerbalName}.");
        }

        /// <summary>Pin a kerbal's TAC stamps to the current UT. False when TAC has no
        /// record for them — which is the good case: TAC then starts fresh on its own.</summary>
        private bool PinToNow(string kerbalName)
        {
            if (!IsInstalled || string.IsNullOrEmpty(kerbalName)) return false;
            object info = LsReflect.GetByKey(KnownCrew, kerbalName);
            if (info == null) return false;

            double now = LsReflect.Now();
            foreach (var stamp in TimeStamps) LsReflect.SetMember(info, stamp, now);
            return true;
        }
    }
}
