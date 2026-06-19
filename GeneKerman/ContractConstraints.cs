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

        public bool IsEmpty =>
            ForbiddenParts.Count == 0 && RequiredParts.Count == 0 &&
            ForbiddenPropellants.Count == 0 && RequiredPropellants.Count == 0 &&
            ForbiddenEngineCategories.Count == 0 && RequiredEngineCategories.Count == 0 &&
            ForbiddenPartCategories.Count == 0 && RequiredPartCategories.Count == 0;

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
            return c;
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

        /// <summary>
        /// Validate a complete craft. Returns a list of human-readable violation
        /// messages (empty == the craft satisfies every limit).
        /// </summary>
        public List<string> CheckCraft(IEnumerable<Part> parts)
        {
            var violations = new List<string>();
            if (IsEmpty || parts == null) return violations;

            var summaries = parts.Where(p => p != null)
                                 .Select(PartClassifier.Classify)
                                 .ToList();

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
                    violations.Add($"Required propellant not used: {need}.");

            foreach (var need in RequiredEngineCategories)
                if (!summaries.Any(s => s.EngineCategories.Contains(need)))
                    violations.Add($"Required engine type not found: {need}.");

            foreach (var need in RequiredPartCategories)
                if (!summaries.Any(s => s.PartCategories.Contains(need)))
                    violations.Add($"Required part category missing: {need}.");

            // De-duplicate (multiple identical forbidden parts -> one message).
            return violations.Distinct().ToList();
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
                    return $"Forbidden propellant on '{s.Title}': {bad}.";

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
            return string.Join(" | ", bits.ToArray());
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
