/*
 * PartAliases.cs – Swap in an equivalent installed part when a shared craft asks
 * for one the recipient doesn't have.
 *
 * CkanGenerator answers "which MOD is missing?"; this answers the narrower and more
 * common question "this exact PART isn't here, but the same thing is, under another
 * name". The case that motivated it: Making History's `InflatableAirlock` and
 * ReStock+'s `restock-airlock-1` are the same object — ReStock retextures the DLC part
 * with the very asset ReStock+ builds its DLC-free stand-in from, so they are visually
 * identical and stat-identical but for 0.1 t. A craft built with the DLC part simply
 * will not load for someone who has ReStock+ and no Making History, and CkanGenerator
 * stays quiet because they aren't missing a *mod*: SquadExpansion is treated as stock,
 * and their ReStock+ folder is right there.
 *
 * ReStock+ marks every one of these stand-ins `MHReplacement = True` and hides them
 * (TechHidden + `category = none`, see ReStockPlus/Patches/MakingHistoryPartHiding.cfg)
 * when the DLC *is* installed — which breaks the same craft from the other side, since
 * a career/science save treats a hidden part as unpurchased and refuses to launch. So
 * substitution runs in both directions and "usable" means loaded AND not hidden.
 *
 * The table below was derived mechanically rather than by eye: two parts are listed as
 * the same thing only when ReStock's DLC patch and the ReStock+ stand-in resolve to the
 * SAME `ReStock/Assets/...` model. Shared art is proof of shared geometry — identical
 * attach nodes, identical size — which is what makes a swap safe to do silently.
 *
 * Shared art is NOT proof of shared balance, and two pairs prove it: ReStock+ reuses
 * the Wolfhound's and Skiff's bells for much smaller engines. Those live in LookAlikes
 * and are only ever *reported*, never substituted — quietly turning a 375 kN stage into
 * a 110 kN one is worse than a craft that refuses to load.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GeneKerman
{
    public static class PartAliases
    {
        /// <summary>A set of part names that are the same object under different names.
        /// Any one may stand in for any other.</summary>
        public class Group
        {
            public string label;    // human name, for the swap report
            public string[] names;  // interchangeable part names
            public string note;     // non-null when the swap is not stat-for-stat
        }

        /// <summary>Two parts that share art but are deliberately different hardware.
        /// Reported so the player knows why the look-alike in their catalogue is not
        /// the part the craft wanted; never substituted.</summary>
        private class LookAlike
        {
            public string missing;   // the part the craft asks for
            public string lookalike; // the part that resembles it
            public string warning;   // why it is not a stand-in
        }

        private static Group G(string label, string a, string b, string note)
        {
            return new Group { label = label, names = new[] { a, b }, note = note };
        }

        // Making History ↔ ReStock+. Every pair verified by shared ReStock art asset;
        // all are stat-for-stat except the three carrying a note. ReStock+ flags one more
        // part `MHReplacement` that is deliberately absent here: restock-engineplate-125-1
        // has no DLC counterpart left (all five MH engine plates are already paired), so
        // there is nothing it could stand in for.
        private static readonly Group[] Groups =
        {
            G("inflatable airlock",             "InflatableAirlock",        "restock-airlock-1",                    "ReStock+'s weighs 0.1 t more"),
            G("1.875 m decoupler",              "Decoupler_1p5",            "restock-decoupler-1875-1",             null),
            G("1.875 m truss decoupler",        "Size1p5_Strut_Decoupler",  "restock-decoupler-1875-truss-1",       "ReStock+'s weighs 0.01 t less"),
            G("5 m decoupler",                  "Decoupler_4",              "restock-decoupler-5-1",                null),
            G("1.875 m separator",              "Separator_1p5",            "restock-separator-1875-1",             null),
            G("5 m separator",                  "Separator_4",              "restock-separator-5-1",                null),

            G("KE-1 'Mastodon' engine",         "LiquidEngineKE-1",         "restock-engine-galleon-1",             null),
            G("RV-1 vernier engine",            "LiquidEngineRV-1",         "restock-engine-panda-1",               null),
            G("RK-7 'Kodiak' engine",           "LiquidEngineRK-7",         "restock-engine-ursa-1",                null),
            G("THK 'Pollux' booster",           "Pollux",                   "restock-srb-castor-1",                 null),

            G("1.25 m engine plate",            "EnginePlate5",             "restock-engineplate-125-2",            null),
            G("1.875 m engine plate",           "EnginePlate1p5",           "restock-engineplate-1875-1",           null),
            G("2.5 m engine plate",             "EnginePlate2",             "restock-engineplate-25-1",             null),
            G("3.75 m engine plate",            "EnginePlate3",             "restock-engineplate-375-1",            null),
            G("5 m engine plate",               "EnginePlate4",             "restock-engineplate-5-1",              null),

            G("1.875 m LFO tank (long)",        "Size1p5_Tank_04",          "restock-fueltank-1875-1",              null),
            G("1.875 m LFO tank (medium)",      "Size1p5_Tank_03",          "restock-fueltank-1875-2",              null),
            G("1.875 m LFO tank (small)",       "Size1p5_Tank_02",          "restock-fueltank-1875-3",              null),
            G("1.875 m LFO tank (tiny)",        "Size1p5_Tank_01",          "restock-fueltank-1875-4",              null),
            G("1.875 m Soyuz LFO tank",         "Size1p5_Tank_05",          "restock-fueltank-1875-soyuz-1",        null),
            G("5 m LFO tank (long)",            "Size4_Tank_04",            "restock-fueltank-5-1",                 null),
            G("5 m LFO tank (medium)",          "Size4_Tank_03",            "restock-fueltank-5-2",                 null),
            G("5 m LFO tank (short)",           "Size4_Tank_02",            "restock-fueltank-5-3",                 null),
            G("5 m LFO tank (mini)",            "Size4_Tank_01",            "restock-fueltank-5-4",                 null),
            G("1.875 m monopropellant tank",    "Size1p5_Monoprop",         "restock-fuel-tank-rcs-1875-1",         null),
            G("tiny radial monoprop tank",      "monopropMiniSphere",       "restock-fuel-tank-rcs-radial-tiny-1",  null),

            G("1.875 m → 0.625 m adapter",      "Size1p5_Size0_Adapter_01", "restock-fueltank-adapter-1875-0625-1", null),
            G("1.875 m → 1.25 m adapter (long)","Size1p5_Size1_Adapter_01", "restock-fueltank-adapter-1875-125-1",  null),
            G("1.875 m → 1.25 m adapter",       "Size1p5_Size1_Adapter_02", "restock-fueltank-adapter-1875-125-2",  null),
            G("2.5 m → 1.875 m adapter",        "Size1p5_Size2_Adapter_01", "restock-fueltank-adapter-25-1875-1",   null),
            G("5 m → 3.75 m adapter",           "Size3_Size4_Adapter_01",   "restock-fueltank-adapter-375-5-1",     null),
            G("5 m engine mount",               "Size4_EngineAdapter_01",   "restock-fueltank-saturn-engine-1",     null),

            G("Mk2 command pod",                "Mk2Pod",                   "restock-mk2-pod",                      null),
            G("KV-1 'Onion' pod",               "kv1Pod",                   "restock-pod-sphere-1",                 null),
            G("KV-2 'Pea' pod",                 "kv2Pod",                   "restock-pod-sphere-2",                 null),
            G("KV-3 'Pomegranate' pod",         "kv3Pod",                   "restock-pod-sphere-3",                 null),

            G("1.875 m heat shield",            "HeatShield1p5",            "restock-heatshield-1875-1",            null),
            G("1.875 m nose cone",              "Size_1_5_Cone",            "restock-nosecone-1875-2",              null),
            G("5 m nose cone",                  "rocketNoseConeSize4",      "restock-nosecone-5-1",                 null),
            G("1.875 m fairing base",           "fairingSize1p5",           "restock-fairing-base-1875-1",          null),
            G("5 m fairing base",               "fairingSize4",             "restock-fairing-base-5-1",             null),
            G("1.25 m → 0.625 m service module","Size1to0ServiceModule",    "restock-service-module-125-625-1",     null),
            G("1.875 m service module",         "ServiceModule18",          "restock-service-module-1875-1",        null),

            G("1.25 m structural tube",         "Tube1",                    "restock-structural-tube-125-1",        null),
            G("1.875 m structural tube",        "Tube1p5",                  "restock-structural-tube-1875-1",       null),
            G("2.5 m structural tube",          "Tube2",                    "restock-structural-tube-25-1",         null),
            G("3.75 m structural tube",         "Tube3",                    "restock-structural-tube-375-1",        null),
            G("5 m structural tube",            "Tube4",                    "restock-structural-tube-5-1",          null),

            G("folding rover wheel",            "roverWheelM1-F",           "restock-wheel-4",                      "ReStock+'s weighs 0.015 t more"),
        };

        // Same bell, different engine. Listed both ways round so the advice is useful
        // whichever side the recipient is on.
        private static readonly LookAlike[] LookAlikes =
        {
            new LookAlike { missing = "LiquidEngineRE-J10", lookalike = "restock-engine-schnauzer-1",
                warning = "ReStock+'s Schnauzer shares the Wolfhound's bell but is a much smaller engine "
                          + "(110 kN / 0.8 t against 375 kN / 3.3 t), so it is not a stand-in." },
            new LookAlike { missing = "restock-engine-schnauzer-1", lookalike = "LiquidEngineRE-J10",
                warning = "Making History's Wolfhound shares the Schnauzer's bell but is a far bigger engine "
                          + "(375 kN / 3.3 t against 110 kN / 0.8 t), so it is not a stand-in." },
            new LookAlike { missing = "LiquidEngineRE-I2", lookalike = "restock-engine-caravel-1",
                warning = "ReStock+'s Caravel shares the Skiff's bell but is a different engine "
                          + "(510 kN / 2 t against 300 kN / 1.6 t), so it is not a stand-in." },
            new LookAlike { missing = "restock-engine-caravel-1", lookalike = "LiquidEngineRE-I2",
                warning = "Making History's Skiff shares the Caravel's bell but is a different engine "
                          + "(300 kN / 1.6 t against 510 kN / 2 t), so it is not a stand-in." },
        };

        private static Dictionary<string, Group> _byName;

        private static Dictionary<string, Group> ByName
        {
            get
            {
                if (_byName != null) return _byName;
                _byName = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in Groups)
                    foreach (var n in g.names)
                        _byName[n] = g;
                return _byName;
            }
        }

        // A .craft references every part as `<partName>_<craftID>` — in `part`, `link`,
        // `sym`, `attN` and `srfN` lines alike — so rewriting that one token form covers
        // every reference at once. The lookbehind keeps a name from matching inside a
        // longer one (part names may contain '-' and '.').
        private static readonly Regex PartIdRx =
            new Regex(@"(?m)^\s*part\s*=\s*(.+?)_\d+\s*$", RegexOptions.Compiled);
        private static readonly Regex PartNameRx =
            new Regex(@"(?m)^\s*partName\s*=\s*(.+?)\s*$", RegexOptions.Compiled);

        // ── Lookup ───────────────────────────────────────────────────────────

        /// <summary>Whether a part is present AND actually buildable here. A ReStock+
        /// MH stand-in on a DLC install is loaded but hidden (TechHidden, category none);
        /// it flies, but career and science saves treat it as unpurchased and block
        /// launch, so it is not a valid substitution target.</summary>
        private static bool Usable(string partName)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(partName);
            if (ap == null) return false;
            if (ap.TechHidden && ap.category == PartCategories.none) return false;
            return true;
        }

        /// <summary>An installed, buildable stand-in for <paramref name="partName"/>, or
        /// null when the part is fine as-is or nothing equivalent is installed.</summary>
        public static string SubstituteFor(string partName, out Group group)
        {
            group = null;
            if (string.IsNullOrEmpty(partName)) return null;
            if (Usable(partName)) return null; // nothing to fix

            Group g;
            if (!ByName.TryGetValue(partName, out g)) return null;

            foreach (var candidate in g.names)
            {
                if (string.Equals(candidate, partName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Usable(candidate)) { group = g; return candidate; }
            }
            return null;
        }

        /// <summary>Advice for a missing part that has a look-alike installed which is
        /// NOT a substitute, or null. Explains why the similar part isn't the answer.</summary>
        private static string LookAlikeAdvice(string partName)
        {
            foreach (var la in LookAlikes)
                if (string.Equals(la.missing, partName, StringComparison.OrdinalIgnoreCase)
                    && Usable(la.lookalike))
                    return la.warning;
            return null;
        }

        private static bool Enabled
        {
            get
            {
                var api = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.Api : null;
                return api == null || api.PartSubstitutionEnabled;
            }
        }

        // ── Apply: raw .craft ────────────────────────────────────────────────

        /// <summary>Rewrite a downloaded .craft so parts the recipient doesn't have are
        /// replaced by their installed equivalents, and report anything still missing.
        /// Run AFTER every side-channel strip, so only the real craft body is touched.
        /// Returns the input unchanged when nothing needs swapping or on any failure.</summary>
        public static byte[] ApplyToCraft(byte[] craftBytes, string context)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            // Switched off, we still scan and report — the player who opted out of having
            // their crafts rewritten still needs to know why one won't load.
            bool rewrite = Enabled;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);

                var referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in PartIdRx.Matches(text)) referenced.Add(m.Groups[1].Value.Trim());
                foreach (Match m in PartNameRx.Matches(text)) referenced.Add(m.Groups[1].Value.Trim());
                if (referenced.Count == 0) return craftBytes;

                var report = new Report(rewrite);
                foreach (var name in referenced)
                {
                    Group g;
                    string sub = SubstituteFor(name, out g);
                    if (sub == null) { report.NoteUnresolved(name); continue; }

                    if (rewrite)
                    {
                        text = Regex.Replace(text, @"(?<![\w.\-])" + Regex.Escape(name) + @"_(\d+)\b",
                                             sub + "_${1}");
                        text = Regex.Replace(text, @"(?m)^(\s*partName\s*=\s*)" + Regex.Escape(name) + @"\s*$",
                                             "${1}" + sub);
                    }
                    report.NoteSwap(name, sub, g);
                }

                report.Post(context);
                if (!rewrite || report.SwapCount == 0) return craftBytes;
                return Encoding.UTF8.GetBytes(text);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PartAliases.ApplyToCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Apply: VESSEL ConfigNode ─────────────────────────────────────────

        /// <summary>Same substitution over an imported VESSEL node, where a part is a
        /// PART node carrying `name` rather than a `part = name_id` line. Recurses, so
        /// items stashed in inventory containers are covered too.</summary>
        public static void ApplyToVesselNode(ConfigNode node, string context)
        {
            if (node == null) return;
            try
            {
                var report = new Report(Enabled); // report-only when switched off
                Walk(node, report);
                report.Post(context);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PartAliases.ApplyToVesselNode failed: {ex.Message}");
            }
        }

        private static void Walk(ConfigNode node, Report report)
        {
            if (node.name == "PART") Swap(node, "name", report);
            // KIS records a stored item's part as `partName`; stock nests a PART node,
            // which the PART branch above already catches on the way down.
            if (node.HasValue("partName")) Swap(node, "partName", report);

            for (int i = 0; i < node.nodes.Count; i++)
                Walk(node.nodes[i], report);
        }

        private static void Swap(ConfigNode node, string key, Report report)
        {
            string name = node.GetValue(key);
            if (string.IsNullOrEmpty(name)) return;

            Group g;
            string sub = SubstituteFor(name, out g);
            if (sub == null) { report.NoteUnresolved(name); return; }

            if (report.Applied) node.SetValue(key, sub);
            report.NoteSwap(name, sub, g);
        }

        // ── Reporting ────────────────────────────────────────────────────────

        /// <summary>Accumulates what was swapped and what is still missing across a whole
        /// craft, so the player gets one message rather than one per part.</summary>
        private class Report
        {
            private readonly List<string> swaps = new List<string>();
            private readonly List<string> notes = new List<string>();
            private readonly HashSet<string> missing = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> advice = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>False when substitution is switched off: the same scan runs and
            /// the same findings are reported, but as advice rather than as changes made.</summary>
            public bool Applied { get; private set; }

            public Report(bool applied) { Applied = applied; }

            public int SwapCount { get { return swaps.Count; } }

            public void NoteSwap(string from, string to, Group g)
            {
                if (!seen.Add(from)) return;
                string label = g != null ? g.label : to;
                swaps.Add($"{label}: {from} → {to}");
                if (g != null && !string.IsNullOrEmpty(g.note) && !notes.Contains(g.note))
                    notes.Add(g.note);
            }

            /// <summary>A referenced part with no substitution available. Only worth
            /// reporting when it isn't installed at all — otherwise it's a normal part.</summary>
            public void NoteUnresolved(string name)
            {
                if (!seen.Add(name)) return;
                if (Usable(name)) return;

                missing.Add(name);
                string tip = LookAlikeAdvice(name);
                if (!string.IsNullOrEmpty(tip)) advice.Add(tip);
            }

            public void Post(string context)
            {
                if (swaps.Count == 0 && missing.Count == 0) return;

                string what = string.IsNullOrEmpty(context) ? "this craft" : "'" + context + "'";
                var sb = new StringBuilder();

                if (swaps.Count > 0)
                {
                    sb.Append(Applied
                        ? $"{swaps.Count} part(s) in {what} were replaced with the equivalent you have: "
                        : $"{swaps.Count} part(s) in {what} aren't installed, but you have an equivalent "
                          + "(part substitution is off in settings.cfg): ");
                    sb.Append(string.Join("; ", swaps.ToArray())).Append(". ");
                    if (notes.Count > 0)
                        sb.Append("Note: ").Append(string.Join("; ", notes.ToArray())).Append(". ");
                }
                if (missing.Count > 0)
                {
                    sb.Append($"Still missing, with no equivalent installed: ")
                      .Append(string.Join(", ", new List<string>(missing).ToArray())).Append(". ");
                    foreach (var a in advice) sb.Append(a).Append(" ");
                }

                string title = swaps.Count == 0
                    ? $"⚠ {what} has {missing.Count} part(s) you don't have"
                    : Applied
                        ? $"Swapped {swaps.Count} part(s) to load {what}"
                        : $"⚠ {what} needs {swaps.Count} part(s) you have under another name";
                string body = sb.ToString().TrimEnd();

                Debug.Log($"[GeneKerman] PartAliases: {title} — {body}");

                var mod = GeneKermanMod.Instance;
                if (mod != null)
                {
                    try { mod.ShowNotification(title, body); return; }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] PartAliases: notification failed, " +
                                         $"falling back to screen message: {ex.Message}");
                    }
                }

                try { ScreenMessages.PostScreenMessage($"{title}: {body}", 12f, ScreenMessageStyle.UPPER_CENTER); }
                catch { /* no screen (headless) — the log line is enough */ }
            }
        }
    }
}
