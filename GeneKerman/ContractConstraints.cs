using System;
using System.Collections.Generic;
using System.Linq;

namespace GeneKerman
{
    /// <summary>
    /// A contract's part-usage limits ("mission limits"), parsed from the
    /// <c>constraints</c> object the bot attaches to each contract. Drives two
    /// kinds of enforcement:
    ///   • <see cref="IsForbidden"/> — hides forbidden parts in the VAB/SPH editor.
    ///   • <see cref="CheckCraft"/> — validates a finished craft at submit time
    ///     (forbidden parts present + required parts missing).
    ///
    /// Schema mirrors data/mission_constraints.py. Comparisons are
    /// case-insensitive; part names match as substrings of a part's title.
    /// </summary>
    public class ContractConstraints
    {
        public List<string> ForbiddenParts = new List<string>();
        public List<string> RequiredParts = new List<string>();
        // Loose part mentions resolved to exact installed parts by the bot. Matched
        // against a part's internal name; the *Unresolved lists fall back to title
        // substring matching for mentions the bot couldn't pin down.
        public List<string> ForbiddenPartNames = new List<string>();
        public List<string> RequiredPartNames = new List<string>();
        public List<string> ForbiddenPartsUnresolved = new List<string>();
        public List<string> RequiredPartsUnresolved = new List<string>();
        public List<string> ForbiddenPropellants = new List<string>();
        public List<string> RequiredPropellants = new List<string>();
        public List<string> ForbiddenEngineCategories = new List<string>();
        public List<string> RequiredEngineCategories = new List<string>();
        public List<string> ForbiddenPartCategories = new List<string>();
        public List<string> RequiredPartCategories = new List<string>();
        public int MaxParts = -1;  // -1 == no limit
        public int MinParts = -1;
        // Crew-aboard limits (-1 == no limit). Whole-craft metric like Δv: can't be
        // enforced by hiding parts, so it's only checked at submit time. MaxCrew is
        // the one bound where 0 is a real value — an uncrewed mission — which is why
        // "no limit" is -1 here and null on the server, never 0.
        public int MaxCrew = -1;
        public int MinCrew = -1;
        // Per-profession crew requirements, keyed by the exact trait name KSP stores
        // in ProtoCrewMember.trait ("Pilot", "Kolonist"). Same shape as the crew band
        // one level down: -1 == that bound is unset, and a Max of 0 is real ("no
        // tourists"). Matching is by trait *string*, which is what makes a contract
        // written on a modded install still mean something here — the name survives
        // in a save even where the mod defining it doesn't (see ApplyTrait), so a
        // profession this install can't field reads as "nobody aboard has it" rather
        // than as an error.
        public Dictionary<string, CrewTraitRule> CrewTraits = new Dictionary<string, CrewTraitRule>();

        public class CrewTraitRule
        {
            public int Min = -1;
            public int Max = -1;
        }

        // Canonical trait -> the mod that defines it. Stock's four are absent on
        // purpose: a profession every install already has is not a dependency worth
        // naming.
        //
        // This is the one dependency no part walk can find. Every mod-detection path
        // in this project resolves parts to a GameData folder via AvailablePart.partUrl
        // (see CkanGenerator.GetModFolder), and a profession requirement has no part to
        // walk — the same blind spot TextureTransfer exists for. So it is written down.
        //
        // Deliberately coarse (one mod per profession, the one this community actually
        // gets it from) and closed: an unlisted trait yields no mod name rather than a
        // guessed one, because sending a player to install the wrong mod is worse than
        // telling them only which profession is missing. Kept in sync with the bot's
        // data/mission_constraints.py::_TRAIT_MODS — the two ends naming different mods
        // for one profession would read as two different problems.
        private static readonly Dictionary<string, string> TraitMods =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Kolonist", "USI/MKS" },
            { "Miner", "USI/MKS" },
            { "Mechanic", "USI/MKS" },
            { "Technician", "USI/MKS" },
            { "Medic", "USI/MKS" },
            { "Quartermaster", "USI/MKS" },
            { "Scout", "USI/MKS" },
            { "Biologist", "USI/MKS" },
            { "Geologist", "USI/MKS" },
            { "Botanist", "USI/MKS" },
            { "Chemist", "USI/MKS" },
            { "Farmer", "USI/MKS" },
        };

        /// <summary>The mod that defines a profession, or null for stock's four and for
        /// anything <see cref="TraitMods"/> doesn't know. Public because the crew-import
        /// path needs the same answer (see <c>VesselTransfer.ApplyTrait</c>), and one
        /// table beats two that can drift.</summary>
        public static string TraitMod(string trait)
        {
            if (string.IsNullOrEmpty(trait)) return null;
            string mod;
            return TraitMods.TryGetValue(trait.Trim(), out mod) ? mod : null;
        }
        // Vacuum delta-v limits in m/s (-1 == no limit). Whole-craft metric: can't
        // be enforced by hiding parts, so it's only checked at submit time.
        public double MaxDeltaV = -1;
        public double MinDeltaV = -1;
        // Orbit-type ("orbital regime") requirement parsed from the mission text by
        // the bot (polar/equatorial/keostationary/…). A flight state, not a part
        // choice, so it's gated at submit time only — see OrbitConstraint.cs.
        public OrbitConstraint Orbit = new OrbitConstraint();

        public bool IsEmpty =>
            ForbiddenParts.Count == 0 && RequiredParts.Count == 0 &&
            ForbiddenPropellants.Count == 0 && RequiredPropellants.Count == 0 &&
            ForbiddenEngineCategories.Count == 0 && RequiredEngineCategories.Count == 0 &&
            ForbiddenPartCategories.Count == 0 && RequiredPartCategories.Count == 0 &&
            MaxParts <= 0 && MinParts <= 0 && MaxDeltaV <= 0 && MinDeltaV <= 0 &&
            MaxCrew < 0 && MinCrew <= 0 && CrewTraits.Count == 0 &&
            (Orbit == null || Orbit.IsEmpty);

        public bool HasForbidRules =>
            ForbiddenParts.Count > 0 || ForbiddenPartNames.Count > 0 ||
            ForbiddenPropellants.Count > 0 ||
            ForbiddenEngineCategories.Count > 0 || ForbiddenPartCategories.Count > 0;

        public static ContractConstraints Parse(Dictionary<string, object> dict)
        {
            var c = new ContractConstraints();
            if (dict == null) return c;
            c.ForbiddenParts = StrList(dict, "forbidden_parts");
            c.RequiredParts = StrList(dict, "required_parts");
            c.ForbiddenPartNames = StrList(dict, "forbidden_part_names");
            c.RequiredPartNames = StrList(dict, "required_part_names");
            // When the bot ran resolution, prefer its split; otherwise treat every
            // loose mention as unresolved (title-substring) — same as before.
            c.ForbiddenPartsUnresolved = dict.ContainsKey("forbidden_parts_unresolved")
                ? StrList(dict, "forbidden_parts_unresolved") : new List<string>(c.ForbiddenParts);
            c.RequiredPartsUnresolved = dict.ContainsKey("required_parts_unresolved")
                ? StrList(dict, "required_parts_unresolved") : new List<string>(c.RequiredParts);
            c.ForbiddenPropellants = StrList(dict, "forbidden_propellants");
            c.RequiredPropellants = StrList(dict, "required_propellants");
            c.ForbiddenEngineCategories = StrList(dict, "forbidden_engine_categories");
            c.RequiredEngineCategories = StrList(dict, "required_engine_categories");
            c.ForbiddenPartCategories = StrList(dict, "forbidden_part_categories");
            c.RequiredPartCategories = StrList(dict, "required_part_categories");
            c.MaxParts = dict.ContainsKey("max_parts") ? MiniJSON.GetInt(dict, "max_parts", -1) : -1;
            c.MinParts = dict.ContainsKey("min_parts") ? MiniJSON.GetInt(dict, "min_parts", -1) : -1;
            c.MaxDeltaV = dict.ContainsKey("max_dv") ? MiniJSON.GetDouble(dict, "max_dv", -1) : -1;
            c.MinDeltaV = dict.ContainsKey("min_dv") ? MiniJSON.GetDouble(dict, "min_dv", -1) : -1;
            c.MaxCrew = dict.ContainsKey("max_crew") ? MiniJSON.GetInt(dict, "max_crew", -1) : -1;
            c.MinCrew = dict.ContainsKey("min_crew") ? MiniJSON.GetInt(dict, "min_crew", -1) : -1;
            c.CrewTraits = ParseCrewTraits(MiniJSON.GetDict(dict, "crew_traits"));
            c.Orbit = OrbitConstraint.Parse(MiniJSON.GetDict(dict, "orbit"));
            return c;
        }

        /// <summary>Read the `crew_traits` object: trait name → {min, max}. A bare
        /// number instead of an object is read as a floor, matching the shorthand the
        /// server accepts. Entries with neither bound are dropped rather than kept as
        /// a rule that can never be broken.</summary>
        private static Dictionary<string, CrewTraitRule> ParseCrewTraits(Dictionary<string, object> dict)
        {
            var rules = new Dictionary<string, CrewTraitRule>(StringComparer.OrdinalIgnoreCase);
            if (dict == null) return rules;

            foreach (var kvp in dict)
            {
                if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null) continue;
                var rule = new CrewTraitRule();

                var bounds = kvp.Value as Dictionary<string, object>;
                if (bounds != null)
                {
                    rule.Min = bounds.ContainsKey("min") ? MiniJSON.GetInt(bounds, "min", -1) : -1;
                    rule.Max = bounds.ContainsKey("max") ? MiniJSON.GetInt(bounds, "max", -1) : -1;
                }
                else
                {
                    var shorthand = new Dictionary<string, object> { { "min", kvp.Value } };
                    rule.Min = MiniJSON.GetInt(shorthand, "min", -1);
                }

                if (rule.Min <= 0 && rule.Max < 0) continue;
                rules[kvp.Key.Trim()] = rule;
            }
            return rules;
        }

        /// <summary>
        /// True when this part breaks a *forbidden* rule and should be hidden in
        /// the editor. Required rules can't be evaluated per-part, so they're
        /// only checked at submit time.
        /// </summary>
        public bool IsForbidden(AvailablePart ap)
        {
            if (!HasForbidRules || ap == null) return false;
            var s = PartClassifier.Classify(ap);
            return ViolatesForbid(s) != null;
        }

        // Slack so a craft that rounds to the Δv limit isn't unfairly rejected
        // (mirrors _DV_TOLERANCE in data/mission_constraints.py).
        private const double DvTolerance = 0.005;

        /// <summary>
        /// Validate a complete craft. Returns a list of human-readable violation
        /// messages (empty == the craft satisfies every limit).
        /// <paramref name="deltaVVac"/> is the craft's stock vacuum Δv (m/s); pass
        /// -1 when it couldn't be read, and the Δv limit is skipped rather than
        /// failed (the server skips a missing value too).
        /// </summary>
        public List<string> CheckCraft(IEnumerable<Part> parts, double deltaVVac = -1, int crewCount = -1,
                                       Dictionary<string, int> crewTraits = null)
        {
            var violations = new List<string>();
            if (IsEmpty || parts == null) return violations;

            violations.AddRange(CheckCrewTraits(crewTraits));

            // Crew aboard (-1 == unavailable, so skip rather than fail — like Δv). The
            // server re-checks authoritatively from the submitted telemetry crew count.
            if (crewCount >= 0)
            {
                if (MaxCrew >= 0 && crewCount > MaxCrew)
                    violations.Add(MaxCrew == 0
                        ? $"This mission must fly uncrewed: {crewCount} crew aboard."
                        : $"Too many crew aboard: {crewCount} (max {MaxCrew}).");
                if (MinCrew > 0 && crewCount < MinCrew)
                    violations.Add($"Too few crew aboard: {crewCount} (min {MinCrew}).");
            }

            var summaries = parts.Where(p => p != null)
                                 .Select(PartClassifier.Classify)
                                 .ToList();

            // Part-count limits.
            int count = summaries.Count;
            if (MaxParts > 0 && count > MaxParts)
                violations.Add($"Too many parts: {count} (max {MaxParts}).");
            if (MinParts > 0 && count < MinParts)
                violations.Add($"Too few parts: {count} (min {MinParts}).");

            // Delta-v limits (vacuum). deltaVVac < 0 means unavailable — skip.
            if (deltaVVac >= 0)
            {
                if (MaxDeltaV > 0 && deltaVVac > MaxDeltaV * (1 + DvTolerance))
                    violations.Add($"Too much delta-v: {deltaVVac:F0} m/s (max {MaxDeltaV:F0}).");
                if (MinDeltaV > 0 && deltaVVac < MinDeltaV * (1 - DvTolerance))
                    violations.Add($"Not enough delta-v: {deltaVVac:F0} m/s (min {MinDeltaV:F0}).");
            }

            // Forbidden: anything present that shouldn't be.
            foreach (var s in summaries)
            {
                string v = ViolatesForbid(s);
                if (v != null) violations.Add(v);
            }

            // Required: something that must be present somewhere on the craft.
            // Resolved names match a part's exact internal name; unresolved
            // mentions fall back to title substring.
            foreach (var need in RequiredPartNames)
                if (!summaries.Any(s => string.Equals(s.Name, need, StringComparison.OrdinalIgnoreCase)))
                    violations.Add($"Required part not found: '{need}'.");
            foreach (var need in RequiredPartsUnresolved)
                if (!summaries.Any(s => Contains(s.Title, need)))
                    violations.Add($"Required part not found: '{need}'.");

            foreach (var need in RequiredPropellants)
                if (!summaries.Any(s => s.Propellants.Contains(need)))
                    violations.Add($"Required: an engine powered by {need}.");

            foreach (var need in RequiredEngineCategories)
                if (!summaries.Any(s => s.EngineCategories.Contains(need)))
                    violations.Add($"Required engine type not found: {need}.");

            foreach (var need in RequiredPartCategories)
                if (!summaries.Any(s => s.PartCategories.Contains(need)))
                    violations.Add($"Required part category missing: {need}.");

            // De-duplicate (multiple identical forbidden parts -> one message).
            return violations.Distinct().ToList();
        }

        /// <summary>
        /// Per-profession crew check, against the head count aboard by trait. Null (the
        /// caller couldn't read the crew) skips it rather than failing it, like Δv.
        ///
        /// The messages match the server's word for word: this runs as a pre-flight so
        /// the player is told before they submit, and the server re-checks the same
        /// rule afterwards — the two disagreeing on wording would read as two different
        /// problems. A profession this install doesn't even define is called out
        /// separately, because "0 aboard" is true but useless advice when no kerbal in
        /// the save could ever have been one.
        /// </summary>
        public List<string> CheckCrewTraits(Dictionary<string, int> crewTraits)
        {
            var violations = new List<string>();
            if (CrewTraits.Count == 0 || crewTraits == null) return violations;

            foreach (var kvp in CrewTraits)
            {
                string trait = kvp.Key;
                CrewTraitRule rule = kvp.Value;
                int aboard;
                if (!crewTraits.TryGetValue(trait, out aboard)) aboard = 0;

                if (rule.Min > 0 && aboard < rule.Min)
                {
                    violations.Add($"Too few {trait}s aboard: {aboard} (need {rule.Min}).");
                    if (aboard == 0 && !TraitExistsHere(trait))
                    {
                        string mod = TraitMod(trait);
                        violations.Add(mod == null
                            ? $"No mod installed here defines the '{trait}' profession — " +
                              "this contract was written on an install that has it."
                            : $"No mod installed here defines the '{trait}' profession — it comes " +
                              $"from {mod}, which the contract's author has installed.");
                    }
                }
                if (rule.Max >= 0 && aboard > rule.Max)
                    violations.Add(rule.Max == 0
                        ? $"No {trait} may fly this mission: {aboard} aboard."
                        : $"Too many {trait}s aboard: {aboard} (max {rule.Max}).");
            }
            return violations;
        }

        private static bool TraitExistsHere(string trait)
        {
            try
            {
                var configs = GameDatabase.Instance != null
                    ? GameDatabase.Instance.ExperienceConfigs : null;
                return configs != null && configs.GetExperienceTraitConfig(trait) != null;
            }
            catch { return true; }   // can't tell — don't add a second, wrong message
        }

        /// <summary>Head count aboard a vessel by profession, keyed the way
        /// <see cref="CrewTraits"/> is. The key is `ProtoCrewMember.trait`, the same
        /// string the contract names, so this works for professions this install has
        /// no config for.</summary>
        public static Dictionary<string, int> CountCrewTraits(IEnumerable<ProtoCrewMember> crew)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (crew == null) return counts;
            foreach (var pcm in crew)
            {
                if (pcm == null || string.IsNullOrEmpty(pcm.trait)) continue;
                int n;
                counts[pcm.trait] = counts.TryGetValue(pcm.trait, out n) ? n + 1 : 1;
            }
            return counts;
        }

        /// <summary>First forbidden-rule violation for a single part, or null.</summary>
        private string ViolatesForbid(PartSummary s)
        {
            // Resolved part names match the exact installed part by internal name.
            foreach (var bad in ForbiddenPartNames)
                if (string.Equals(s.Name, bad, StringComparison.OrdinalIgnoreCase))
                    return $"Forbidden part: '{s.Title}'.";
            // Unresolved mentions fall back to title substring matching.
            foreach (var bad in ForbiddenPartsUnresolved)
                if (Contains(s.Title, bad))
                    return $"Forbidden part: '{s.Title}' (matches '{bad}').";

            foreach (var bad in ForbiddenPropellants)
                if (s.Propellants.Contains(bad))
                    return $"Forbidden: '{s.Title}' is an engine powered by {bad}.";

            foreach (var bad in ForbiddenEngineCategories)
                if (s.EngineCategories.Contains(bad))
                    return $"Forbidden engine type on '{s.Title}': {bad}.";

            foreach (var bad in ForbiddenPartCategories)
                if (s.PartCategories.Contains(bad))
                    return $"Forbidden part category: {bad} ('{s.Title}').";

            return null;
        }

        /// <summary>One-line summary for the contract UI, or empty.</summary>
        public string Describe()
        {
            var bits = new List<string>();
            void Add(string label, List<string> items)
            {
                if (items.Count > 0) bits.Add($"{label}: {string.Join(", ", items.ToArray())}");
            }
            Add("No", Concat(ForbiddenParts, ForbiddenEngineCategories, ForbiddenPropellants, ForbiddenPartCategories));
            Add("Must use", Concat(RequiredParts, RequiredEngineCategories, RequiredPropellants, RequiredPartCategories));
            if (MaxParts > 0) bits.Add($"≤{MaxParts} parts");
            if (MinParts > 0) bits.Add($"≥{MinParts} parts");
            if (MaxDeltaV > 0) bits.Add($"≤{MaxDeltaV:F0} m/s Δv");
            if (MinDeltaV > 0) bits.Add($"≥{MinDeltaV:F0} m/s Δv");
            if (MaxCrew == 0) bits.Add("uncrewed");
            else if (MaxCrew > 0) bits.Add($"≤{MaxCrew} crew");
            if (MinCrew > 0) bits.Add($"≥{MinCrew} crew");
            foreach (var kvp in CrewTraits)
            {
                CrewTraitRule r = kvp.Value;
                string phrase = null;
                if (r.Max == 0) phrase = $"no {kvp.Key}";
                else if (r.Min > 0 && r.Max > 0 && r.Min == r.Max) phrase = $"exactly {r.Min}× {kvp.Key}";
                else if (r.Min > 0 && r.Max > 0) phrase = $"{r.Min}–{r.Max}× {kvp.Key}";
                else if (r.Max > 0) phrase = $"≤{r.Max}× {kvp.Key}";
                else if (r.Min > 0) phrase = $"{r.Min}× {kvp.Key}";
                if (phrase != null) bits.Add(phrase + MissingTraitSuffix(kvp.Key, r));
            }
            if (Orbit != null && !Orbit.IsEmpty) bits.Add(Orbit.Describe());
            return string.Join(" | ", bits.ToArray());
        }

        /// <summary>" (needs USI/MKS)" for a profession this install cannot field, or ""
        /// — so the requirement names its mod while the contract is still being read,
        /// not for the first time when the submit pre-flight refuses it.
        ///
        /// Quiet for a player who already has the mod, and quiet for a *ceiling* ("no
        /// Kolonists aboard"), which is satisfied by not having the mod at all: naming
        /// it there would read as advice to install something in order to obey a ban.
        /// </summary>
        private static string MissingTraitSuffix(string trait, CrewTraitRule rule)
        {
            if (rule == null || rule.Min <= 0) return "";
            string mod = TraitMod(trait);
            if (mod == null || TraitExistsHere(trait)) return "";
            return $" (needs {mod})";
        }

        private static List<string> Concat(params List<string>[] lists)
        {
            var o = new List<string>();
            foreach (var l in lists) o.AddRange(l);
            return o;
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> StrList(Dictionary<string, object> dict, string key)
        {
            var outList = new List<string>();
            foreach (var o in MiniJSON.GetList(dict, key))
            {
                string v = o?.ToString();
                if (!string.IsNullOrEmpty(v)) outList.Add(v.Trim());
            }
            return outList;
        }
    }
}
