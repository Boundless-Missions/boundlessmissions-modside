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
        // The Finance tab's blob: balance, lifetime totals, the daily series and a
        // page of recent movements, exactly as /api/v1/finance returned it. Held
        // here rather than in the panel for the reason every other cache is — a
        // panel is destroyed and rebuilt, and re-fetching a history on every
        // rebuild would hammer the server for data that has not changed.
        private Dictionary<string, object> finance;
        // The page currently held, so a refresh after sending money re-reads the
        // same slice of the ledger the player was looking at rather than jumping
        // them back to the top.
        private int financeOffset;
        private string financeCategory = "";
        // Client-raised notifications (photo shared, craft installed, device
        // approved, …) have no server record, so they're kept here and re-merged
        // into `notifications` on every refresh — a bare server fetch would wipe
        // them. Session-local: not persisted, gone on restart.
        private readonly List<object> localNotifications = new List<object>();

        // Loading states
        private bool loadingMissions, loadingContracts, loadingProfile, loadingNotifs;
        private bool loadingFinance;

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

        /// <summary>
        /// Corp-channel deliveries @-mention this account (server-side preference,
        /// carried on the profile). True before the first fetch, which is the
        /// server's default too — a switch has to draw *something*, and the panel
        /// showing it waits on <see cref="ProfileData"/> being non-null anyway.
        /// </summary>
        internal bool CorpPings => profile == null || MiniJSON.GetBool(profile, "corp_pings", true);

        /// <summary>
        /// Record a preference the server has just confirmed, so the switch that
        /// flipped it stays where the player put it.
        ///
        /// Writing into the cached blob rather than re-fetching the profile is the
        /// point: a refetch costs a round trip to learn something the write already
        /// told us, and until it landed the panel would redraw with the old value —
        /// the switch visibly springing back and then flipping again.
        /// </summary>
        internal void NoteCorpPings(bool enabled)
        {
            if (profile != null) profile["corp_pings"] = enabled;
        }

        /// <summary>The finance blob. Null before the first fetch.</summary>
        internal Dictionary<string, object> FinanceData => finance;
        internal bool FinanceLoading => loadingFinance;
        internal int FinanceOffset => financeOffset;
        internal string FinanceCategory => financeCategory;

        /// <summary>
        /// Re-read the wallet history. <paramref name="offset"/> and
        /// <paramref name="category"/> are remembered, so the plain no-argument
        /// refresh below re-reads whatever page is on screen.
        /// </summary>
        internal void RequestFinanceRefresh(int offset, string category)
        {
            financeOffset = offset < 0 ? 0 : offset;
            financeCategory = category ?? "";
            RefreshFinance();
        }

        internal void RequestFinanceRefresh() => RefreshFinance();

        /// <summary>
        /// Send coins to another player, then re-read both the finance blob and the
        /// profile — the balance is shown in two places and they must not disagree.
        /// The refresh is fired on success only: a refused transfer moved nothing.
        /// </summary>
        internal void RequestSendMoney(string toUserId, int amount, string note,
                                       Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoSendMoney(toUserId, amount, note, onDone));

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

        /// <summary>Answer the contractor's pending settle / more-time request as the
        /// issuer. <paramref name="kind"/> is "settle" or "more_time" — the same two
        /// the browser UI answers via /{kind}_response.</summary>
        internal void RequestDisputeResponse(string contractId, string kind, bool approve, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoDisputeResponse(contractId, kind, approve, onDone));

        /// <summary>Report the counterparty of a contract. Not a state transition — the
        /// contract is untouched and only a moderation ticket is opened.</summary>
        internal void RequestReportContract(string contractId, string reason, Action<bool, string> onDone)
            => GeneKermanMod.Instance.RunCoroutine(DoReportContract(contractId, reason, onDone));

        /// <summary><paramref name="ownerId"/> is the deliverable's owner as an immutable
        /// account id (the contract's `contractor_id`), which is what crew ownership is
        /// decided on; <paramref name="ownerName"/> stays for the tag text and as the
        /// fallback for a caller that has no id. See VesselTransfer.DecideComingHome.</summary>
        internal void RequestDownloadCraft(string contractId, string ownerName, Action<bool, string> onDone,
                                           string ownerId = "")
            => GeneKermanMod.Instance.RunCoroutine(DoDownloadCraft(contractId, ownerName, onDone, ownerId));

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
                onDone,
                // The issuer's immutable account id, which the contract list carries
                // alongside the display name. It is what crew ownership is decided on;
                // the name is passed too, and is only used for the tag text and as the
                // fallback comparison for a contract written before the server sent it.
                MiniJSON.GetString(contract, "issuer_id", "")));
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
                if (!ok) return;
                profile = data;
                // The same response the mod's identity comes from, so adopt it here too.
                // Updating only this cache is what left a client that started while the
                // server was down with an empty LinkedAccountId for the whole session:
                // InitialFetch is the only other setter and it runs once, so every later
                // refresh re-read the id and threw it away. See NoteProfileIdentity.
                GeneKermanMod.Instance.NoteProfileIdentity(data);
            }));
        }

        /// <summary>
        /// Fetch the wallet history for the page currently selected.
        ///
        /// A failed fetch deliberately leaves the previous blob in place rather than
        /// nulling it: a dropped request would otherwise blank a screen the player is
        /// reading, and stale history is far better than none. The panel distinguishes
        /// the two by watching <see cref="FinanceLoading"/>.
        /// </summary>
        private void RefreshFinance()
        {
            if (loadingFinance) return;          // one in flight is enough
            var api = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.Api : null;
            if (api == null || !api.IsLinked) return;

            loadingFinance = true;
            GeneKermanMod.Instance.RunCoroutine(api.GetFinance(
                FinanceGraphDays, FinancePageSize, financeOffset, financeCategory,
                (ok, data, err) =>
                {
                    loadingFinance = false;
                    if (ok && data != null) finance = data;
                }));
        }

        /// <summary>How many days of bars the graph asks for.</summary>
        internal const int FinanceGraphDays = 14;

        /// <summary>How many movements one page of the list holds.</summary>
        internal const int FinancePageSize = 40;

        private System.Collections.IEnumerator DoSendMoney(string toUserId, int amount, string note,
                                        Action<bool, string> onDone)
        {
            var api = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.Api : null;
            if (api == null || !api.IsLinked)
            {
                if (onDone != null) onDone(false, "Not linked to an account.");
                yield break;
            }

            bool sent = false;
            string message = "";
            yield return api.SendMoney(toUserId, amount, note, (ok, data, err) =>
            {
                sent = ok;
                message = ok
                    ? MiniJSON.GetString(data, "message", "Sent.")
                    : (err ?? "Transfer failed");
            });

            if (sent)
            {
                // Both, and in this order: the panel shows the balance from the
                // finance blob and the Profile tab shows it from the profile blob,
                // so refreshing one leaves the other quietly wrong until something
                // else happens to touch it.
                RefreshFinance();
                RefreshProfile();
            }

            if (onDone != null) onDone(sent, message);
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
                if (ok)
                {
                    contracts = MiniJSON.GetList(data, "contracts");
                    // The rescue landing-site markers are derived from this list, so a
                    // freshly accepted (or just-ended) rescue draws — or disappears —
                    // on the refresh rather than on the next idle sync.
                    RescueWaypoints.Poke();
                }
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
                    // Discord-authored text: wash out emoji and <:name:id> markup the
                    // game fonts can't draw, once, before any renderer sees it.
                    foreach (var o in notifications)
                    {
                        var nd = o as Dictionary<string, object>;
                        if (nd == null) continue;
                        nd["title"] = TextSanitizer.CleanNotif(MiniJSON.GetString(nd, "title"));
                        nd["message"] = TextSanitizer.CleanNotif(MiniJSON.GetString(nd, "message"));
                    }
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
                // Two kinds of refusal, and this used to report neither. A business rule
                // arrives as HTTP 200 + success:false with the reason in `message` ("Contract
                // is not pending." — it was withdrawn or accepted while this panel sat open),
                // which a status-only check read as *accepted*. A wrong recipient or a deleted
                // contract arrives as 403/404 with the reason in FastAPI's `detail`. Both are
                // sentences the player can act on, and "Failed to accept contract" is not.
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && d != null && MiniJSON.GetBool(d, "success", false);
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";

                if (success)
                {
                    if (string.IsNullOrEmpty(msg)) msg = "Contract accepted!";
                    SetStatus("(Ok) " + msg);
                    RefreshContracts();
                    onDone?.Invoke(true, msg);
                }
                else
                {
                    string err = RefusalMessage(status, d, "Couldn't accept the contract");
                    SetStatus("(No) " + err);
                    // Deliberately no RefreshContracts() here, even though a refusal usually
                    // means the offer moved on under us. The panel draws this message inside
                    // the open contract's detail pane, and a re-read that drops the contract
                    // from the list closes that pane — taking the explanation with it.
                    onDone?.Invoke(false, err);
                }
            });
        }

        private System.Collections.IEnumerator DoSpawnRescueWreck(string contractId, string wreckUrl, RescueTargetSpec target, string issuerName, List<string> rescueKerbals, string builtWithLs = "none", Action<bool, string> onDone = null, string issuerId = "")
        {
            // The scenario is what makes every guard below real: without it the dedup
            // and the freeze records have nowhere to live, and a null-Instance spawn
            // used to silently skip them all — six identical wrecks from six clicks in
            // an old save KSP never injected the module into. Heal it, and if it still
            // isn't there, refuse to spawn at all rather than spawn unaccountably.
            GKContractScenario.EnsureExists();
            if (GKContractScenario.Instance == null)
            {
                SetStatus("(No) This save's contract records aren't available.");
                onDone?.Invoke(false, "Couldn't prepare this save's contract records; " +
                                      "visit the Space Center once and try again.");
                yield break;
            }

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
                // Ownership is decided on `issuerId` — the issuer's immutable account id,
                // now carried on the contract list next to the display name. The name is
                // still passed because it is the tag text ("{owner}'s {kerbal}"), and it
                // is still the fallback comparison when the id is absent: a contract
                // fetched from a server that predates the field, or an old cached list.
                // Not fail-closed on a missing id, deliberately — refusing to strip the
                // tag makes an honest returning kerbal read as borrowed, and
                // PurgeBorrowedGhostCrew then deletes it. VesselTransfer.DecideComingHome
                // logs the fallback once per session.
                string name = VesselTransfer.ImportVesselAtTarget(
                    node, null, issuerName, myName, null, issuerId);
                if (!string.IsNullOrEmpty(name))
                {
                    // Mark imported only on a real spawn, so a scene-guard / parse
                    // failure leaves the contract retryable instead of locked out.
                    GKContractScenario.Instance?.MarkVesselImported(contractId);

                    // Write down what the wreck IS, while it is unambiguous. Later — at
                    // submission, with its crew aboard the rescue craft and its freeze
                    // record long dropped — nothing else in this save can tell it apart
                    // from a support craft the rescuer flew out, and the extras list is a
                    // list of craft to hand over. On a crew-only rescue the server never
                    // sends wreck_parts, so this is the only answer there is.
                    // The renames ride with the record. TagCrew moves an arriving kerbal
                    // out of the way when this roster already holds the name, and from
                    // that moment the contract's list — written on the issuer's machine,
                    // held by the server, and never told — names nobody in this save. The
                    // freeze below and the hand-over that eventually settles these crew
                    // both work by name, so an unrecorded rename means the stranded crew
                    // are never lifted out of the life-support simulation at all.
                    GKContractScenario.Instance?.RecordRescueWreck(
                        contractId, VesselTransfer.LastSpawnedPid,
                        VesselTransfer.PartFlightIdsOf(VesselTransfer.LastSpawnedPid),
                        VesselTransfer.LastCrewRenames);

                    // Emergency freeze: the stranded crew are lifted out of the simulation
                    // (and released by every installed LS mod) until the rescuer reaches the
                    // wreck, which also gets a ration kit of THIS install's life support in
                    // case it was built for another mod.
                    RescueImmunityGuardian.Register(
                        contractId, VesselTransfer.LastSpawnedPid,
                        GKContractScenario.Instance?.LocalRescueCrewNames(contractId, rescueKerbals)
                            ?? rescueKerbals,
                        builtWithLs);
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

        private System.Collections.IEnumerator DoDisputeResponse(string contractId, string kind,
                                                                 bool approve, Action<bool, string> onDone = null)
        {
            string body = approve ? "{\"approve\":true}" : "{\"approve\":false}";
            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/contracts/{contractId}/{kind}_response", body,
                (ok, resp, status) =>
            {
                // Same soft-failure contract as the dispute endpoint: HTTP 200 with
                // success=false when the request was already answered or withdrawn.
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && (d == null || MiniJSON.GetBool(d, "success", true));
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";
                if (string.IsNullOrEmpty(msg))
                    msg = success ? (approve ? "Request approved." : "Request refused.")
                                  : "Could not answer the request.";

                if (success) { SetStatus(msg); RefreshContracts(); }
                else SetStatus("(No) " + msg);
                onDone?.Invoke(success, msg);
            });
        }

        private System.Collections.IEnumerator DoCancelContract(string contractId, Action<bool, string> onDone = null)
        {
            yield return GeneKermanMod.Instance.Api.Post($"/api/v1/contracts/{contractId}/cancel", "{}", (ok, resp, status) =>
            {
                // Same soft-failure contract as accept, and one refusal here is shaped as a
                // sentence on purpose: a contractor who already accepted is told to use Give
                // Up instead and what the fine costs. `USE_GIVE_UP` is returned as a 200
                // rather than a 403 precisely so both clients can render that text — and
                // trusting the HTTP status alone rendered it as "Contract cancelled".
                var d = MiniJSON.DeserializeDict(resp);
                bool success = ok && d != null && MiniJSON.GetBool(d, "success", false);
                string msg = d != null ? MiniJSON.GetString(d, "message", "") : "";

                if (success)
                {
                    if (string.IsNullOrEmpty(msg)) msg = "Contract cancelled.";
                    SetStatus("🗑 " + msg);
                    // Clear enforcer if it was active for this contract
                    if (EditorPartEnforcer.Instance != null &&
                        EditorPartEnforcer.Instance.ActiveContractId == contractId)
                        EditorPartEnforcer.Instance.StopEnforcing();
                    RefreshContracts();
                    onDone?.Invoke(true, msg);
                }
                else
                {
                    string err = RefusalMessage(status, d, "Couldn't cancel the contract");
                    SetStatus("(No) " + err);
                    // See DoAcceptContract: refreshing here would close the pane the message
                    // is drawn in.
                    onDone?.Invoke(false, err);
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

        /// <summary>
        /// Report the other party of a contract to the moderators — the marketplace's
        /// report system pointed at a person rather than a craft.
        ///
        /// Deliberately does not refresh the list: a report changes nothing about the
        /// contract, and re-reading it afterwards would make the row flicker for no
        /// visible reason. It is also the one contract call whose refusals arrive as
        /// HTTP failures rather than as 200 + success:false — "you have already
        /// reported this", "that contract was issued by the bot" — so the server's own
        /// sentence is dug out of `detail` and shown, since a bare "failed" would send
        /// the player back to press the same button again.
        /// </summary>
        private System.Collections.IEnumerator DoReportContract(string contractId, string reason,
                                                                Action<bool, string> onDone = null)
        {
            var body = new Dictionary<string, object> { { "reason", reason ?? "" } };
            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/contracts/{contractId}/report", MiniJSON.Serialize(body),
                (ok, resp, status) =>
                {
                    var d = MiniJSON.DeserializeDict(resp);
                    bool success = ok && d != null && MiniJSON.GetBool(d, "success", false);
                    if (success)
                    {
                        string msg = MiniJSON.GetString(d, "message", "");
                        if (string.IsNullOrEmpty(msg))
                            msg = "Reported. A moderator will pick it up in Discord.";
                        SetStatus("🚩 " + msg);
                        onDone?.Invoke(true, msg);
                    }
                    else
                    {
                        string err = ReportRefusal(status, d);
                        SetStatus("(No) " + err);
                        onDone?.Invoke(false, err);
                    }
                });
        }

        /// <summary>
        /// Why a contract action did not land, in words the player can act on.
        ///
        /// The two shapes a refusal arrives in are both read here, because a caller
        /// cannot know in advance which one it will get. A *business rule* is a 200
        /// with `success:false` and the sentence in `message` ("Contract is not
        /// pending.", "Use Give Up instead; it costs the agreed 1,000 KCoin fine.");
        /// a wrong recipient or a missing contract is a 403/404 with the sentence in
        /// FastAPI's `detail`. Either way the server already wrote the explanation,
        /// and a client that replaces it with "failed" only sends the player back to
        /// press the same button.
        ///
        /// FastAPI puts a deliberate refusal in `detail` as a string; a validation
        /// failure puts a list of objects there instead, which would ToString() as a
        /// type name — so anything that is not a string is dropped and `fallback`
        /// carries the status number a maintainer needs instead.
        /// </summary>
        internal static string RefusalMessage(long status, Dictionary<string, object> body,
                                             string fallback)
        {
            if (status == 0)
                return "Couldn't reach the server; check you're online and try again.";

            string detail = null;
            object v;
            if (body != null && body.TryGetValue("detail", out v) && v is string)
                detail = (string)v;
            if (string.IsNullOrEmpty(detail) && body != null)
                detail = MiniJSON.GetString(body, "message", null);

            return string.IsNullOrEmpty(detail)
                ? fallback + " (HTTP " + status + ")."
                : detail;
        }

        private static string ReportRefusal(long status, Dictionary<string, object> body)
            => RefusalMessage(status, body, "The server refused the report");

        private System.Collections.IEnumerator DoDownloadCraft(string contractId, string ownerName = "", Action<bool, string> onDone = null, string ownerId = "")
        {
            SetStatus("[+] Fetching craft info...");
            yield return CraftDelivery.Deliver(contractId, ownerName, (ok, msg) =>
            {
                SetStatus((ok ? "(Ok) " : "(No) ") + msg);
                onDone?.Invoke(ok, msg);
            }, ownerId);
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

        /// <summary>
        /// The crew names an arriving payload is <i>attested</i> to be bringing home to
        /// this save, or null when nothing attests to anything.
        ///
        /// Two things can attest, and both are records the server kept rather than
        /// anything the payload says.
        ///
        /// A rescue this player <b>issued</b> attests with its <c>rescue_kerbals</c>,
        /// tagged by this very client when it handed the wreck over and held by the
        /// server ever since. A <b>friend quicksend</b> attests with the
        /// <c>homebound</c> list the server puts on the import entry: it recorded which
        /// of this save's own crew left for that friend on the outbound leg, and offers
        /// them back only on a return from that same friend (see
        /// <c>data/crew_ledger.py</c>). Neither is a list a counterparty can write —
        /// whoever is returning the craft can only bring back kerbals this save actually
        /// sent <i>them</i>.
        ///
        /// Everything else — a third party's multi-hop, a contractor's own submission
        /// coming back — has no such record and gets the plain refusal (see
        /// VesselTransfer.ApplyIncomingOwnershipTag).
        ///
        /// Until the quicksend half existed, an honest round trip cost a player their
        /// crew's identity for the life of the save: lend a crewed ship to a friend, get
        /// it back, and the kerbals returned double-tagged and <c>borrowed</c>, eligible
        /// for <see cref="VesselTransfer.PurgeBorrowedGhostCrew"/> to delete outright.
        ///
        /// <paramref name="ready"/> separates "nothing to attest" from "cannot tell yet".
        /// A rescue delivery that arrives before the contract list has ever been fetched
        /// is not un-attested, it is un-checked, and treating the two the same would
        /// re-tag the issuer's own kerbals under the rescuer — which is exactly the
        /// corruption the allow-list exists to prevent. The caller defers instead.
        /// </summary>
        private List<string> HomeboundCrewFor(
            Dictionary<string, object> entry, string source, string refId, out bool ready)
        {
            ready = true;

            // The quicksend half. The attestation rides on the entry itself, so unlike
            // the rescue case below there is nothing to fetch and nothing to defer on:
            // the server either vouched for these names or it did not. `Has` rather
            // than a count, because an absent list and an empty one must not read the
            // same — "nobody vouched" keeps the impersonation refusal, while an empty
            // attested list would be a promise with nothing behind it.
            if (source == "gift_vessel")
            {
                if (!MiniJSON.Has(entry, "homebound")) return null;
                var vouched = new List<string>();
                foreach (var k in MiniJSON.GetList(entry, "homebound"))
                    if (k != null && !string.IsNullOrEmpty(k.ToString())) vouched.Add(k.ToString());
                return vouched.Count > 0 ? vouched : null;
            }

            if (source != "rescue_delivery" || string.IsNullOrEmpty(refId)) return null;
            if (contracts == null) { ready = false; return null; }

            foreach (var o in contracts)
            {
                var c = o as Dictionary<string, object>;
                if (c == null || MiniJSON.GetString(c, "contract_id") != refId) continue;
                // Not ours to attest: on the contractor's side this contract's crew are
                // somebody else's kerbals, and nothing here may strip a tag off them.
                if (!MiniJSON.GetBool(c, "is_outgoing")) return null;
                var names = new List<string>();
                foreach (var k in MiniJSON.GetList(c, "rescue_kerbals"))
                    if (k != null && !string.IsNullOrEmpty(k.ToString())) names.Add(k.ToString());
                return names.Count > 0 ? names : null;
            }
            // The list is loaded and does not carry this contract (purged, or from a
            // guild this account no longer reads). Nothing is attested, which costs an
            // ownership tag rather than a kerbal — and, unlike the un-fetched case, it
            // resolves to the same answer next time, so deferring here would loop.
            return null;
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
            // The immutable account id of whoever owns the arriving crew. `owner_name` is
            // still what gets written into a roster tag ("A's Jeb" is for a human to
            // read), but only this decides whether those kerbals are OURS — a display
            // name is self-chosen and not unique, so deciding on it let anyone rename
            // themselves to a victim and have the victim's own kerbals adopted onto the
            // arriving vessel. Empty on entries queued before the server carried it; see
            // VesselTransfer.DecideComingHome for what happens then.
            string ownerId = MiniJSON.GetString(entry, "owner_id", "");
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
            // gift_vessel: a vessel a friend sent straight to your save.
            // submission_restore: the contractor's own submitted craft coming back
            // after they recovered it and a dispute made the contract active again.
            // Crew are tagged/stripped by owner on import — your own kerbals come back
            // to their original names; anyone else's keep their owner tag.
            if ((source == "rescue_delivery" || source == "gift_vessel" ||
                 source == "submission_restore") && !string.IsNullOrEmpty(vesselNodeUrl))
            {
                // The poll also runs in the editor now (for blueprint installs), but a
                // live vessel cannot spawn there — leave the entry queued for a scene
                // that can, and skip the download it would waste.
                if (!GiftInbox.CanDeliverHere(source))
                {
                    processingImports.Remove(importId);
                    yield break;
                }

                // gift_vessel doubles as the decline-return of our own quicksend, and
                // rescue_delivery as the restore of a cancelled rescue's wreck — both
                // are OUR vessel coming home, and the pid we reported at send time
                // decides what "delivering" means:
                //  • the original is still in this save (its removal is deferred while
                //    we fly it, or a quickload rolled it back, or the scenario holding
                //    the queue was lost) → the return is a no-op. Cancel the removal
                //    and keep the ship; spawning the snapshot next to it would
                //    duplicate hull and crew.
                //  • the hull is gone but its removal entry is still settling crew (a
                //    straggler on EVA) → defer to the next poll; spawning now would
                //    re-create the very names the entry is hunting.
                //  • fully gone → spawn from the snapshot like any other delivery.
                // A friend's normal gift carries THEIR save's pid, which can't match
                // anything here, so it falls through to the spawn as before. An
                // approved rescue's delivery (the rescuer's craft) and returns from
                // an old server carry no pid at all and fall through the same way.
                string returnPid = MiniJSON.GetString(entry, "vessel_pid", "");
                if ((source == "gift_vessel" || source == "rescue_delivery") &&
                    !string.IsNullOrEmpty(returnPid))
                {
                    if (VesselTransfer.VesselExists(returnPid))
                    {
                        GeneKermanMod.Instance.CancelQueuedRemoval(returnPid);
                        GeneKermanMod.Instance.ShowNotification("📪 Vessel Returned",
                            source == "rescue_delivery"
                                ? $"The rescue was cancelled and {craftName} hadn't left yet; it stays right where it is."
                                : $"{craftName} was declined and hadn't left yet; it stays right where it is.");
                        yield return GeneKermanMod.Instance.Api.Post(
                            $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
                        processingImports.Remove(importId);
                        yield break;
                    }
                    if (GeneKermanMod.Instance.HasQueuedRemoval(returnPid))
                    {
                        processingImports.Remove(importId);
                        yield break;
                    }
                }

                string myName = GeneKermanMod.Instance.LinkedUsername;
                string refId = MiniJSON.GetString(entry, "ref_id", "");

                // Which of these crew, if any, this save is *owed back*. The names on a
                // returning craft look exactly like a forgery — "{me}'s Jeb" written by
                // somebody else — so nothing in the payload can tell them apart, and only
                // a record the server kept can: the contract, for a rescue this player
                // issued, or the crew ledger, for a friend returning a ship this save
                // lent them. See VesselTransfer.ApplyIncomingOwnershipTag.
                bool attestable;
                var homebound = HomeboundCrewFor(entry, source, refId, out attestable);
                if (!attestable)
                {
                    // The contract list has not been fetched yet this session, so we
                    // cannot tell the honest return from the forgery. Guessing costs the
                    // issuer their own kerbals, and the payload is not going anywhere:
                    // ask for the list and leave the entry queued for the next poll.
                    RefreshContracts();
                    processingImports.Remove(importId);
                    yield break;
                }

                bool spawned = false;
                bool unpackRefused = false;
                string importRefusal = null;
                yield return GeneKermanMod.Instance.Api.DownloadFile(vesselNodeUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string node = CraftDelivery.DecompressToString(fileData, out unpackRefused);
                    if (unpackRefused) return;
                    // Fleet-aware: a rescue submission may now carry extra craft the
                    // rescuer sent alongside, packed as a GKFLEET container. Both this
                    // delivery (to the issuer) and the restore (back to the contractor)
                    // are that same stored node, so both have to spawn every vessel in
                    // it — and the restore has to know which one is the contract craft.
                    string vesselName;
                    var spawnedPids = VesselTransfer.ImportDeliveredFleet(
                        node, ownerName, myName, out vesselName, homebound, ownerId);
                    // A payload refused for its node count is the same permanent case
                    // the decompression cap refuses: the bytes are the same bytes every
                    // time, so leaving it queued is a re-download and a re-refusal on
                    // every launch rather than a retry.
                    importRefusal = VesselTransfer.LastImportRefusal;
                    if (!string.IsNullOrEmpty(vesselName))
                    {
                        spawned = true;
                        // A restored submission is still what this save owes the
                        // contract: re-key the contract→pid record to the spawned
                        // copies, or a later approval's removal targets the pids the
                        // player recovered and the restored hulls would survive the
                        // hand-over. Primary first — ImportDeliveredFleet guarantees
                        // that ordering, and the removal path relies on it to decide
                        // which entry carries the contract's crew list.
                        if (source == "submission_restore" && !string.IsNullOrEmpty(refId) &&
                            spawnedPids.Count > 0)
                            GeneKermanMod.Instance.RecordRescueSubmission(refId, spawnedPids);

                        // A gift_vessel whose owner is *us* is our own quicksend coming
                        // home after a decline, not a present from ourselves.
                        bool isReturn = source == "gift_vessel" &&
                                        VesselTransfer.DecideComingHome(
                                            ownerId, GeneKermanMod.Instance.LinkedAccountId,
                                            ownerName, myName, out _);
                        string title = isReturn ? "📪 Vessel Returned"
                                     : source == "gift_vessel" ? "🎁 Vessel Received"
                                     : source == "submission_restore" ? "Craft restored"
                                     : "🛟 Rescue Delivered";
                        string body = isReturn
                            ? $"{vesselName} was declined and is back in your save."
                            : source == "submission_restore"
                            ? $"{vesselName} is back where it was when you submitted. It still belongs to the contract."
                            : $"{vesselName}{(source == "gift_vessel" && !string.IsNullOrEmpty(ownerName) ? $" from {ownerName}" : "")} has arrived in your save.";
                        // A fleet arrives under the primary's name; say how many came
                        // with it, or the other hulls read as craft that appeared from
                        // nowhere in the tracking station.
                        if (spawnedPids.Count > 1)
                            body += $" {spawnedPids.Count - 1} more craft came with it.";
                        GeneKermanMod.Instance.ShowNotification(title, body);
                    }
                });
                if (!spawned && !unpackRefused && importRefusal == null)
                {
                    processingImports.Remove(importId);
                    yield break;
                }
                // Only a SIZE refusal acks, and the split is the whole point.
                //
                // Everything in this branch is a live vessel the other side has already
                // let go of: ToolActions.Quicksend deletes the sender's ship and crew on
                // the server's `ok`, a rescue delivery is the issuer's own wreck coming
                // home, and a submission_restore is a craft that left the contractor's
                // save when they submitted it. The server's stored snapshot is therefore
                // the ONLY remaining copy, and /done is what tells it to drop that copy.
                // Acking a refusal here destroyed the ship outright: refused, acked,
                // snapshot gone, and it exists in neither save.
                //
                // The unpack cap is different, and may still ack. It is derived from the
                // server's own upload ceiling, so nothing the server accepted can trip
                // it — a payload that does is bytes no client will ever unpack, and the
                // snapshot it names is not a ship anybody can be given back.
                //
                // A node-level refusal is a CLIENT policy constant (MaxFleetVessels, the
                // orbit guard) rather than a property of the bytes: a later build can
                // relax it and import the very same payload. So it is left queued — not
                // as a retry, since the entry stays in `processingImports` and is not
                // re-downloaded again this session, but so that the copy survives.
                //
                // `spawned` outranks both. A fleet whose extras were refused after the
                // primary went in has been consumed: leaving it queued would re-import
                // and duplicate what did spawn, which is the one outcome worse than
                // losing the extras.
                if (!spawned && !unpackRefused)
                {
                    GeneKermanMod.Instance.ShowNotification("(No) Delivery was refused",
                        $"{craftName} was not imported: {importRefusal}. Nothing was spawned, and " +
                        "it has been LEFT in your import queue so the sender's copy is not thrown " +
                        "away — nothing has been lost. Please report this with your KSP.log.");
                    yield break;   // deliberately no /done: the ack is what drops the snapshot
                }
                if (unpackRefused)
                    GeneKermanMod.Instance.ShowNotification("(No) Delivery could not be unpacked",
                        $"{craftName} could not be unpacked safely — it is past the size limit or " +
                        "damaged — and has been cleared from your import queue. Nothing was spawned. " +
                        "Please report this with your KSP.log; the sender can try again.");
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
                string refusal = null;
                yield return GeneKermanMod.Instance.Api.DownloadFile(craftUrl, (ok, fileData) =>
                {
                    if (!ok || fileData == null) return;
                    string path = CraftInstaller.Install(fileData, craftFilename, loadmeta, out refusal);
                    if (path != null)
                    {
                        installed = true;
                        GeneKermanMod.Instance.ShowNotification("🚀 Craft Imported",
                            $"{craftName} saved to your Ships folder.");
                    }
                });
                if (!installed && refusal == null)
                {
                    // Leave it queued for the next poll (e.g. a download hiccup).
                    processingImports.Remove(importId);
                    yield break;
                }
                // A payload this build refuses is refused identically every time, so
                // leaving it queued is a silent loop rather than a retry: ack it and say
                // what happened. Falls through to the ack below.
                if (refusal != null)
                    GeneKermanMod.Instance.ShowNotification("(No) Craft could not be installed",
                        $"{craftName} was dropped from your import queue: {refusal}. " +
                        "Please report this with your KSP.log.");
            }

            // Ack — remove from the player's queue so it isn't installed again.
            yield return GeneKermanMod.Instance.Api.Post(
                $"/api/v1/craft/imports/{importId}/done", "{}", (ok, resp, status) => { });
            processingImports.Remove(importId);
        }


    }
}
