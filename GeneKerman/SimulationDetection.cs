/*
 * SimulationDetection.cs – Answers one question: is the current flight a
 * SIMULATION rather than a real launch?
 *
 * RSS/RO installs (and plenty of others) fly missions twice: once in a
 * simulation to test the craft, then for real. Two mods provide that:
 * RP-1/KCT's "Simulate" button and KRASH. A simulated flight is free —
 * no build time, no funds, instant launch, and the whole thing reverts when
 * it ends — so a contract submitted from inside one claims a flight that
 * never happened. Telemetry cannot tell (the simulated vessel really is
 * where it says it is), and the flight-state watchdog cannot either (the
 * physics inside a sim is perfectly continuous). Only the sim mod itself
 * knows, and both are polite enough to say so in a readable flag.
 *
 * All reflection, defensive-list style (see LsReflect / CheatWatchdog's
 * VesselMover probe): the build references neither mod, an absent mod is a
 * no-op, and a fork that renamed things degrades to "installed but not
 * understood" — a missed sim still faces the issuer's review, while a false
 * positive would block an honest submission with no appeal.
 *
 * Probed shapes, in the order the mods shipped them:
 *   - KRASH:            KRASHShelter.persistent.shelterSimulationActive
 *   - KCT (RP-1 fork):  KCTGameStates.IsSimulatedFlight            (static)
 *   - RP-1 v3+ (RP0):   SpaceCenterManagement.Instance.IsSimulatedFlight
 *
 * Consumers: CheatWatchdog taints the active vessel while a sim is running
 * (so the server's cheat gate refuses it even from an out-of-date client),
 * and SubmissionSession refuses a flight submit outright with a message that
 * says what to do instead — fly it for real.
 */

using System;
using System.Collections.Generic;
using System.Reflection;

namespace GeneKerman
{
    public static class SimulationDetection
    {
        private class Probe
        {
            public string Tool;        // player-facing name for the message
            public MemberInfo Holder;  // static member yielding the object the flag lives on; null = flag is static
            public MemberInfo Flag;    // bool member (static when Holder is null, instance otherwise)
        }

        private const BindingFlags STATIC = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags INST   = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Resolved once — the loaded-assembly set never changes mid-game. An empty
        // list means no recognised sim mod is installed and every poll is free.
        private static List<Probe> probes;

        /// <summary>True while a recognised simulation (KRASH, RP-1/KCT) is running,
        /// with the mod's name for the player-facing refusal. Never throws; absent
        /// or unrecognised mods simply answer false.</summary>
        public static bool SimulationActive(out string tool)
        {
            tool = null;
            Resolve();
            foreach (var p in probes)
            {
                try
                {
                    object target = null;
                    if (p.Holder != null)
                    {
                        target = Read(p.Holder, null);
                        // Mod present but not initialised yet (main menu, early scene
                        // load) — then nothing is simulating.
                        if (target == null) continue;
                    }
                    if (Read(p.Flag, target) is bool b && b) { tool = p.Tool; return true; }
                }
                catch { /* one broken probe must not silence the others */ }
            }
            return false;
        }

        private static void Resolve()
        {
            if (probes != null) return;
            probes = new List<Probe>();
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    string an = la?.name?.ToLowerInvariant() ?? "";
                    bool krash = an.Contains("krash");
                    bool kct = an.Contains("kerbalconstructiontime")
                            || an.Contains("rp0") || an.Contains("rp-0")
                            || an.Contains("rp1") || an.Contains("rp-1");
                    if (!krash && !kct) continue;

                    Type[] types;
                    try { types = la.assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types ?? Type.EmptyTypes; }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;

                        if (krash && t.Name == "KRASHShelter")
                        {
                            // The flag lives on the persistent-state object, reached
                            // through a static field on the shelter.
                            var holder = FindMember(t, null, STATIC, "persistent", "Persistent");
                            var holderType = MemberType(holder);
                            var flag = holderType == null ? null
                                : FindMember(holderType, typeof(bool), INST,
                                    "shelterSimulationActive", "simulationActive", "SimulationActive");
                            if (holder != null && flag != null)
                                probes.Add(new Probe { Tool = "KRASH", Holder = holder, Flag = flag });
                        }
                        else if (kct && (t.Name == "KCTGameStates"
                                      || t.Name == "SpaceCenterManagement"
                                      || t.Name == "KerbalConstructionTimeData"))
                        {
                            string tool = (an.Contains("rp0") || an.Contains("rp-0") ||
                                           an.Contains("rp1") || an.Contains("rp-1"))
                                ? "RP-1 (KCT)" : "Kerbal Construction Time";

                            // Older forks: a plain static bool on the states class.
                            var flag = FindMember(t, typeof(bool), STATIC,
                                "IsSimulatedFlight", "isSimulatedFlight", "IsSimulationActive");
                            if (flag != null)
                            {
                                probes.Add(new Probe { Tool = tool, Flag = flag });
                                continue;
                            }
                            // RP-1 v3+: an instance bool behind a static Instance.
                            var holder = FindMember(t, t, STATIC, "Instance", "instance", "fetch");
                            var instFlag = FindMember(t, typeof(bool), INST,
                                "IsSimulatedFlight", "isSimulatedFlight", "IsSimulationActive");
                            if (holder != null && instFlag != null)
                                probes.Add(new Probe { Tool = tool, Holder = holder, Flag = instFlag });
                        }
                    }
                }
            }
            catch { /* keep whatever resolved */ }
        }

        /// <summary>Field or property of the wanted type (null = any type), first
        /// name that matches wins. Mirrors CheatWatchdog.FindMember but with the
        /// binding flags as a parameter, since these probes mix static and instance.</summary>
        private static MemberInfo FindMember(Type t, Type wanted, BindingFlags flags, params string[] names)
        {
            if (t == null) return null;
            foreach (var n in names)
            {
                var p = t.GetProperty(n, flags);
                if (p != null && (wanted == null || wanted.IsAssignableFrom(p.PropertyType))) return p;
                var f = t.GetField(n, flags);
                if (f != null && (wanted == null || wanted.IsAssignableFrom(f.FieldType))) return f;
            }
            return null;
        }

        private static Type MemberType(MemberInfo m)
        {
            if (m is PropertyInfo p) return p.PropertyType;
            if (m is FieldInfo f) return f.FieldType;
            return null;
        }

        private static object Read(MemberInfo m, object target)
        {
            try
            {
                if (m is PropertyInfo p) return p.GetValue(target, null);
                if (m is FieldInfo f) return f.GetValue(target);
            }
            catch { }
            return null;
        }
    }
}
