using System;
using System.Collections.Generic;

namespace GeneKerman
{
    /// <summary>
    /// A contract's orbit-type ("orbital regime") requirement, parsed from the
    /// <c>orbit</c> sub-object the bot attaches to a contract's <c>constraints</c>
    /// when the mission text names a specific orbit (polar, equatorial,
    /// keostationary, Molniya, …). Drives the submit-button gate: a craft whose
    /// reported orbital elements don't match is blocked before upload, and the bot
    /// re-checks authoritatively on /submit.
    ///
    /// Schema + tolerances mirror data/orbit_constraints.py and the ORBIT_* values
    /// in settings.py. Unlike part limits there is no editor enforcement — an orbit
    /// is a flight state, not a part choice.
    /// </summary>
    public class OrbitConstraint
    {
        public List<string> Requirements = new List<string>();
        public string Notes = "";

        // Tolerances — keep in sync with settings.py (ORBIT_*).
        private const double PolarInclTol = 10.0;
        private const double EquatorialInclTol = 5.0;
        private const double InclinedMargin = 1.0;
        private const double CircularEccTol = 0.05;
        private const double EllipticEccMin = 0.20;
        private const double SyncPeriodTol = 0.05;
        private const double FrozenIncl = 63.4;
        private const double FrozenInclTol = 5.0;
        private const double MolniyaEccMin = 0.50;
        private const double TundraEccMin = 0.20;

        public bool IsEmpty => Requirements.Count == 0;

        public static OrbitConstraint Parse(Dictionary<string, object> dict)
        {
            var o = new OrbitConstraint();
            if (dict == null) return o;
            foreach (var item in MiniJSON.GetList(dict, "requirements"))
            {
                string v = item?.ToString();
                if (!string.IsNullOrEmpty(v)) o.Requirements.Add(v.Trim().ToLowerInvariant());
            }
            o.Notes = MiniJSON.GetString(dict, "notes", "");
            return o;
        }

        /// <summary>
        /// Validate a vessel's orbit against the requirement. Returns human-readable
        /// violation messages (empty == satisfies it). Elements the snapshot doesn't
        /// carry are skipped rather than failed — except that every requirement needs
        /// the craft to actually be in orbit, so a non-orbital situation always fails.
        /// </summary>
        public List<string> CheckOrbit(VesselDataCollector.VesselSnapshot snap)
        {
            var violations = new List<string>();
            if (IsEmpty || snap == null) return violations;

            string sit = (snap.situation ?? "").ToUpperInvariant();
            if (sit != "ORBITING" && sit != "DOCKED")
            {
                var names = new List<string>();
                foreach (var r in Requirements) names.Add(Label(r));
                violations.Add($"❌ Craft must be in orbit ({string.Join(", ", names.ToArray())}); " +
                               $"it is currently {(sit.Length == 0 ? "not orbiting" : sit)}.");
                return violations;
            }

            foreach (var req in Requirements)
            {
                string m = CheckOne(req, snap.inclination, snap.eccentricity,
                                    snap.period, snap.rotationPeriod);
                if (m != null) violations.Add("❌ " + m);
            }
            return violations;
        }

        private string CheckOne(string req, double incl, double ecc, double period, double rot)
        {
            switch (req)
            {
                case "polar":
                    if (Math.Abs(incl - 90.0) > PolarInclTol)
                        return $"Orbit must be polar (inclination ≈ 90°, ±{PolarInclTol:F0}°); current is {incl:F1}°.";
                    break;
                case "equatorial":
                    if (!(incl <= EquatorialInclTol || incl >= 180.0 - EquatorialInclTol))
                        return $"Orbit must be equatorial (inclination ≈ 0°, ±{EquatorialInclTol:F0}°); current is {incl:F1}°.";
                    break;
                case "retrograde":
                    if (incl <= 90.0 + InclinedMargin)
                        return $"Orbit must be retrograde (inclination > 90°); current is {incl:F1}°.";
                    break;
                case "prograde":
                    if (incl >= 90.0 - InclinedMargin)
                        return $"Orbit must be prograde (inclination < 90°); current is {incl:F1}°.";
                    break;
                case "circular":
                    if (ecc > CircularEccTol)
                        return $"Orbit must be circular (eccentricity ≤ {CircularEccTol:F2}); current is {ecc:F3}.";
                    break;
                case "elliptical":
                    if (ecc < EllipticEccMin)
                        return $"Orbit must be elliptical (eccentricity ≥ {EllipticEccMin:F2}); current is {ecc:F3}.";
                    break;
                case "synchronous":
                    return CheckPeriod(period, rot, 1.0, "synchronous");
                case "semisynchronous":
                    return CheckPeriod(period, rot, 0.5, "semi-synchronous");
                case "stationary":
                    foreach (var sub in new[] { "equatorial", "circular", "synchronous" })
                    {
                        string m = CheckOne(sub, incl, ecc, period, rot);
                        if (m != null)
                            return "Orbit must be geostationary (equatorial, circular and synchronous): " + m;
                    }
                    break;
                case "molniya":
                    return CheckFrozen(incl, ecc, period, rot, MolniyaEccMin, 0.5, "Molniya");
                case "tundra":
                    return CheckFrozen(incl, ecc, period, rot, TundraEccMin, 1.0, "Tundra");
            }
            return null;
        }

        // Period must equal factor× the body's sidereal rotation period. rotation
        // period <= 0 means the client didn't report it (old DLL) — skip, don't fail.
        private string CheckPeriod(double period, double rot, double factor, string label)
        {
            if (period <= 0 || rot <= 0) return null;
            double target = rot * factor;
            if (Math.Abs(period - target) / target > SyncPeriodTol)
                return $"Orbit must be {label} (period ≈ {target / 3600.0:F2} h); current is {period / 3600.0:F2} h.";
            return null;
        }

        private string CheckFrozen(double incl, double ecc, double period, double rot,
                                   double eccMin, double periodFactor, string label)
        {
            if (Math.Abs(incl - FrozenIncl) > FrozenInclTol)
                return $"{label} orbit needs the critical inclination ≈ {FrozenIncl:F1}° " +
                       $"(±{FrozenInclTol:F0}°); current is {incl:F1}°.";
            if (ecc < eccMin)
                return $"{label} orbit must be highly eccentric (eccentricity ≥ {eccMin:F2}); current is {ecc:F3}.";
            return CheckPeriod(period, rot, periodFactor, periodFactor < 1 ? label + " (half-day)" : label + " (one-day)");
        }

        /// <summary>
        /// Plane match against an explicit target inclination, in degrees. Returns a
        /// violation message, or null when it passes / can't be checked.
        ///
        /// Unlike everything else here this is not parsed from mission text — a rescue
        /// target carries the number the issuer asked for (see RescueTargetSpec). A
        /// margin &lt;= 0 means "any plane", which is what every rescue issued before the
        /// field existed asked for. Inclination runs 0..180° (>90° is retrograde) and
        /// the comparison deliberately doesn't wrap: 179° is not 1°, because opposite
        /// directions in one plane are opposite rendezvous problems.
        /// Mirrors check_inclination() in data/orbit_constraints.py.
        /// </summary>
        public static string CheckInclination(double target, double margin, double incl)
        {
            if (margin <= 0) return null;
            if (double.IsNaN(target) || double.IsNaN(margin) || double.IsNaN(incl)) return null;
            if (double.IsInfinity(target) || double.IsInfinity(incl)) return null;
            if (Math.Abs(incl - target) > margin)
                return $"Orbit must be inclined {target:F1}° (±{margin:F1}°); current is {incl:F1}°.";
            return null;
        }

        /// <summary>Just the regime names ("polar, circular"), or empty. For callers
        /// that supply their own heading — Describe() adds one.</summary>
        public string LabelList()
        {
            if (IsEmpty) return "";
            if (!string.IsNullOrEmpty(Notes)) return Notes;
            var names = new List<string>();
            foreach (var r in Requirements) names.Add(Label(r));
            return string.Join(", ", names.ToArray());
        }

        /// <summary>One-line summary for the contract UI, or empty.</summary>
        public string Describe()
        {
            if (IsEmpty) return "";
            return "Orbit: " + LabelList();
        }

        private static string Label(string req)
        {
            switch (req)
            {
                case "semisynchronous": return "semi-synchronous";
                case "stationary": return "geostationary";
                case "molniya": return "Molniya";
                case "tundra": return "Tundra";
                default: return req;
            }
        }
    }
}
