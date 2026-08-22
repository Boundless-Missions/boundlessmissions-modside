/*
 * ContractCreation.cs – Issuing work: a direct contract, a reverse auction, or a rescue.
 *
 * Factored out of CreateContractWindow so the classic window and the web bridge run the
 * same code rather than two copies that drift. The window keeps its own layout and status
 * line; everything below the buttons lives here.
 *
 * Two things are deliberately *not* parameters:
 *
 *   - The modlist. Callers name a mode ("stock", "janitor", …) and this file derives the
 *     folder list from the running game. A caller that could pass the string outright
 *     would make the mode labels a lie — "Stock Only" has to mean what the game says
 *     Squad is, not what a UI claims it is.
 *   - The rescue vessel. It is read from FlightGlobals here, never named by the caller,
 *     because sending it is what gets the player's ship destroyed.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GeneKerman
{
    public static class ContractCreation
    {
        /// <summary>Contract types the server understands. An allow-list because this
        /// string is forwarded verbatim and "auto" hands classification to the AI.</summary>
        private static readonly HashSet<string> ContractTypes = new HashSet<string>(StringComparer.Ordinal)
        { "auto", "craft_build", "active_vessel", "rescue", "flag_design" };

        // Part-restriction modes. Same five the classic window offers, named rather than
        // indexed so the two UIs cannot disagree about which is which.
        public const string ModlistNone = "none";
        public const string ModlistStock = "stock";
        public const string ModlistStockDlc = "stock_dlc";
        public const string ModlistMine = "mine";
        public const string ModlistJanitor = "janitor";

        /// <summary>Lowest price a reverse auction may open at, mirroring
        /// settings.AUCTION_MIN_START_VALUE on the server. A bid must undercut the
        /// current price by one and still be above zero, so an auction opened at 1
        /// escrows the money and then refuses every bid anyone tries to place.</summary>
        public const int MinAuctionStartValue = 2;

        /// <summary>Tolerance floors on a rescue target. Public because both UIs label
        /// their margin fields with them, and a label that disagreed with the clamp would
        /// quietly mislead the issuer about how hard the contract is.</summary>
        public const double MinMarginOrbitKm = 5.0;
        public const double MinMarginSurfaceDeg = 0.5;
        // Plane match. The default is what an issuer who asks for an inclination but
        // not a tolerance gets; the floor is the same idea as the two above — a margin
        // tighter than this makes the contract impossible rather than demanding.
        // Mirrors RESCUE_INCL_MARGIN_DEFAULT / _MIN in settings.py.
        public const double DefaultMarginInclDeg = 5.0;
        public const double MinMarginInclDeg = 1.0;

        /// <summary>The orbital regimes a rescue target can demand, in the order both
        /// UIs offer them. Tokens are the canonical ones from
        /// data/orbit_constraints.py — the server drops anything it doesn't know, so
        /// this list and REQUIREMENTS there have to agree.</summary>
        public static readonly string[] OrbitTypeTokens =
        {
            "polar", "equatorial", "prograde", "retrograde", "circular",
            "elliptical", "stationary", "synchronous", "molniya", "tundra",
        };

        /// <summary>
        /// Regime pairs no single orbit can satisfy, derived from the same check maths
        /// OrbitConstraint enforces (polar needs inclination ≈90° while equatorial needs
        /// ≈0°/180°; Molniya's half-day period contradicts a synchronous one; …). The
        /// form de-selects a token's conflicts when it is picked, and Validate refuses a
        /// set containing one — a contradictory requirement isn't "strict", it is a
        /// rescue nobody can ever deliver. Redundant pairs (stationary already implies
        /// circular) stay allowed: satisfiable is the test, not sensible.
        /// Mirrors _CONFLICTS in data/orbit_constraints.py — keep the two in sync (same
        /// convention as OrbitTypeTokens ↔ REQUIREMENTS).
        /// </summary>
        private static readonly Dictionary<string, string[]> OrbitTypeConflictMap =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "prograde",    new[] { "retrograde" } },
            { "retrograde",  new[] { "prograde", "molniya", "tundra" } },
            { "polar",       new[] { "equatorial", "stationary", "molniya", "tundra" } },
            { "equatorial",  new[] { "polar", "molniya", "tundra" } },
            { "circular",    new[] { "elliptical", "molniya", "tundra" } },
            { "elliptical",  new[] { "circular", "stationary" } },
            { "stationary",  new[] { "polar", "elliptical", "molniya", "tundra" } },
            { "synchronous", new[] { "molniya" } },
            { "molniya",     new[] { "retrograde", "polar", "equatorial", "circular",
                                     "stationary", "synchronous", "tundra" } },
            { "tundra",      new[] { "retrograde", "polar", "equatorial", "circular",
                                     "stationary", "molniya" } },
        };

        private static readonly string[] NoConflicts = new string[0];

        /// <summary>The regime tokens that cannot be combined with <paramref name="token"/>.</summary>
        public static string[] OrbitTypeConflicts(string token)
        {
            string[] found;
            return OrbitTypeConflictMap.TryGetValue(token ?? "", out found) ? found : NoConflicts;
        }

        /// <summary>Player-facing name for an orbit-regime token.</summary>
        public static string OrbitTypeLabel(string token)
        {
            switch (token)
            {
                case "polar": return "Polar";
                case "equatorial": return "Equatorial";
                case "prograde": return "Prograde";
                case "retrograde": return "Retrograde";
                case "circular": return "Circular";
                case "elliptical": return "Elliptical";
                case "stationary": return "Keostationary";
                case "synchronous": return "Keosynchronous";
                case "molniya": return "Molniya";
                case "tundra": return "Tundra";
                default: return token;
            }
        }

        /// <summary>What the rescuer has to bring back. Crew-only is the historical
        /// behaviour and stays the default; "vessel" additionally requires the wreck
        /// itself to arrive, which turns a rescue into a salvage/tow job.</summary>
        public const string RecoveryCrew = "crew";
        public const string RecoveryVessel = "vessel";

        /// <summary>Fraction of the wreck's original parts that must reach the target on
        /// a "vessel" recovery. Not 100%: a tow that loses a solar panel to a docking
        /// bump is still the wreck brought home, and holding out for every part would
        /// make the mode unplayable. Both the client check and the server's re-check
        /// read this same number — see _WRECK_COVERAGE_REQUIRED in api_server.py.</summary>
        public const double WreckCoverageRequired = 0.5;

        private static readonly HashSet<string> StockBodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Kerbol", "Sun", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike",
            "Dres", "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo",
        };

        private static bool? jcAvailable;

        // ── Modlist ─────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a part-restriction mode to the comma-separated GameData folder list
        /// the server stores on the contract. Returns null for "no restriction" — and
        /// also null with <paramref name="error"/> set when a mode was asked for but
        /// could not be read, which the caller must not treat as "unrestricted".
        /// </summary>
        public static string BuildModlist(string mode, out string error)
        {
            error = null;
            switch (mode ?? ModlistNone)
            {
                case ModlistNone:
                    return null;

                // DLC lives under its own SquadExpansion folder, so naming Squad alone
                // excludes MakingHistory and Serenity automatically.
                case ModlistStock:
                    return "Squad";
                case ModlistStockDlc:
                    return "Squad,SquadExpansion";

                case ModlistMine:
                {
                    string all = BuildActiveModlist();
                    if (string.IsNullOrEmpty(all)) error = "No parts are loaded to build a mod list from.";
                    return string.IsNullOrEmpty(all) ? null : all;
                }

                case ModlistJanitor:
                {
                    string jc = ReadJanitorsClosetModlist();
                    if (string.IsNullOrEmpty(jc))
                        error = "Open the VAB or SPH to capture the Janitor's Closet filter.";
                    return jc;
                }

                default:
                    error = "Unknown part restriction.";
                    return null;
            }
        }

        /// <summary>Every GameData folder that contributed a loaded part — the player's
        /// actual install. Also the automatic restriction on a rescue, where the
        /// contractor has to be able to load the wreck.</summary>
        public static string BuildActiveModlist()
        {
            var folders = new HashSet<string>();
            foreach (var p in PartLoader.LoadedPartsList)
                if (p != null && !string.IsNullOrEmpty(p.partUrl))
                    folders.Add(p.partUrl.Split('/')[0]);
            return string.Join(",", new List<string>(folders).ToArray());
        }

        public static bool IsJanitorsClosetAvailable()
        {
            if (jcAvailable.HasValue) return jcAvailable.Value;
            foreach (var asm in AssemblyLoader.loadedAssemblies)
            {
                if (asm.name.IndexOf("JanitorsCloset", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    jcAvailable = true;
                    return true;
                }
            }
            jcAvailable = false;
            return false;
        }

        /// <summary>True when the Janitor's Closet filter is readable right now. Its
        /// filter is registered into the editor's part list, which only exists in the
        /// VAB/SPH — everywhere else the answer is "come back from the editor".</summary>
        public static bool IsEditorFilterReadable()
        {
            try
            {
                var editorList = KSP.UI.Screens.EditorPartList.Instance;
                return editorList != null && editorList.ExcludeFilters != null;
            }
            catch (Exception) { return false; }
        }

        // Reads the set of mod folders currently visible under Janitor's Closet's mod
        // filter. JC registers its mod-level filter into KSP's own
        // EditorPartList.ExcludeFilters under the id "Mod Filter", so we read it through
        // KSP's core API rather than reflecting into JC internals (whose layout varies by
        // version). FilterCriteria(part) returns true when the part is kept/visible, so
        // hiding a mod drops its folder here too.
        private static string ReadJanitorsClosetModlist()
        {
            try
            {
                var editorList = KSP.UI.Screens.EditorPartList.Instance;
                if (editorList == null || editorList.ExcludeFilters == null)
                {
                    Debug.LogWarning("[GeneKerman] EditorPartList not available — open the VAB/SPH to read the JC mod filter.");
                    return null;
                }

                EditorPartListFilter<AvailablePart> modFilter = editorList.ExcludeFilters["Mod Filter"];
                if (modFilter == null || modFilter.FilterCriteria == null)
                {
                    Debug.LogWarning("[GeneKerman] JC 'Mod Filter' not registered in EditorPartList.ExcludeFilters.");
                    return null;
                }

                var criteria = modFilter.FilterCriteria; // true == part kept (mod allowed)
                var visibleFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in PartLoader.LoadedPartsList)
                {
                    if (part == null || string.IsNullOrEmpty(part.partUrl)) continue;
                    bool visible;
                    try { visible = criteria(part); }
                    catch { continue; }
                    if (visible)
                        visibleFolders.Add(part.partUrl.Split('/')[0]);
                }

                if (visibleFolders.Count > 0)
                {
                    Debug.Log($"[GeneKerman] JC modlist via 'Mod Filter' criteria: {visibleFolders.Count} visible folders");
                    return string.Join(",", new List<string>(visibleFolders).ToArray());
                }

                Debug.LogWarning("[GeneKerman] JC 'Mod Filter' matched no visible parts.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] JC modlist read failed: {ex.Message}");
            }
            return null;
        }

        // ── Game context ────────────────────────────────────────────────────

        public struct BodyInfo
        {
            public string Name;
            public bool Modded;
        }

        /// <summary>What the rescue panel can see of the live game: the bodies that
        /// exist, and — only in flight — the vessel that would be handed over.</summary>
        public sealed class RescueContext
        {
            public List<BodyInfo> Bodies = new List<BodyInfo>();
            public bool Available;          // in flight, with a crewed active vessel
            public string VesselName = "";
            public List<string> Crew = new List<string>();
            public string Body = "";
            public double ApKm, PeKm, Lat, Lon;
            /// <summary>Inclination of the vessel being handed over, in degrees. Offered
            /// as the prefill for a plane requirement: the orbit the wreck is actually
            /// in is the one a rescuer has to match to reach it.</summary>
            public double InclDeg;
            public bool InOrbit;   // false when the wreck isn't orbiting (landed/suborbital)
        }

        public static RescueContext ScanRescueContext()
        {
            var ctx = new RescueContext();

            if (FlightGlobals.Bodies != null)
            {
                foreach (var b in FlightGlobals.Bodies)
                {
                    if (b == null) continue;
                    ctx.Bodies.Add(new BodyInfo { Name = b.bodyName, Modded = !StockBodies.Contains(b.bodyName) });
                }
            }

            var v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v == null) return ctx;

            ctx.VesselName = LocalizedVesselName(v);
            // Off the parts, not KSP's cached crew list — a stranded kerbal missing from
            // the list here would never make it into the rescue contract at all.
            foreach (var pcm in VesselTransfer.CrewOf(v))
                if (pcm != null) ctx.Crew.Add(pcm.name);

            ctx.Body = v.mainBody != null ? v.mainBody.bodyName : "";

            // Seed the target from where the vessel actually is. The classic window
            // starts everyone at 100/100 km and 0°/0°, which is almost never what the
            // issuer wants — they are describing the wreck they are sitting in.
            try
            {
                if (v.orbit != null)
                {
                    ctx.ApKm = v.orbit.ApA / 1000.0;
                    ctx.PeKm = v.orbit.PeA / 1000.0;
                    ctx.InclDeg = v.orbit.inclination;
                    ctx.InOrbit = v.situation == Vessel.Situations.ORBITING;
                }
                ctx.Lat = v.latitude;
                ctx.Lon = v.longitude;
            }
            catch (Exception) { /* prefill is a convenience; the form still works blank */ }

            ctx.Available = ctx.Crew.Count > 0;
            return ctx;
        }

        // ── Creation ────────────────────────────────────────────────────────

        /// <summary>Everything the three create paths share. Validated in Validate().</summary>
        public sealed class Request
        {
            public string Kind = "contract";        // contract | auction | rescue
            public string ContractorId = "";        // unused by auction
            public string ContractorName = "";      // for the confirmation message only
            public string Mission = "";
            public int Payment;
            public int Fine;
            public string DueDate = "";
            public string ContractType = "craft_build";
            public string ModlistMode = ModlistNone;
            public int DurationHours = 24;          // auction only

            // Rescue only. Altitudes in km and margins in km/degrees, as typed.
            public string RescueMode = "orbit";     // orbit | surface — where
            public string RescueRecovery = RecoveryCrew;  // crew | vessel — what
            public double MinDvMs;                  // Δv the delivering craft must have left; 0 = off
            public string RescueBody = "";
            public double ApKm = 100, PeKm = 100, MarginAltKm = MinMarginOrbitKm;
            public double Lat, Lon, MarginPosDeg = MinMarginSurfaceDeg;

            // Orbit mode only. RequireIncl off (the default) means any plane, which is
            // what every rescue asked for before the field existed; OrbitTypes empty
            // means any regime. Both are dropped for a surface target.
            public bool RequireIncl;
            public double InclDeg;
            public double MarginInclDeg = DefaultMarginInclDeg;
            public List<string> OrbitTypes = new List<string>();
        }

        /// <summary>
        /// Checks everything that can be checked without talking to the server, and
        /// returns the reason if something is wrong.
        ///
        /// The player's balance is not among them: this layer would have to fetch it,
        /// and the server re-checks it at escrow time anyway. Callers that already hold
        /// a balance should pre-empt the round trip themselves.
        /// </summary>
        public static bool Validate(Request r, out string error)
        {
            error = null;

            if (r == null) { error = "Nothing to send."; return false; }
            if (r.Kind != "contract" && r.Kind != "auction" && r.Kind != "rescue")
            { error = "Unknown contract kind."; return false; }

            if (r.Kind != "auction" && string.IsNullOrEmpty(r.ContractorId))
            { error = "Pick who this is for."; return false; }

            if (string.IsNullOrEmpty(r.Mission) || r.Mission.Trim().Length < 3)
            { error = "Mission description is too short."; return false; }

            if (r.Payment <= 0) { error = "Enter a valid payment amount."; return false; }
            if (r.Kind == "auction" && r.Payment < MinAuctionStartValue)
            {
                error = "An auction has to start at " + MinAuctionStartValue +
                        " KCoins or more — nobody can undercut a lower opening price.";
                return false;
            }
            if (r.Fine < 0) r.Fine = 0;

            // yyyy-MM-dd is what the server parses and what <input type="date"> emits.
            if (!IsIsoDate(r.DueDate)) { error = "Enter a due date."; return false; }

            if (!ContractTypes.Contains(r.ContractType ?? ""))
            { error = "Unknown contract type."; return false; }

            if (r.Kind == "auction" && r.DurationHours < 1)
            { error = "Enter a valid auction duration in hours."; return false; }

            if (r.Kind == "rescue")
            {
                if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
                { error = "Switch to the crewed vessel in flight first."; return false; }
                if (string.IsNullOrEmpty(r.RescueBody))
                { error = "Pick a target body."; return false; }
                if (r.RescueMode != "orbit" && r.RescueMode != "surface")
                { error = "Pick orbit or surface."; return false; }
                if (r.RescueRecovery != RecoveryCrew && r.RescueRecovery != RecoveryVessel)
                { error = "Pick what has to come back: the crew, or the vessel too."; return false; }
                if (!Finite(r.MinDvMs) || r.MinDvMs < 0)
                { error = "Enter a valid Δv requirement in m/s (0 for none)."; return false; }

                if (r.RescueMode == "orbit")
                {
                    if (!Finite(r.ApKm) || !Finite(r.PeKm) || !Finite(r.MarginAltKm) ||
                        r.ApKm < 0 || r.PeKm < 0)
                    { error = "Enter a valid apoapsis and periapsis in km."; return false; }

                    if (r.RequireIncl)
                    {
                        if (!Finite(r.InclDeg) || r.InclDeg < 0 || r.InclDeg > 180)
                        { error = "Inclination must be between 0° and 180°."; return false; }
                        if (!Finite(r.MarginInclDeg))
                        { error = "Enter a valid inclination margin in degrees."; return false; }
                    }

                    var types = r.OrbitTypes ?? new List<string>();
                    foreach (var t in types)
                        if (Array.IndexOf(OrbitTypeTokens, t) < 0)
                        { error = "Unknown orbit type: " + t; return false; }

                    // No orbit can be, say, polar and equatorial at once — a set
                    // containing a conflicting pair is a contract nobody can fill.
                    // The form de-selects conflicts as they are picked, so this only
                    // fires for the bridge or a stale UI; the server refuses it too.
                    for (int i = 0; i < types.Count; i++)
                        foreach (var other in OrbitTypeConflicts(types[i]))
                            if (types.IndexOf(other) > i)
                            {
                                error = OrbitTypeLabel(types[i]) + " and " + OrbitTypeLabel(other) +
                                        " contradict each other — no orbit can be both.";
                                return false;
                            }
                }
                else if (!Finite(r.Lat) || !Finite(r.Lon) || !Finite(r.MarginPosDeg) ||
                         r.Lat < -90 || r.Lat > 90)
                {
                    error = "Latitude must be between -90 and 90.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Sends the request. Calls <paramref name="onDone"/> exactly once.
        ///
        /// A rescue that succeeds queues the issuer's vessel for removal — the ship is
        /// handed to the contractor, so it cannot stay in the issuer's save. That is
        /// irreversible, which is why every caller confirms first.
        /// </summary>
        public static IEnumerator Create(Request r, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            if (!Validate(r, out string error)) { onDone(false, error); yield break; }

            r.Mission = r.Mission.Trim();
            string who = string.IsNullOrEmpty(r.ContractorName) ? "them" : r.ContractorName;

            if (r.Kind == "rescue") { yield return CreateRescue(r, who, onDone); yield break; }

            // Flag design has no in-game build step, so a part restriction would mean
            // nothing on it.
            string modlist = null;
            if (r.ContractType != "flag_design")
            {
                modlist = BuildModlist(r.ModlistMode, out string modlistError);
                if (modlistError != null) { onDone(false, modlistError); yield break; }
            }

            bool ok = false;
            string message = null;

            if (r.Kind == "auction")
            {
                yield return mod.Api.CreateAuction(
                    r.Mission, r.Payment, r.Fine, r.DueDate, r.DurationHours, modlist, r.ContractType,
                    (success, data, err) =>
                    {
                        ok = success;
                        message = success ? "Auction posted. Bidding happens in Discord." : (err ?? "Failed to post the auction.");
                    });
            }
            else
            {
                yield return mod.Api.CreateContract(
                    r.ContractorId, r.Mission, r.Payment, r.Fine, r.DueDate, modlist,
                    (success, data, err) =>
                    {
                        ok = success;
                        message = success ? $"Contract sent to {who}." : (err ?? "Failed to create the contract.");
                    },
                    r.ContractType);
            }

            onDone(ok, message);
        }

        private static IEnumerator CreateRescue(Request r, string who, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;

            // Re-read the game rather than trusting anything the caller passed about the
            // vessel: between opening the form and pressing send, the player may have
            // switched ships, EVA'd, or left flight entirely.
            var ctx = ScanRescueContext();
            if (!ctx.Available) { onDone(false, "No crew aboard to rescue."); yield break; }

            bool bodyExists = false;
            bool isModded = false;
            foreach (var b in ctx.Bodies)
                if (b.Name == r.RescueBody) { bodyExists = true; isModded = b.Modded; break; }
            if (!bodyExists) { onDone(false, "That body does not exist in this save."); yield break; }

            double ap = 0, pe = 0, lat = 0, lon = 0, marginAlt = 0, marginPos = 0;
            // A plane/regime requirement belongs to an orbit; a surface target has none,
            // so they are dropped here rather than sent for the server to ignore.
            double incl = -1, marginIncl = 0;
            string orbitTypes = "";
            if (r.RescueMode == "orbit")
            {
                // The margin is a floor, not a validation error: too tight a tolerance
                // makes the contract impossible rather than wrong.
                double mk = r.MarginAltKm < MinMarginOrbitKm ? MinMarginOrbitKm : r.MarginAltKm;
                ap = r.ApKm * 1000.0;
                pe = r.PeKm * 1000.0;
                marginAlt = mk * 1000.0;

                if (r.RequireIncl)
                {
                    incl = r.InclDeg;
                    marginIncl = r.MarginInclDeg < MinMarginInclDeg
                                 ? MinMarginInclDeg : r.MarginInclDeg;
                }
                if (r.OrbitTypes != null && r.OrbitTypes.Count > 0)
                    orbitTypes = string.Join(",", r.OrbitTypes.ToArray());
            }
            else
            {
                lat = r.Lat;
                lon = r.Lon;
                marginPos = r.MarginPosDeg < MinMarginSurfaceDeg ? MinMarginSurfaceDeg : r.MarginPosDeg;
            }

            // Snapshot with the crew roster embedded so their attributes survive the
            // transfer. Crew are not renamed here — they are tagged "{me}'s {kerbal}" when
            // the rescuer imports the wreck, and stripped when returned.
            string node = VesselTransfer.ExportActiveVessel(true);
            if (string.IsNullOrEmpty(node)) { onDone(false, "Could not snapshot your vessel."); yield break; }

            var vessel = FlightGlobals.ActiveVessel;
            string pid = vessel.id.ToString();
            string vesselName = LocalizedVesselName(vessel);

            string myName = mod.LinkedUsername ?? "";
            var taggedKerbals = new List<object>();
            foreach (var k in ctx.Crew) taggedKerbals.Add(VesselTransfer.TagName(myName, k));

            bool ok = false;
            string message = null;

            // Life-support flag of the wreck itself: which mod its supplies belong to and
            // how long they last. The rescuer's client compares this with their own install
            // — a craft built for another LS mod carries nothing they can use, which is what
            // the emergency freeze and ration kit exist for.
            LifeSupportInfo ls = LifeSupportScan.Scan(vessel.parts);

            yield return mod.Api.CreateRescueContract(
                r.ContractorId, r.Mission, r.Payment, r.Fine, r.DueDate,
                BuildActiveModlist(), r.RescueBody, r.RescueMode,
                ap, pe, lat, lon, marginAlt, marginPos, isModded,
                pid, MiniJSON.Serialize(taggedKerbals), node,
                ls.ModKey, ls.EnduranceDaysPerKerbal, ls.CrewCapacity,
                r.RescueRecovery, r.MinDvMs, incl, marginIncl, orbitTypes,
                (success, data, err) =>
                {
                    ok = success;
                    message = success
                        ? $"Rescue sent to {who}. Your vessel will be removed."
                        : (err ?? "Failed to create the rescue contract.");
                });

            // Only once the server has it: the vessel is gone for the issuer, so losing
            // it on a failed send would destroy the ship and produce no contract.
            //
            // A queue that doesn't take is not fatal any more — the contract now carries
            // the vessel's pid, and ReconcileRescueVessels will find the ship still here
            // on the next Space Center visit and finish the job. Say so rather than
            // promising a removal that is about to look like it didn't happen.
            // The stranded crew are the contract: they leave with the ship, and come back
            // as an import (tag stripped) if and when the rescue is delivered.
            // returnToSpaceCenter: the player just gave this vessel away, so if it's
            // the one they're flying, finish the hand-over now rather than leaving a
            // window where a cancel can cross the un-run removal and duplicate it.
            if (ok && !mod.QueueRescueVesselRemoval(pid, vesselName,
                                                    VesselTransfer.CrewFate.LeavesWithCraft,
                                                    ctx.Crew, returnToSpaceCenter: true))
                message = $"Rescue sent to {who}. Your vessel will be removed the next " +
                          "time you visit the Space Center.";

            onDone(ok, message);
        }

        /// <summary>
        /// A vessel's display name. KSP stores an unnamed craft's name as a raw
        /// localization tag ("#autoLOC_501232" — "Untitled Space Craft") and localizes
        /// it at display time; anything we show or send as a *name* has to do the
        /// same, or the permanence gate reads "#autoLOC_501232 and its crew leave my
        /// save". The snapshot node keeps the raw tag — that is KSP's own storage
        /// format, and the rescuer's game localizes it exactly like any other vessel.
        /// </summary>
        private static string LocalizedVesselName(Vessel v)
        {
            string raw = v != null ? (v.vesselName ?? "") : "";
            try { return KSP.Localization.Localizer.Format(raw); }
            catch (Exception) { return raw; }
        }

        /// <summary>
        /// Rejects NaN and ±Infinity.
        ///
        /// Every ordinary comparison against NaN is false, so a range check like
        /// `ap &lt; 0` passes it straight through — and both front ends can produce one.
        /// The classic window parses its text boxes with double.TryParse under
        /// NumberStyles.Any, which accepts the literal "NaN" and "Infinity"; the bridge
        /// takes the same values as JSON. Either way it would reach
        /// CreateRescueContract, be formatted as "NaN" into a multipart field, and hand
        /// the server a rescue target no vessel can ever satisfy.
        /// </summary>
        private static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        private static bool IsIsoDate(string s)
        {
            return !string.IsNullOrEmpty(s) &&
                   DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out _);
        }
    }
}
