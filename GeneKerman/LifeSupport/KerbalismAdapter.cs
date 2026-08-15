/*
 * KerbalismAdapter.cs – Kerbalism (assembly "Kerbalism", shipped via KerbalismBootstrap).
 *
 * CONFIRMED against the installed Kerbalism.dll:
 *
 *   • KERBALISM.API.DisableKerbal(string name, bool disabled) — Kerbalism's own supported
 *     hook for exactly this situation (it is what the DeepFreeze integration uses).
 *     KERBALISM.Rule.Execute skips any kerbal whose KerbalData.disabled is set, so a
 *     disabled kerbal consumes nothing and accumulates no problem, even under warp.
 *   • KERBALISM.DB.ContainsKerbal / DB.Kerbal(name) → KerbalData.rules :
 *     Dictionary<string, RuleData>, each RuleData carrying problem + time_since. Kerbalism
 *     stores accumulated deficit rather than a "last fed" timestamp, so a thaw clears the
 *     accumulation instead of resetting a clock.
 *   • KERBALISM.Profile.rules : List<Rule>, each with input / rate / interval — the live
 *     profile's real consumption rates. Reading them means endurance under Kerbalism is
 *     measured from the player's actual profile instead of reported as unknown.
 *
 * Everything degrades to a no-op if a member moves: freeze then rests on stasis alone
 * (crew removed from the simulation), which Kerbalism cannot see through either.
 */

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public class KerbalismAdapter : LsAdapterBase
    {
        public override string ModKey => "kerbalism";
        public override string DisplayName => "Kerbalism";

        /// <summary>The profile's own consumables when it can be read, so a non-default
        /// profile is tagged by what it actually burns; the stock trio otherwise.</summary>
        public override string[] ResourceNames
        {
            get
            {
                var needs = DailyNeedPerKerbal;
                return needs.Count > 0 ? needs.Keys.ToArray() : new[] { "Food", "Water", "Oxygen" };
            }
        }

        // Kerbalism ships as KerbalismBootstrap.dll, which side-loads the real "Kerbalism"
        // assembly from a .kbin during startup — either name may carry the types.
        private static readonly string[] Assemblies = { "Kerbalism", "KerbalismBootstrap" };

        private IDictionary<string, double> _needs;

        protected override bool Detect() =>
            LsReflect.HasAssembly("Kerbalism") || LsReflect.HasAssembly("KerbalismBootstrap");

        /// <summary>Per-day rates read from the live profile's rules. A rule with an
        /// interval consumes `rate` units per interval (2 meals a day); one without is a
        /// continuous per-second rate. ElectricCharge is skipped — it's generated, not
        /// stowed, so counting it would make every craft look like it had hours to live.</summary>
        public override IDictionary<string, double> DailyNeedPerKerbal
        {
            get
            {
                if (_needs != null) return _needs;
                if (!IsInstalled) return EmptyNeeds;

                var needs = new Dictionary<string, double>();
                try
                {
                    var profile = LsReflect.FindTypeAny("KERBALISM.Profile", Assemblies);
                    var rules = LsReflect.GetStatic(profile, "rules") as System.Collections.IEnumerable;
                    if (rules == null) return EmptyNeeds;

                    double day = LsEndurance.SecondsPerDay();
                    foreach (object rule in rules)
                    {
                        string input = LsReflect.GetMember(rule, "input") as string;
                        if (string.IsNullOrEmpty(input) || input == "ElectricCharge") continue;

                        double rate = ToDouble(LsReflect.GetMember(rule, "rate"));
                        double interval = ToDouble(LsReflect.GetMember(rule, "interval"));
                        if (rate <= 0) continue;

                        double perSecond = interval > 0 ? rate / interval : rate;
                        double perDay = perSecond * day;
                        if (perDay <= 0) continue;

                        // Several rules can share one resource; they stack.
                        double existing;
                        needs.TryGetValue(input, out existing);
                        needs[input] = existing + perDay;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] Kerbalism: could not read profile rules: {ex.Message}");
                    return EmptyNeeds;
                }

                if (needs.Count > 0)
                {
                    _needs = needs;
                    Debug.Log($"[GeneKerman] Kerbalism: profile needs/kerbal/day — " +
                              $"{string.Join(", ", needs.Select(kvp => $"{kvp.Key} {kvp.Value:F3}"))}.");
                    return _needs;
                }
                return EmptyNeeds;
            }
        }

        public override void SuspendKerbal(string kerbalName) => SetDisabled(kerbalName, true);

        public override void ResumeKerbal(string kerbalName)
        {
            if (!SetDisabled(kerbalName, false)) return;
            ClearAccumulatedProblems(kerbalName);
            Debug.Log($"[GeneKerman] Kerbalism: re-enabled {kerbalName} with cleared rule state.");
        }

        private bool SetDisabled(string kerbalName, bool disabled)
        {
            if (!IsInstalled || string.IsNullOrEmpty(kerbalName)) return false;
            var api = LsReflect.FindTypeAny("KERBALISM.API", Assemblies);
            if (api == null) return false;
            LsReflect.InvokeStatic(api, "DisableKerbal", kerbalName, disabled);
            return true;
        }

        /// <summary>Zero the deficit Kerbalism accumulated against every rule for this
        /// kerbal, so a thawed kerbal isn't one tick from a rule's fatal threshold.</summary>
        private void ClearAccumulatedProblems(string kerbalName)
        {
            var db = LsReflect.FindTypeAny("KERBALISM.DB", Assemblies);
            if (db == null) return;
            object contains = LsReflect.InvokeStatic(db, "ContainsKerbal", kerbalName);
            if (!(contains is bool has) || !has) return;

            object kerbalData = LsReflect.InvokeStatic(db, "Kerbal", kerbalName);
            object rules = LsReflect.GetMember(kerbalData, "rules");
            foreach (object ruleData in LsReflect.Values(rules))
            {
                LsReflect.SetMember(ruleData, "problem", 0d);
                LsReflect.SetMember(ruleData, "time_since", 0d);
            }
        }

        private static double ToDouble(object value)
        {
            try { return value == null ? 0d : System.Convert.ToDouble(value); }
            catch { return 0d; }
        }
    }
}
