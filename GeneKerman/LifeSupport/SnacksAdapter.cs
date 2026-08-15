/*
 * SnacksAdapter.cs – Snacks! continued (assembly "SnacksUtils", namespace "Snacks").
 *
 * CONFIRMED against the installed SnacksUtils.dll (the assembly is "SnacksUtils", not
 * "Snacks"): Snacks.SnacksScenario.Instance → AstronautData GetAstronautData(ProtoCrewMember),
 * whose lastUpdated is the UT Snacks measures the next meal from.
 *
 * AstronautData is a reference type held by the scenario, so writing lastUpdated on the
 * fetched instance is the update — there's nothing to store back. Freeze and thaw both
 * pin it to now, for the same reason as TAC.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class SnacksAdapter : LsAdapterBase
    {
        public override string ModKey => "snacks";
        public override string DisplayName => "Snacks";
        public override string[] ResourceNames => new[] { "Snacks" };

        // Snacks default: 1 snack/meal × 3 meals/day.
        private const double SnacksPerDay = 3.0;

        public override IDictionary<string, double> DailyNeedPerKerbal =>
            new Dictionary<string, double> { { "Snacks", SnacksPerDay } };

        private static object Scenario =>
            LsReflect.GetStatic(LsReflect.FindType("SnacksUtils", "Snacks.SnacksScenario"), "Instance");

        protected override bool Detect() => Scenario != null;

        public override void SuspendKerbal(string kerbalName) => PinToNow(kerbalName);

        public override void ResumeKerbal(string kerbalName)
        {
            if (PinToNow(kerbalName))
                Debug.Log($"[GeneKerman] Snacks: reset meal clock for {kerbalName}.");
        }

        private bool PinToNow(string kerbalName)
        {
            if (!IsInstalled || string.IsNullOrEmpty(kerbalName)) return false;
            ProtoCrewMember pcm = LsReflect.FindCrew(kerbalName);
            if (pcm == null) return false;

            object data = LsReflect.Invoke(Scenario, "GetAstronautData", pcm);
            if (data == null) return false;
            return LsReflect.SetMember(data, "lastUpdated", LsReflect.Now());
        }
    }
}
