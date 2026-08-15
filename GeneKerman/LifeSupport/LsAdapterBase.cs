/*
 * LsAdapterBase.cs – Shared plumbing for the life-support adapters.
 *
 * Every adapter detects its mod exactly once and caches the answer, and every
 * consumption adapter derives endurance the same way: the resource that runs out first
 * sets it. Declaring per-day rates once (DailyNeedPerKerbal) means the same numbers feed
 * both the endurance display and the emergency ration kit — they can't drift apart.
 *
 * Suspend/Resume default to no-ops so an adapter only overrides what its mod can
 * actually do.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public abstract class LsAdapterBase : ILifeSupportAdapter
    {
        public abstract string ModKey { get; }
        public abstract string DisplayName { get; }
        public abstract string[] ResourceNames { get; }

        public virtual bool IsConsumptionLs => true;

        /// <summary>Empty by default — an adapter that can't read its mod's rates reports
        /// unknown endurance rather than guessing.</summary>
        public virtual IDictionary<string, double> DailyNeedPerKerbal => EmptyNeeds;

        protected static readonly IDictionary<string, double> EmptyNeeds =
            new Dictionary<string, double>();

        private bool _checked;
        private bool _installed;

        /// <summary>Resolve the mod's assembly/members. Called once, lazily.</summary>
        protected abstract bool Detect();

        public bool IsInstalled
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    try { _installed = Detect(); }
                    catch (System.Exception ex)
                    {
                        _installed = false;
                        Debug.LogWarning($"[GeneKerman] {DisplayName} detection failed: {ex.Message}");
                    }
                    if (_installed) Debug.Log($"[GeneKerman] {DisplayName} detected.");
                }
                return _installed;
            }
        }

        /// <summary>Endurance is set by the limiting resource: the smallest
        /// amount ÷ daily-need across everything the craft actually carries. Resources the
        /// craft carries none of are ignored rather than counted as zero days — the scan
        /// only reaches here once at least one is aboard, and a pod that carries food but
        /// no water is still better described by its food than by a flat "0 days".</summary>
        public virtual double EnduranceDaysPerKerbal(IDictionary<string, double> amounts)
        {
            if (amounts == null) return 0;
            var needs = DailyNeedPerKerbal;
            if (needs == null || needs.Count == 0) return 0;

            double best = double.PositiveInfinity;
            foreach (var kvp in needs)
            {
                if (kvp.Value <= 0) continue;
                double amount;
                if (!amounts.TryGetValue(kvp.Key, out amount) || amount <= 0) continue;
                double days = amount / kvp.Value;
                if (days < best) best = days;
            }
            return double.IsInfinity(best) ? 0 : best;
        }

        public virtual void SuspendKerbal(string kerbalName) { }

        public virtual void ResumeKerbal(string kerbalName) { }
    }
}
