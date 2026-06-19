using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// Inspects a part to derive the semantic facts a contract's "mission limit"
    /// constraints are expressed in: which propellants it burns, what kind of
    /// engine it is (nuclear / ion / solid / ...), and which part categories it
    /// belongs to (heatshield, parachute, ...).
    ///
    /// Detection is property-driven (engine modules, propellant resource names,
    /// part modules) rather than a hardcoded part list, so it also classifies
    /// modded parts — e.g. a modded engine that burns LqdHe3 with no oxidizer is
    /// reported as a nuclear engine consuming LqdHe3.
    ///
    /// Canonical category tokens are kept in sync with the bot's
    /// data/mission_constraints.py.
    /// </summary>
    public class PartSummary
    {
        public string Title = "";
        public string Name = "";
        public HashSet<string> Propellants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EngineCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PartCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serialise to the JSON shape the bot's verifier expects.</summary>
        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "name", Name },
                { "title", Title },
                { "propellants", Propellants.Cast<object>().ToList() },
                { "engine_categories", EngineCategories.Cast<object>().ToList() },
                { "part_categories", PartCategories.Cast<object>().ToList() },
            };
        }
    }

    public static class PartClassifier
    {
        public static PartSummary Classify(AvailablePart ap)
        {
            if (ap == null) return new PartSummary();
            return ClassifyCore(ap.partPrefab, ap.title, ap.name, ap.category);
        }

        public static PartSummary Classify(Part part)
        {
            if (part == null) return new PartSummary();
            string title = part.partInfo?.title ?? part.name;
            string name = part.partInfo?.name ?? part.name;
            PartCategories cat = part.partInfo != null ? part.partInfo.category : PartCategories.none;
            return ClassifyCore(part, title, name, cat);
        }

        private static PartSummary ClassifyCore(Part prefab, string title, string name, PartCategories category)
        {
            var s = new PartSummary { Title = title ?? "", Name = name ?? "" };
            string lowTitle = (title ?? "").ToLowerInvariant();
            string lowName = (name ?? "").ToLowerInvariant();

            // KSP part category (e.g. "Engine", "Thermal", "Pods").
            if (category != PartCategories.none)
                s.PartCategories.Add(category.ToString().ToLowerInvariant());

            bool hasEngine = false;
            bool anyThrottleLocked = false;

            if (prefab != null && prefab.Modules != null)
            {
                // Engines (ModuleEnginesFX derives from ModuleEngines, so this
                // catches both stock and most modded engines).
                foreach (var eng in prefab.Modules.GetModules<ModuleEngines>())
                {
                    if (eng == null) continue;
                    hasEngine = true;
                    if (eng.throttleLocked) anyThrottleLocked = true;
                    AddPropellants(s.Propellants, eng.propellants);
                }

                // RCS thrusters carry propellants too and answer "rcs"/"monoprop".
                foreach (var rcs in prefab.Modules.GetModules<ModuleRCS>())
                {
                    if (rcs == null) continue;
                    AddPropellants(s.Propellants, rcs.propellants);
                    s.PartCategories.Add("rcs");
                    s.EngineCategories.Add("rcs");
                }

                // Module-presence part categories (string match so modded class
                // names and DLC-only modules don't break the build).
                if (HasModule(prefab, "ModuleAblator")) s.PartCategories.Add("heatshield");
                if (HasModule(prefab, "ModuleParachute")) s.PartCategories.Add("parachute");
                if (HasModule(prefab, "ModuleDeployableSolarPanel") || HasModule(prefab, "ModuleDeployableSolarPanelFX"))
                    s.PartCategories.Add("solarpanel");
                if (HasModule(prefab, "ModuleReactionWheel")) s.PartCategories.Add("reactionwheel");
                if (HasModule(prefab, "ModuleWheelBase") || HasModule(prefab, "ModuleWheelMotor"))
                    s.PartCategories.Add("wheel");
                if (HasModule(prefab, "RetractableLadder") || HasModule(prefab, "ModuleWheelBase"))
                { /* ladder handled by title below */ }
            }

            if (hasEngine) s.PartCategories.Add("engine");

            // Title-driven part categories (covers parts whose modules we don't
            // probe, e.g. ladders, and RTGs).
            if (lowTitle.Contains("heat shield") || lowTitle.Contains("heatshield"))
                s.PartCategories.Add("heatshield");
            if (lowTitle.Contains("ladder")) s.PartCategories.Add("ladder");
            if (lowTitle.Contains("solar panel")) s.PartCategories.Add("solarpanel");
            if (lowTitle.Contains("landing gear") || lowTitle.Contains("landing leg") || lowTitle.Contains("wheel"))
                s.PartCategories.Add("wheel");
            if (lowTitle.Contains("rtg") || lowTitle.Contains("radioisotope") || lowName.Contains("rtg"))
                s.PartCategories.Add("rtg");

            // ── Engine categories ──
            ClassifyEngine(s, hasEngine, anyThrottleLocked, lowTitle, lowName);
            return s;
        }

        private static void ClassifyEngine(PartSummary s, bool hasEngine, bool throttleLocked,
                                           string lowTitle, string lowName)
        {
            var props = s.Propellants; // case-insensitive set

            bool hasOx = props.Contains("Oxidizer");
            bool hasLF = props.Contains("LiquidFuel");
            bool hasIntake = props.Contains("IntakeAir") || props.Contains("IntakeAtm") || props.Contains("IntakeLqd");
            bool hasEC = props.Contains("ElectricCharge");

            // Solid: locked-throttle engine, or burns solid fuel, or named like one.
            if ((hasEngine && throttleLocked) || props.Contains("SolidFuel")
                || lowTitle.Contains("srb") || lowTitle.Contains("solid booster")
                || lowName.Contains("srb"))
                s.EngineCategories.Add("solid");

            // Ion / electric.
            if (props.Contains("XenonGas") || props.Contains("ArgonGas") || lowTitle.Contains("ion"))
                s.EngineCategories.Add("ion");
            if (hasEngine && hasEC)
            {
                s.EngineCategories.Add("electric");
                s.EngineCategories.Add("ion"); // electric-propulsion engines read as ion
            }

            // Chemical: bipropellant (burns an oxidizer), or a jet (air-breathing).
            if (hasOx || hasIntake)
                s.EngineCategories.Add("chemical");

            // Monopropellant main engine (e.g. O-10 "Puff").
            if (hasEngine && props.Contains("MonoPropellant"))
                s.EngineCategories.Add("monoprop");

            // Nuclear: explicit naming, exotic fusion/fission fuels, or the stock
            // NTR signature (an engine burning LiquidFuel with no oxidizer and no
            // air intake — i.e. not chemical and not a jet).
            bool nuclearName = lowTitle.Contains("nuclear") || lowName.Contains("nuclear")
                || lowTitle.Contains("nerv") || lowName.Contains("nerv")
                || lowTitle.Contains("ntr") || lowName.Contains("ntr")
                || lowTitle.Contains("atomic") || lowTitle.Contains("fission")
                || lowTitle.Contains("fusion");
            bool exoticFuel = props.Contains("LqdHe3") || props.Contains("LqdDeuterium")
                || props.Contains("Antimatter") || props.Contains("FusionPellets")
                || props.Contains("EnrichedUranium");
            bool ntrSignature = hasEngine && hasLF && !hasOx && !hasIntake && !hasEC;
            if (nuclearName || exoticFuel || ntrSignature)
                s.EngineCategories.Add("nuclear");
        }

        private static void AddPropellants(HashSet<string> set, List<Propellant> propellants)
        {
            if (propellants == null) return;
            foreach (var p in propellants)
                if (p != null && !string.IsNullOrEmpty(p.name))
                    set.Add(p.name);
        }

        private static bool HasModule(Part prefab, string moduleClassName)
        {
            try { return prefab.Modules != null && prefab.Modules.Contains(moduleClassName); }
            catch { return false; }
        }
    }
}
