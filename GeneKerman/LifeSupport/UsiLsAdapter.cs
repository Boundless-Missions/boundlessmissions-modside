/*
 * UsiLsAdapter.cs – USI Life Support (assembly "USILifeSupport").  [CONFIRMED]
 *
 * Verified against the installed USILifeSupport.dll (see INTEGRATION_NOTES.md):
 * LifeSupport.LifeSupportManager.Instance, with IsKerbalTracked / FetchKerbal /
 * TrackKerbal / UntrackKerbal, and a LifeSupportStatus record of UT stamps.
 *
 * Freeze:  UntrackKerbal — with no record at all, USI's background pass has nothing to
 *          age, so a frozen kerbal cannot starve however long the wreck drifts.
 * Thaw:    FetchKerbal re-creates the record (USI stamps a new one at "now"), then every
 *          stamp is pinned to now and written back. Both halves matter: an untracked
 *          kerbal gets a fresh record, and a kerbal USI re-tracked behind our back while
 *          the wreck was loaded gets its back-dated "last meal" erased instead of dying
 *          the instant it is thawed.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public class UsiLsAdapter : LsAdapterBase
    {
        public override string ModKey => "usi";
        public override string DisplayName => "USI-LS";
        public override string[] ResourceNames => new[] { "Supplies" };

        // USI default: a kerbal consumes Supplies at 0.00005 units/second.
        private const double SuppliesPerSecond = 0.00005;

        public override IDictionary<string, double> DailyNeedPerKerbal =>
            new Dictionary<string, double>
            {
                { "Supplies", SuppliesPerSecond * LsEndurance.SecondsPerDay() },
            };

        // The UT stamps USI ages a kerbal from. Pinning all of them to "now" is what
        // makes a thaw survivable; SetMember quietly skips any this USI build lacks.
        private static readonly string[] TimeStamps =
        {
            "LastMeal", "LastEC", "LastUpdate", "LastAtHome", "LastSOIChange", "TimeEnteredVessel",
        };

        private static System.Type ManagerType =>
            LsReflect.FindType("USILifeSupport", "LifeSupport.LifeSupportManager");

        private static object Manager => LsReflect.GetStatic(ManagerType, "Instance");

        protected override bool Detect() => Manager != null;

        public override void SuspendKerbal(string kerbalName)
        {
            if (!IsInstalled || string.IsNullOrEmpty(kerbalName)) return;
            object mgr = Manager;
            if (mgr == null) return;
            LsReflect.Invoke(mgr, "UntrackKerbal", kerbalName);
        }

        public override void ResumeKerbal(string kerbalName)
        {
            if (!IsInstalled || string.IsNullOrEmpty(kerbalName)) return;
            object mgr = Manager;
            ProtoCrewMember pcm = LsReflect.FindCrew(kerbalName);
            if (mgr == null || pcm == null) return;

            object status = LsReflect.Invoke(mgr, "FetchKerbal", pcm);
            if (status == null) return;

            double now = LsReflect.Now();
            foreach (var stamp in TimeStamps) LsReflect.SetMember(status, stamp, now);
            LsReflect.Invoke(mgr, "TrackKerbal", status);
            Debug.Log($"[GeneKerman] USI-LS: reset supply tracking for {kerbalName}.");
        }
    }
}
