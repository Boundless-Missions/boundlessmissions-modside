/*
 * ClientState.cs – Everything the mod knows about the player's account, and every
 * call that changes it.
 *
 * This is the classic window (UI/MainWindow.cs) with the window taken away. That
 * file was two things at once: seven IMGUI tabs, and the single owner of the
 * profile, the mission list, the contract list and the notification feed — fetch,
 * cache, de-dup, unread count and every action coroutine. The tabs have all been
 * replaced by the uGUI sidebar, but the second half is what the sidebar *reads*,
 * so deleting the file would have deleted the sidebar's data source.
 *
 * So the rule that made the split worth doing survives the window it was written
 * for: there is exactly one copy of this state, and every front end — the sidebar
 * panels, the browser bridge (Web/GkRoutes.cs), the notification socket — reads
 * that copy rather than fetching its own. The lists are exposed by reference, not
 * copied, because two copies drift the moment either side gains a mutation.
 *
 * Every action is a Request* wrapper over a coroutine that calls back exactly once
 * with (ok, message). That shape is not decoration: it is what let the sidebar's
 * panels drive these without a status line to write into, and it is why the
 * coroutines below never touch a UI field.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public class ClientState
    {
        // Cached data
        private Dictionary<string, object> profile;
        private List<object> missions;
        private string weekKey = "";
        private bool missionsLocked;
        private List<object> contracts;
        private List<object> notifications;
        // Client-raised notifications (photo shared, craft installed, device
        // approved, …) have no server record, so they're kept here and re-merged
        // into `notifications` on every refresh — a bare server fetch would wipe
        // them. Session-local: not persisted, gone on restart.
        private readonly List<object> localNotifications = new List<object>();

        // Loading states
        private bool loadingMissions, loadingContracts, loadingProfile, loadingNotifs;

        // Craft import queue — crafts the player selected in Discord. The mod polls
        // /api/v1/craft/imports/pending and auto-imports each into the active save.
        private bool importPollInFlight;
        private readonly HashSet<string> processingImports = new HashSet<string>();

        // Rescue wrecks whose download is currently in flight, keyed by contract id, so
        // a rapid double-click of the Spawn button can't kick off two spawns at once.
        // Permanent dedup lives in GKContractScenario.HasImportedVessel (per-save).
        private readonly HashSet<string> spawnedRescueWrecks = new HashSet<string>();

        /// <summary>
        /// The client is behind the server's required version and the player chose
        /// "Continue anyway". Every fetch is skipped, since each one would just come
        /// back 426; the sidebar narrows itself to the panels that work without a
        /// server (see SidebarPanel.WorksOffline).
        /// </summary>
        private static bool LimitedMode =>
            GeneKermanMod.Instance != null && GeneKermanMod.Instance.UpdateRequired;

        // ── Refresh ─────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch everything. Called when the mod links, when the interface is opened
        /// and after a server change.
        /// </summary>
        public void RefreshAll()
        {
            // Under the version gate every one of these comes back 426. They are
            // re-run by RecheckVersion clearing the gate, so nothing is lost.
            if (LimitedMode) return;

            RefreshProfile();
            RefreshMissions();
            RefreshContracts();
            RefreshNotifications();
        }

        /// <summary>
        /// The mod was pointed at a different server. Whatever was in flight belonged
        /// to the old one and its callbacks will be dropped, so the loading flags have
        /// to be cleared by hand or a panel waits forever on a fetch that will never
        /// land. GeneKermanMod.OnServerChanged does the socket, the version gate and
        /// the link prompt; this does the data.
        /// </summary>
        public void ServerChanged()
        {
            loadingProfile = false;
            loadingMissions = false;
            loadingContracts = false;
            loadingNotifs = false;
            RefreshAll();
        }

        /// <summary>
        /// Progress and failures from the action coroutines. The classic window drew
        /// these on a status line; every caller now gets the same text through its
        /// onDone callback, so this is the log copy — kept because a message that
        /// nothing was watching for is still the first thing asked for in a bug report.
        /// </summary>
        private void SetStatus(string msg)
        {
            if (!string.IsNullOrEmpty(msg)) Debug.Log("[GeneKerman] " + msg);
        }

        public void UpdateProfile(Dictionary<string, object> data)
        {
            profile = data;
        }

        // ── Read-only views, for whoever is drawing ─────────────────────────
        //
        // A panel *displays* these; this class keeps owning the fetch, the
        // local-notification merge, the de-dup and the unread count. Exposing the
        // list rather than copying it is the point — a second copy would drift the
        // moment either side gained a mutation.

        /// <summary>The loaded feed, newest first. Null before the first fetch.</summary>
        internal IList<object> NotificationFeed => notifications;

        /// <summary>True while a notification fetch is in flight.</summary>
        internal bool NotificationsLoading => loadingNotifs;

        /// <summary>Kick a refresh from a front end (the same call its refresh button makes).</summary>
        internal void RequestNotificationRefresh() => RefreshNotifications();

        /// <summary>The account profile blob. Null before the first fetch.</summary>
        internal Dictionary<string, object> ProfileData => profile;
        internal bool ProfileLoading => loadingProfile;
        internal void RequestProfileRefresh() => RefreshProfile();

        /// <summary>This week's missions, plus the two facts that qualify them.</summary>
        internal IList<object> MissionList => missions;
        internal string MissionWeekKey => weekKey;
        internal bool MissionsLocked => missionsLocked;
        internal bool MissionsLoading => loadingMissions;
        internal void RequestMissionsRefresh() => RefreshMissions();

        /// <summary>Active + incoming contracts, as the API returned them.</summary>
        internal IList<object> ContractList => contracts;
        internal bool ContractsLoading => loadingContracts;
        internal void RequestContractsRefresh() => RefreshContracts();

        /// <summary>One contract by id, from the same cache the list renders.</summary>
        internal Dictionary<string, object> FindContract(string contractId) => FindContractById(contractId);

        // ── Actions ─────────────────────────────────────────────────────────
        //
        // These are the whole action surface: one wrapper per coroutine, rather than
        // reissuing the API calls from the sidebar. That matters more than it
        // looks: the bodies carry side effects a second copy would silently drop —
        // DoSelectMission injects the contract into KSP's stock contract system,
        // DoCancelContract and DoGiveUpContract stop EditorPartEnforcer if it is
        // gating parts for that contract, and every one of them re-reads the list
        // afterwards. The Python side learned this the hard way in 6a-i, where two
        // copies of "give up" disagreed about whether the fine was charged.
        //
        // Each takes an onDone the coroutine invokes exactly once, so a caller that
        // is not the caller can report the outcome; the old status line
        // still updates either way.

        internal void RequestSelectMission(int missionId, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoSelectMission(missionId, onDone));

        internal void RequestAcceptContract(string contractId, string issuerName, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoAcceptContract(contractId, issuerName, onDone));

        internal void RequestCancelContract(string contractId, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoCancelContract(contractId, onDone));

        internal void RequestGiveUpContract(string contractId, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoGiveUpContract(contractId, onDone));

        internal void RequestReviewContract(string contractId, bool approve, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoReviewContract(contractId, approve, onDone));

        internal void RequestDispute(string contractId, string action, string newDate, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoDispute(contractId, action, newDate, onDone));

        internal void RequestDownloadCraft(string contractId, string ownerName, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoDownloadCraft(contractId, ownerName, onDone));

        /// <summary>Spawn an accepted rescue's stranded vessel into this save. Takes the
        /// contract dict rather than the pieces because everything the spawn needs — the
        /// wreck URL, the target, the tagged crew names, the LS flag — is read off it,
        /// and a caller assembling those itself would be a second place to get them
        /// wrong.</summary>
        internal void RequestSpawnRescueWreck(Dictionary<string, object> contract, Action<bool, string> onDone)
        {
            if (contract == null) { onDone?.Invoke(false, "No contract."); return; }

            string cid = MiniJSON.GetString(contract, "contract_id");
            string wreckUrl = MiniJSON.GetString(contract, "rescue_vessel_node_url", null);
            if (string.IsNullOrEmpty(wreckUrl))
            {
                onDone?.Invoke(false, "Vessel data unavailable. Refresh contracts and try again.");
                return;
            }

            var kerbals = MiniJSON.GetList(contract, "rescue_kerbals")
                .Select(o => o?.ToString())
                .Where(s => !string.IsNullOrEmpty(s)).ToList();

            GeneKermanMod.Instance.RunCoroutine(DoSpawnRescueWreck(
                cid, wreckUrl,
                RescueTargetSpec.FromDict(MiniJSON.GetDict(contract, "rescue_target")),
                MiniJSON.GetString(contract, "issuer_name", ""),
                kerbals,
                MiniJSON.GetString(contract, "life_support", "none"),
                onDone));
        }

        internal void RequestLogoutAllDevices()
            => GeneKermanMod.Instance.RunCoroutine(DoLogoutAllDevices());

        /// <summary>Mark one notification read. The feed object is found here rather
        /// than passed in, so a caller cannot hand us a dict that is not in the list
        /// and leave the badge counting something that is no longer on screen.</summary>
        internal void RequestMarkNotificationRead(string id, Action<bool, string> onDone)
        {
            var n = FindNotification(id);
            if (n == null) { onDone?.Invoke(false, "That notification is gone."); return; }
            DoMarkNotificationRead(n, id, onDone);
        }

        internal void RequestDismissNotification(string id, Action<bool, string> onDone)
            => DoDismissNotification(id, onDone);

        internal void RequestMarkAllNotificationsRead(Action<bool, string> onDone)
            => DoMarkAllNotificationsRead(onDone);

        internal void RequestDismissReadNotifications(Action<bool, string> onDone)
            => DoDismissReadNotifications(onDone);

        private Dictionary<string, object> FindNotification(string id)
        {
            if (notifications == null || string.IsNullOrEmpty(id)) return null;
            foreach (var o in notifications)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && MiniJSON.GetString(d, "id") == id) return d;
            }
            return null;
        }

        public void AddNotification(Dictionary<string, object> n)
        {
            if (n == null) return;
            if (notifications == null) notifications = new List<object>();

            string id = MiniJSON.GetString(n, "id");
            foreach (var o in notifications)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && MiniJSON.GetString(d, "id") == id) return; // already present
            }
            notifications.Insert(0, n); // newest first
        }

        /// <summary>
        /// Add a client-originated notification (no server record) to the feed.
        /// Kept in a separate backing list so RefreshNotifications can merge it back
        /// after replacing `notifications` with the server's. The unread badge is
        /// managed by the caller (GeneKermanMod.RaiseLocalNotification).
        /// </summary>
        public void AddLocalNotification(Dictionary<string, object> n)
        {
            if (n == null) return;
            localNotifications.Insert(0, n);          // newest first
            if (notifications == null) notifications = new List<object>();
            notifications.Insert(0, n);               // show now, without a refresh
        }

        /// <summary>True for ids minted by RaiseLocalNotification (no server record).</summary>
        private static bool IsLocalNotif(string id)
        {
            return id != null && id.StartsWith("local-");
        }

        /// <summary>Switch to the feed. Used when a toast has something to press rather
        /// than a contract to open (see LocalNotifActions).</summary>
        private static List<string> ToStringList(List<object> list)
        {
            var result = new List<string>();
            if (list != null)
                foreach (var o in list)
                    if (o != null) result.Add(o.ToString());
            return result;
        }

        private Dictionary<string, object> FindContractById(string cid)
        {
            if (contracts == null) return null;
            foreach (var cObj in contracts)
            {
                var c = cObj as Dictionary<string, object>;
                if (c != null && MiniJSON.GetString(c, "contract_id") == cid)
                    return c;
            }
            return null;
        }

        // ── Account ─────────────────────────────────────────────────────────

        private System.Collections.IEnumerator DoLogoutAllDevices()
        {
            yield return GeneKermanMod.Instance.Api.LogoutAllDevices((ok, resp, status) =>
            {
                if (ok)
                {
                    GeneKermanMod.Instance.ShowLinkWindow = true;
                    SetStatus("Logged out of all devices.");
                }
                else
                {
                    SetStatus("(No) Could not log out all devices. Try again.");
                }
            });
        }

        private void DoMarkNotificationRead(Dictionary<string, object> n, string id,
                                            Action<bool, string> onDone = null)
        {
            if (string.IsNullOrEmpty(id)) { onDone?.Invoke(false, "No notification."); return; }
            // Local notifications have no server record — mark them read in place.
            if (IsLocalNotif(id))
            {
                n["read"] = true;
                RecountUnread();
                onDone?.Invoke(true, "Marked read.");
                return;
            }
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.MarkNotificationRead(id, (ok, resp, status) =>
            {
                if (ok)
                {
                    n["read"] = true;
                    RecountUnread();
                }
                onDone?.Invoke(ok, ok ? "Marked read." : "Could not mark it read.");
            }));
        }

        /// <summary>
        /// Mark the whole feed read. Extracted from the notifications screen when the
        /// sidebar grew the same button: the read flags, the unread badge and the
        /// server call have to move together, and two copies of that is how a badge
        /// ends up disagreeing with the list under it.
        /// </summary>
        private void DoMarkAllNotificationsRead(Action<bool, string> onDone = null)
        {
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.MarkNotificationsRead((ok, resp, status) =>
            {
                if (ok)
                {
                    if (notifications != null)
                        foreach (var o in notifications)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d != null) d["read"] = true;
                        }
                    RecountUnread();
                    SetStatus("(Ok) All notifications marked read.");
                }
                onDone?.Invoke(ok, ok ? "All notifications marked read." : "Could not mark them read.");
            }));
        }

        private void DoDismissNotification(string id, Action<bool, string> onDone = null)
        {
            if (string.IsNullOrEmpty(id)) { onDone?.Invoke(false, "No notification."); return; }
            // Local notifications have no server record — drop them from both lists.
            if (IsLocalNotif(id))
            {
                localNotifications.RemoveAll(o =>
                {
                    var d = o as Dictionary<string, object>;
                    return d != null && MiniJSON.GetString(d, "id") == id;
                });
                if (notifications != null)
                    notifications.RemoveAll(o =>
                    {
                        var d = o as Dictionary<string, object>;
                        return d != null && MiniJSON.GetString(d, "id") == id;
                    });
                RecountUnread();
                SetStatus("(Ok) Notification dismissed.");
                onDone?.Invoke(true, "Notification dismissed.");
                return;
            }
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.DismissNotification(id, (ok, resp, status) =>
            {
                if (ok && notifications != null)
                {
                    notifications.RemoveAll(o =>
                    {
                        var d = o as Dictionary<string, object>;
                        return d != null && MiniJSON.GetString(d, "id") == id;
                    });
                    RecountUnread();
                    SetStatus("(Ok) Notification dismissed.");
                }
                onDone?.Invoke(ok, ok ? "Notification dismissed." : "Could not dismiss it.");
            }));
        }

        /// <summary>
        /// Clear the read half of the feed. The two kinds have to be handled apart:
        /// local notifications have no server record and are dropped here, while the
        /// server-backed ones are only dropped once the delete lands — so a failed
        /// call leaves the list exactly as the server still has it rather than hiding
        /// rows that come straight back on the next refresh.
        ///
        /// A feed whose read rows are all local skips the call altogether; there is
        /// nothing on the server to delete, and a request that can only 200 on an
        /// empty query is one the player waits through for no reason.
        /// </summary>
        private void DoDismissReadNotifications(Action<bool, string> onDone = null)
        {
            int serverBacked = 0;
            if (notifications != null)
                foreach (var o in notifications)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && MiniJSON.GetBool(d, "read") &&
                        !IsLocalNotif(MiniJSON.GetString(d, "id")))
                        serverBacked++;
                }

            if (serverBacked == 0)
            {
                int dropped = DropReadNotifications(true);
                if (dropped == 0) { onDone?.Invoke(false, "Nothing read to clear."); return; }
                SetStatus("(Ok) Cleared " + dropped + " read notification" + (dropped == 1 ? "." : "s."));
                onDone?.Invoke(true, "Cleared read notifications.");
                return;
            }

            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.DismissReadNotifications((ok, resp, status) =>
            {
                if (ok)
                {
                    int dropped = DropReadNotifications(true);
                    SetStatus("(Ok) Cleared " + dropped + " read notification" + (dropped == 1 ? "." : "s."));
                }
                onDone?.Invoke(ok, ok ? "Cleared read notifications." : "Could not clear them.");
            }));
        }

        /// <summary>Remove every read notification from the feed (and, when
        /// <paramref name="includeLocal"/>, from the local backing list too, or a
        /// refresh would merge them straight back in). Returns how many went.</summary>
        private int DropReadNotifications(bool includeLocal)
        {
            Predicate<object> isRead = o =>
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !MiniJSON.GetBool(d, "read")) return false;
                return includeLocal || !IsLocalNotif(MiniJSON.GetString(d, "id"));
            };

            int dropped = 0;
            if (includeLocal) localNotifications.RemoveAll(isRead);
            if (notifications != null) dropped = notifications.RemoveAll(isRead);
            RecountUnread();
            return dropped;
        }

        /// <summary>Recompute the unread badge from the currently loaded notifications.</summary>
        private void RecountUnread()
        {
            int unread = 0;
            if (notifications != null)
                foreach (var o in notifications)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && !MiniJSON.GetBool(d, "read")) unread++;
                }
            GeneKermanMod.Instance.UnreadNotifications = unread;
        }

        // ── Fetching ────────────────────────────────────────────────────────

        private void RefreshProfile()
        {
            loadingProfile = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetProfile((ok, data, err) =>
            {
                loadingProfile = false;
                if (ok) profile = data;
            }));
        }

        private void RefreshMissions()
        {
            loadingMissions = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetWeeklyMissions((ok, data, err) =>
            {
                loadingMissions = false;
                if (ok)
                {
                    missions = MiniJSON.GetList(data, "missions");
                    weekKey = MiniJSON.GetString(data, "week_key");
                    missionsLocked = MiniJSON.GetBool(data, "is_locked");
                }
            }));
        }

        public void RefreshContracts()
        {
            // Make sure the bot has this install's part list so it can resolve the
            // exact parts named in any mission limits (hash-gated, ~once per session).
            PartCatalogUploader.EnsureUploaded(GeneKermanMod.Instance.Api);
            loadingContracts = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetActiveContracts((ok, data, err) =>
            {
                loadingContracts = false;
                if (ok) contracts = MiniJSON.GetList(data, "contracts");
            }));
        }

        private void RefreshNotifications()
        {
            loadingNotifs = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.GetNotifications((ok, data, err) =>
            {
                loadingNotifs = false;
                if (ok)
                {
                    notifications = MiniJSON.GetList(data, "notifications") ?? new List<object>();
                    int unread = MiniJSON.GetInt(data, "unread_count");
                    // Re-attach session-local notifications the server doesn't know
                    // about (newest first), and fold their unread count into the badge.
                    for (int i = localNotifications.Count - 1; i >= 0; i--)
                    {
                        notifications.Insert(0, localNotifications[i]);
                        var d = localNotifications[i] as Dictionary<string, object>;
                        if (d != null && !MiniJSON.GetBool(d, "read")) unread++;
                    }
                    GeneKermanMod.Instance.UnreadNotifications = unread;
                }
            }));
        }

        // ── Actions ─────────────────────────────────────────────────────────

        private System.Collections.IEnumerator DoSelectMission(int missionId, Action<bool, string> onDone = null)
        {
            // Store mission info for contract injection
            Dictionary<string, object> selectedMission = null;
            if (missions != null)
            {
                selectedMission = missions.Find(m =>
                {
                    var d = m as Dictionary<string, object>;
                    return d != null && MiniJSON.GetInt(d, "id") == missionId;
                }) as Dictionary<string, object>;
            }

            yield return GeneKermanMod.Instance.Api.SelectMission(missionId, (ok, data, err) =>
            {
                if (ok)
                {
                    SetStatus($"(Ok) {MiniJSON.GetString(data, "message", "Mission accepted!")}");
                    RefreshContracts();
                    onDone?.Invoke(true, MiniJSON.GetString(data, "message", "Mission accepted."));

                    // Inject into stock contract system
                    if (GKContractScenario.Instance != null && selectedMission != null)
                    {
                        string cid = MiniJSON.GetString(data, "contract_id", "");
                        if (!string.IsNullOrEmpty(cid))
                        {
                            GKContractScenario.Instance.InjectContract(
                                cid,
                                MiniJSON.GetString(selectedMission, "desc_en"),
                                MiniJSON.GetInt(selectedMission, "coins"),
                                MiniJSON.GetInt(selectedMission, "difficulty"),
                                "" // due date from response
                            );
                        }
                    }
                }
                else
                {
                    SetStatus($"(No) {err ?? "Failed to select mission."}");
                    onDone?.Invoke(false, err ?? "Failed to select mission.");
                }
            });
        }

        private System.Collections.IEnumerator DoAcceptContract(string contractId, string issuerName = "", Action<bool, string> onDone = null)
        {
            // Accept only flips the contract to active. The rescue wreck is NOT spawned
            // here anymore — it spawns on demand via the "Spawn stranded vessel" button on
            // the active contract, so the player triggers it from a valid scene (Flight /
            // Space Center / Tracking Station) and can retry if a spawn fails. Auto-spawning
            // on accept silently lost the wreck whenever accept happened from the editor.
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/accept", "{}", (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus("(Ok) Contract accepted!");
                    RefreshContracts();
                    onDone?.Invoke(true, "Contract accepted.");
                }
                else
                {
                    SetStatus("(No) Failed to accept contract.");
                    onDone?.Invoke(false, "Failed to accept contract.");
                }
            });
        }

        private System.Collections.IEnumerator DoSpawnRescueWreck(string contractId, string wreckUrl, RescueTargetSpec target, string issuerName, List<string> rescueKerbals, string builtWithLs = "none", Action<bool, string> onDone = null)
        {
            // Permanent, per-save dedup: if the wreck is already in this save, never
            // spawn a second one. This is persisted in GKContractScenario, so it holds
            // across restarts (the in-memory set below only guards a double-click while
            // a download is mid-flight).
            if (!string.IsNullOrEmpty(contractId) && GKContractScenario.Instance != null
                && GKContractScenario.Instance.HasImportedVessel(contractId))
            {
                SetStatus("🛟 Stranded vessel already spawned for this contract.");
                onDone?.Invoke(false, "Already spawned into this save.");
                yield break;
            }
            // Transient guard against a double-click while the download is in flight.
            if (!string.IsNullOrEmpty(contractId) && !spawnedRescueWrecks.Add(contractId))
            {
                onDone?.Invoke(false, "Already spawning, give it a moment.");
                yield break;
            }
            string myName = GeneKermanMod.Instance.LinkedUsername;
            yield return GeneKermanMod.Instance.Api.DownloadFile(wreckUrl, (ok, fileData) =>
            {
                if (!ok || fileData == null)
                {
                    SetStatus("⚠ Could not download the stranded vessel. Try again.");
                    onDone?.Invoke(false, "Could not download the stranded vessel. Try again.");
                    return;
                }
                string node = CraftDelivery.DecompressToString(fileData);
                // Spawn the stranded vessel where it actually is (its real orbit from the
                // snapshot) — NOT at the delivery target. The target is where the rescuer
                // must DELIVER the crew, so the wreck has to be elsewhere or there's no
                // mission. Import freezes the snapshot's orbit epoch to "now" so the wreck
                // appears exactly where the issuer left it, instead of KSP propagating the
                // stale epoch forward and placing it wherever the source vessel would be at
                // the current universe time. Crew are tagged with the issuer's name; the
                // rescuer collects them and brings them to the target to complete.
                string name = VesselTransfer.ImportVesselAtTarget(node, null, issuerName, myName);
                if (!string.IsNullOrEmpty(name))
                {
                    // Mark imported only on a real spawn, so a scene-guard / parse
                    // failure leaves the contract retryable instead of locked out.
                    GKContractScenario.Instance?.MarkVesselImported(contractId);

                    // Emergency freeze: the stranded crew are lifted out of the simulation
                    // (and released by every installed LS mod) until the rescuer reaches the
                    // wreck, which also gets a ration kit of THIS install's life support in
                    // case it was built for another mod.
                    RescueImmunityGuardian.Register(contractId, VesselTransfer.LastSpawnedPid,
                                                    rescueKerbals, builtWithLs);
                    string dest = target != null ? target.body : "the target";
                    SetStatus($"🛟 Stranded vessel '{name}' is adrift. Find it and bring the crew to {dest}.");
                    onDone?.Invoke(true, $"'{name}' is adrift. Find it and bring the crew to {dest}.");
                }
                else
                {
                    SetStatus("⚠ Could not spawn. Enter Flight, Space Center, or Tracking Station and try again.");
                    onDone?.Invoke(false, "Could not spawn. Enter Flight, Space Center, or Tracking Station and try again.");
                }
            });
            // Always clear the transient guard so a failed attempt can be retried.
            if (!string.IsNullOrEmpty(contractId)) spawnedRescueWrecks.Remove(contractId);
        }

        private System.Collections.IEnumerator DoReviewContract(string contractId, bool approve, Action<bool, string> onDone = null)
        {
            string body = approve ? "{\"approve\":true}" : "{\"approve\":false}";
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/review", body, (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus(approve ? "Submission approved." : "Submission refused; a dispute is open.");
                    RefreshContracts();
                    onDone?.Invoke(true, approve ? "Submission approved." : "Submission refused; a dispute is open.");
                }
                else
                {
                    SetStatus("(No) Failed to review submission.");
                    onDone?.Invoke(false, "Failed to review submission.");
                }
            });
        }

        private System.Collections.IEnumerator DoDispute(string contractId, string action, string newDate, Action<bool, string> onDone = null)
        {
            var body = new Dictionary<string, object> { { "action", action } };
            if (!string.IsNullOrEmpty(newDate)) body["new_date"] = newDate;

            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/contracts/{contractId}/dispute", MiniJSON.Serialize(body),
                (ok, resp, status) =>
            {
                // The endpoint returns HTTP 200 with success=false for soft failures
                // (e.g. insufficient funds), so check the body, not just the status.
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && (d == null || MiniJSON.GetBool(d, "success", true));
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";

                if (success)
                {
                    SetStatus(string.IsNullOrEmpty(msg) ? "Done." : msg);
                    RefreshContracts();
                    onDone?.Invoke(true, string.IsNullOrEmpty(msg) ? "Done." : msg);
                }
                else
                {
                    SetStatus("(No) " + (string.IsNullOrEmpty(msg) ? "Action failed." : msg));
                    onDone?.Invoke(false, string.IsNullOrEmpty(msg) ? "Action failed." : msg);
                }
            });
        }

        private System.Collections.IEnumerator DoCancelContract(string contractId, Action<bool, string> onDone = null)
        {
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/cancel", "{}", (ok, resp, status) =>
            {
                if (ok)
                {
                    SetStatus("🗑 Contract cancelled.");
                    // Clear enforcer if it was active for this contract
                    if (EditorPartEnforcer.Instance != null &&
                        EditorPartEnforcer.Instance.ActiveContractId == contractId)
                        EditorPartEnforcer.Instance.StopEnforcing();
                    RefreshContracts();
                    onDone?.Invoke(true, "Contract cancelled.");
                }
                else
                {
                    SetStatus("(No) Failed to cancel contract.");
                    onDone?.Invoke(false, "Failed to cancel contract.");
                }
            });
        }

        private System.Collections.IEnumerator DoGiveUpContract(string contractId, Action<bool, string> onDone = null)
        {
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/give_up", "{}", (ok, resp, status) =>
            {
                // The endpoint returns 200 + success:false for soft failures (e.g. the
                // contractor can't cover the fine), so read the body rather than trust
                // the HTTP status alone — and show the server's own message.
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && d != null && MiniJSON.GetBool(d, "success", false);
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";

                if (success)
                {
                    SetStatus("🏳️ " + (string.IsNullOrEmpty(msg) ? "Contract given up." : msg));
                    // Clear the editor enforcer if it was gating parts for this contract.
                    if (EditorPartEnforcer.Instance != null &&
                        EditorPartEnforcer.Instance.ActiveContractId == contractId)
                        EditorPartEnforcer.Instance.StopEnforcing();
                    RefreshContracts();
                    onDone?.Invoke(true, string.IsNullOrEmpty(msg) ? "Contract given up." : msg);
                }
                else
                {
                    SetStatus("(No) " + (string.IsNullOrEmpty(msg) ? "Failed to give up contract." : msg));
                    onDone?.Invoke(false, string.IsNullOrEmpty(msg) ? "Failed to give up contract." : msg);
                }
            });
        }

        private System.Collections.IEnumerator DoDownloadCraft(string contractId, string ownerName = "", Action<bool, string> onDone = null)
        {
            SetStatus("[+] Fetching craft info...");
            yield return CraftDelivery.Deliver(contractId, ownerName, (ok, msg) =>
            {
                SetStatus((ok ? "(Ok) " : "(No) ") + msg);
                onDone?.Invoke(ok, msg);
            });
        }

        // ── Craft import queue ──────────────────────────────────────────────
        //
        // Crafts the player accepted in Discord. Polled at the space centre and in
        // the editor, and auto-imported into the active save; the front ends only
        // ever hear about the result, as a notification.

        public void PollCraftImports()
        {
            if (importPollInFlight) return;
            importPollInFlight = true;
            GeneKermanMod.Instance.RunCoroutine(GeneKermanMod.Instance.Api.Get(
                "/api/v1/craft/imports/pending", (ok, resp, status) =>
            {
                importPollInFlight = false;
                if (!ok || string.IsNullOrEmpty(resp)) return;

                var data = MiniJSON.DeserializeDict(resp);
                foreach (var obj in MiniJSON.GetList(data, "imports"))
                {
                    var entry = obj as Dictionary<string, object>;
                    if (entry == null) continue;
                    string importId = MiniJSON.GetString(entry, "import_id", "");
                    if (string.IsNullOrEmpty(importId) || processingImports.Contains(importId))
                        continue;
                    processingImports.Add(importId);
                    GeneKermanMod.Instance.RunCoroutine(DoProcessImport(entry));
                }
            }));
        }

        private System.Collections.IEnumerator DoProcessImport(Dictionary<string, object> entry)
        {
            string importId = MiniJSON.GetString(entry, "import_id", "");
            string craftName = MiniJSON.GetString(entry, "craft_name", "Craft");
            string craftUrl = MiniJSON.GetString(entry, "craft_url", null);
            string craftFilename = MiniJSON.GetString(entry, "craft_filename", "craft.craft");
            string loadmeta = MiniJSON.GetString(entry, "loadmeta", null);
            string source = MiniJSON.GetString(entry, "source", "");
            string vesselNodeUrl = MiniJSON.GetString(entry, "vessel_node_url", null);
            string ownerName = MiniJSON.GetString(entry, "owner_name", "");
            string flagUrl = MiniJSON.GetString(entry, "flag_url", null);

            // Flag-design payout: a delivered flag PNG. Install it into the flag picker
            // (GameData/BoundlessMissions/Flags) — never a craft or live vessel.
            if (source == "flag" && !string.IsNullOrEmpty(flagUrl))
            {
                bool flagInstalled = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(flagUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    FlagTransfer.InstallStandaloneFlag(craftName, fileData);
                    flagInstalled = true;
                    GeneKermanMod.Instance.ShowNotification("🚩 Flag Installed",
                        $"{craftName} is now available in your flag picker.");
                });
                if (!flagInstalled)
                {
                    // Leave it queued for the next poll (e.g. a download hiccup).
                    processingImports.Remove(importId);
                    yield break;
                }
                yield return GeneKermanMod.Instance.Api.Post(
                    $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
                processingImports.Remove(importId);
                yield break;
            }

            // Rescue deliveries and friend quicksends are LIVE vessels. Rescue: the
            // rescued kerbals coming home (or a cancelled rescue returning to its spot).
            // gift_vessel: a vessel a friend sent straight to your save. Crew are
            // tagged/stripped by owner on import — your own kerbals come back to their
            // original names; anyone else's keep their owner tag.
            if ((source == "rescue_delivery" || source == "gift_vessel") && !string.IsNullOrEmpty(vesselNodeUrl))
            {
                // The poll also runs in the editor now (for blueprint installs), but a
                // live vessel cannot spawn there — leave the entry queued for a scene
                // that can, and skip the download it would waste.
                if (!GiftInbox.CanDeliverHere(source))
                {
                    processingImports.Remove(importId);
                    yield break;
                }
                // A rescue delivery is also reachable through the completed contract's
                // manual Import button (CraftDelivery), which records the contract id
                // in GKContractScenario. Same per-save dedup here, both ways: skip an
                // entry whose craft is already in this save (and ack it, or it re-fires
                // every poll), and record our own spawn so the manual path skips too.
                string rescueCid = source == "rescue_delivery"
                    ? MiniJSON.GetString(entry, "ref_id", "") : "";
                if (!string.IsNullOrEmpty(rescueCid) && GKContractScenario.Instance != null
                    && GKContractScenario.Instance.HasImportedVessel(rescueCid))
                {
                    yield return GeneKermanMod.Instance.Api.Post(
                        $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
                    processingImports.Remove(importId);
                    yield break;
                }
                string myName = GeneKermanMod.Instance.LinkedUsername;
                bool spawned = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(vesselNodeUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string node = CraftDelivery.DecompressToString(fileData);
                    string vesselName = VesselTransfer.ImportVesselAtTarget(node, null, ownerName, myName);
                    if (!string.IsNullOrEmpty(vesselName))
                    {
                        spawned = true;
                        string title = source == "gift_vessel" ? "🎁 Vessel Received" : "🛟 Rescue Delivered";
                        string from = string.IsNullOrEmpty(ownerName) ? "" : $" from {ownerName}";
                        GeneKermanMod.Instance.ShowNotification(title,
                            $"{vesselName}{(source == "gift_vessel" ? from : "")} has arrived in your save.");
                    }
                });
                if (!spawned)
                {
                    processingImports.Remove(importId);
                    yield break;
                }
                if (!string.IsNullOrEmpty(rescueCid))
                    GKContractScenario.Instance?.MarkVesselImported(rescueCid);
                yield return GeneKermanMod.Instance.Api.Post(
                    $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
                processingImports.Remove(importId);
                yield break;
            }

            // Queue entries are blueprint installs (marketplace purchases). We drop
            // the craft into the save's Ships folder — never spawn a live vessel.
            if (!string.IsNullOrEmpty(craftUrl))
            {
                bool installed = false;
                yield return GeneKermanMod.Instance.Api.DownloadFile(craftUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string path = CraftInstaller.Install(fileData, craftFilename, loadmeta);
                    if (path != null)
                    {
                        installed = true;
                        GeneKermanMod.Instance.ShowNotification("🚀 Craft Imported",
                            $"{craftName} saved to your Ships folder.");
                    }
                });
                if (!installed)
                {
                    // Leave it queued for the next poll (e.g. a download hiccup).
                    processingImports.Remove(importId);
                    yield break;
                }
            }

            // Ack — remove from the player's queue so it isn't installed again.
            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
            processingImports.Remove(importId);
        }


    }
}
