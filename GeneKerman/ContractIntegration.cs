/*
 * ContractIntegration.cs – Stock KSP contract system bridge.
 *
 * Injects Gene Kerman missions as stock contracts so they appear in
 * the Mission Control building UI alongside any other contracts.
 *
 * Uses KSP's Contract and ContractParameter classes:
 *   - GKMissionContract: the main contract wrapper
 *   - GKMissionParameter: tracks a single objective
 *
 * Completion is driven by our mod's API status, not stock contract logic.
 * When the API marks a contract as completed, we complete the stock contract too.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Contracts;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// One craft queued to leave this save: the name to show the player, and what
    /// happens to the crew aboard when it goes.
    ///
    /// The fate is decided where the removal is *queued*, not where it runs, because
    /// only the caller knows which side of the hand-over this is — and the removal
    /// itself can run scenes, or sessions, later.
    /// </summary>
    public class PendingRescueRemoval
    {
        public string Name;
        public VesselTransfer.CrewFate CrewFate = VesselTransfer.CrewFate.LeavesWithCraft;

        /// <summary>The kerbal names this contract hands over — settled by name at
        /// removal time so a kerbal who stepped off the hull still leaves with it
        /// (see VesselTransfer.RemoveContractCrew). Empty on entries queued by older
        /// builds or without a contract in hand; those settle hull crew only.</summary>
        public List<string> Crew = new List<string>();

        /// <summary>Read a saved fate, falling back to the one that was implicit before
        /// this field existed — so an entry queued by an older build still does what it
        /// was queued to do.</summary>
        public static VesselTransfer.CrewFate ParseFate(string saved)
        {
            if (!string.IsNullOrEmpty(saved))
            {
                try
                {
                    return (VesselTransfer.CrewFate)Enum.Parse(
                        typeof(VesselTransfer.CrewFate), saved, true);
                }
                catch { /* unknown value — fall through */ }
            }
            return VesselTransfer.CrewFate.LeavesWithCraft;
        }
    }

    /// <summary>
    /// A stranded wreck this save spawned for a rescue it accepted: which vessel it was
    /// and, more durably, which parts it is made of.
    ///
    /// It exists because the wreck has to stay recognisable at *submission* time, and by
    /// then every other marker has gone. Its crew are aboard the rescue craft, so the
    /// contract's stranded-kerbal list no longer matches it; its emergency-freeze record
    /// was dropped the moment they thawed; and the server only pins wreck_parts for a
    /// "vessel" recovery, so on a crew-only rescue it can say nothing at all. Without
    /// this an emptied hull parked alongside is indistinguishable from a support craft
    /// the rescuer brought — and the extras list is a list of craft to hand over.
    ///
    /// flightIDs rather than the pid alone because they survive docking and undocking:
    /// a wreck towed in and released has a new pid and the same parts. The pid is kept
    /// as well, for the cheap case where it hasn't changed.
    /// </summary>
    public class RescueWreckRecord
    {
        public string ContractId;
        public string Pid;
        public List<uint> PartFlightIds = new List<uint>();

        /// <summary>Crew this save had to rename on the way in (the name the contract
        /// knows → the name the kerbal actually got), because the roster already held it.
        ///
        /// Persisted with the wreck rather than derived later because it cannot be
        /// derived later: the contract's rescue_kerbals list lives on the server and
        /// never learns about a rename that happened in one save. Everything that acts on
        /// those kerbals by name — the emergency freeze, and the hand-over that settles
        /// them when the rescue is approved — would otherwise hunt names nobody in this
        /// save answers to, leaving the stranded crew in the life-support simulation the
        /// freeze exists to lift them out of. Empty on the overwhelmingly common import,
        /// where nothing collided.</summary>
        public Dictionary<string, string> CrewRenames = new Dictionary<string, string>();
    }

    /// <summary>
    /// ScenarioModule that manages the bridge between our API contracts
    /// and the stock contract system. Registered via a MODULE Manager config.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class GKContractScenario : ScenarioModule
    {
        public static GKContractScenario Instance { get; private set; }

        // Maps API contract_id → stock contract guid
        private Dictionary<string, string> activeContracts = new Dictionary<string, string>();
        
        // List of contract_ids whose vessels have already been imported into this save
        private HashSet<string> importedVessels = new HashSet<string>();

        // Rescue kerbals currently held immune from life support (one record per spawned
        // wreck). Persisted so immunity survives restarts while a wreck waits to be reached.
        private List<RescueImmunityRecord> immunities = new List<RescueImmunityRecord>();

        // Rescue craft this client handed over, keyed by contract_id → vessel pids, the
        // contract craft FIRST and any extras sent alongside it after. Recorded at
        // submission and persisted, so we still know which craft to delete when the issuer
        // approves — even if the player quit and relaunched in between (the common case).
        //
        // The primary-first ordering is what makes the record safe to downgrade across:
        // an older build reads a RECORD with GetValue("pid"), which returns the first
        // value — the rescue craft — so it leaves the extras behind rather than deleting
        // the wrong hull. Nothing may reorder this list.
        private Dictionary<string, List<string>> rescueSubmittedPids =
            new Dictionary<string, List<string>>();

        // Stranded wrecks this save has spawned for a rescue it accepted, keyed by
        // contract_id. See RescueWreckRecord: this is how a wreck stays recognisable
        // once its crew are off it and its emergency-freeze record has been dropped.
        private Dictionary<string, RescueWreckRecord> rescueWrecks =
            new Dictionary<string, RescueWreckRecord>();

        // Rescue craft queued for removal (pid → what to do with it), persisted so a removal
        // that couldn't run yet (player was in flight) survives a restart and fires at the next
        // Space Center / Tracking Station visit.
        private Dictionary<string, PendingRescueRemoval> pendingRescueRemovals =
            new Dictionary<string, PendingRescueRemoval>();

        // ── Carry across scene changes ───────────────────────────────────────
        //
        // The queue is lost on every scene change and this is the repair.
        //
        // Measured: the module is destroyed holding its entries and the next scene's
        // OnLoad receives a node that does not contain them. Writing into the proto node
        // first does not help — ProtoScenarioModule.GetData() returns moduleValues by
        // reference, a flush was verified to land there and read back, and OnLoad still
        // arrived with nothing. KSP simply does not rebuild the module from the node we
        // can reach.
        //
        // For every vessel EXCEPT the one being flown the loss is invisible, because the
        // removal executes before the transition. The active vessel is the only case that
        // must survive one — RemoveVesselFromSave refuses to delete the hull under the
        // player — and it is exactly the case the live-vessel quicksend uses.
        //
        // A static survives what an instance does not: the type outlives every scene.
        // Three things keep that from becoming a liability of its own.
        private static string carrySaveFolder;
        private static readonly Dictionary<string, PendingRescueRemoval> carryRemovals =
            new Dictionary<string, PendingRescueRemoval>();
        /// <summary>Universe time each carried entry was queued at. A quickload moves UT
        /// backwards, and an entry stamped after "now" belongs to a future that no longer
        /// happened — the same reading CheatDetection takes of a rolled-back taint.</summary>
        private static readonly Dictionary<string, double> carryUt =
            new Dictionary<string, double>();

        private static double NowUt()
        {
            try { return Planetarium.GetUniversalTime(); } catch (Exception) { return 0.0; }
        }

        /// <summary>
        /// The universal time of the save being *loaded*, for use during OnLoad.
        ///
        /// <see cref="NowUt"/> is wrong here and silently so. Planetarium is the running
        /// universe's clock, and at the point a ScenarioModule's OnLoad runs it has not
        /// been re-timed yet — after a quickload it still reads the UT of the timeline
        /// being thrown away. Measured: a removal queued at UT 1218.1, quickloaded back
        /// to 1208, and Planetarium reported 1218.1 during the merge. So a rollback rule
        /// written against it compares a value to itself and can never fire, which is not
        /// a tuning problem that a wider guard band would fix.
        ///
        /// HighLogic.CurrentGame is the game just built from the .sfs, and its
        /// UniversalTime comes off that file's own UT field before scenario modules are
        /// loaded — so it is the clock this save actually has. Planetarium stays as the
        /// fallback for the case where there is no game yet, where it is no worse than
        /// what it replaces.
        /// </summary>
        private static double LoadedUt()
        {
            try
            {
                var g = HighLogic.CurrentGame;
                if (g != null && g.UniversalTime > 0.0) return g.UniversalTime;
            }
            catch (Exception) { }
            return NowUt();
        }

        /// <summary>Remember a queued removal so it survives the next scene change.</summary>
        internal static void CarryRemember(string pid, PendingRescueRemoval entry)
        {
            if (string.IsNullOrEmpty(pid) || entry == null) return;
            string save = HighLogic.SaveFolder ?? "";
            // (1) Keyed by save. A carried entry must never follow the player into a
            // different save and delete a craft there.
            if (carrySaveFolder != save)
            {
                carryRemovals.Clear();
                carryUt.Clear();
                carrySaveFolder = save;
            }
            carryRemovals[pid] = entry;
            carryUt[pid] = NowUt();
        }

        /// <summary>Drop a carried entry once the removal has actually been dealt with.
        /// (2) Without this the carry would re-add an entry the queue had finished with,
        /// and the removal would run a second time on the next scene change.</summary>
        internal static void CarryForget(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return;
            carryRemovals.Remove(pid);
            carryUt.Remove(pid);
        }

        /// <summary>Merge carried entries back after a load. Additive only — a value that
        /// came off disk always wins, so this can restore what was lost but never
        /// overwrite what survived.</summary>
        private void MergeCarriedRemovals()
        {
            try
            {
                string save = HighLogic.SaveFolder ?? "";
                if (carrySaveFolder != save)
                {
                    carryRemovals.Clear();
                    carryUt.Clear();
                    carrySaveFolder = save;
                    return;
                }
                double now = LoadedUt();
                foreach (var kv in new List<KeyValuePair<string, PendingRescueRemoval>>(carryRemovals))
                {
                    // (3) Dropped if it was queued in a future this timeline no longer
                    // has: the player quickloaded to before the hand-over, so the removal
                    // they were promised never happened and must not be re-asserted.
                    //
                    // A plain scene change out of flight does NOT reach this: the
                    // hand-over's own ReturnToSpaceCenterRoutine saves before it loads
                    // the Space Center, which both persists the queue and re-times
                    // CurrentGame.UniversalTime. Measured 2026-09-01 — a *forced*,
                    // unsaved scene change (the test bridge's /actions/scene, fired
                    // 0.8 s after the send and so ahead of that routine's 1.5 s wait)
                    // does reach it, and is dropped correctly: an unsaved exit from
                    // flight really does revert the universe to the last save, so the
                    // hand-over is genuinely in a discarded future. Both clocks agree
                    // about that, which is why this rule is sound and why the harness
                    // must let the auto-return run rather than pre-empting it.
                    double when;
                    bool haveWhen = carryUt.TryGetValue(kv.Key, out when);
                    if (haveWhen && when > now + 1.0)
                    {
                        carryRemovals.Remove(kv.Key);
                        carryUt.Remove(kv.Key);
                        Debug.Log($"[GeneKerman] Carried removal for {kv.Key} dropped: queued at " +
                                  $"UT {when:F1}, loaded UT {now:F1} (Planetarium {NowUt():F1}) " +
                                  "— rolled back by a quickload.");
                        continue;
                    }
                    if (!pendingRescueRemovals.ContainsKey(kv.Key))
                    {
                        pendingRescueRemovals[kv.Key] = kv.Value;
                        // The two clock values are logged on the *restore* path as well as
                        // the drop path: a removal that should have been dropped and was
                        // not is indistinguishable from one that was correctly carried,
                        // unless the numbers that decided it are written down.
                        Debug.Log($"[GeneKerman] Carried removal for {kv.Key} restored across " +
                                  $"a scene change (queued at UT {when:F1}, loaded UT {now:F1}, " +
                                  $"Planetarium {NowUt():F1}).");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] MergeCarriedRemovals failed: " + ex.Message);
            }
        }

        public override void OnAwake()
        {
            Instance = this;
            GKScenarioTrace("OnAwake");
        }

#if GK_DEBUG_PANEL
        /// <summary>
        /// Dev-only lifecycle trace. COMPILED OUT of production builds.
        ///
        /// Exists to answer one question that cannot be answered by reading the code:
        /// in what ORDER do OnSave, OnDestroy, OnAwake and OnLoad run across a scene
        /// change, and does the queue survive the round trip?
        ///
        /// A removal queued in flight was observed to vanish on the next scene change
        /// with the vessel still in the save — and OnDestroy's own comment says a
        /// removal written to a dead module "is simply lost". Whether the loss is that
        /// (state never serialised because OnSave did not run before teardown) or a
        /// race (EnsureExists installing an empty module ahead of the ScenarioRunner)
        /// changes the fix completely, so the trace prints the instance identity and
        /// the pending count at every transition rather than guessing.
        /// </summary>
        private void GKScenarioTrace(string where)
        {
            try
            {
                Debug.Log($"[GeneKerman][ScenarioTrace] {where} scene={HighLogic.LoadedScene} " +
                          $"instance=#{GetInstanceID()} pending={pendingRescueRemovals?.Count ?? -1} " +
                          $"immunities={immunities?.Count ?? -1} " +
                          $"isCurrentInstance={ReferenceEquals(Instance, this)}");
            }
            catch (Exception) { /* a trace must never be the thing that breaks a load */ }
        }
#else
        private void GKScenarioTrace(string where) { }
#endif

        /// <summary>
        /// Make sure this scenario exists in the loaded game, installing it when
        /// KSP's [KSPScenario] injection didn't.
        ///
        /// Observed in the wild (2026-08-20, an old pre-mod sandbox save): the save
        /// went through SPACECENTER → TRACKSTATION → FLIGHT without the module ever
        /// being created, despite AddToAllGames. With Instance null, every guard
        /// hanging off it silently no-ops — the wreck-spawn dedup, the emergency-
        /// freeze records, the removal queue — and the visible result was six
        /// identical wrecks spawned from six clicks, with no defreeze button and
        /// nothing persisted. Belt over stock's braces: check the game's proto list
        /// ourselves and add/instantiate what's missing. Cheap when healthy (one
        /// null check), loud when it has to act, so the logs say which saves ever
        /// needed it.
        /// </summary>
        /// <summary>How long to let KSP instantiate the module itself before self-healing.
        /// Comfortably longer than the few seconds measured between a scene starting and
        /// the ScenarioRunner's own load, short enough that a genuinely missing module is
        /// repaired well within a player's first action in the scene.</summary>
        private const float SelfHealGraceSeconds = 8f;

        private static GameScenes graceScene = (GameScenes)(-1);
        private static float graceSince;

        /// <summary>True once the current scene has been up long enough that KSP would
        /// have loaded the module if it were going to.</summary>
        private static bool SelfHealGraceElapsed()
        {
            var scene = HighLogic.LoadedScene;
            if (scene != graceScene)
            {
                graceScene = scene;
                graceSince = Time.realtimeSinceStartup;
                return false;
            }
            return Time.realtimeSinceStartup - graceSince >= SelfHealGraceSeconds;
        }

        public static void EnsureExists()
        {
            if (Instance != null) return;
            var game = HighLogic.CurrentGame;
            if (game == null || ScenarioRunner.Instance == null) return;
            var scene = HighLogic.LoadedScene;
            // The editor is not one of the module's scenes, so nothing is *instantiated*
            // there — but the proto entry is still added to the game. A launch from
            // the VAB/SPH builds the flight from the save the editor writes, and a
            // revert-to-editor restores that same save; when the proto only ever got
            // added in flight, every revert dropped it again and the next launch
            // logged "GKContractScenario is missing from this save" (three times in
            // one session on 2026-08-30), losing whatever the queue held in between.
            bool editor = scene == GameScenes.EDITOR;
            if (!editor && scene != GameScenes.SPACECENTER && scene != GameScenes.FLIGHT &&
                scene != GameScenes.TRACKSTATION) return;

            try
            {
                ProtoScenarioModule psm = null;
                if (game.scenarios != null)
                    psm = game.scenarios.Find(s => s != null && s.moduleName == "GKContractScenario");

                if (psm == null)
                {
                    Debug.LogWarning("[GeneKerman] GKContractScenario is missing from this save " +
                                     "(KSPScenario injection didn't run), installing it now.");
                    psm = game.AddProtoScenarioModule(typeof(GKContractScenario),
                        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION);
                }
                // Let KSP's own ScenarioRunner instantiate the module before doing it
                // ourselves. Without this the two race and BOTH run: measured across a
                // session, every scene produced two OnAwake/OnLoad pairs with different
                // instance ids — ours from Update within ~50 ms of the scene starting,
                // KSP's a few seconds later. Each sets Instance, so KSP's wins and ours is
                // orphaned: a whole module built, loaded and discarded per scene, with a
                // window in between where a caller could write into the copy about to lose.
                //
                // The self-heal is still needed — a save whose KSPScenario injection never
                // ran has no moduleRef and never will — so this delays that repair rather
                // than removing it. Only the racing branch waits; a missing *proto* is
                // added immediately above, since nothing else will add it.
                if (!editor && psm != null && psm.moduleRef == null &&
                    !SelfHealGraceElapsed())
                    return;

                if (!editor && psm != null && psm.moduleRef == null)
                {
                    Debug.LogWarning("[GeneKerman] GKContractScenario not instantiated in this " +
                                     "scene, loading it now.");
#if GK_DEBUG_PANEL
                    // What the proto node holds at the moment we rebuild from it. This is
                    // the load-bearing observation: psm.Load re-creates the module from
                    // THIS node, so if the outgoing module's OnSave never wrote into it,
                    // whatever was queued in the previous scene is already gone by now —
                    // and no amount of persisting later can recover it.
                    try
                    {
                        var pn = psm.GetData()?.GetNode("RESCUE_PENDING_REMOVALS");
                        int queued = pn?.GetNodes("RECORD")?.Length ?? -1;
                        Debug.Log($"[GeneKerman][ScenarioTrace] EnsureExists:beforeLoad " +
                                  $"scene={HighLogic.LoadedScene} protoPending={queued}");
                    }
                    catch (Exception) { /* diagnostics only */ }
#endif
                    psm.Load(ScenarioRunner.Instance);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GKContractScenario.EnsureExists failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear the static handle when KSP tears this module down — it is registered
        /// for SPACECENTER/FLIGHT/TRACKSTATION only, so in the editor (and mid scene
        /// change) there is no live instance at all.
        ///
        /// Without this, `Instance` keeps pointing at the destroyed module, and
        /// `Instance?.Something` does NOT catch it: `?.` is a plain reference-null
        /// check, not UnityEngine.Object's overloaded ==. Callers would then read and
        /// write the dead module's dictionaries, which nothing will ever serialize —
        /// a queued rescue removal written there is simply lost. Nulling the field
        /// makes `?.` and `== null` agree again everywhere.
        /// </summary>
        private void OnDestroy()
        {
            // ReferenceEquals, not ==: whether KSP builds the next scene's module before
            // or after tearing this one down, we must only clear the handle when it is
            // still pointing at *us*, and Unity's == would blur a destroyed object into
            // null on both sides of that test.
            GKScenarioTrace("OnDestroy");
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        public override void OnLoad(ConfigNode node)
        {
            GKScenarioTrace("OnLoad:enter");
#if GK_DEBUG_PANEL
            // What KSP actually handed us, before any parsing.
            //
            // This is how it was established that writing into the proto node
            // (ProtoScenarioModule.GetData(), which returns moduleValues by reference)
            // does NOT reach the node KSP rebuilds the module from: a flush that wrote
            // one pending record and read it back still arrived here as zero, with a
            // single proto entry and a non-null node. That experiment has been reverted,
            // but the measurement stands — a queue that must survive a scene change needs
            // its own storage rather than KSP's scenario plumbing.
            try
            {
                int incoming = node?.GetNode("RESCUE_PENDING_REMOVALS")?
                                    .GetNodes("RECORD")?.Length ?? -1;
                int protos = HighLogic.CurrentGame?.scenarios?
                    .FindAll(s => s != null && s.moduleName == "GKContractScenario")?.Count ?? -1;
                Debug.Log($"[GeneKerman][ScenarioTrace] OnLoad incoming node pending={incoming} " +
                          $"protoEntries={protos} nodeNull={(node == null)}");
            }
            catch (Exception) { }
#endif
            // Never let an exception escape into KSP's ScenarioRunner.AddModule — a throw
            // here logs "Exception loading ScenarioModule GKContractScenario" and drops
            // our persisted state. Guard the node and collections defensively.
            if (activeContracts == null) activeContracts = new Dictionary<string, string>();
            if (importedVessels == null) importedVessels = new HashSet<string>();
            if (immunities == null) immunities = new List<RescueImmunityRecord>();
            if (rescueSubmittedPids == null)
                rescueSubmittedPids = new Dictionary<string, List<string>>();
            if (rescueWrecks == null) rescueWrecks = new Dictionary<string, RescueWreckRecord>();
            if (pendingRescueRemovals == null)
                pendingRescueRemovals = new Dictionary<string, PendingRescueRemoval>();
            activeContracts.Clear();
            importedVessels.Clear();
            immunities.Clear();
            rescueSubmittedPids.Clear();
            rescueWrecks.Clear();
            pendingRescueRemovals.Clear();

            // The cheat-taint store lives in CheatDetection (static, so flight-scene
            // writers never race the scenario's lifecycle); this scenario is only its
            // persistence. Must run before the null-node return: loading a save with
            // no taints has to CLEAR taints carried over from another save.
            CheatDetection.LoadFrom(node);

            if (node == null) return;

            try
            {
                var mappings = node.GetNode("CONTRACT_MAPPINGS");
                if (mappings != null)
                {
                    foreach (ConfigNode.Value val in mappings.values)
                        activeContracts[val.name] = val.value;
                }

                var imports = node.GetNode("IMPORTED_VESSELS");
                if (imports != null)
                {
                    foreach (ConfigNode.Value val in imports.values)
                        importedVessels.Add(val.value);
                }

                var imm = node.GetNode("RESCUE_IMMUNITY");
                if (imm != null)
                {
                    foreach (ConfigNode rec in imm.GetNodes("RECORD"))
                    {
                        var r = RescueImmunityRecord.FromNode(rec);
                        if (r != null) immunities.Add(r);
                    }
                }

                var subs = node.GetNode("RESCUE_SUBMISSIONS");
                if (subs != null)
                {
                    foreach (ConfigNode rec in subs.GetNodes("RECORD"))
                    {
                        string cid = rec.GetValue("cid");
                        if (string.IsNullOrEmpty(cid)) continue;
                        // GetValues, not GetValue: a record written by an older build
                        // carries exactly one pid and comes back as a 1-element array,
                        // so the upgrade needs no migration step of its own.
                        var pids = new List<string>();
                        foreach (var p in rec.GetValues("pid"))
                            if (!string.IsNullOrEmpty(p) && !pids.Contains(p)) pids.Add(p);
                        if (pids.Count > 0) rescueSubmittedPids[cid] = pids;
                    }
                }

                var wrecks = node.GetNode("RESCUE_WRECKS");
                if (wrecks != null)
                {
                    foreach (ConfigNode rec in wrecks.GetNodes("RECORD"))
                    {
                        string cid = rec.GetValue("cid");
                        if (string.IsNullOrEmpty(cid)) continue;
                        var w = new RescueWreckRecord
                        {
                            ContractId = cid,
                            Pid = rec.GetValue("pid") ?? "",
                        };
                        foreach (var p in rec.GetValues("part"))
                        {
                            uint fid;
                            if (uint.TryParse(p, out fid) && fid != 0) w.PartFlightIds.Add(fid);
                        }
                        // A child node per rename rather than one packed value: a kerbal
                        // name carries spaces and an apostrophe, so any separator we
                        // picked would be a name we could not round-trip.
                        foreach (ConfigNode rn in rec.GetNodes("RENAME"))
                        {
                            string from = rn.GetValue("from");
                            string to = rn.GetValue("to");
                            if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                                w.CrewRenames[from] = to;
                        }
                        rescueWrecks[cid] = w;
                    }
                }

                var pend = node.GetNode("RESCUE_PENDING_REMOVALS");
                if (pend != null)
                {
                    foreach (ConfigNode rec in pend.GetNodes("RECORD"))
                    {
                        string pid = rec.GetValue("pid");
                        if (string.IsNullOrEmpty(pid)) continue;
                        var entry = new PendingRescueRemoval
                        {
                            Name = rec.GetValue("name") ?? pid,
                            // Absent on records queued before the fate was recorded: keep
                            // what those records meant when they were written.
                            CrewFate = PendingRescueRemoval.ParseFate(rec.GetValue("crewFate")),
                        };
                        foreach (var cn in rec.GetValues("crew"))
                            if (!string.IsNullOrEmpty(cn)) entry.Crew.Add(cn);
                        pendingRescueRemovals[pid] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GKContractScenario.OnLoad failed: {ex.Message}");
            }
            // The one that actually answers "did the state survive?". OnLoad:enter is
            // taken before the dictionaries are filled, so its counts are always zero and
            // prove nothing — a gap that cost a whole verification round.
            MergeCarriedRemovals();
            GKScenarioTrace("OnLoad:exit");
        }

        public override void OnSave(ConfigNode node)
        {
            GKScenarioTrace("OnSave");
            if (node == null) return;
            try
            {
                var mappings = node.AddNode("CONTRACT_MAPPINGS");
                foreach (var kvp in (activeContracts ?? new Dictionary<string, string>()))
                    mappings.AddValue(kvp.Key, kvp.Value);

                var imports = node.AddNode("IMPORTED_VESSELS");
                foreach (var cid in (importedVessels ?? new HashSet<string>()))
                    imports.AddValue("contract_id", cid);

                var imm = node.AddNode("RESCUE_IMMUNITY");
                foreach (var r in (immunities ?? new List<RescueImmunityRecord>()))
                    r.Save(imm.AddNode("RECORD"));

                var subs = node.AddNode("RESCUE_SUBMISSIONS");
                foreach (var kvp in (rescueSubmittedPids ?? new Dictionary<string, List<string>>()))
                {
                    if (kvp.Value == null || kvp.Value.Count == 0) continue;
                    var rec = subs.AddNode("RECORD");
                    rec.AddValue("cid", kvp.Key);
                    // Order is load-bearing: the contract craft first, so an older build
                    // reading this back with GetValue("pid") gets the rescue craft and
                    // not an extra. Never sort or reverse this.
                    foreach (var pid in kvp.Value)
                        if (!string.IsNullOrEmpty(pid)) rec.AddValue("pid", pid);
                }

                var wrecks = node.AddNode("RESCUE_WRECKS");
                foreach (var kvp in (rescueWrecks ?? new Dictionary<string, RescueWreckRecord>()))
                {
                    if (kvp.Value == null) continue;
                    var rec = wrecks.AddNode("RECORD");
                    rec.AddValue("cid", kvp.Key);
                    rec.AddValue("pid", kvp.Value.Pid ?? "");
                    foreach (var fid in (kvp.Value.PartFlightIds ?? new List<uint>()))
                        rec.AddValue("part", fid);
                    foreach (var rn in (kvp.Value.CrewRenames ?? new Dictionary<string, string>()))
                    {
                        var rnode = rec.AddNode("RENAME");
                        rnode.AddValue("from", rn.Key);
                        rnode.AddValue("to", rn.Value);
                    }
                }

                CheatDetection.SaveTo(node);

                var pend = node.AddNode("RESCUE_PENDING_REMOVALS");
                foreach (var kvp in (pendingRescueRemovals ?? new Dictionary<string, PendingRescueRemoval>()))
                {
                    if (kvp.Value == null) continue;
                    var rec = pend.AddNode("RECORD");
                    rec.AddValue("pid", kvp.Key);
                    rec.AddValue("name", kvp.Value.Name ?? kvp.Key);
                    rec.AddValue("crewFate", kvp.Value.CrewFate.ToString());
                    foreach (var cn in (kvp.Value.Crew ?? new List<string>()))
                        rec.AddValue("crew", cn);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GKContractScenario.OnSave failed: {ex.Message}");
            }
        }

        public bool HasImportedVessel(string contractId)
        {
            return importedVessels.Contains(contractId);
        }

        public void MarkVesselImported(string contractId)
        {
            importedVessels.Add(contractId);
        }

        // ── Rescue-kerbal life-support immunity ──────────────────────────────

        /// <summary>Live list of immunity records (the guardian reads/mutates this).</summary>
        public IList<RescueImmunityRecord> Immunities => immunities;

        /// <summary>Register a new immunity record (replacing any for the same contract).</summary>
        public void AddImmunity(RescueImmunityRecord record)
        {
            if (record == null) return;
            immunities.RemoveAll(r => r.ContractId == record.ContractId);
            immunities.Add(record);
        }

        /// <summary>Drop the immunity record for a contract (after handoff).</summary>
        public void RemoveImmunity(string contractId)
        {
            immunities.RemoveAll(r => r.ContractId == contractId);
        }

        // ── Rescue craft hand-over bookkeeping (persisted) ───────────────────

        /// <summary>Remember the craft a rescuer submitted for a contract, so it can be
        /// removed once the issuer approves — survives a relaunch in between.</summary>
        public void RecordRescueSubmission(string contractId, string pid)
        {
            if (!string.IsNullOrEmpty(pid))
                RecordRescueSubmission(contractId, new List<string> { pid });
        }

        /// <summary>
        /// Remember every craft a rescuer handed over for a contract — the rescue craft
        /// FIRST, then any extras sent alongside it.
        ///
        /// This REPLACES whatever was recorded, and must never append. The server keeps
        /// one delivered node per contract and overwrites it on resubmission
        /// (_submit_contract_locked -> delivered_vessel_node_url), so only the newest
        /// submission is ever handed to the issuer. A record that accumulated across
        /// attempts would queue the *previous* attempt's hulls for deletion when the
        /// current one is approved — deleting craft that were never given to anybody.
        /// </summary>
        public void RecordRescueSubmission(string contractId, IEnumerable<string> pids)
        {
            if (string.IsNullOrEmpty(contractId)) return;
            var list = new List<string>();
            foreach (var p in (pids ?? new List<string>()))
                if (!string.IsNullOrEmpty(p) && !list.Contains(p)) list.Add(p);

            if (list.Count == 0) rescueSubmittedPids.Remove(contractId);
            else rescueSubmittedPids[contractId] = list;
        }

        /// <summary>Look up the submitted rescue craft pid for a contract, without
        /// forgetting it. Deliberately non-destructive: the record is the only thing
        /// that ties a contract to a craft in this save, so it must survive until the
        /// removal has actually been queued (see ForgetRescueSubmission). Reading and
        /// forgetting in one step meant a queue that failed — no scenario, no save —
        /// dropped the craft's identity on the floor with it.</summary>
        /// <remarks>Answers with the CONTRACT craft only. Anything that has to act on
        /// every hull handed over — a removal, a duplication check — wants
        /// <see cref="PeekRescueSubmissions"/>; this one is for the caller that means
        /// "the rescue craft itself".</remarks>
        public bool PeekRescueSubmission(string contractId, out string pid)
        {
            pid = null;
            List<string> pids;
            if (!PeekRescueSubmissions(contractId, out pids)) return false;
            pid = pids[0];      // the contract craft — see RecordRescueSubmission
            return true;
        }

        /// <summary>Every craft handed over for a contract, primary first. Same
        /// non-destructive contract as <see cref="PeekRescueSubmission"/>; the caller
        /// gets a copy so it can queue removals while deciding whether to forget.</summary>
        public bool PeekRescueSubmissions(string contractId, out List<string> pids)
        {
            pids = null;
            if (string.IsNullOrEmpty(contractId)) return false;
            List<string> stored;
            if (!rescueSubmittedPids.TryGetValue(contractId, out stored)) return false;
            if (stored == null || stored.Count == 0) return false;
            pids = new List<string>(stored);
            return true;
        }

        /// <summary>Drop the submission record for a contract, once its craft is either
        /// queued for removal or known to be irrelevant (the contract never completed).</summary>
        public void ForgetRescueSubmission(string contractId)
        {
            if (!string.IsNullOrEmpty(contractId))
                rescueSubmittedPids.Remove(contractId);
        }

        /// <summary>Contracts with a craft still awaiting hand-over, newest state as of
        /// the last save. Copied, so a caller can forget entries while iterating.</summary>
        public List<string> OutstandingRescueSubmissions()
        {
            return new List<string>(rescueSubmittedPids.Keys);
        }

        /// <summary>Live map of craft (pid → queued removal) awaiting a safe scene.</summary>
        public IDictionary<string, PendingRescueRemoval> PendingRescueRemovals => pendingRescueRemovals;

#if GK_DEBUG_PANEL
        // Dev-only reads for Web/DebugBridge. COMPILED OUT of production builds, so no
        // shipped code gains access to these. Everything else the bridge needs is
        // already public; these two are not, and both are load-bearing for a crew test:
        // the import dedup is what a re-spawn must not bypass, and CrewRenames is the
        // only record of a stranded kerbal this save had to rename on the way in.
        internal IEnumerable<string> DebugImportedVessels => importedVessels;
        internal IDictionary<string, RescueWreckRecord> DebugRescueWrecks => rescueWrecks;
        internal IDictionary<string, List<string>> DebugRescueSubmittedPids => rescueSubmittedPids;
#endif

        // ── Spawned rescue wrecks (persisted) ────────────────────────────────
        //
        // Deliberately never pruned. Every rule for expiring one trades a bounded
        // storage cost — a pid and a few hundred flightIDs per rescue accepted, beside
        // the wreck's own VESSEL node in the same file — for an unbounded safety cost,
        // because the only thing a stale record can do is keep a hull out of an extras
        // list it should not have been in.

        /// <summary>Remember the wreck this save just spawned for a rescue, so it stays
        /// recognisable after its crew are off it. Replaces any earlier record for the
        /// contract (a respawn is the same wreck under a new pid).</summary>
        public void RecordRescueWreck(string contractId, string pid, IEnumerable<uint> partFlightIds,
                                      IDictionary<string, string> crewRenames = null)
        {
            if (string.IsNullOrEmpty(contractId)) return;
            var rec = new RescueWreckRecord { ContractId = contractId, Pid = pid ?? "" };
            foreach (var fid in (partFlightIds ?? new List<uint>()))
                if (fid != 0 && !rec.PartFlightIds.Contains(fid)) rec.PartFlightIds.Add(fid);
            foreach (var kvp in (crewRenames ?? new Dictionary<string, string>()))
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                    rec.CrewRenames[kvp.Key] = kvp.Value;
            rescueWrecks[contractId] = rec;
        }

        /// <summary>The names this save actually holds for a rescue contract's crew:
        /// the contract's own list with any import-time rename applied. Returns the list
        /// unchanged when nothing was renamed, which is every ordinary rescue — see
        /// <see cref="RescueWreckRecord.CrewRenames"/> for why a rename must not be
        /// allowed to go unrecorded.</summary>
        public List<string> LocalRescueCrewNames(string contractId, IEnumerable<string> names)
        {
            var mapped = new List<string>();
            RescueWreckRecord rec = null;
            if (!string.IsNullOrEmpty(contractId)) rescueWrecks.TryGetValue(contractId, out rec);
            foreach (var n in (names ?? new List<string>()))
            {
                if (string.IsNullOrEmpty(n)) continue;
                string local;
                mapped.Add(rec != null && rec.CrewRenames != null &&
                           rec.CrewRenames.TryGetValue(n, out local) ? local : n);
            }
            return mapped;
        }

        /// <summary>Whether this save knows, by itself, which craft the wreck for this
        /// contract is. False means the wreck cannot be told apart from a craft the
        /// rescuer brought along, and nothing may be handed over on that assumption.</summary>
        public bool KnowsRescueWreck(string contractId)
        {
            RescueWreckRecord rec;
            if (string.IsNullOrEmpty(contractId) ||
                !rescueWrecks.TryGetValue(contractId, out rec) || rec == null) return false;
            return !string.IsNullOrEmpty(rec.Pid) || rec.PartFlightIds.Count > 0;
        }

        /// <summary>Every part flightID belonging to any wreck this save has spawned for
        /// a rescue. Union rather than per-contract on purpose: a rescuer running two
        /// rescues must not hand the second one's wreck to the first one's issuer, and
        /// over-excluding a craft only costs an extra that could have been sent.</summary>
        public HashSet<uint> AllRescueWreckPartIds()
        {
            var ids = new HashSet<uint>();
            foreach (var rec in rescueWrecks.Values)
            {
                if (rec?.PartFlightIds == null) continue;
                foreach (var fid in rec.PartFlightIds) ids.Add(fid);
            }
            return ids;
        }

        /// <summary>The pids of every wreck this save has spawned for a rescue. Same
        /// union reasoning as <see cref="AllRescueWreckPartIds"/>.</summary>
        public HashSet<string> AllRescueWreckPids()
        {
            var pids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in rescueWrecks.Values)
                if (rec != null && !string.IsNullOrEmpty(rec.Pid)) pids.Add(rec.Pid);
            return pids;
        }

        /// <summary>
        /// Inject a mission from our API as a stock contract.
        /// </summary>
        public void InjectContract(string apiContractId, string missionDesc,
            int payment, int difficulty, string dueDate)
        {
            if (activeContracts.ContainsKey(apiContractId))
            {
                Debug.Log($"[GeneKerman] Contract {apiContractId} already injected.");
                return;
            }

            try
            {
                // Track the mapping — stock contract injection happens when
                // the ContractSystem is ready and processes our contract type.
                // For now, store the mapping so we can complete/cancel later.
                activeContracts[apiContractId] = apiContractId; // self-mapping until stock contract is created

                Debug.Log($"[GeneKerman] Tracked contract mapping: {apiContractId} → {missionDesc}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Failed to track contract: {ex.Message}");
            }
        }

        /// <summary>
        /// Mark a stock contract as completed when our API confirms it.
        /// </summary>
        public void CompleteContract(string apiContractId)
        {
            if (!activeContracts.TryGetValue(apiContractId, out string guid))
                return;

            var contract = ContractSystem.Instance?.Contracts
                .FirstOrDefault(c => c.ContractGuid.ToString() == guid);

            if (contract != null && contract.ContractState == Contract.State.Active)
            {
                // Complete all parameters first
                foreach (var param in contract.AllParameters)
                {
                    if (param is GKMissionParameter gkParam)
                        gkParam.MarkComplete();
                }

                Debug.Log($"[GeneKerman] Stock contract completed: {apiContractId}");
            }

            activeContracts.Remove(apiContractId);
        }

        /// <summary>
        /// Cancel a stock contract when the API contract is cancelled/expired.
        /// </summary>
        public void CancelContract(string apiContractId)
        {
            if (!activeContracts.TryGetValue(apiContractId, out string guid))
                return;

            var contract = ContractSystem.Instance?.Contracts
                .FirstOrDefault(c => c.ContractGuid.ToString() == guid);

            if (contract != null)
            {
                contract.Cancel();
                Debug.Log($"[GeneKerman] Stock contract cancelled: {apiContractId}");
            }

            activeContracts.Remove(apiContractId);
        }
    }

    /// <summary>
    /// Custom contract type for Gene Kerman missions.
    /// Appears in Mission Control with our custom description and rewards.
    /// </summary>
    public class GKMissionContract : Contract
    {
        // Stored data
        private string apiContractId = "";
        private string missionDescription = "";
        private int missionPayment;
        private int missionDifficulty;
        private string missionDueDate = "";

        public void SetMissionData(string contractId, string desc, int payment, int difficulty, string dueDate)
        {
            apiContractId = contractId;
            missionDescription = desc;
            missionPayment = payment;
            missionDifficulty = difficulty;
            missionDueDate = dueDate;
        }

        protected override bool Generate()
        {
            // This is called by the contract system — we handle generation ourselves
            // via InjectContract, so this just needs to return true for manual injection
            SetExpiry();
            SetDeadlineYears(0.1f); // Short deadline
            SetReputation(missionDifficulty * 5f, missionDifficulty * -2f, null);
            SetFunds(0, missionPayment, missionPayment * 0.5f, null);

            // Add a single parameter
            AddParameter(new GKMissionParameter(missionDescription, apiContractId));

            return true;
        }

        public override bool CanBeCancelled() => true;
        public override bool CanBeDeclined() => true;

        protected override string GetTitle()
        {
            return $"[BM] {missionDescription}";
        }

        protected override string GetDescription()
        {
            return $"Boundless Missions assignment.\n\n" +
                   $"Mission: {missionDescription}\n" +
                   $"Difficulty: {missionDifficulty}/10\n" +
                   $"Due: {missionDueDate}\n\n" +
                   $"Submit your completion from the Boundless Missions mod panel (Boundless Missions toolbar button).";
        }

        protected override string GetSynopsys()
        {
            return missionDescription;
        }

        protected override string MessageCompleted()
        {
            return $"Mission completed! Rewards distributed via Boundless Missions system.";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("gk_contractId", apiContractId);
            node.AddValue("gk_description", missionDescription);
            node.AddValue("gk_payment", missionPayment);
            node.AddValue("gk_difficulty", missionDifficulty);
            node.AddValue("gk_dueDate", missionDueDate);
        }

        protected override void OnLoad(ConfigNode node)
        {
            apiContractId = node.GetValue("gk_contractId") ?? "";
            missionDescription = node.GetValue("gk_description") ?? "";
            int.TryParse(node.GetValue("gk_payment") ?? "0", out missionPayment);
            int.TryParse(node.GetValue("gk_difficulty") ?? "0", out missionDifficulty);
            missionDueDate = node.GetValue("gk_dueDate") ?? "";
        }

        public override bool MeetRequirements() => true;
    }

    /// <summary>
    /// Parameter that tracks a Gene Kerman mission objective.
    /// Completion is driven by the API, not by in-game events.
    /// </summary>
    public class GKMissionParameter : ContractParameter
    {
        private string description = "";
        private string apiContractId = "";

        public GKMissionParameter() { } // Required for deserialization

        public GKMissionParameter(string desc, string contractId)
        {
            description = desc;
            apiContractId = contractId;
        }

        protected override string GetTitle()
        {
            return description;
        }

        protected override string GetHashString()
        {
            return $"GKMission_{apiContractId}";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("gk_desc", description);
            node.AddValue("gk_cid", apiContractId);
        }

        protected override void OnLoad(ConfigNode node)
        {
            description = node.GetValue("gk_desc") ?? "";
            apiContractId = node.GetValue("gk_cid") ?? "";
        }

        /// <summary>
        /// Called by our scenario when the API confirms completion.
        /// </summary>
        public void MarkComplete()
        {
            SetComplete();
        }
    }
}
