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

        // Numeric altitude requirement parsed from the mission text by the server
        // ("a 100x100 km orbit", "orbit above 400 km"). Metres above the surface,
        // NaN = unset. The margin arrives materialised from the server (explicit
        // "within N km" or its default formula), so the two ends verify against the
        // same number; the local default below is only for a dict that predates it.
        public double AltAp = double.NaN;
        public double AltPe = double.NaN;
        public double AltMargin = double.NaN;
        public double AltMin = double.NaN;
        public double AltMax = double.NaN;

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
        // Altitude-target tolerance default — mirrors ORBIT_ALT_MARGIN_MIN / _FRAC.
        private const double AltMarginMin = 10000.0;
        private const double AltMarginFrac = 0.05;

        public bool HasAltitude => !double.IsNaN(AltAp) || !double.IsNaN(AltPe)
                                   || !double.IsNaN(AltMin) || !double.IsNaN(AltMax);

        public bool IsEmpty => Requirements.Count == 0 && !HasAltitude;

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

            var alt = MiniJSON.GetDict(dict, "alt");
            if (alt != null)
            {
                o.AltAp = PositiveOrNaN(alt, "ap");
                o.AltPe = PositiveOrNaN(alt, "pe");
                o.AltMargin = PositiveOrNaN(alt, "margin");
                o.AltMin = PositiveOrNaN(alt, "min");
                o.AltMax = PositiveOrNaN(alt, "max");
            }
            return o;
        }

        private static double PositiveOrNaN(Dictionary<string, object> dict, string key)
        {
            double v = MiniJSON.GetDouble(dict, key, double.NaN);
            return (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) ? double.NaN : v;
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
                if (HasAltitude) names.Add(AltSummary());
                violations.Add($"Craft must be in orbit ({string.Join(", ", names.ToArray())}); " +
                               $"it is currently {(sit.Length == 0 ? "not orbiting" : sit)}.");
                return violations;
            }

            foreach (var req in Requirements)
            {
                string m = CheckOne(req, snap.inclination, snap.eccentricity,
                                    snap.period, snap.rotationPeriod);
                if (m != null) violations.Add(m);
            }
            CheckAlt(snap.apoapsis, snap.periapsis, violations);
            return violations;
        }

        /// <summary>Verify the craft's Ap/Pe (metres above the surface) against the
        /// altitude requirement. Mirrors _check_alt in data/orbit_constraints.py.</summary>
        private void CheckAlt(double apo, double peri, List<string> violations)
        {
            bool hasAp = !double.IsNaN(AltAp), hasPe = !double.IsNaN(AltPe);
            if (hasAp || hasPe)
            {
                double margin = double.IsNaN(AltMargin) || AltMargin <= 0
                                ? DefaultAltMargin() : AltMargin;
                bool badAp = hasAp && Math.Abs(apo - AltAp) > margin;
                bool badPe = hasPe && Math.Abs(peri - AltPe) > margin;
                if (badAp || badPe)
                {
                    string need = hasAp && hasPe ? $"Ap {Km(AltAp)} / Pe {Km(AltPe)}"
                                : hasAp ? $"Ap {Km(AltAp)}" : $"Pe {Km(AltPe)}";
                    string have = hasAp && hasPe ? $"Ap {Km(apo)} / Pe {Km(peri)}"
                                : hasAp ? $"Ap {Km(apo)}" : $"Pe {Km(peri)}";
                    violations.Add($"Orbit off target: need {need} (±{Km(margin)}); " +
                                   $"current is {have}.");
                }
            }
            if (!double.IsNaN(AltMin) && peri < AltMin)
                violations.Add($"The whole orbit must stay above {Km(AltMin)}; " +
                               $"current periapsis is {Km(peri)}.");
            if (!double.IsNaN(AltMax) && apo > AltMax)
                violations.Add($"The whole orbit must stay below {Km(AltMax)}; " +
                               $"current apoapsis is {Km(apo)}.");
        }

        /// <summary>Backstop for a constraint dict that carried targets but no margin
        /// (older server) — same formula the server materialises.</summary>
        private double DefaultAltMargin()
        {
            double target = Math.Max(double.IsNaN(AltAp) ? 0 : AltAp,
                                     double.IsNaN(AltPe) ? 0 : AltPe);
            return Math.Max(AltMarginMin, target * AltMarginFrac);
        }

        private static string Km(double metres)
        {
            double km = metres / 1000.0;
            return Math.Abs(km) < 10 ? $"{km:N1} km" : $"{km:N0} km";
        }

        /// <summary>"100 km (±10 km)", "250 km × 80 km (±13 km)", "above 400 km".</summary>
        private string AltSummary()
        {
            var bits = new List<string>();
            bool hasAp = !double.IsNaN(AltAp), hasPe = !double.IsNaN(AltPe);
            if (hasAp || hasPe)
            {
                double margin = double.IsNaN(AltMargin) || AltMargin <= 0
                                ? DefaultAltMargin() : AltMargin;
                string core = hasAp && hasPe
                              ? (AltAp == AltPe ? Km(AltAp) : $"{Km(AltAp)} × {Km(AltPe)}")
                              : hasAp ? $"Ap {Km(AltAp)}" : $"Pe {Km(AltPe)}";
                bits.Add($"{core} (±{Km(margin)})");
            }
            if (!double.IsNaN(AltMin)) bits.Add($"above {Km(AltMin)}");
            if (!double.IsNaN(AltMax)) bits.Add($"below {Km(AltMax)}");
            return string.Join(", ", bits.ToArray());
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

        /// <summary>The regime names plus any altitude requirement ("polar, circular",
        /// "100 km (±10 km)"), or empty. For callers that supply their own heading —
        /// Describe() adds one.</summary>
        public string LabelList()
        {
            if (IsEmpty) return "";
            if (!string.IsNullOrEmpty(Notes)) return Notes;
            var names = new List<string>();
            foreach (var r in Requirements) names.Add(Label(r));
            if (HasAltitude) names.Add(AltSummary());
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
