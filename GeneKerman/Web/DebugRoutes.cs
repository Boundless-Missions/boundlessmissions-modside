#if GK_DEBUG_PANEL
/*
 * DebugRoutes.cs — the /gk/debug/* surface. COMPILED OUT of production builds.
 *
 * Read (Phase 1) and command (Phase 2). The split matters: everything under /state is
 * a pure read and can be called at any moment in any scene, so a driver may poll it
 * freely to decide whether an assertion holds yet. Everything under /actions/ changes
 * the save, and each one refuses on a failed precondition rather than half-acting —
 * "you cannot spawn a vessel from the main menu" has to be an answer, not a
 * NullReferenceException in KSP.log that the driver reads as a timeout.
 *
 * Two rules the whole file obeys:
 *
 *   - Every handler reaches KSP through MainThreadQueue. Touching HighLogic or
 *     FlightGlobals from a request thread works most of the time and corrupts state
 *     the rest, which is the worst possible failure for a harness whose purpose is
 *     deciding whether the mod corrupts state.
 *
 *   - Every read is defensive to the point of paranoia. This runs against saves that
 *     are, by design, in the broken states being tested — a kerbal with an
 *     unresolvable trait, a vessel mid-Die(), a scenario module that KSP never
 *     injected. A snapshot that throws tells the driver nothing; a snapshot with one
 *     null field tells it exactly where the damage is.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GeneKerman.Web
{
    internal sealed class DebugRoutes
    {
        private readonly DebugBridge bridge;
        private readonly MainThreadQueue queue;

        public DebugRoutes(DebugBridge bridge, MainThreadQueue queue)
        {
            this.bridge = bridge;
            this.queue = queue;
        }

        public void Dispatch(HttpListenerContext ctx, string path)
        {
            var req = ctx.Request;

            switch (path)
            {
                // ── Reads ───────────────────────────────────────────────────
                case "/gk/debug/state":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(State));
                    return;

                case "/gk/debug/roster":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(() => JobResult.Json(RosterJson())));
                    return;

                case "/gk/debug/vessels":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(() => JobResult.Json(VesselsJson())));
                    return;

                case "/gk/debug/scenario":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(() => JobResult.Json(ScenarioJson())));
                    return;

                case "/gk/debug/contracts":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(() => JobResult.Json(ContractsJson())));
                    return;

                // The event stream. Authenticated by the same token header as every
                // other route — deliberately NOT the cookie-only shape /gk/events uses,
                // because a driver is not an EventSource and has no reason to inherit
                // that constraint (audit finding LB2 is still open).
                case "/gk/debug/events":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    bridge.Events.Accept(ctx);
                    return;

                // ── Commands ────────────────────────────────────────────────
                case "/gk/debug/actions/save":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(SaveNow));
                    return;

                case "/gk/debug/actions/remove-vessel":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleRemoveVessel(ctx);
                    return;

                case "/gk/debug/actions/repair-traits":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(RepairTraits));
                    return;

                case "/gk/debug/actions/restore-traits":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(RestoreTraits));
                    return;

                case "/gk/debug/actions/purge-ghosts":
                {
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    var pgBody = ReadJson(ctx);
                    bool pgOrphans = pgBody != null && MiniJSON.GetBool(pgBody, "orphans", false);
                    Respond(ctx, queue.RunSync(() => PurgeGhosts(pgOrphans)));
                    return;
                }

                case "/gk/debug/actions/poll-imports":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(PollImports));
                    return;

                case "/gk/debug/actions/refresh":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(RefreshAll));
                    return;

                case "/gk/debug/actions/scene":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleScene(ctx);
                    return;

                case "/gk/debug/actions/load-save":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleLoadSave(ctx);
                    return;

                case "/gk/debug/actions/quicksave":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleQuicksave(ctx);
                    return;

                case "/gk/debug/actions/quickload":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleQuickload(ctx);
                    return;

                case "/gk/debug/actions/unlink":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(Unlink));
                    return;

                // Binary. Runs as a coroutine because a frame has to finish rendering
                // before there is anything to read back.
                case "/gk/debug/screenshot":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    HandleScreenshot(ctx);
                    return;

                case "/gk/debug/ui":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(UiTree));
                    return;

                case "/gk/debug/saves":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(ListSaves));
                    return;

                case "/gk/debug/actions/crew":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleCrew(ctx);
                    return;

                case "/gk/debug/actions/spawn-test-craft":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleSpawnTestCraft(ctx);
                    return;

                case "/gk/debug/actions/fly-vessel":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleFlyVessel(ctx);
                    return;

                case "/gk/debug/actions/spawn-wreck":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleSpawnWreck(ctx);
                    return;

                case "/gk/debug/actions/quicksend":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleQuicksend(ctx);
                    return;
                case "/gk/debug/actions/gift":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleGift(ctx);
                    return;

                case "/gk/debug/actions/issue-rescue":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleIssueRescue(ctx);
                    return;

                case "/gk/debug/actions/accept-contract":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleAcceptContract(ctx);
                    return;

                default:
                    if (path.StartsWith("/gk/debug/jobs/", StringComparison.Ordinal))
                    {
                        if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                        var rec = bridge.Jobs.Get(path.Substring("/gk/debug/jobs/".Length));
                        if (rec == null)
                        {
                            LocalServer.Respond(ctx, 404, "application/json", "{\"error\":\"no_such_job\"}");
                            return;
                        }
                        LocalServer.Respond(ctx, 200, "application/json", rec.ToJson());
                        return;
                    }
                    LocalServer.Respond(ctx, 404, "application/json", "{\"error\":\"not_found\"}");
                    return;
            }
        }

        // ══ Reads ═══════════════════════════════════════════════════════════

        /// <summary>
        /// The whole picture in one request. A driver asserting on a crew hand-over needs
        /// the roster, the vessel list and the scenario queues to be consistent with each
        /// other, and three separate requests can straddle a frame in which a removal
        /// ran — so they are gathered in one main-thread job or they are useless.
        /// </summary>
        private JobResult State()
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"protocol\":").Append(DebugBridge.Protocol)
              .Append(",\"modVersion\":").Append(JobResult.Quote(ModVersion.Current))
              .Append(",\"scene\":").Append(JobResult.Quote(SceneName()))
              .Append(",\"gameLoaded\":").Append(B(HighLogic.CurrentGame != null))
              .Append(",\"save\":").Append(JobResult.Quote(SaveFolder()))
              .Append(",\"identity\":").Append(IdentityJson())
              .Append(",\"activeVessel\":").Append(ActiveVesselJson())
              .Append(",\"vessels\":").Append(VesselsJson())
              .Append(",\"roster\":").Append(RosterJson())
              // Emitted AFTER the roster, which is what sets these. "Zero entries" and
              // "I could not read it" are different answers, and conflating them turns
              // every roster assertion into a pass.
              .Append(",\"rosterOk\":").Append(B(lastRosterError == null))
              .Append(",\"rosterError\":").Append(JobResult.Quote(lastRosterError ?? ""))
              .Append(",\"crew\":").Append(CrewDerivationsJson())
              .Append(",\"scenario\":").Append(ScenarioJson())
              .Append(",\"client\":").Append(ContractsJson())
              .Append('}');
            return JobResult.Json(sb.ToString());
        }

        /// <summary>
        /// Who this client believes it is. `accountId` is the field the whole crew-
        /// ownership fix rests on (see RM1 in 3108_security_audit.md): the impersonation
        /// test is exactly "two instances report the same username and different
        /// accountIds", and it cannot be run without reading both.
        /// </summary>
        private static string IdentityJson()
        {
            var mod = GeneKermanMod.Instance;
            var sb = new StringBuilder();
            sb.Append("{\"linked\":").Append(B(mod?.Api?.IsLinked == true))
              .Append(",\"accountId\":").Append(JobResult.Quote(mod?.LinkedAccountId ?? ""))
              .Append(",\"username\":").Append(JobResult.Quote(mod?.LinkedUsername ?? ""))
              .Append(",\"serverUrl\":").Append(JobResult.Quote(mod?.Api?.ServerUrl ?? ""))
              .Append(",\"consent\":").Append(B(SafeConsent()))
              .Append(",\"updateRequired\":").Append(B(mod?.UpdateRequired == true))
              .Append('}');
            return sb.ToString();
        }

        private static bool SafeConsent()
        {
            try { return Consent.Accepted; } catch (Exception) { return false; }
        }

        private static string ActiveVesselJson()
        {
            try
            {
                if (HighLogic.LoadedScene != GameScenes.FLIGHT) return "null";
                Vessel v = FlightGlobals.ActiveVessel;
                return v == null ? "null" : VesselJson(v);
            }
            catch (Exception) { return "null"; }
        }

        private static string VesselsJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            try
            {
                var list = FlightGlobals.Vessels;
                if (list != null)
                {
                    bool first = true;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Vessel v = list[i];
                        if (v == null) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(VesselJson(v));
                    }
                }
            }
            catch (Exception e)
            {
                // An unreadable vessel list is itself a finding, so say so in-band
                // rather than 500ing the whole snapshot.
                sb.Append(sb.Length > 1 ? "," : "").Append("{\"error\":").Append(JobResult.Quote(e.Message)).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// One vessel. `pid` is the GUID string every removal, import and scenario record
        /// in this codebase keys on; `persistentId` is KSP's separate uint, kept because
        /// the two are routinely confused and a test that cannot tell them apart cannot
        /// prove a removal targeted the right hull.
        /// </summary>
        private static string VesselJson(Vessel v)
        {
            var sb = new StringBuilder();
            sb.Append("{\"pid\":").Append(JobResult.Quote(Safe(() => v.id.ToString())))
              .Append(",\"persistentId\":").Append(SafeU(() => v.persistentId))
              .Append(",\"name\":").Append(JobResult.Quote(Safe(() => v.vesselName ?? "")))
              .Append(",\"type\":").Append(JobResult.Quote(Safe(() => v.vesselType.ToString())))
              .Append(",\"situation\":").Append(JobResult.Quote(Safe(() => v.situation.ToString())))
              .Append(",\"body\":").Append(JobResult.Quote(Safe(() => v.mainBody != null ? v.mainBody.bodyName : "")))
              .Append(",\"landed\":").Append(B(SafeB(() => v.Landed)))
              .Append(",\"splashed\":").Append(B(SafeB(() => v.Splashed)))
              .Append(",\"loaded\":").Append(B(SafeB(() => v.loaded)))
              .Append(",\"packed\":").Append(B(SafeB(() => v.packed)))
              .Append(",\"dead\":").Append(B(SafeB(() => v.state == Vessel.State.DEAD)))
              .Append(",\"lat\":").Append(Num(SafeD(() => v.latitude)))
              .Append(",\"lon\":").Append(Num(SafeD(() => v.longitude)))
              .Append(",\"alt\":").Append(Num(SafeD(() => v.altitude)))
              .Append(",\"partCount\":").Append(SafeI(() => v.loaded && v.parts != null
                                                     ? v.parts.Count
                                                     : (v.protoVessel?.protoPartSnapshots?.Count ?? 0)))
              .Append(",\"crew\":").Append(CrewNamesJson(v))
              // Cheat-detection state, read from the production store rather than
              // re-derived. A tainted vessel is refused at submission by the server, so a
              // scenario that stages a flight with the F12 menu needs to see the taint
              // to tell "the test setup was rejected" from "the mod is broken".
              .Append(",\"cheatTainted\":").Append(B(SafeB(() => CheatDetection.IsTainted(v))))
              .Append(",\"cheatReasons\":")
              .Append(StringListJson(SafeReasons(v)))
              .Append('}');
            return sb.ToString();
        }

        private static List<string> SafeReasons(Vessel v)
        {
            try { return CheatDetection.ReasonsFor(v); }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>
        /// Crew aboard, via VesselTransfer.CrewOf — the same reader the transfer path
        /// uses, so a test sees what the code being tested sees. It resolves both a
        /// loaded vessel's live parts and an unloaded one's proto snapshots, which is
        /// the case that matters: a rescue wreck is almost never loaded.
        /// </summary>
        private static string CrewNamesJson(Vessel v)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            try
            {
                bool first = true;
                foreach (var pcm in VesselTransfer.CrewOf(v))
                {
                    if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JobResult.Quote(pcm.name));
                }
            }
            catch (Exception) { /* an unreadable crew reads as empty; the vessel row still lands */ }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// The full roster, with the three things a crew bug actually shows up in:
        /// rosterStatus (the freeze parks crew at Dead, and a leaked park is permanent),
        /// the ownership tag (a borrowed kerbal that should have been stripped, or a
        /// home one that should not have been), and whether the trait resolves at all
        /// (an unresolvable one NullRefs the Astronaut Complex mid-draw).
        /// </summary>
        /// <summary>Set by the most recent <see cref="RosterJson"/> call: null when the
        /// read was clean, a reason when it was not. Surfaced on /state as `rosterOk` so
        /// an assertion can refuse to run instead of passing on nothing.</summary>
        private static string lastRosterError;

        private static string RosterJson()
        {
            string readError = null;
            var sb = new StringBuilder(2048);
            sb.Append('[');
            try
            {
                var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
                if (roster == null)
                {
                    // No game is not a failure — there is genuinely no roster — but it is
                    // not "the roster is empty" either, and the caller has gameLoaded to
                    // tell them which.
                    readError = HighLogic.CurrentGame == null ? null : "CrewRoster was null";
                }
                else
                {
                    var assigned = AssignedVesselByCrewName();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    bool first = true;

                    foreach (var pcm in AllRosterEntries(roster, out readError))
                    {
                        if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                        if (!seen.Add(pcm.name)) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(RosterEntryJson(pcm, assigned));
                    }
                }
            }
            catch (Exception e)
            {
                readError = (readError == null ? "" : readError + "; ") + e.Message;
                sb.Append(sb.Length > 1 ? "," : "").Append("{\"error\":").Append(JobResult.Quote(e.Message)).Append('}');
            }
            sb.Append(']');
            lastRosterError = readError;
            return sb.ToString();
        }

        /// <summary>
        /// Every roster entry, whatever its status. Tourists are enumerated separately
        /// because KerbalRoster.Kerbals(statuses) does not return them — the same
        /// two-step LsReflect.FindCrew does, for the same reason.
        /// </summary>
        /// <summary>
        /// Every roster entry, whatever its status, with any read failure REPORTED
        /// rather than swallowed.
        ///
        /// The first version caught each enumeration and ignored it, so a throwing
        /// roster came back as an empty one — and an empty roster is not a neutral
        /// answer here, it is a passing one. "No borrowed ghosts" and "every trait
        /// resolves" are both trivially true of nothing, so T5 and T7 would have
        /// reported PASS against a roster this code could not read. Observed live: the
        /// save held four kerbals, the vessel list and CrewedNames() both saw them, and
        /// this returned zero with no error anywhere.
        ///
        /// A harness that cannot distinguish "empty" from "I could not tell" is worse
        /// than no harness, so the distinction is now carried out to the caller and every
        /// assertion built on it fails closed.
        /// </summary>
        private static IEnumerable<ProtoCrewMember> AllRosterEntries(KerbalRoster roster, out string error)
        {
            error = null;
            var statuses = new[]
            {
                ProtoCrewMember.RosterStatus.Available,
                ProtoCrewMember.RosterStatus.Assigned,
                ProtoCrewMember.RosterStatus.Missing,
                ProtoCrewMember.RosterStatus.Dead,
            };

            var all = new List<ProtoCrewMember>();
            try { foreach (var p in roster.Kerbals(statuses)) all.Add(p); }
            catch (Exception e) { error = "Kerbals(): " + e.Message; }

            // Tourists and applicants are enumerated separately because
            // KerbalRoster.Kerbals(statuses) does not return them — the same two-step
            // LsReflect.FindCrew does. Their failures are appended rather than
            // overwriting, so one bad collection does not hide another.
            try { foreach (var p in roster.Tourist) all.Add(p); }
            catch (Exception e) { error = (error == null ? "" : error + "; ") + "Tourist: " + e.Message; }

            try { foreach (var p in roster.Applicants) all.Add(p); }
            catch (Exception e) { error = (error == null ? "" : error + "; ") + "Applicants: " + e.Message; }

            return all;
        }

        private static string RosterEntryJson(ProtoCrewMember pcm, Dictionary<string, string> assigned)
        {
            string name = pcm.name;
            bool borrowed = false;
            try { borrowed = VesselTransfer.IsBorrowedCrewName(name); } catch (Exception) { }

            string stripped = name;
            try { if (borrowed) stripped = VesselTransfer.StripOwnershipTag(name); } catch (Exception) { }

            // experienceTrait null is the exact shape that breaks the Astronaut Complex,
            // so it is reported as its own field rather than folded into the trait string.
            bool traitResolves = false;
            try { traitResolves = pcm.experienceTrait != null; } catch (Exception) { }

            // "name|pid" — the pid half matters because vessel names are not unique and
            // a test that clones a craft necessarily produces two of the same name. The
            // first version reported the name alone and first-match-wins, so arrivals
            // aboard the copy were reported as being aboard the original, which is the
            // precise confusion a crew-ownership test exists to detect.
            string assignedTo = "", assignedPid = "";
            if (assigned != null && name != null && assigned.TryGetValue(name, out string pair))
            {
                int bar = pair.IndexOf('|');
                assignedTo = bar >= 0 ? pair.Substring(0, bar) : pair;
                assignedPid = bar >= 0 ? pair.Substring(bar + 1) : "";
            }

            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(JobResult.Quote(name))
              .Append(",\"status\":").Append(JobResult.Quote(Safe(() => pcm.rosterStatus.ToString())))
              .Append(",\"type\":").Append(JobResult.Quote(Safe(() => pcm.type.ToString())))
              .Append(",\"trait\":").Append(JobResult.Quote(Safe(() => pcm.trait ?? "")))
              .Append(",\"traitResolves\":").Append(B(traitResolves))
              .Append(",\"level\":").Append(SafeI(() => pcm.experienceLevel))
              .Append(",\"borrowed\":").Append(B(borrowed))
              .Append(",\"baseName\":").Append(JobResult.Quote(stripped ?? ""))
              .Append(",\"aboard\":").Append(JobResult.Quote(assignedTo ?? ""))
              .Append(",\"aboardPid\":").Append(JobResult.Quote(assignedPid ?? ""))
              .Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// crew name → the vessel they are actually aboard, built from the vessel list
        /// rather than from rosterStatus. The two disagree in precisely the states worth
        /// testing: the emergency freeze parks a kerbal at Dead while they are still in a
        /// seat, and a proto vessel read back from a ConfigNode carries names KSP has not
        /// resolved to roster entries yet.
        /// </summary>
        private static Dictionary<string, string> AssignedVesselByCrewName()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var list = FlightGlobals.Vessels;
                if (list == null) return map;
                for (int i = 0; i < list.Count; i++)
                {
                    Vessel v = list[i];
                    if (v == null) continue;
                    string vn = Safe(() => v.vesselName ?? "");
                    try
                    {
                        string vpid = Safe(() => v.id.ToString());
                        foreach (var pcm in VesselTransfer.CrewOf(v))
                            if (pcm != null && !string.IsNullOrEmpty(pcm.name) && !map.ContainsKey(pcm.name))
                                map[pcm.name] = vn + "|" + vpid;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return map;
        }

        /// <summary>
        /// The three derived sets a crew assertion is actually written against, computed
        /// by the production code rather than re-implemented here — see the note on
        /// VesselTransfer.DebugCrewedNames for why that distinction is the whole point.
        ///
        /// `ghostCandidates` is what PurgeBorrowedGhostCrew WOULD drop if called now:
        /// borrowed, Dead or Missing, and not held by an active freeze record. Reported
        /// rather than acted on, so a test can assert the sweep has nothing to do — the
        /// healthy state — without the act of asking changing the answer.
        ///
        /// `orphanedBorrowed` is its wider counterpart and is NOT a prediction of any
        /// automatic sweep: it is what the player's "Release stranded crew" button would
        /// drop — borrowed, crewing nothing, unfrozen, and unclaimed by any live
        /// contract, in any roster status. It is empty while the contract list is
        /// unfetched, on purpose, so a driver that reads it before a refresh sees
        /// "nothing to release" rather than a list that would delete a live rescue.
        /// </summary>
        private static string CrewDerivationsJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"crewedNow\":").Append(StringSetJson(SafeCrewedNames()))
              .Append(",\"ghostCandidates\":").Append(StringListJson(GhostCandidates()))
              .Append(",\"orphanedBorrowed\":").Append(StringListJson(SafeOrphanedBorrowed()))
              .Append(",\"brokenTraits\":").Append(StringListJson(SafeBrokenTraits()))
              .Append('}');
            return sb.ToString();
        }

        private static List<string> SafeOrphanedBorrowed()
        {
            try { return VesselTransfer.OrphanedBorrowedCrew(); }
            catch (Exception) { return new List<string>(); }
        }

        private static IEnumerable<string> SafeCrewedNames()
        {
            try { return VesselTransfer.DebugCrewedNames(); }
            catch (Exception) { return new List<string>(); }
        }

        private static List<string> SafeBrokenTraits()
        {
            try { return VesselTransfer.FindUnresolvableTraitCrew(); }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>
        /// Mirrors PurgeBorrowedGhostCrew's selection exactly — borrowed name, Dead or
        /// Missing, not named by a live freeze record, and not still claimed by any
        /// vessel — without removing anything.
        ///
        /// This one IS a re-implementation of a private selection, which the rest of the
        /// file avoids. It is worth the drift risk only because the alternative is a
        /// destructive read: the sweep's return value is a count of kerbals it already
        /// deleted, so asking it what it would do is asking it to do it. The test that
        /// covers the drift is the pair — assert this list, call the sweep, assert the
        /// count matches.
        /// </summary>
        private static List<string> GhostCandidates()
        {
            var found = new List<string>();
            try
            {
                var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
                if (roster == null) return found;

                var frozen = new HashSet<string>(StringComparer.Ordinal);
                var records = GKContractScenario.Instance != null ? GKContractScenario.Instance.Immunities : null;
                if (records != null)
                    foreach (var rec in records)
                    {
                        if (rec?.Crew == null) continue;
                        foreach (var c in rec.Crew)
                            if (c != null && !string.IsNullOrEmpty(c.Name)) frozen.Add(c.Name);
                    }

                var statuses = new[]
                {
                    ProtoCrewMember.RosterStatus.Dead,
                    ProtoCrewMember.RosterStatus.Missing,
                };
                // Mirrors the sweep's "still claimed by a vessel" guard. Drift here is
                // exactly what T5's prediction-vs-sweep pair exists to catch.
                var crewedNow = new HashSet<string>(VesselTransfer.DebugCrewedNames(),
                                                    StringComparer.Ordinal);
                foreach (var pcm in roster.Kerbals(statuses))
                {
                    if (pcm == null || !VesselTransfer.IsBorrowedCrewName(pcm.name)) continue;
                    if (frozen.Contains(pcm.name)) continue;
                    if (crewedNow.Contains(pcm.name)) continue;
                    found.Add(pcm.name);
                }
            }
            catch (Exception) { }
            return found;
        }

        /// <summary>
        /// The persisted queues. Every one of them is a promise the mod made to do
        /// something later — remove this hull, thaw this crew, delete this submission —
        /// and a broken hand-over is almost always a promise that was dropped or one
        /// that fired twice. None of it is visible in-game.
        /// </summary>
        private static string ScenarioJson()
        {
            var sc = GKContractScenario.Instance;
            var sb = new StringBuilder(1024);
            sb.Append("{\"present\":").Append(B(sc != null));
            if (sc == null) { sb.Append('}'); return sb.ToString(); }

            // The universal time, because every rollback rule in the removal queue is a
            // comparison against it. Without it a scenario cannot tell "the guard band
            // was wider than my test's gap" from "the guard never fired", and those two
            // want opposite fixes.
            sb.Append(",\"ut\":").Append(Num(SafeD(() => Planetarium.GetUniversalTime())));

            // Pending removals
            sb.Append(",\"pendingRemovals\":[");
            try
            {
                bool first = true;
                foreach (var kv in sc.PendingRescueRemovals)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    var r = kv.Value;
                    sb.Append("{\"pid\":").Append(JobResult.Quote(kv.Key))
                      .Append(",\"name\":").Append(JobResult.Quote(r?.Name ?? ""))
                      .Append(",\"crewFate\":").Append(JobResult.Quote(r != null ? r.CrewFate.ToString() : ""))
                      .Append(",\"crew\":").Append(StringListJson(r?.Crew))
                      // Whether the hull is still here. A queued removal whose vessel is
                      // already gone is the exact residue PurgeBorrowedGhostCrew exists
                      // to clean up after, so the pair is worth reading together.
                      .Append(",\"vesselStillPresent\":").Append(B(SafeB(() => VesselTransfer.VesselExists(kv.Key))))
                      .Append('}');
                }
            }
            catch (Exception) { }
            sb.Append(']');

            // Freeze records
            sb.Append(",\"immunities\":[");
            try
            {
                bool first = true;
                foreach (var rec in sc.Immunities)
                {
                    if (rec == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"contractId\":").Append(JobResult.Quote(rec.ContractId ?? ""))
                      .Append(",\"vesselPid\":").Append(JobResult.Quote(rec.VesselPid ?? ""))
                      .Append(",\"builtWithLs\":").Append(JobResult.Quote(rec.BuiltWithLs ?? ""))
                      .Append(",\"vesselStillPresent\":").Append(B(SafeB(() => VesselTransfer.VesselExists(rec.VesselPid))))
                      .Append(",\"crew\":[");
                    bool c1 = true;
                    foreach (var c in (rec.Crew ?? new List<StasisCrew>()))
                    {
                        if (c == null) continue;
                        if (!c1) sb.Append(',');
                        c1 = false;
                        sb.Append("{\"name\":").Append(JobResult.Quote(c.Name ?? ""))
                          .Append(",\"partFlightId\":").Append(c.PartFlightId)
                          .Append(",\"seatIdx\":").Append(c.SeatIdx)
                          .Append('}');
                    }
                    sb.Append("]}");
                }
            }
            catch (Exception) { }
            sb.Append(']');

            // Wrecks this save spawned, with the renames only this record remembers.
            sb.Append(",\"rescueWrecks\":[");
            try
            {
                bool first = true;
                foreach (var kv in sc.DebugRescueWrecks)
                {
                    var w = kv.Value;
                    if (w == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"contractId\":").Append(JobResult.Quote(kv.Key))
                      .Append(",\"pid\":").Append(JobResult.Quote(w.Pid ?? ""))
                      .Append(",\"partFlightIds\":[");
                    bool p1 = true;
                    foreach (var id in (w.PartFlightIds ?? new List<uint>()))
                    {
                        if (!p1) sb.Append(',');
                        p1 = false;
                        sb.Append(id);
                    }
                    sb.Append("],\"crewRenames\":{");
                    bool r1 = true;
                    foreach (var rn in (w.CrewRenames ?? new Dictionary<string, string>()))
                    {
                        if (!r1) sb.Append(',');
                        r1 = false;
                        sb.Append(JobResult.Quote(rn.Key)).Append(':').Append(JobResult.Quote(rn.Value));
                    }
                    sb.Append("}}");
                }
            }
            catch (Exception) { }
            sb.Append(']');

            sb.Append(",\"importedVessels\":").Append(StringSetJson(SafeImported(sc)));
            sb.Append(",\"outstandingSubmissions\":").Append(StringListJson(SafeOutstanding(sc)));

            sb.Append(",\"submittedPids\":{");
            try
            {
                bool first = true;
                foreach (var kv in sc.DebugRescueSubmittedPids)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JobResult.Quote(kv.Key)).Append(':').Append(StringListJson(kv.Value));
                }
            }
            catch (Exception) { }
            sb.Append('}');

            sb.Append('}');
            return sb.ToString();
        }

        private static IEnumerable<string> SafeImported(GKContractScenario sc)
        {
            try { return sc.DebugImportedVessels; } catch (Exception) { return new List<string>(); }
        }

        private static List<string> SafeOutstanding(GKContractScenario sc)
        {
            try { return sc.OutstandingRescueSubmissions(); } catch (Exception) { return new List<string>(); }
        }

        /// <summary>
        /// The contract list exactly as ClientState holds it, plus the loading flag.
        ///
        /// The flag is not decoration: HomeboundCrewFor draws a hard distinction between
        /// "nothing is attested" and "the list has not been fetched yet", and treating
        /// the two alike is what re-tags the issuer's own kerbals under the rescuer. A
        /// driver that asserts on an empty contract list without checking whether it has
        /// ever loaded would be reproducing that exact bug in the harness.
        /// </summary>
        private static string ContractsJson()
        {
            var mod = GeneKermanMod.Instance;
            var state = mod?.State;
            var sb = new StringBuilder(1024);
            sb.Append("{\"contractsLoaded\":").Append(B(state?.ContractList != null))
              .Append(",\"contractsLoading\":").Append(B(state?.ContractsLoading == true))
              .Append(",\"unread\":").Append(mod?.UnreadNotifications ?? 0)
              .Append(",\"contracts\":");
            try
            {
                var list = state?.ContractList;
                if (list == null) sb.Append("null");
                else
                {
                    var copy = new List<object>();
                    foreach (var o in list) copy.Add(o);
                    sb.Append(MiniJSON.Serialize(copy));
                }
            }
            catch (Exception) { sb.Append("null"); }
            sb.Append('}');
            return sb.ToString();
        }

        // ══ Commands ════════════════════════════════════════════════════════
        //
        // Each answers 200 with {"ok":bool,"message":...} and refuses on a failed
        // precondition. A refusal is a normal 200 with ok:false when the state is simply
        // wrong for the request (no save loaded, in the editor), and a 4xx when the
        // request itself is malformed — the same split the mod's own panels read.

        /// <summary>
        /// Force a save. The driver's synchronisation primitive: a test that asserts on
        /// persistent.sfs has to know the write already happened, and KSP's own autosave
        /// is on nobody's schedule.
        /// </summary>
        private static JobResult SaveNow()
        {
            if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
            // VesselTransfer.SaveNow refuses in flight itself — KSP would write a
            // half-serialised flight state — so the scene check is its, not ours.
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                return Refused("Cannot save from flight; switch to the Space Center first.");
            try
            {
                VesselTransfer.SaveNow();
                return Ok("Saved.");
            }
            catch (Exception e) { return Refused("Save failed: " + e.Message); }
        }

        /// <summary>
        /// Run the ghost sweep and report what it dropped. Destructive by nature — the
        /// count IS the removal — which is why /state reports the candidates separately
        /// and a driver asserts the two agree.
        /// </summary>
        private static JobResult PurgeGhosts(bool orphans)
        {
            if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
            try
            {
                // Two different operations behind one route, and the flag is required
                // rather than defaulted for the crew_fate reason: the wide one is the
                // player's button, not a sweep, and a driver that got it by accident
                // would delete borrowed crew a live contract is still counting on.
                if (orphans)
                    return JobResult.Json("{\"ok\":true,\"message\":" +
                        JobResult.Quote(VesselTransfer.PurgeOrphanedBorrowedCrew()) + "}");
                int n = VesselTransfer.PurgeBorrowedGhostCrew();
                return JobResult.Json("{\"ok\":true,\"removed\":" + n + "}");
            }
            catch (Exception e) { return Refused("Sweep failed: " + e.Message); }
        }

        /// <summary>
        /// Run TraitRepair over the roster — the same call the Tools card and the
        /// notification button make.
        ///
        /// Destructive by design, and deliberately reachable only from an explicit
        /// request: the trait string is the ONLY record of a kerbal's profession, so
        /// overwriting it is a decision, not a cleanup. It is safe to test because the
        /// repair is a loan rather than a deletion — the original is copied into
        /// PluginData/trait_repairs.cfg first, and RestoreRecovered hands it back once
        /// the defining mod is installed again.
        /// </summary>
        private static JobResult RepairTraits()
        {
            if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
            try
            {
                var before = VesselTransfer.FindUnresolvableTraitCrew();
                string summary = TraitRepair.Repair();
                var after = VesselTransfer.FindUnresolvableTraitCrew();
                var sb = new StringBuilder();
                sb.Append("{\"ok\":true,\"summary\":").Append(JobResult.Quote(summary ?? ""))
                  .Append(",\"brokenBefore\":").Append(StringListJson(before))
                  .Append(",\"brokenAfter\":").Append(StringListJson(after))
                  .Append('}');
                return JobResult.Json(sb.ToString());
            }
            catch (Exception e) { return Refused("Trait repair threw: " + e.Message); }
        }

        /// <summary>Hand back any professions whose defining mod is present again — the
        /// other half of the loan, and the half that makes the repair reversible.</summary>
        private static JobResult RestoreTraits()
        {
            if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
            try
            {
                var restored = TraitRepair.RestoreRecovered();
                return JobResult.Json("{\"ok\":true,\"restored\":" + StringListJson(restored) + "}");
            }
            catch (Exception e) { return Refused("Trait restore threw: " + e.Message); }
        }

        /// <summary>Kick the import poll the mod runs on its own timer, so a test does
        /// not have to wait out the interval for a queued craft to arrive.</summary>
        private static JobResult PollImports()
        {
            var state = GeneKermanMod.Instance?.State;
            if (state == null) return Refused("Mod not ready.");
            if (GeneKermanMod.Instance?.Api?.IsLinked != true) return Refused("Not linked.");
            try { state.PollCraftImports(); return Ok("Import poll requested."); }
            catch (Exception e) { return Refused("Poll failed: " + e.Message); }
        }

        /// <summary>Re-fetch profile, missions, contracts and notifications.</summary>
        private static JobResult RefreshAll()
        {
            var state = GeneKermanMod.Instance?.State;
            if (state == null) return Refused("Mod not ready.");
            if (GeneKermanMod.Instance?.Api?.IsLinked != true) return Refused("Not linked.");
            try { state.RefreshAll(); return Ok("Refresh requested."); }
            catch (Exception e) { return Refused("Refresh failed: " + e.Message); }
        }

        /// <summary>
        /// Remove a vessel by pid, with an explicit crew fate.
        ///
        /// The fate is required rather than defaulted on purpose. RemoveVesselFromSave
        /// defaults to LeavesWithCraft, which kills everyone aboard, and a harness that
        /// silently took that default would delete a tester's crew on a typo'd request.
        /// The caller has to say which side of the hand-over this is, which is the same
        /// rule the production callers follow.
        /// </summary>
        private void HandleRemoveVessel(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }

            string pid = MiniJSON.GetString(body, "pid", "");
            string fateRaw = MiniJSON.GetString(body, "crew_fate", "");
            if (string.IsNullOrEmpty(pid)) { BadRequest(ctx, "pid is required"); return; }
            if (string.IsNullOrEmpty(fateRaw))
            {
                BadRequest(ctx, "crew_fate is required (LeavesWithCraft|BorrowedOnly|StaysInRoster)");
                return;
            }

            VesselTransfer.CrewFate fate;
            try { fate = (VesselTransfer.CrewFate)Enum.Parse(typeof(VesselTransfer.CrewFate), fateRaw, true); }
            catch (Exception)
            {
                BadRequest(ctx, "unknown crew_fate '" + fateRaw + "'");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                if (!VesselTransfer.VesselExists(pid)) return Refused("No such vessel in this save: " + pid);
                try
                {
                    // Through GeneKermanMod.QueueRescueVesselRemoval, not straight to
                    // VesselTransfer.RemoveVesselFromSave, and the difference is the
                    // whole point of the scenario built on this.
                    //
                    // The primitive removes a vessel and answers Deferred when the pid is
                    // the one being flown — but deferring is all it does; it records
                    // nothing, so the removal is simply forgotten. Persisting the intent
                    // is the caller's job, and every production caller (ToolActions,
                    // ContractCreation, the rescue paths) goes through the queue. Calling
                    // the primitive here tested that Deferred is returned and nothing
                    // else, which is precisely the half that cannot lose a player's ship.
                    var mod = GeneKermanMod.Instance;
                    if (mod == null) return Refused("Mod not ready.");
                    string name = VesselTransfer.GetVesselName(pid);
                    bool queued = mod.QueueRescueVesselRemoval(pid, name, fate);

                    // Whether the hull actually went now, or is waiting on a safe scene.
                    bool gone = !VesselTransfer.VesselExists(pid);
                    return JobResult.Json("{\"ok\":" + B(queued) +
                                          ",\"queued\":" + B(queued) +
                                          ",\"removedNow\":" + B(gone) +
                                          ",\"vessel\":" + JobResult.Quote(name) + "}");
                }
                catch (Exception e) { return Refused("Removal threw: " + e.Message); }
            }));
        }

        /// <summary>
        /// Change scene. The single most expensive thing a driver does and the one it
        /// cannot do any other way — a crew hand-over is only settled at the Space
        /// Center, and a deferred removal only fires there.
        ///
        /// Answers 202 with a job id: the load takes 5-40s on a modded install, during
        /// which Update() does not run at all, so holding the request open would burn
        /// the queue's whole 30s timeout and report a false failure on a load that
        /// worked. Completion is broadcast on the event stream.
        /// </summary>
        private void HandleScene(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string want = MiniJSON.GetString(body, "scene", "");

            GameScenes target;
            switch ((want ?? "").ToUpperInvariant())
            {
                case "SPACECENTER": target = GameScenes.SPACECENTER; break;
                case "TRACKSTATION": target = GameScenes.TRACKSTATION; break;
                default:
                    // Deliberately short. FLIGHT is not offered because entering flight
                    // means choosing a vessel and waiting on physics, and EDITOR/MAINMENU
                    // would drop the save this bridge exists to inspect.
                    BadRequest(ctx, "scene must be SPACECENTER or TRACKSTATION");
                    return;
            }

            string jobId = bridge.Jobs.Begin();
            // Whether a coroutine was actually started, tracked as a flag rather than
            // sniffed back out of the response body. Reading intent out of JSON we just
            // wrote is the kind of thing that keeps working until someone rewords a
            // message, and then silently answers 202 with a job id nothing will complete.
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                if (HighLogic.LoadedScene == target) return Ok("Already there.");
                try
                {
                    GeneKermanMod.Instance.RunCoroutine(SceneRoutine(target, jobId));
                    started = true;
                    return Ok("Scene change started.");
                }
                catch (Exception e) { return Refused("Scene change threw: " + e.Message); }
            });

            if (!started)
            {
                // Already-there and refused are both final and carry no job. The job
                // record is closed either way so a driver polling it never hangs.
                bridge.Jobs.Complete(jobId, pre.Status == 200 && !pre.Body.Contains("\"ok\":false"), "");
                Respond(ctx, pre);
                return;
            }

            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        private IEnumerator SceneRoutine(GameScenes target, string jobId)
        {
            HighLogic.LoadScene(target);
            // Wait for the scene to actually be up, not merely requested: a driver that
            // acts on the "done" event has to be able to read state immediately after it.
            float deadline = Time.realtimeSinceStartup + 180f;
            while (HighLogic.LoadedScene != target && Time.realtimeSinceStartup < deadline)
                yield return null;
            // One more frame so the scene's own Start()s have run — GKContractScenario
            // is instantiated during the load and reading it a frame early reports a
            // missing scenario module on a save that has one.
            yield return null;

            bool ok = HighLogic.LoadedScene == target;
            bridge.Jobs.Complete(jobId, ok, ok ? target.ToString() : "timed out loading " + target);
            bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
        }

        /// <summary>
        /// Spawn the stranded wreck for an accepted rescue — the same call the sidebar's
        /// button makes, deliberately routed through ClientState rather than reimplemented,
        /// because the per-save dedup, the orbit-epoch freeze and the emergency-freeze
        /// registration all hang off that path and a test that skipped them would be
        /// testing something the game never does.
        /// </summary>
        private void HandleSpawnWreck(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string contractId = MiniJSON.GetString(body, "contract_id", "");
            if (string.IsNullOrEmpty(contractId)) { BadRequest(ctx, "contract_id is required"); return; }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                var state = mod?.State;
                if (state == null) return Refused("Mod not ready.");
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                if (mod.Api?.IsLinked != true) return Refused("Not linked.");
                if (HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                    HighLogic.LoadedScene != GameScenes.TRACKSTATION &&
                    HighLogic.LoadedScene != GameScenes.FLIGHT)
                    return Refused("Must be in the Space Center, Tracking Station or flight.");

                // The contract dict, not the id: RequestSpawnRescueWreck reads issuer_id
                // off it to decide crew ownership, which is the whole point of the fix
                // this bridge exists to verify. Looking it up here also means an
                // unfetched contract list refuses out loud instead of spawning nothing.
                var contract = FindContract(state, contractId);
                if (contract == null)
                {
                    return Refused(state.ContractList == null
                        ? "The contract list has not been fetched yet; call /actions/refresh first."
                        : "No contract with id " + contractId + " in this client's list.");
                }
                if (GKContractScenario.Instance != null &&
                    GKContractScenario.Instance.HasImportedVessel(contractId))
                    return Refused("This contract's vessel has already been imported into this save.");

                try
                {
                    state.RequestSpawnRescueWreck(contract, (ok, msg) =>
                    {
                        bridge.Jobs.Complete(jobId, ok, msg ?? "");
                        bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                    });
                    started = true;
                    return Ok("Spawn started.");
                }
                catch (Exception e) { return Refused("Spawn threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        private static Dictionary<string, object> FindContract(ClientState state, string contractId)
        {
            var list = state?.ContractList;
            if (list == null) return null;
            foreach (var o in list)
            {
                var c = o as Dictionary<string, object>;
                if (c != null && MiniJSON.GetString(c, "contract_id") == contractId) return c;
            }
            return null;
        }

        /// <summary>
        /// Quicksend the active vessel to another account — the T4 case. Routed through
        /// ToolActions so the send is byte-identical to the one the Tools panel makes,
        /// including the bake chain and the live-vessel removal that follows a confirmed
        /// hand-over.
        /// </summary>
        private void HandleQuicksend(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string recipientId = MiniJSON.GetString(body, "recipient_id", "");
            string recipientName = MiniJSON.GetString(body, "recipient_name", "");
            string kind = MiniJSON.GetString(body, "kind", "");
            if (string.IsNullOrEmpty(recipientId)) { BadRequest(ctx, "recipient_id is required"); return; }
            if (kind != "craft" && kind != "vessel")
            {
                BadRequest(ctx, "kind must be 'craft' (a blueprint copy) or 'vessel' (a live hand-over)");
                return;
            }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                if (mod?.Api == null) return Refused("Mod not ready.");
                if (mod.Api.IsLinked != true) return Refused("Not linked.");
                // Only the live hand-over needs a flight: a "craft" send is a blueprint
                // read out of the editor, and gating it on flight would refuse the one
                // send that is safe in any scene.
                if (kind == "vessel")
                {
                    if (HighLogic.LoadedScene != GameScenes.FLIGHT) return Refused("A vessel send needs flight.");
                    if (FlightGlobals.ActiveVessel == null) return Refused("No active vessel.");
                }

                try
                {
                    mod.RunCoroutine(ToolActions.QuicksendCurrent(recipientId, recipientName, kind,
                        (ok, msg) =>
                        {
                            bridge.Jobs.Complete(jobId, ok, msg ?? "");
                            bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                        }));
                    started = true;
                    return Ok("Quicksend started.");
                }
                catch (Exception e) { return Refused("Quicksend threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        /// <summary>
        /// Decide a friend quicksend offer: accept it, or decline it.
        ///
        /// The receiving half of the hand-over, and the last step of a two-player craft
        /// exchange that still needed either the mouse or a hand-written API call. It
        /// deliberately drives <see cref="GiftInbox"/>'s own entry points rather than
        /// POSTing to the server, for the same reason `issue-rescue` calls
        /// `ContractCreation.Create`: the accept is not just a status flip on the
        /// server. It also refreshes the local inbox, and — when the scene can take a
        /// live vessel — runs the import on the spot through
        /// `ClientState.PollCraftImports`, which is where the ownership tagging, the
        /// homebound attestation and the return-pid reconciliation all happen. A
        /// harness that accepted over HTTP would exercise none of that and would report
        /// green on a client that never ran a line of it.
        ///
        /// `import_id` is optional only when exactly one offer is waiting. With two it
        /// is required rather than defaulted, on the `crew_fate` principle: a decline
        /// is destructive to somebody's ship, and a harness that guesses which offer it
        /// meant would eventually guess wrong in a way nobody would notice.
        /// </summary>
        private void HandleGift(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string action = MiniJSON.GetString(body, "action", "").ToLowerInvariant();
            string importId = MiniJSON.GetString(body, "import_id", "");
            if (action != "accept" && action != "decline" && action != "list")
            {
                BadRequest(ctx, "action must be 'accept', 'decline' or 'list'");
                return;
            }

            // A plain read needs no job: answer the offers as the client currently holds
            // them, having asked for a refresh first.
            if (action == "list")
            {
                var listed = queue.RunSync(() =>
                {
                    var mod = GeneKermanMod.Instance;
                    if (mod?.Api == null) return Refused("Mod not ready.");
                    if (mod.Api.IsLinked != true) return Refused("Not linked.");
                    GiftInbox.Refresh();
                    return Ok("Refresh requested; read /gk/debug/state or call again.");
                });
                Respond(ctx, listed);
                return;
            }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                if (mod?.Api == null) return Refused("Mod not ready.");
                if (mod.Api.IsLinked != true) return Refused("Not linked.");
                try
                {
                    mod.RunCoroutine(DecideGift(action, importId, jobId));
                    started = true;
                    return Ok("Gift decision started.");
                }
                catch (Exception e) { return Refused("Gift decision threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        /// <summary>Refresh the inbox, find the offer, and run the real accept/decline.
        /// The wait is on the offer actually appearing rather than on a fixed delay: the
        /// send that produced it completed on the *other* instance, so how long the
        /// server takes to show it here is not something this side can know.</summary>
        private IEnumerator DecideGift(string action, string importId, string jobId)
        {
            GiftInbox.Refresh();

            Dictionary<string, object> offer = null;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                int waiting = GiftInbox.Offers.Count;
                if (waiting > 0)
                {
                    if (!string.IsNullOrEmpty(importId))
                    {
                        foreach (var o in GiftInbox.Offers)
                            if (MiniJSON.GetString(o, "import_id", "") == importId) { offer = o; break; }
                    }
                    else if (waiting == 1)
                    {
                        offer = GiftInbox.Offers[0];
                    }
                    else
                    {
                        bridge.Jobs.Complete(jobId, false,
                            waiting + " offers are waiting — pass import_id to say which one.");
                        bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                        yield break;
                    }
                    if (offer != null) break;
                }
                yield return new WaitForSeconds(1f);
                GiftInbox.Refresh();
            }

            if (offer == null)
            {
                bridge.Jobs.Complete(jobId, false,
                    string.IsNullOrEmpty(importId)
                        ? "No offers are waiting."
                        : "No offer with import_id " + importId + " arrived within 30s.");
                bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                yield break;
            }

            bool done = false;
            Action<bool, string> onDone = (ok, msg) =>
            {
                bridge.Jobs.Complete(jobId, ok, msg ?? "");
                bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                done = true;
            };
            if (action == "accept") yield return GiftInbox.Accept(offer, onDone);
            else yield return GiftInbox.Reject(offer, onDone);

            if (!done)
            {
                bridge.Jobs.Complete(jobId, false, "the decision reported nothing");
                bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
            }
        }

        /// <summary>
        /// Issue a rescue contract against the vessel being flown.
        ///
        /// Two of T1's four manual steps were pure setup — issue, then accept — and
        /// neither is what T1 tests. Everything it actually asserts about the crew path
        /// (the ownership tag, the emergency-freeze record, and whether the ghost sweep
        /// would take frozen crew) sits between the wreck spawning and the rescuer
        /// flying, so leaving the setup manual left those assertions unrun rather than
        /// merely inconvenient.
        ///
        /// This calls the real <see cref="ContractCreation.Create"/> with the same
        /// Request the sidebar's form builds. That is the whole point: the vessel
        /// snapshot, the permanence gate's actual consequence, the per-save dedup and
        /// the orbit-epoch freeze all hang off that path, and a route that posted to the
        /// server itself would test a rescue no player can issue.
        ///
        /// It destroys the issuer's vessel, exactly as it does in play. Callers back the
        /// save up first; nothing here does it for them, because a fixture that quietly
        /// protects you from a scenario's real consequence is how a hand-over bug
        /// survives its own test.
        /// </summary>
        private void HandleIssueRescue(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }

            string contractorId = MiniJSON.GetString(body, "contractor_id", "");
            if (string.IsNullOrEmpty(contractorId)) { BadRequest(ctx, "contractor_id is required"); return; }

            var r = new ContractCreation.Request
            {
                Kind = "rescue",
                ContractorId = contractorId,
                ContractorName = MiniJSON.GetString(body, "contractor_name", ""),
                Mission = MiniJSON.GetString(body, "mission", "Bridge test rescue — bring the crew home."),
                Payment = MiniJSON.GetInt(body, "payment", 1000),
                Fine = MiniJSON.GetInt(body, "fine", 0),
                DueDate = MiniJSON.GetString(body, "due_date", ""),
                RescueMode = MiniJSON.GetString(body, "mode", "orbit"),
                RescueRecovery = MiniJSON.GetString(body, "recovery", "crew"),
                RescueBody = MiniJSON.GetString(body, "body", "Kerbin"),
                // Absent is the answer, never a zero — the same rule the server states
                // for ap/pe and lat/lon. A caller that sends no target is asking for
                // "anywhere on the body", which is the rescue T1 wants: the delivery
                // orbit is not what it is testing, and a tight one would make the
                // scenario fail on flying rather than on the crew path.
                RequireAlt = MiniJSON.GetBool(body, "require_alt", false),
                RequirePos = MiniJSON.GetBool(body, "require_pos", false),
            };
            if (MiniJSON.Has(body, "ap")) r.ApKm = MiniJSON.GetDouble(body, "ap", r.ApKm);
            if (MiniJSON.Has(body, "pe")) r.PeKm = MiniJSON.GetDouble(body, "pe", r.PeKm);
            if (MiniJSON.Has(body, "lat")) r.Lat = MiniJSON.GetDouble(body, "lat", 0);
            if (MiniJSON.Has(body, "lon")) r.Lon = MiniJSON.GetDouble(body, "lon", 0);
            if (MiniJSON.Has(body, "min_dv")) r.MinDvMs = MiniJSON.GetDouble(body, "min_dv", 0);

            if (string.IsNullOrEmpty(r.DueDate))
                r.DueDate = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd");

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                if (mod?.Api == null) return Refused("Mod not ready.");
                if (mod.Api.IsLinked != true) return Refused("Not linked.");
                if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                    return Refused("A rescue is issued against the vessel being flown — "
                                   + "enter flight on the craft that is to become the wreck.");
                var av = FlightGlobals.ActiveVessel;
                if (av == null) return Refused("No active vessel.");
                // Refused rather than allowed with a warning: a rescue whose wreck has
                // nobody aboard has no crew to come home, and every assertion T1 makes
                // about the ownership tag would then pass over an empty set.
                if (av.GetCrewCount() <= 0)
                    return Refused("The stranded vessel has no crew — a crewless rescue "
                                   + "makes every crew assertion true of nothing.");

                string why;
                if (!ContractCreation.Validate(r, out why)) return Refused(why);

                try
                {
                    mod.RunCoroutine(ContractCreation.Create(r, (ok, msg) =>
                    {
                        bridge.Jobs.Complete(jobId, ok, msg ?? "");
                        bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                    }));
                    started = true;
                    return Ok("Issuing rescue against " + av.vesselName + "…");
                }
                catch (Exception e) { return Refused("Create threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        /// <summary>
        /// Accept an offered contract — T1's second manual step, and the rescuer's half
        /// of the same setup. Goes through ClientState so the contract list, the unread
        /// count and the local notification merge all move as they do in play.
        /// </summary>
        private void HandleAcceptContract(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string contractId = MiniJSON.GetString(body, "contract_id", "");
            string issuerName = MiniJSON.GetString(body, "issuer_name", "");
            if (string.IsNullOrEmpty(contractId)) { BadRequest(ctx, "contract_id is required"); return; }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                if (mod?.Api == null) return Refused("Mod not ready.");
                if (mod.Api.IsLinked != true) return Refused("Not linked.");
                var state = GeneKermanMod.Instance.State;
                if (state == null) return Refused("Client state not ready.");
                try
                {
                    state.RequestAcceptContract(contractId, issuerName, (ok, msg) =>
                    {
                        bridge.Jobs.Complete(jobId, ok, msg ?? "");
                        bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                    });
                    started = true;
                    return Ok("Accepting " + contractId + "…");
                }
                catch (Exception e) { return Refused("Accept threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        // ── Fixtures ────────────────────────────────────────────────────────
        //
        // Staging a test, not exercising one. Two of the manual steps in T1/T4 are pure
        // setup — forging a kerbal's name, and getting a crewed craft into orbit — and
        // neither is what those scenarios are testing.
        //
        // Deliberately NOT done with the cheat menu, which would be the obvious way and
        // is the wrong one: CheatDetection taints by *effect*, so F12 Set Orbit and
        // HyperEdit are caught tool-agnostically, the taint spreads across an EVA crew
        // transfer (which is exactly how a rescue completes), and the server refuses a
        // tainted submission by default. A cheated fixture makes T1 fail correctly,
        // which is the most expensive kind of false alarm.
        //
        // Spawning is clean where teleporting is not, and the reason is structural: the
        // watchdog judges continuity between ticks, and a vessel it has not seen before
        // gets a fresh baseline (CheatDetection.cs, the !hasBaseline branch). A new hull
        // has no previous position to contradict. That is also why the mod's own rescue
        // wreck spawner has never tripped it.

        /// <summary>
        /// Roster surgery: rename, add, remove.
        ///
        /// Rename is the one T4 needs — the attacker has to hold kerbals named exactly
        /// like the victim's — and it goes through ProtoCrewMember.ChangeName rather
        /// than assigning pcm.name, because the roster indexes by name and a raw
        /// assignment leaves the index pointing at the old string.
        /// </summary>
        private void HandleCrew(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string action = MiniJSON.GetString(body, "action", "");
            string name = MiniJSON.GetString(body, "name", "");
            string to = MiniJSON.GetString(body, "to", "");
            string trait = MiniJSON.GetString(body, "trait", "");

            string status = MiniJSON.GetString(body, "status", "");

            if (action != "rename" && action != "add" && action != "remove" && action != "status")
            {
                BadRequest(ctx, "action must be rename|add|remove|status");
                return;
            }
            if (action == "status" && string.IsNullOrEmpty(status))
            {
                BadRequest(ctx, "status action needs 'status'");
                return;
            }
            if (string.IsNullOrEmpty(name)) { BadRequest(ctx, "name is required"); return; }
            if (action == "rename" && string.IsNullOrEmpty(to))
            {
                BadRequest(ctx, "rename needs 'to'");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                var roster = HighLogic.CurrentGame.CrewRoster;
                if (roster == null) return Refused("No roster.");

                try
                {
                    ProtoCrewMember existing = FindCrew(roster, name);

                    if (action == "rename")
                    {
                        if (existing == null) return Refused("No kerbal named '" + name + "'.");
                        if (FindCrew(roster, to) != null)
                            return Refused("'" + to + "' already exists — renaming onto it would "
                                           + "create the duplicate the collision guard exists to "
                                           + "prevent, and hide whatever it was going to catch.");
                        existing.ChangeName(to);
                        return JobResult.Json("{\"ok\":true,\"renamed\":" + JobResult.Quote(name) +
                                              ",\"to\":" + JobResult.Quote(to) + "}");
                    }

                    if (action == "add")
                    {
                        if (existing != null) return Refused("'" + name + "' already exists.");
                        var pcm = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                        if (pcm == null) return Refused("GetNewKerbal returned nothing.");
                        pcm.ChangeName(name);
                        if (!string.IsNullOrEmpty(trait))
                            // Through the production writer, which refuses a profession
                            // this install cannot define. Writing one directly would
                            // manufacture the exact broken roster T7 checks for.
                            VesselTransfer.DebugApplyTrait(pcm, trait);
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        if (!string.IsNullOrEmpty(status))
                        {
                            string err = SetRosterStatus(pcm, status);
                            if (err != null) return Refused(err);
                        }
                        return JobResult.Json("{\"ok\":true,\"added\":" + JobResult.Quote(pcm.name) +
                                              ",\"trait\":" + JobResult.Quote(pcm.trait ?? "") +
                                              ",\"status\":" + JobResult.Quote(pcm.rosterStatus.ToString()) + "}");
                    }

                    if (action == "status")
                    {
                        if (existing == null) return Refused("No kerbal named '" + name + "'.");
                        // Assigned is refused rather than supported: it is the one status
                        // that is a claim about a *vessel* as well as about the roster, and
                        // setting it here would produce a kerbal that every crewed-vessel
                        // walk disagrees with — a fixture that manufactures the exact
                        // desync the crew tests exist to catch.
                        string err2 = SetRosterStatus(existing, status);
                        if (err2 != null) return Refused(err2);
                        return JobResult.Json("{\"ok\":true,\"name\":" + JobResult.Quote(name) +
                                              ",\"status\":" + JobResult.Quote(existing.rosterStatus.ToString()) + "}");
                    }

                    // remove
                    if (existing == null) return Refused("No kerbal named '" + name + "'.");
                    if (existing.rosterStatus == ProtoCrewMember.RosterStatus.Assigned)
                        return Refused("'" + name + "' is Assigned — aboard something. Removing a "
                                       + "crewed roster entry desyncs the vessel that holds it.");
                    bool gone = roster.Remove(existing);
                    return JobResult.Json("{\"ok\":" + B(gone) + ",\"removed\":" +
                                          JobResult.Quote(name) + "}");
                }
                catch (Exception e) { return Refused("Roster edit failed: " + e.Message); }
            }));
        }

        /// <summary>
        /// Fixture support for T5: a ghost is a *borrowed* kerbal the roster still holds
        /// at Dead or Missing after the craft carrying it vanished. Nothing in the
        /// production API produces that state on demand — it is the residue of a
        /// hand-over that went wrong — so the harness has to be able to place it.
        ///
        /// This writes an input, not a verdict: the rule under test is which entries
        /// PurgeBorrowedGhostCrew *selects*, and that code is still the real one.
        /// Assigned is refused, because it is a claim about a vessel too.
        /// </summary>
        private static string SetRosterStatus(ProtoCrewMember pcm, string status)
        {
            switch ((status ?? "").ToUpperInvariant())
            {
                case "AVAILABLE": pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available; return null;
                case "DEAD":      pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;      return null;
                case "MISSING":   pcm.rosterStatus = ProtoCrewMember.RosterStatus.Missing;   return null;
                case "ASSIGNED":
                    return "Assigned is refused — it asserts the kerbal is aboard a vessel, "
                           + "and setting it from the roster side alone manufactures a desync.";
                default:
                    return "status must be Available|Dead|Missing (got '" + status + "').";
            }
        }

        private static ProtoCrewMember FindCrew(KerbalRoster roster, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var statuses = new[]
            {
                ProtoCrewMember.RosterStatus.Available,
                ProtoCrewMember.RosterStatus.Assigned,
                ProtoCrewMember.RosterStatus.Missing,
                ProtoCrewMember.RosterStatus.Dead,
            };
            try
            {
                foreach (var pcm in roster.Kerbals(statuses))
                    if (pcm != null && pcm.name == name) return pcm;
                foreach (var pcm in roster.Tourist)
                    if (pcm != null && pcm.name == name) return pcm;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Enter flight on a chosen vessel, by pid.
        ///
        /// This is what the Tracking Station's "Fly" button does
        /// (FlightDriver.StartAndFocusVessel), and having it as a route removes the last
        /// hard dependency on the mouse for a two-instance test: entering flight was the
        /// only step that could not be driven any other way, and one of the two windows
        /// here cannot be given focus at all, so anything requiring its mouse was simply
        /// unreachable.
        ///
        /// The vessel is addressed by pid and resolved to an INDEX into
        /// FlightGlobals.Vessels, because that is what StartAndFocusVessel takes — and an
        /// index is exactly the kind of value that silently means a different vessel if
        /// the list shifts between the caller reading it and the game using it. Resolving
        /// it here, on the main thread, in the same job that starts the flight, is what
        /// keeps "fly THIS craft" from becoming "fly whatever is third in the list".
        /// </summary>
        private void HandleFlyVessel(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string pid = MiniJSON.GetString(body, "pid", "");
            if (string.IsNullOrEmpty(pid)) { BadRequest(ctx, "pid is required"); return; }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                    return Refused("Already in flight. Leave to the Space Center first.");

                Guid g;
                if (!Guid.TryParse(pid, out g)) return Refused("pid is not a GUID: " + pid);

                var list = FlightGlobals.Vessels;
                int idx = -1;
                for (int n = 0; list != null && n < list.Count; n++)
                    if (list[n] != null && list[n].id == g) { idx = n; break; }
                if (idx < 0) return Refused("No vessel with pid " + pid + " in this save.");

                var v = list[idx];
                if (SafeB(() => v.state == Vessel.State.DEAD))
                    return Refused("That vessel is dead.");

                try
                {
                    FlightDriver.StartAndFocusVessel(HighLogic.CurrentGame, idx);
                    started = true;
                    return JobResult.Json("{\"ok\":true,\"vessel\":" +
                                          JobResult.Quote(Safe(() => v.vesselName ?? "")) +
                                          ",\"index\":" + idx + "}");
                }
                catch (Exception e) { return Refused("StartAndFocusVessel threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            bridge.Jobs.Complete(jobId, true, "flight requested");
            Respond(ctx, pre);
        }

        /// <summary>
        /// Copy the active vessel — crew and all — into an orbit, as a second hull.
        ///
        /// A copy rather than a move, and that is the whole trick: the import mints a
        /// fresh pid, so the watchdog meets a vessel it has never seen and baselines it
        /// instead of judging it. The original stays where it is, which is usually
        /// wanted anyway (T6 needs a crewed vessel parked *and* something to compare
        /// against).
        ///
        /// Routed through ImportVesselAtTarget, the same call the rescue wreck spawner
        /// uses, so the placement, the orbit-epoch handling and the crew resolution are
        /// the production ones. ownerName and myName are both this account: the arrival
        /// is ours, so no ownership tag is applied and the fixture does not quietly
        /// become a test of the tagging path.
        /// </summary>
        private void HandleSpawnTestCraft(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }

            string bodyName = MiniJSON.GetString(body, "body", "");
            double ap = GetDouble(body, "ap", 100000);
            double pe = GetDouble(body, "pe", 100000);

            Respond(ctx, queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                    return Refused("Must be in flight — the fixture is a copy of the active vessel.");
                var src = FlightGlobals.ActiveVessel;
                if (src == null) return Refused("No active vessel to copy.");

                string myId = GeneKermanMod.Instance?.LinkedAccountId ?? "";
                string myName = GeneKermanMod.Instance?.LinkedUsername ?? "";

                // Refuse rather than spawn with an unknown owner. Both fields are filled
                // by the profile fetch, not by having a token, so a client that loaded a
                // save without refreshing has them empty — and empty is not neutral here.
                // DecideComingHome skips the id comparison when either id is blank and
                // falls through to a name compare that is also blank, so comingHome comes
                // out FALSE and every arriving kerbal is tagged with UnknownOwnerTag.
                // The fixture then looks like a hand-over from a stranger when it was
                // meant to be this account's own craft, and an assertion written against
                // it tests the wrong branch while appearing to pass. Measured: this
                // produced a "someone else's ..." rename that was mistaken for the
                // busy-crew guard firing.
                if (string.IsNullOrEmpty(myId) || string.IsNullOrEmpty(myName))
                    return Refused("Identity not loaded (owner='" + myName + "', id='" + myId +
                                   "'). Call /gk/debug/actions/refresh and wait for the profile " +
                                   "before spawning a fixture, or it will be tagged as a stranger's.");

                try
                {
                    string node = VesselTransfer.ExportActiveVessel(embedRoster: true);
                    if (string.IsNullOrEmpty(node)) return Refused("Could not read the active vessel.");

                    var spec = new RescueTargetSpec
                    {
                        body = string.IsNullOrEmpty(bodyName)
                                 ? (src.mainBody != null ? src.mainBody.bodyName : "Kerbin")
                                 : bodyName,
                        mode = "orbit",
                        ap = ap,
                        pe = pe,
                    };

                    // owner == me on both sides: this is my own craft arriving, so the
                    // crew keep their names and gain no tag.
                    // ImportVesselAtTarget returns the vessel NAME, not the pid — the
                    // fresh pid minted during the import is left in LastSpawnedPid.
                    // Reporting the name as a pid is not a cosmetic mislabel: every
                    // removal, scenario record and vessel lookup in this codebase keys on
                    // the pid GUID, so a caller that fed this value to remove-vessel
                    // would match nothing and read the resulting no-op as success.
                    string spawnedName = VesselTransfer.ImportVesselAtTarget(
                        node, spec, myName, myName, null, myId);

                    if (string.IsNullOrEmpty(spawnedName))
                        return Refused("Import produced no vessel.");

                    string spawnedPid = VesselTransfer.LastSpawnedPid ?? "";

                    return JobResult.Json("{\"ok\":true,\"name\":" + JobResult.Quote(spawnedName) +
                                          ",\"pid\":" + JobResult.Quote(spawnedPid) +
                                          ",\"body\":" + JobResult.Quote(spec.body) +
                                          ",\"ap\":" + Num(ap) + ",\"pe\":" + Num(pe) + "}");
                }
                catch (Exception e) { return Refused("Spawn failed: " + e.Message); }
            }));
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            try
            {
                if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
                return Convert.ToDouble(d[key], CultureInfo.InvariantCulture);
            }
            catch (Exception) { return def; }
        }

        // ── Seeing the screen ───────────────────────────────────────────────
        //
        // Captured inside the game rather than by the OS, and that is not a fallback —
        // it is the better answer here. This install runs under Proton on a Wayland
        // desktop, where a screen grab means either an XWayland tool that cannot
        // enumerate the window or a portal prompt nobody is there to accept. Unity is
        // already holding the finished frame; asking it costs one readback and works
        // identically on every platform the mod ships to.

        /// <summary>
        /// A PNG of exactly what the player sees, downscaled.
        ///
        /// The scale is not a nicety. A 4K frame is ~8 MB as PNG, and the thing reading
        /// this is a conversation with a context budget — a screenshot too large to look
        /// at is a screenshot that does not exist. `w` caps the long edge; the aspect
        /// ratio is kept, because a squashed navball is worse than a small one.
        /// </summary>
        private void HandleScreenshot(HttpListenerContext ctx)
        {
            int maxW = 1280;
            try
            {
                string raw = ctx.Request.QueryString["w"];
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int parsed))
                    maxW = Math.Max(160, Math.Min(3840, parsed));
            }
            catch (Exception) { /* a bad query string is not worth refusing over */ }

            var result = queue.Run(MainThreadQueue.Coroutine(done => CaptureRoutine(maxW, done)));
            if (result.Bytes != null)
                LocalServer.RespondBytes(ctx, result.Status, result.ContentType, result.Bytes, false);
            else
                Respond(ctx, result);
        }

        private static IEnumerator CaptureRoutine(int maxW, Action<JobResult> done)
        {
            // CaptureScreenshotAsTexture must run after the frame has rendered; called
            // any earlier it returns the previous frame or an empty texture.
            yield return new WaitForEndOfFrame();

            Texture2D shot = null, scaled = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null) { done(JobResult.Error(500, "Capture returned nothing.")); yield break; }

                Texture2D source = shot;
                if (shot.width > maxW)
                {
                    // Through a RenderTexture rather than pixel-averaging on the CPU: a
                    // 4K readback loop takes long enough on the main thread to stutter
                    // the game, and stuttering the thing under test is not acceptable
                    // for a diagnostic.
                    int w = maxW;
                    int h = Mathf.Max(1, Mathf.RoundToInt(shot.height * (maxW / (float)shot.width)));
                    RenderTexture rt = RenderTexture.GetTemporary(w, h, 0);
                    RenderTexture prev = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(shot, rt);
                        RenderTexture.active = rt;
                        scaled = new Texture2D(w, h, TextureFormat.RGB24, false);
                        scaled.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        scaled.Apply();
                        source = scaled;
                    }
                    finally
                    {
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                    }
                }

                byte[] png = source.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    done(JobResult.Error(500, "PNG encode produced nothing."));
                    yield break;
                }
                done(new JobResult { Status = 200, ContentType = "image/png", Bytes = png });
            }
            finally
            {
                // Both are throwaway textures created this frame; leaking one per
                // screenshot would be a slow memory leak in a long test session.
                if (shot != null) UnityEngine.Object.Destroy(shot);
                if (scaled != null) UnityEngine.Object.Destroy(scaled);
            }
        }

        /// <summary>
        /// The live uGUI hierarchy: every interactable element, its label, its screen
        /// rect and whether it is usable.
        ///
        /// Worth far more than the screenshot for driving anything, because it is
        /// semantic rather than pixels — "the button reading 'Hire' is at (840, 512) and
        /// is not interactable" is a fact, where reading the same off an image is a
        /// guess. It is also the only honest way to answer "what is on screen": KSP
        /// mixes uGUI with legacy IMGUI, and IMGUI draws no objects at all, so anything
        /// missing here is drawn immediate-mode and cannot be inspected or clicked by
        /// any means short of real OS input.
        /// </summary>
        private static JobResult UiTree()
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"scene\":").Append(JobResult.Quote(SceneName()))
              .Append(",\"screen\":{\"w\":").Append(Screen.width)
              .Append(",\"h\":").Append(Screen.height).Append('}')
              .Append(",\"pointer\":").Append(PointerJson())
              .Append(",\"elements\":[");
            int count = 0;
            try
            {
                var selectables = UnityEngine.UI.Selectable.allSelectablesArray;
                bool first = true;
                foreach (var sel in selectables)
                {
                    if (sel == null || count >= 400) break;   // bound the payload
                    var go = sel.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    var rt = sel.transform as RectTransform;
                    if (rt == null) continue;
                    if (!ScreenRectOf(rt, out float x, out float y, out float w, out float h))
                        continue;

                    if (!first) sb.Append(',');
                    first = false;
                    count++;
                    sb.Append("{\"name\":").Append(JobResult.Quote(go.name))
                      .Append(",\"path\":").Append(JobResult.Quote(PathOf(go.transform)))
                      .Append(",\"type\":").Append(JobResult.Quote(sel.GetType().Name))
                      .Append(",\"label\":").Append(JobResult.Quote(LabelOf(go)))
                      .Append(",\"interactable\":").Append(B(SafeB(() => sel.IsInteractable())))
                      // Screen pixels, TOP-LEFT origin — the same frame `pointer` reports
                      // and the same one a screenshot is measured in, so a caller can move
                      // straight to cx/cy without transforming anything.
                      .Append(",\"rect\":{\"x\":").Append(Num(x)).Append(",\"y\":").Append(Num(y))
                      .Append(",\"w\":").Append(Num(w)).Append(",\"h\":").Append(Num(h)).Append('}')
                      .Append(",\"cx\":").Append(Num(x + w / 2f))
                      .Append(",\"cy\":").Append(Num(y + h / 2f))
                      .Append(",\"onScreen\":")
                      .Append(B(x + w > 0 && y + h > 0 && x < Screen.width && y < Screen.height))
                      .Append('}');
                }
            }
            catch (Exception e)
            {
                return JobResult.Error(500, "UI walk failed: " + e.Message);
            }
            sb.Append("],\"count\":").Append(count).Append('}');
            return JobResult.Json(sb.ToString());
        }

        /// <summary>
        /// Where the game thinks the pointer is, and what is under it.
        ///
        /// This exists because the host is Wayland, which offers no way to read the
        /// cursor position back — there is no equivalent of xdotool's
        /// getmouselocation, and the in-game capture does not contain the OS cursor
        /// either. Without this, driving the mouse is open-loop: you inject a movement
        /// and then guess from hover highlights whether it landed, which is exactly the
        /// pixel-hunting that makes UI automation rot.
        ///
        /// It also separates the two failure modes that look identical from outside. If
        /// `x`/`y` track what was injected, the pointer is fine and any miss is a
        /// coordinate problem. If they never move at all, the events are not reaching
        /// the game — a focus problem, and a completely different fix.
        ///
        /// Unity's origin is BOTTOM-left; every screen coordinate a person or a
        /// screenshot deals in is top-left. Both are reported rather than picking one,
        /// because silently mixing them is a bug that looks like bad calibration.
        /// </summary>
        private static string PointerJson()
        {
            var sb = new StringBuilder();
            try
            {
                Vector3 m = Input.mousePosition;
                float topDownY = Screen.height - m.y;
                sb.Append("{\"x\":").Append(Num(m.x))
                  .Append(",\"yUnity\":").Append(Num(m.y))
                  .Append(",\"y\":").Append(Num(topDownY))
                  .Append(",\"insideWindow\":")
                  .Append(B(m.x >= 0 && m.y >= 0 && m.x <= Screen.width && m.y <= Screen.height))
                  .Append(",\"overUI\":").Append(B(SafeB(() =>
                      EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())))
                  .Append(",\"hovered\":").Append(JobResult.Quote(HoveredName()))
                  .Append('}');
            }
            catch (Exception e)
            {
                return "{\"error\":" + JobResult.Quote(e.Message) + "}";
            }
            return sb.ToString();
        }

        /// <summary>The topmost uGUI object under the pointer, by an actual raycast — so
        /// a caller can confirm it is over the control it meant to hit before clicking,
        /// rather than after.</summary>
        private static string HoveredName()
        {
            try
            {
                var es = EventSystem.current;
                if (es == null) return "";
                var data = new PointerEventData(es) { position = Input.mousePosition };
                var hits = new List<RaycastResult>();
                es.RaycastAll(data, hits);
                if (hits.Count == 0) return "";
                var go = hits[0].gameObject;
                return go == null ? "" : PathOf(go.transform);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// A control's rect in SCREEN PIXELS, top-left origin. False when it has no
        /// usable size.
        ///
        /// The naive version of this returned <c>GetWorldCorners</c> directly, which is
        /// only screen pixels for a Screen-Space-Overlay canvas. KSP's are not: the
        /// values came back centred on the middle of the screen (a left edge of -1266 on
        /// a 2560-wide display), so every consumer had to know the canvas mode and undo
        /// it — and would have been silently wrong the first time it met a canvas of a
        /// different mode.
        ///
        /// RectTransformUtility.WorldToScreenPoint with the canvas's own camera handles
        /// every mode uniformly. The camera is null for Overlay by definition, which is
        /// exactly what that call wants, so the same line covers both cases.
        ///
        /// The Y flip is the other half: Unity measures screen space from the BOTTOM,
        /// while screenshots, the pointer report and anything a person types are measured
        /// from the top. Returning Unity's convention here would put the one coordinate
        /// system mismatch in the codebase right where clicks are aimed.
        /// </summary>
        private static bool ScreenRectOf(RectTransform rt, out float x, out float y,
                                         out float w, out float h)
        {
            x = y = w = h = 0f;
            try
            {
                var canvas = rt.GetComponentInParent<Canvas>();
                Camera cam = null;
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    cam = canvas.worldCamera;

                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);

                Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

                float left = Mathf.Min(bl.x, tr.x);
                float right = Mathf.Max(bl.x, tr.x);
                float bottom = Mathf.Min(bl.y, tr.y);
                float top = Mathf.Max(bl.y, tr.y);

                w = right - left;
                h = top - bottom;
                if (w <= 1f || h <= 1f) return false;   // zero-sized: not on screen in any useful sense

                x = left;
                y = Screen.height - top;                // bottom-up → top-down
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The text a human would read on this control, from whichever of the
        /// two text components KSP happened to use.</summary>
        private static string LabelOf(GameObject go)
        {
            try
            {
                var tmp = go.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return Trim(tmp.text);
                var txt = go.GetComponentInChildren<UnityEngine.UI.Text>(true);
                if (txt != null && !string.IsNullOrEmpty(txt.text)) return Trim(txt.text);
            }
            catch (Exception) { }
            return "";
        }

        private static string Trim(string s)
        {
            s = s.Replace("\n", " ").Trim();
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            int guard = 0;
            while (t != null && guard++ < 12)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        // ── Save control ────────────────────────────────────────────────────
        //
        // Loading a save is the single biggest manual step in a single-instance run:
        // the swap between roles is "quit to menu, change account, load the other
        // save", and two thirds of that is automatable. Reading the save list is what
        // lets a driver check the target exists before asking for a scene transition
        // that would otherwise fail somewhere deep in KSP with no answer coming back.

        /// <summary>Save folders on disk, and which one is live.</summary>
        private static JobResult ListSaves()
        {
            var sb = new StringBuilder();
            sb.Append("{\"current\":").Append(JobResult.Quote(SaveFolder()))
              .Append(",\"loaded\":").Append(B(HighLogic.CurrentGame != null))
              .Append(",\"saves\":[");
            try
            {
                string root = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves");
                if (System.IO.Directory.Exists(root))
                {
                    bool first = true;
                    foreach (var dir in System.IO.Directory.GetDirectories(root))
                    {
                        string name = System.IO.Path.GetFileName(dir);
                        // A folder with no persistent.sfs is not a save — `scenarios`
                        // and stray directories live here too, and offering one as a
                        // load target would fail after the scene had already torn down.
                        bool hasPersistent = System.IO.File.Exists(
                            System.IO.Path.Combine(dir, "persistent.sfs"));
                        bool hasQuick = System.IO.File.Exists(
                            System.IO.Path.Combine(dir, "quicksave.sfs"));
                        if (!hasPersistent) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"name\":").Append(JobResult.Quote(name))
                          .Append(",\"hasQuicksave\":").Append(B(hasQuick))
                          .Append('}');
                    }
                }
            }
            catch (Exception e)
            {
                return JobResult.Error(500, "Could not read the saves directory: " + e.Message);
            }
            sb.Append("]}");
            return JobResult.Json(sb.ToString());
        }

        /// <summary>
        /// Load a save folder. 202 + job id, because this is a scene load.
        ///
        /// Refused from FLIGHT, and that is the one guard that matters here: loading
        /// replaces the live game outright, so doing it mid-flight silently discards
        /// everything since the last save. Every other refusal in this file is about a
        /// request that cannot work; this one is about a request that works and costs
        /// the operator their session.
        /// </summary>
        private void HandleLoadSave(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null) { BadRequest(ctx, "bad_body"); return; }
            string save = MiniJSON.GetString(body, "save", "");
            string file = MiniJSON.GetString(body, "file", "persistent");
            string sceneRaw = MiniJSON.GetString(body, "scene", "SPACECENTER");
            if (string.IsNullOrEmpty(save)) { BadRequest(ctx, "save is required"); return; }
            // A save name is a folder name. Anything with a separator in it is either a
            // mistake or an attempt to walk out of saves/, and neither should reach the
            // filesystem call below.
            if (save.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || save == "." || save == "..")
            {
                BadRequest(ctx, "save must be a plain folder name");
                return;
            }
            if (file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            {
                BadRequest(ctx, "file must be a plain file name without an extension");
                return;
            }

            GameScenes target;
            switch ((sceneRaw ?? "").ToUpperInvariant())
            {
                case "SPACECENTER": target = GameScenes.SPACECENTER; break;
                case "TRACKSTATION": target = GameScenes.TRACKSTATION; break;
                default: BadRequest(ctx, "scene must be SPACECENTER or TRACKSTATION"); return;
            }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            var pre = queue.RunSync(() =>
            {
                if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                    return Refused("Refusing to load a save from flight — it would discard "
                                   + "everything since the last save. Leave the flight first.");

                string path = System.IO.Path.Combine(
                    System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves"), save);
                if (!System.IO.Directory.Exists(path))
                    return Refused("No such save folder: " + save);
                if (!System.IO.File.Exists(System.IO.Path.Combine(path, file + ".sfs")))
                    return Refused("Save '" + save + "' has no " + file + ".sfs");

                try
                {
                    GeneKermanMod.Instance.RunCoroutine(LoadSaveRoutine(save, file, target, jobId));
                    started = true;
                    return Ok("Loading " + save + "…");
                }
                catch (Exception e) { return Refused("Load threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        private IEnumerator LoadSaveRoutine(string save, string file, GameScenes scene, string jobId)
        {
            // The load itself is not a coroutine step, but it can throw, and a try
            // block cannot span a yield — so it is done first and the waiting after.
            string error = null;
            Game game = null;
            string previousFolder = HighLogic.SaveFolder;
            try
            {
                HighLogic.SaveFolder = save;
                // nullIfIncompatible:true — a save from another KSP version comes back
                // null rather than half-loading, which is the answer a harness wants.
                // suppressIncompatibleMessage:true — nobody is watching the screen.
                game = GamePersistence.LoadGame(file, save, true, true);
                if (game == null) error = "LoadGame returned null (incompatible or corrupt save).";
            }
            catch (Exception e) { error = "LoadGame threw: " + e.Message; }

            if (error == null)
            {
                try
                {
                    // This sequence is stock's, read out of MainMenu's own resume path
                    // (OnLoadDialogPipelineFinished), and the order is not decorative.
                    //
                    // CurrentGame must be assigned BEFORE Start(): Start reaches back
                    // through HighLogic.CurrentGame for state it does not take as an
                    // argument, so calling it on a game that is merely in a local
                    // variable throws a NullReferenceException naming nothing. That was
                    // the actual failure the first version of this route produced.
                    //
                    // UpdateScenarioModules is what installs [KSPScenario] modules that
                    // a save does not already carry — GKContractScenario among them.
                    // Skipping it would hand the harness a save missing the very
                    // scenario module every queue-backed guard hangs off, and the
                    // resulting "scenario absent" would look like a mod bug rather than
                    // a loader bug.
                    HighLogic.CurrentGame = game;
                    GamePersistence.UpdateScenarioModules(game);

                    // Stock fires this so mods can react to a state change they did not
                    // initiate. A harness that skips it is testing a load no player can
                    // perform.
                    try
                    {
                        string sfs = System.IO.Path.Combine(
                            System.IO.Path.Combine(
                                System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves"), save),
                            file + ".sfs");
                        ConfigNode node = ConfigNode.Load(sfs);
                        if (node != null) GameEvents.onGameStatePostLoad.Fire(node);
                    }
                    catch (Exception evx)
                    {
                        // A listener throwing is that mod's problem, not a failed load.
                        Debug.LogWarning("[GeneKerman] onGameStatePostLoad listener threw: " + evx.Message);
                    }

                    HighLogic.SaveFolder = save;
                    game.startScene = scene;
                    game.Start();
                }
                catch (Exception e) { error = "Game.Start threw: " + e.Message; }
            }

            if (error != null)
            {
                // Put the folder back. Leaving it pointing at a save that failed to load
                // makes every later read report the wrong save name, which is worse than
                // the failure itself — the next assertion would be about a save that was
                // never loaded.
                HighLogic.SaveFolder = previousFolder;
                // Logged as well as returned: a failure that exists only in an HTTP
                // response is invisible in the bug report the log becomes.
                Debug.LogError("[GeneKerman] Debug bridge could not load save '" + save + "': " + error);
            }

            if (error != null)
            {
                bridge.Jobs.Complete(jobId, false, error);
                bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + 300f;
            while ((HighLogic.LoadedScene != scene || HighLogic.CurrentGame == null) &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;
            // One more frame so the scene's own Start()s have run. GKContractScenario is
            // instantiated during the load, and reading it a frame early reports a
            // missing scenario module on a save that has one.
            yield return null;

            bool ok = HighLogic.LoadedScene == scene && HighLogic.CurrentGame != null;
            bridge.Jobs.Complete(jobId, ok,
                ok ? save + " loaded" : "timed out loading " + save);
            bridge.Broadcast("job", bridge.Jobs.Get(jobId).ToJson());
        }

        /// <summary>
        /// Write a quicksave. Named rather than using QuickSaveLoad.QuickSave() so the
        /// file is deterministic and a driver knows exactly what quickload will restore
        /// — the stock call also drives UI and the "quicksave in progress" lock.
        /// </summary>
        private void HandleQuicksave(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx) ?? new Dictionary<string, object>();
            string name = MiniJSON.GetString(body, "name", "quicksave");
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            {
                BadRequest(ctx, "name must be a plain file name");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                try
                {
                    // Updated() stamps the game's own state before serialising, which is
                    // what the stock quicksave path does; without it the written file can
                    // lag the live game by a frame's worth of changes.
                    HighLogic.CurrentGame.Updated();
                    GamePersistence.SaveGame(name, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                    return JobResult.Json("{\"ok\":true,\"file\":" + JobResult.Quote(name) +
                                          ",\"save\":" + JobResult.Quote(SaveFolder()) + "}");
                }
                catch (Exception e) { return Refused("Quicksave failed: " + e.Message); }
            }));
        }

        /// <summary>
        /// Restore a quicksave — the same operation as load-save, pointed at the
        /// quicksave file in the current folder. This is what makes T3 (the quickload
        /// rollback that a hand-over has to survive) drivable rather than a keystroke.
        /// </summary>
        private void HandleQuickload(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx) ?? new Dictionary<string, object>();
            string name = MiniJSON.GetString(body, "name", "quicksave");
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            {
                BadRequest(ctx, "name must be a plain file name");
                return;
            }

            string jobId = bridge.Jobs.Begin();
            bool started = false;
            string folder = null;
            var pre = queue.RunSync(() =>
            {
                if (HighLogic.CurrentGame == null) return Refused("No save is loaded.");
                folder = HighLogic.SaveFolder;
                string path = System.IO.Path.Combine(
                    System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves"), folder ?? "");
                if (!System.IO.File.Exists(System.IO.Path.Combine(path, name + ".sfs")))
                    return Refused("No " + name + ".sfs in save '" + folder + "'. Quicksave first.");

                // Unlike load-save this IS allowed from flight: rolling a flight back is
                // the whole point of a quickload, and the state being discarded is
                // exactly what the caller asked to discard.
                try
                {
                    GeneKermanMod.Instance.RunCoroutine(
                        LoadSaveRoutine(folder, name, GameScenes.SPACECENTER, jobId));
                    started = true;
                    return Ok("Quickloading…");
                }
                catch (Exception e) { return Refused("Quickload threw: " + e.Message); }
            });

            if (!started)
            {
                bridge.Jobs.Complete(jobId, false, "refused");
                Respond(ctx, pre);
                return;
            }
            LocalServer.Respond(ctx, 202, "application/json",
                "{\"ok\":true,\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        /// <summary>
        /// Drop the session token — half of an account swap. The other half cannot be
        /// automated from here: linking needs a 6-digit code typed in Discord, which is
        /// deliberately a human action and not something this bridge should be able to
        /// perform even if it could.
        /// </summary>
        private static JobResult Unlink()
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) return Refused("Mod not ready.");
            try
            {
                mod.Api.ClearToken();
                return Ok("Unlinked. Link as the other account in-game to finish the swap.");
            }
            catch (Exception e) { return Refused("Unlink failed: " + e.Message); }
        }

        // ══ Helpers ═════════════════════════════════════════════════════════

        private static JobResult Ok(string message) =>
            JobResult.Json("{\"ok\":true,\"message\":" + JobResult.Quote(message) + "}");

        /// <summary>
        /// A refusal, not an error: the request was well-formed and the game said no.
        /// 200 so a driver's HTTP layer does not raise, with ok:false so an assertion
        /// cannot mistake it for success.
        /// </summary>
        private static JobResult Refused(string message) =>
            JobResult.Json("{\"ok\":false,\"message\":" + JobResult.Quote(message) + "}");

        private static void BadRequest(HttpListenerContext ctx, string message) =>
            LocalServer.Respond(ctx, 400, "application/json",
                "{\"ok\":false,\"error\":" + JobResult.Quote(message) + "}");

        private static Dictionary<string, object> ReadJson(HttpListenerContext ctx)
        {
            try
            {
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    return MiniJSON.DeserializeDict(reader.ReadToEnd());
            }
            catch (Exception) { return null; }
        }

        private static string StringListJson(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            if (items != null)
            {
                bool first = true;
                foreach (var s in items)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JobResult.Quote(s ?? ""));
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string StringSetJson(IEnumerable<string> items) => StringListJson(items);

        private static string B(bool b) => b ? "true" : "false";

        /// <summary>Invariant, and NaN/Infinity as 0 — an escape trajectory genuinely
        /// produces both, and either breaks the whole parse for the driver.</summary>
        private static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string SceneName()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception) { return "unknown"; }
        }

        private static string SaveFolder()
        {
            try { return HighLogic.SaveFolder ?? ""; }
            catch (Exception) { return ""; }
        }

        // A KSP read can throw for reasons that are themselves the bug under test, so
        // every field is fetched behind one of these rather than letting one bad
        // property abort the whole snapshot.
        private static string Safe(Func<string> f) { try { return f() ?? ""; } catch (Exception) { return ""; } }
        private static bool SafeB(Func<bool> f) { try { return f(); } catch (Exception) { return false; } }
        private static int SafeI(Func<int> f) { try { return f(); } catch (Exception) { return 0; } }
        private static double SafeD(Func<double> f) { try { return f(); } catch (Exception) { return 0; } }
        private static uint SafeU(Func<uint> f) { try { return f(); } catch (Exception) { return 0; } }

        private static void Respond(HttpListenerContext ctx, JobResult r) =>
            LocalServer.Respond(ctx, r.Status, r.ContentType, r.Body);

        private static void MethodNotAllowed(HttpListenerContext ctx) =>
            LocalServer.Respond(ctx, 405, "application/json", "{\"error\":\"method_not_allowed\"}");
    }
}
#endif
