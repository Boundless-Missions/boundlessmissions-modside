using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// The one hard coupling between this mod and the multiplayer layer: a
    /// server-signed statement that a vessel is *about to legitimately become* a
    /// particular state, so `CheatDetection` re-baselines against that state
    /// instead of reading the change as a teleport.
    ///
    /// Berthing, a docking snap and a clock relabel all look exactly like an F12
    /// Set Orbit to the watchdog, and they have to: the watchdog's grace forgives
    /// *derivatives* and never state, deliberately, because the blind grace
    /// re-baseline that adopted a cheated orbit before judging it was the
    /// original shipped bug. So there is no way to make these transitions pass
    /// except to tell the watchdog what the answer should be, in a form it can
    /// check.
    ///
    /// ## Two constraints, and neither can be added later
    ///
    /// **It verifies; it never trusts its caller.** This is a public entry point
    /// in an assembly loaded from `GameData`, so *any* mod on the machine can
    /// reach it — the multiplayer mod holds no privileged position and cannot be
    /// distinguished from an unrelated one. Signature, nonce and expiry are
    /// checked here, on the verifying side, and there is deliberately **no**
    /// overload, flag, or debug path amounting to "the caller asserts this is
    /// legitimate". One would make this mod's own watchdog defeatable by any mod
    /// in the game, which is strictly worse than not having the hook at all.
    /// A rejected token is not an error: the transition is simply judged as it
    /// would have been without one.
    ///
    /// **It is inert with no multiplayer mod present.** Nothing here names the
    /// multiplayer assembly, and nothing here runs unless somebody calls
    /// `Submit`. On a standalone install no token ever arrives, so no key is
    /// fetched and none is held — the inertness falls out of the shape rather
    /// than being enforced by a check. `BoundlessMissions` continues to ship
    /// alone.
    ///
    /// ## The token authorises a result, not a vessel
    ///
    /// It names the **expected resulting state**, and that is the load-bearing
    /// detail rather than a refinement: the token says "this vessel may become
    /// exactly this", never "ignore this vessel for a while". Weakening it to the
    /// latter would ship a sanctioned cheat window — a caller could sanction a
    /// transition and then perform an arbitrary jump inside it. So the state is
    /// checked against what the vessel actually became, and a vessel that lands
    /// somewhere else is tainted exactly as if no token had been presented.
    /// </summary>
    public static class SanctionedTransition
    {
        /// <summary>
        /// Wire-format version. Bumped when the canonical form changes, so the
        /// two mods can ship independently: a client that does not understand a
        /// token's version rejects it rather than mis-parsing it.
        /// </summary>
        public const int Version = 1;

        /// <summary>
        /// The origin the verification key is fetched from, **compiled in**.
        ///
        /// Deliberately not `ApiClient`'s configured host. That one is
        /// player-editable in `settings.cfg`, which is right for an API endpoint
        /// and fatal for a trust root: anyone could point verification at a
        /// server they control and mint their own sanctions. The key rotates on a
        /// 90-day schedule; the origin it is fetched from does not, so it is the
        /// part that can be pinned.
        /// </summary>
        public const string KeyOrigin = "https://boundlessmissions.com/api/v1/mp/jwks";

        /// <summary>How long a token may live. Seconds, so a captured one cannot be banked.</summary>
        public const double MaxLifetimeSeconds = 30.0;

        public enum Result
        {
            /// <summary>Verified. The watchdog will re-baseline to the named state.</summary>
            Accepted,
            /// <summary>Not verified. Judge the transition exactly as if no token existed.</summary>
            Rejected,
            /// <summary>
            /// The verification key is not available yet and a fetch is in flight.
            ///
            /// The caller should hold the transition and present the token again,
            /// rather than performing it and hoping. This is the *only* softening
            /// of the failure case, and it is deliberately on the caller's side:
            /// granting the vessel any amnesty here while we waited would be the
            /// "ignore this vessel for N ticks" window this design exists to
            /// avoid, and would make an unreachable key endpoint into the exploit.
            /// </summary>
            Pending,
        }

        public enum Kind
        {
            Berth, Unberth, FreighterMaterialise, ClockRelabel,
            DockSnap, BubbleForm, BubbleDissolve,
        }

        /// <summary>What a vessel is permitted to become.</summary>
        public class ExpectedState
        {
            public string BodyName = "";
            public bool Orbital;
            public double Sma, Ecc, Inc, Lan, ArgPe, Mna, EpochUt;
            public double Lat, Lon, Alt;
        }

        private class Sanction
        {
            public uint Pid;
            public Kind Type;
            public string Nonce = "";
            public double ExpiresWc;
            public string ServerId = "";
            public ExpectedState State;
        }

        /// <summary>Live sanctions, by vessel. At most one per vessel: a second replaces the first.</summary>
        private static readonly Dictionary<uint, Sanction> live = new Dictionary<uint, Sanction>();

        /// <summary>
        /// Nonces already spent, with the wall-clock time they stop mattering.
        ///
        /// Tracked **here**, on the verifying side, because a replay defence the
        /// caller performs is a replay defence an attacker simply does not
        /// perform. Entries are dropped once the token they belong to could no
        /// longer be valid anyway, which bounds the store without a sweeper —
        /// the same lazy-expiry shape `data/suspensions.py` uses server-side.
        /// </summary>
        private static readonly Dictionary<string, double> spent = new Dictionary<string, double>();

        // ── the entry point ─────────────────────────────────────────────────

        /// <summary>
        /// Present a server-signed transition token.
        ///
        /// The only public way in, and it takes nothing but the token: there is
        /// no vessel argument a caller could disagree with the token about, and
        /// no options parameter that could grow a bypass.
        /// </summary>
        public static Result Submit(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return Result.Rejected;

                string payload, signature;
                if (!Split(token, out payload, out signature)) return Result.Rejected;

                // Parse BEFORE verifying only to the extent needed to reject
                // malformed input cheaply; nothing is acted on until the
                // signature has been checked.
                Sanction s = Parse(payload);
                if (s == null) return Result.Rejected;

                double now = NowWc();
                if (s.ExpiresWc <= now) return Result.Rejected;
                if (s.ExpiresWc - now > MaxLifetimeSeconds) return Result.Rejected;

                PruneSpent(now);
                if (spent.ContainsKey(s.Nonce)) return Result.Rejected;

                var keys = TransitionKeys.Get();
                if (keys == null)
                {
                    // Fail closed. An unverifiable token is refused, never
                    // accepted on the grounds that we could not check it —
                    // otherwise making the key endpoint unreachable *is* the
                    // exploit. The caller is told to hold and retry.
                    TransitionKeys.BeginFetch();
                    return Result.Pending;
                }

                if (!keys.Verify(payload, signature, s.ServerId)) return Result.Rejected;

                spent[s.Nonce] = s.ExpiresWc;
                live[s.Pid] = s;
                return Result.Accepted;
            }
            catch (Exception ex)
            {
                // A throw is a rejection, never an acceptance.
                Debug.LogWarning("[GeneKerman] SanctionedTransition.Submit failed: " + ex.Message);
                return Result.Rejected;
            }
        }

        /// <summary>
        /// Whether this vessel currently has a sanction, and what state it names.
        ///
        /// Consumed on read: a sanction authorises **one** transition, so asking
        /// what it permits is the same event as spending it. Leaving it live
        /// would turn a single authorised change into a standing licence.
        /// </summary>
        internal static bool TakeFor(uint pid, out ExpectedState expected, out string label)
        {
            expected = null;
            label = "";
            Sanction s;
            if (!live.TryGetValue(pid, out s)) return false;
            live.Remove(pid);
            if (s.ExpiresWc <= NowWc()) return false;   // arrived, but too late to use
            expected = s.State;
            label = s.Type.ToString();
            return true;
        }

        /// <summary>Drop a vessel's pending sanction — it did not transition after all.</summary>
        internal static void Forget(uint pid)
        {
            live.Remove(pid);
        }

        // ── parsing ─────────────────────────────────────────────────────────

        private static bool Split(string token, out string payload, out string signature)
        {
            payload = signature = null;
            int dot = token.LastIndexOf('.');
            if (dot <= 0 || dot == token.Length - 1) return false;
            payload = token.Substring(0, dot);
            signature = token.Substring(dot + 1);
            return true;
        }

        /// <summary>
        /// The canonical payload: ordered `key=value` pairs joined by `;`.
        ///
        /// Deliberately not JSON. The bytes that are signed and the bytes that are
        /// verified have to be identical, and two JSON libraries — one Python, one
        /// C# — agree on that only by accident: key order, float formatting and
        /// whitespace are all free. A fixed field order and invariant-culture
        /// round-trip formatting removes the question. Culture matters more than
        /// it looks: a machine with a comma decimal separator would otherwise
        /// produce a payload that verifies nowhere.
        /// </summary>
        private static Sanction Parse(string payload)
        {
            var f = new Dictionary<string, string>();
            foreach (string part in payload.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                f[part.Substring(0, eq)] = part.Substring(eq + 1);
            }

            int ver;
            if (!f.ContainsKey("v") || !int.TryParse(f["v"], NumberStyles.Integer,
                                                     CultureInfo.InvariantCulture, out ver)) return null;
            if (ver != Version) return null;

            var s = new Sanction();
            uint pid;
            if (!f.ContainsKey("pid") || !uint.TryParse(f["pid"], NumberStyles.Integer,
                                                       CultureInfo.InvariantCulture, out pid)) return null;
            s.Pid = pid;

            if (!f.ContainsKey("type")) return null;
            try { s.Type = (Kind)Enum.Parse(typeof(Kind), f["type"], true); }
            catch { return null; }

            if (!f.ContainsKey("nonce") || f["nonce"].Length < 16) return null;
            s.Nonce = f["nonce"];

            double exp;
            if (!f.ContainsKey("exp") || !double.TryParse(f["exp"], NumberStyles.Float,
                                                         CultureInfo.InvariantCulture, out exp)) return null;
            s.ExpiresWc = exp;

            s.ServerId = f.ContainsKey("srv") ? f["srv"] : "";

            var st = new ExpectedState();
            st.BodyName = f.ContainsKey("body") ? f["body"] : "";
            if (st.BodyName.Length == 0) return null;

            st.Orbital = f.ContainsKey("sma");
            if (st.Orbital)
            {
                if (!Num(f, "sma", out st.Sma)) return null;
                if (!Num(f, "ecc", out st.Ecc)) return null;
                if (!Num(f, "inc", out st.Inc)) return null;
                if (!Num(f, "lan", out st.Lan)) return null;
                if (!Num(f, "argpe", out st.ArgPe)) return null;
                if (!Num(f, "mna", out st.Mna)) return null;
                if (!Num(f, "epoch", out st.EpochUt)) return null;
            }
            else
            {
                if (!Num(f, "lat", out st.Lat)) return null;
                if (!Num(f, "lon", out st.Lon)) return null;
                if (!Num(f, "alt", out st.Alt)) return null;
            }
            s.State = st;
            return s;
        }

        private static bool Num(Dictionary<string, string> f, string k, out double v)
        {
            v = 0;
            return f.ContainsKey(k) && double.TryParse(f[k], NumberStyles.Float,
                                                       CultureInfo.InvariantCulture, out v)
                   && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static void PruneSpent(double now)
        {
            if (spent.Count == 0) return;
            List<string> dead = null;
            foreach (var kv in spent)
                if (kv.Value <= now) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead != null) foreach (string k in dead) spent.Remove(k);
        }

        /// <summary>
        /// Wall-clock seconds. **Not** `Planetarium.GetUniversalTime()`.
        ///
        /// A token's expiry is a real-world deadline, and UT is not a real-world
        /// clock: under 100,000x warp a thirty-second UT window is a third of a
        /// millisecond, and while the game is paused it never elapses at all.
        /// </summary>
        private static double NowWc()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // ── persistence ─────────────────────────────────────────────────────

        internal const string NodeName = "GKSANCTIONNONCE";

        /// <summary>
        /// Spent nonces survive a save, for the same reason the taint store does:
        /// a quickload must not hand back a nonce that was already used. Live
        /// sanctions deliberately do **not** persist — they expire in seconds, and
        /// one restored from a save is one that outlived its own deadline.
        /// </summary>
        public static void SaveTo(ConfigNode scenarioNode)
        {
            try
            {
                double now = NowWc();
                PruneSpent(now);
                foreach (var kv in spent)
                {
                    var n = scenarioNode.AddNode(NodeName);
                    n.AddValue("nonce", kv.Key);
                    n.AddValue("exp", kv.Value.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] SanctionedTransition.SaveTo failed: " + ex.Message);
            }
        }

        public static void LoadFrom(ConfigNode scenarioNode)
        {
            try
            {
                live.Clear();
                spent.Clear();
                foreach (var n in scenarioNode.GetNodes(NodeName))
                {
                    string nonce = n.GetValue("nonce");
                    double exp;
                    if (string.IsNullOrEmpty(nonce)) continue;
                    if (!double.TryParse(n.GetValue("exp"), NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out exp)) continue;
                    spent[nonce] = exp;
                }
                PruneSpent(NowWc());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] SanctionedTransition.LoadFrom failed: " + ex.Message);
            }
        }
    }
}
