using System;
using System.Collections.Generic;

namespace GeneKerman
{
    /// <summary>
    /// A contract's constellation ("relay network") requirement, parsed from the
    /// <c>fleet</c> sub-object the bot attaches to a contract's <c>constraints</c>
    /// when the mission text asks for a network rather than a craft ("deploy a
    /// communication relay network around the Mun", "deploy three relay satellites
    /// into Kerbin orbit").
    ///
    /// Drives the submit-button gate: the client scans the target body's whole
    /// sphere of influence (<see cref="VesselDataCollector.CaptureBodyFleet"/>),
    /// counts what qualifies, and blocks the button with a specific reason before
    /// anything is uploaded. The bot re-checks authoritatively on /submit.
    ///
    /// Schema + tolerances mirror data/fleet_constraints.py and the FLEET_* values in
    /// settings.py. Like <see cref="OrbitConstraint"/> there is no editor
    /// enforcement — a network is a flight state, and unlike an orbit it is not even
    /// a state of the craft being flown.
    ///
    /// The one number NOT mirrored is the spread: it arrives materialised from the
    /// server, computed there from FLEET_SPREAD_FRAC, for the same reason the orbit
    /// altitude margin does — two ends that each derive a tolerance can derive it
    /// apart, and the player would be refused by a server that had agreed with the
    /// button they just pressed. <see cref="DefaultSpread"/> exists only for a dict
    /// that predates the field.
    /// </summary>
    public class FleetConstraint
    {
        /// <summary>How many vessels must qualify. 0 == no requirement.</summary>
        public int Count;

        /// <summary>Whether each member needs a relay-capable antenna.</summary>
        public bool Relay;

        /// <summary>Minimum gap between neighbouring members in mean longitude,
        /// degrees. Sent by the server; see the class note.</summary>
        public double Spread;

        /// <summary>How strong each member's relay antenna must be, as antenna power.
        /// 0 means "any relay-capable antenna", which is what <see cref="Relay"/> alone
        /// has always meant. A number rather than a part name, because a name is
        /// answerable only by the mod that ships it while every install has a power
        /// figure for every antenna — see VesselDataCollector.BestRelayPower.</summary>
        public double MinRelayPower;

        /// <summary>Which antenna model <see cref="MinRelayPower"/> is written in
        /// ("stock", "realantennas", …). There is no conversion between models, so the
        /// floor is only judged on an install that shares this one — see AntennaModel.</summary>
        public string PowerModel = AntennaModel.Stock;

        public string Notes = "";

        // Keep in sync with settings.py (FLEET_*).
        public const int MaxMembers = 24;
        private const double SmaTolerance = 0.35;
        private const double SpreadFrac = 0.5;

        public bool IsEmpty => Count < 2;

        public static FleetConstraint Parse(Dictionary<string, object> dict)
        {
            var f = new FleetConstraint();
            if (dict == null) return f;

            int n = MiniJSON.GetInt(dict, "count", 0);
            if (n < 2 || n > MaxMembers) return f;      // stays empty
            f.Count = n;
            f.Relay = MiniJSON.GetBool(dict, "relay", false);
            f.Notes = MiniJSON.GetString(dict, "notes", "");

            double minPower = MiniJSON.GetDouble(dict, "min_relay_power", 0.0);
            f.MinRelayPower = (minPower > 0 && !double.IsNaN(minPower)
                               && !double.IsInfinity(minPower)) ? minPower : 0.0;

            string model = MiniJSON.GetString(dict, "relay_power_model", AntennaModel.Stock);
            f.PowerModel = string.IsNullOrEmpty(model) ? AntennaModel.Stock : model;

            double spread = MiniJSON.GetDouble(dict, "spread", double.NaN);
            if (double.IsNaN(spread) || double.IsInfinity(spread) || spread <= 0)
                spread = DefaultSpread(n);
            // N members on a circle cannot all be more than 360/N apart, so a larger
            // gap than the even spacing would be unsatisfiable by construction.
            f.Spread = Math.Min(spread, 360.0 / n);
            return f;
        }

        /// <summary>The gap a network of <paramref name="count"/> gets when the server
        /// sent none — a fraction of the even spacing, so an imperfect network passes
        /// and a formation flying in one place does not.</summary>
        public static double DefaultSpread(int count)
        {
            if (count < 2) return 0.0;
            return (360.0 / count) * SpreadFrac;
        }

        // ── Membership ──────────────────────────────────────────────────────

        /// <summary>
        /// Whether one scanned craft counts toward the network. Mirrors
        /// fleet_constraints.is_member — the server re-runs this on what arrives, so a
        /// disagreement here shows up as a submission the button offered and the
        /// server refused, which is the failure this method exists to prevent.
        ///
        /// The body is NOT tested here: <see cref="VesselDataCollector.CaptureBodyFleet"/>
        /// only ever walks one body's SOI, so every snapshot reaching this is already
        /// at the right one. The server, which cannot trust that, tests it.
        /// </summary>
        public bool IsMember(VesselDataCollector.VesselSnapshot snap)
        {
            if (snap == null) return false;

            // A landed relay is a ground station, not a member of an orbital network.
            if (!string.Equals(snap.situation, "ORBITING", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(snap.situation, "DOCKED", StringComparison.OrdinalIgnoreCase))
                return false;

            if (snap.eccentricity >= 1.0) return false;   // escaping — passing through
            if (snap.periapsis <= 0) return false;        // periapsis underground
            if (snap.sma <= 0 || snap.period <= 0) return false;
            if (Relay && !snap.hasRelay) return false;
            // A negative power means this install does not express antenna strength at
            // all (RealAntennas deletes the field), which is not the same as a weak
            // antenna: the floor is skipped there rather than failed, so a stock-written
            // contract stays fillable on RO. Mirrors fleet_constraints.is_member.
            if (MinRelayPower > 0 && snap.relayPower >= 0 && snap.relayPower < MinRelayPower)
                return false;
            return true;
        }

        /// <summary>Where around the body a member sits, degrees 0..360. LAN +
        /// argument of periapsis + mean anomaly — see VesselSnapshot's note on why
        /// those three are carried separately and summed at each end.</summary>
        public static double MeanLongitude(VesselDataCollector.VesselSnapshot snap)
        {
            double v = (snap.lan + snap.argPe + snap.meanAnomaly) % 360.0;
            return v < 0 ? v + 360.0 : v;
        }

        // ── Verification ────────────────────────────────────────────────────

        /// <summary>
        /// Every way a scanned set fails this requirement, as player-facing sentences.
        /// Empty == satisfied. <paramref name="members"/> must already be filtered by
        /// <see cref="IsMember"/> (CaptureBodyFleet does it).
        /// </summary>
        public List<string> Check(List<VesselDataCollector.VesselSnapshot> members, string bodyName)
        {
            var problems = new List<string>();
            if (IsEmpty) return problems;

            int have = members?.Count ?? 0;
            string kind = Relay ? "relay satellites" : "satellites";
            string where = string.IsNullOrEmpty(bodyName) ? "" : $" around {bodyName}";

            if (have < Count)
            {
                problems.Add($"Needs {Count} {kind}{where}; {have} of your vessels there qualify.");
                return problems;
            }

            var shell = BestShell(members, Count);

            // One shell, not a scattering.
            double lo = double.MaxValue, hi = 0.0, radius = 0.0;
            foreach (var m in shell)
            {
                if (m.sma < lo) lo = m.sma;
                if (m.sma > hi) hi = m.sma;
                radius = m.bodyRadius;
            }
            if (hi > 0 && (hi - lo) / hi > SmaTolerance)
                problems.Add($"The {Count} qualifying vessels aren't in a common shell, "
                             + $"{(lo - radius) / 1000.0:F0} km to {(hi - radius) / 1000.0:F0} km. "
                             + "A network needs them at a comparable altitude.");

            // Spread around the body, not flying in formation.
            double gap = MinGap(shell);
            if (gap + 1e-9 < Spread)
                problems.Add($"The satellites are bunched together, the closest pair is "
                             + $"{gap:F0}° apart and a network of {Count} needs at least "
                             + $"{Spread:F0}°. Spread them around the orbit.");

            return problems;
        }

        /// <summary>
        /// The <paramref name="n"/> members that most look like one shell — the window
        /// of n with the tightest spread in semi-major axis.
        ///
        /// A player's Mun may hold a relay network AND a lander AND a fuel depot, all
        /// orbiting and some carrying relay antennas. The mission asks whether a
        /// network exists, not whether every craft the player owns is part of one, so
        /// the check picks the best candidate subset rather than failing because an
        /// unrelated craft is in a different orbit.
        /// </summary>
        private static List<VesselDataCollector.VesselSnapshot> BestShell(
            List<VesselDataCollector.VesselSnapshot> members, int n)
        {
            if (members.Count <= n) return new List<VesselDataCollector.VesselSnapshot>(members);

            var ordered = new List<VesselDataCollector.VesselSnapshot>(members);
            ordered.Sort((a, b) => a.sma.CompareTo(b.sma));

            var best = ordered.GetRange(0, n);
            double bestSpread = double.MaxValue;
            for (int i = 0; i + n <= ordered.Count; i++)
            {
                double lo = ordered[i].sma, hi = ordered[i + n - 1].sma;
                double spread = hi > 0 ? (hi - lo) / hi : double.MaxValue;
                if (spread < bestSpread)
                {
                    bestSpread = spread;
                    best = ordered.GetRange(i, n);
                }
            }
            return best;
        }

        /// <summary>The smallest gap between neighbours around the circle, degrees.
        /// Wraps, so a member at 359° and one at 1° are 2° apart, not 358°.</summary>
        private static double MinGap(List<VesselDataCollector.VesselSnapshot> shell)
        {
            if (shell.Count < 2) return 360.0;

            var longs = new List<double>();
            foreach (var m in shell) longs.Add(MeanLongitude(m));
            longs.Sort();

            double min = 360.0 - longs[longs.Count - 1] + longs[0];
            for (int i = 0; i + 1 < longs.Count; i++)
            {
                double gap = longs[i + 1] - longs[i];
                if (gap < min) min = gap;
            }
            return min;
        }

        // ── Presentation ────────────────────────────────────────────────────

        public string Describe()
        {
            if (IsEmpty) return "";
            string kind = Relay ? "relay satellites" : "satellites";
            string line = $"{Count} {kind} in orbit, at least {Spread:F0}° apart";
            if (MinRelayPower > 0)
            {
                line += $", each with a {FormatAntennaPower(MinRelayPower)} relay antenna";
                if (PowerModel != AntennaModel.Stock) line += $" ({PowerModel} rating)";
            }
            return line;
        }

        /// <summary>Antenna power as the parts list writes it: KSP stores 2000000 and
        /// shows 2M, and a requirement nobody can compare against their own antenna is
        /// not one they can meet. Mirrors fleet_constraints.format_antenna_power, so the
        /// contract card and the submit panel read the same.</summary>
        public static string FormatAntennaPower(double power)
        {
            if (double.IsNaN(power) || double.IsInfinity(power) || power <= 0) return "?";
            double[] cuts = { 1e9, 1e6, 1e3 };
            string[] suffixes = { "G", "M", "k" };
            for (int i = 0; i < cuts.Length; i++)
            {
                if (power < cuts[i]) continue;
                double scaled = power / cuts[i];
                return Math.Abs(scaled - Math.Round(scaled)) < 0.05
                    ? $"{scaled:F0}{suffixes[i]}"
                    : $"{scaled:F1}{suffixes[i]}";
            }
            return $"{power:F0}";
        }
    }
}
