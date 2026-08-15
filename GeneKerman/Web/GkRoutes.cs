/*
 * GkRoutes.cs – The /gk/* surface: everything the page needs that the bot API cannot
 * answer, because it is about *this running copy of KSP* rather than the account.
 *
 * Currently: the session handshake, a state snapshot, unread-count sync, and the two
 * actions the Profile screen needs. Vessel reads, craft installs and capture jobs
 * arrive with Phase 3b, when there is a UI that uses them.
 *
 * Everything here runs on a ThreadPool thread and must reach KSP through
 * MainThreadQueue. Touching HighLogic or FlightGlobals directly from a handler would
 * work most of the time and corrupt state the rest.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GeneKerman.Web
{
    internal sealed class GkRoutes
    {
        private readonly LocalServer server;
        private readonly MainThreadQueue queue;
        private readonly BridgeAuth auth;

        public GkRoutes(LocalServer server, MainThreadQueue queue, BridgeAuth auth)
        {
            this.server = server;
            this.queue = queue;
            this.auth = auth;
        }

        /// <summary>
        /// Which external sites /gk/actions/open-url may launch. An allow-list because
        /// the page can ask for any string, and Application.OpenURL will happily hand a
        /// file:// or a malicious link to the OS.
        /// </summary>
        private static readonly HashSet<string> OpenUrlAllowList = new HashSet<string>(StringComparer.Ordinal)
        {
            ApiClient.PrivacyPolicyUrl,
            ApiClient.TermsOfServiceUrl,
        };

        public void Dispatch(HttpListenerContext ctx, string path)
        {
            var req = ctx.Request;

            // The handshake is the one route that runs before a session exists — it is
            // what creates one — so it authenticates with the launch nonce instead.
            if (path == "/gk/session")
            {
                if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                HandleSession(ctx);
                return;
            }

            // SSE cannot carry a custom header (EventSource has no API for it), so it
            // authenticates on the SameSite=Strict cookie alone. See BridgeAuth.
            if (path == "/gk/events")
            {
                if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                if (!auth.IsAuthorizedCookieOnly(req)) { Unauthorized(ctx); return; }
                server.Events.Accept(ctx);
                return;
            }

            if (!auth.IsAuthorized(req)) { Unauthorized(ctx); return; }

            switch (path)
            {
                case "/gk/state":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(State));
                    return;

                // Read is a plain KSP read; write moves the mod between servers, so it
                // runs on the main thread like everything else that touches live state.
                case "/gk/settings":
                    if (req.HttpMethod == "GET") { Respond(ctx, queue.RunSync(GetSettings)); return; }
                    if (req.HttpMethod == "POST") { HandleSettingsUpdate(ctx); return; }
                    MethodNotAllowed(ctx);
                    return;

                case "/gk/actions/unlink":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(Unlink));
                    return;

                case "/gk/actions/open-url":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleOpenUrl(ctx);
                    return;

                case "/gk/notifications/unread":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleUnreadSync(ctx);
                    return;

                case "/gk/actions/install-craft":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleInstallCraft(ctx);
                    return;

                case "/gk/craft/current":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(CurrentCraft));
                    return;

                case "/gk/contract/context":
                    if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(ContractContext));
                    return;

                case "/gk/actions/create-contract":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleCreateContract(ctx);
                    return;

                case "/gk/actions/open-submit":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleOpenSubmit(ctx);
                    return;

                case "/gk/actions/import-flag":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleImportFlag(ctx);
                    return;

                case "/gk/actions/export-craft":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    Respond(ctx, queue.RunSync(ExportCraft));
                    return;

                case "/gk/actions/quicksend":
                    if (req.HttpMethod != "POST") { MethodNotAllowed(ctx); return; }
                    HandleQuicksend(ctx);
                    return;

                // Marshalled like everything else, not because favorites touch the scene
                // but because ConfigNode is KSP's and the static set would otherwise be
                // mutated from several request threads at once.
                case "/gk/favorites":
                    if (req.HttpMethod == "GET") { Respond(ctx, queue.RunSync(ListFavorites)); return; }
                    if (req.HttpMethod == "POST") { HandleFavoriteSet(ctx); return; }
                    MethodNotAllowed(ctx);
                    return;

                default:
                    if (path.StartsWith("/gk/jobs/", StringComparison.Ordinal))
                    {
                        if (req.HttpMethod != "GET") { MethodNotAllowed(ctx); return; }
                        HandleJobStatus(ctx, path.Substring("/gk/jobs/".Length));
                        return;
                    }
                    LocalServer.Respond(ctx, 404, "application/json", "{\"error\":\"not_found\"}");
                    return;
            }
        }

        // ── Handshake ───────────────────────────────────────────────────────

        private void HandleSession(HttpListenerContext ctx)
        {
            string nonce = ctx.Request.QueryString["k"];
            if (string.IsNullOrEmpty(nonce))
            {
                // Also accept it in the body so the page can drop the nonce from the
                // address bar before making the call.
                try
                {
                    using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    {
                        var dict = MiniJSON.DeserializeDict(reader.ReadToEnd());
                        if (dict != null) nonce = MiniJSON.GetString(dict, "k");
                    }
                }
                catch (Exception) { /* malformed body is just a failed handshake */ }
            }

            string setCookie = auth.RedeemNonce(nonce, out string csrf);
            if (setCookie == null)
            {
                // Deliberately vague: wrong, expired and already-used are one answer.
                LocalServer.Respond(ctx, 401, "application/json", "{\"error\":\"bad_nonce\"}");
                return;
            }

            ctx.Response.Headers["Set-Cookie"] = setCookie;
            LocalServer.Respond(ctx, 200, "application/json",
                "{\"csrf\":" + JobResult.Quote(csrf) + "}");
        }

        // ── Main-thread handlers ────────────────────────────────────────────

        private JobResult State()
        {
            var mod = GeneKermanMod.Instance;
            var sb = new StringBuilder();
            sb.Append("{\"version\":").Append(JobResult.Quote(ModVersion.Current))
              .Append(",\"scene\":").Append(JobResult.Quote(SceneName()))
              .Append(",\"linked\":").Append(Json(mod?.Api?.IsLinked == true))
              .Append(",\"username\":").Append(JobResult.Quote(mod?.LinkedUsername ?? ""))
              .Append(",\"serverUrl\":").Append(JobResult.Quote(mod?.Api?.ServerUrl ?? ""))
              .Append(",\"consent\":").Append(Json(Consent.Accepted))
              .Append(",\"dataGathering\":").Append(Json(mod?.Api?.DataGatheringEnabled == true))
              .Append(",\"updateRequired\":").Append(Json(mod?.UpdateRequired == true))
              .Append(",\"unread\":").Append(mod?.UnreadNotifications ?? 0)
              .Append('}');
            return JobResult.Json(sb.ToString());
        }

        // ── Settings ────────────────────────────────────────────────────────

        private JobResult GetSettings() => SettingsJson(false);

        /// <summary>
        /// Applies whatever subset of settings the page sent. Absent keys are left alone
        /// rather than defaulted, so a screen that only knows about some of these cannot
        /// silently reset the rest.
        /// </summary>
        private void HandleSettingsUpdate(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null)
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"bad_body\"}");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                var api = mod?.Api;
                if (api == null) return JobResult.Error(503, "Mod not ready.");

                bool serverChanged = false;
                if (body.ContainsKey("official") || body.ContainsKey("customUrl"))
                {
                    if (MiniJSON.GetBool(body, "official", api.UseOfficialServer))
                    {
                        serverChanged = api.SetOfficialServer();
                    }
                    else
                    {
                        // Validate here rather than letting SetCustomServer fail quietly,
                        // so the page can show the player what is wrong with what they
                        // typed instead of the field snapping back with no explanation.
                        string raw = MiniJSON.GetString(body, "customUrl", api.CustomServerUrl);
                        string url = ApiClient.NormalizeServerUrl(raw, out string error);
                        if (url == null) return JobResult.Error(400, error);
                        serverChanged = api.SetCustomServer(url);
                    }
                }

                if (body.ContainsKey("notifications"))
                    api.SetNotificationsEnabled(MiniJSON.GetBool(body, "notifications", true));

                if (body.ContainsKey("checkpointPhotos"))
                    api.SetCheckpointPhotosEnabled(MiniJSON.GetBool(body, "checkpointPhotos", true));

                // Accepted as an opt-*out* only. Turning data sharing back on is a consent
                // action (KSP add-on rule 8.2) and belongs in the game, beside the panel
                // that says what is shared — not in a request a page can make on its own.
                // Switching it off must always work from anywhere, which is why the
                // asymmetry is deliberate rather than an oversight.
                if (body.ContainsKey("dataGathering") && !MiniJSON.GetBool(body, "dataGathering", true))
                    mod.SetDataGatheringEnabled(false);

                if (serverChanged) mod.OnServerChanged();

                return SettingsJson(serverChanged);
            }));
        }

        private static JobResult SettingsJson(bool serverChanged)
        {
            var mod = GeneKermanMod.Instance;
            var api = mod?.Api;
            if (api == null) return JobResult.Error(503, "Mod not ready.");

            var sb = new StringBuilder();
            sb.Append("{\"official\":").Append(Json(api.UseOfficialServer))
              .Append(",\"officialUrl\":").Append(JobResult.Quote(ApiClient.OfficialServerUrl))
              .Append(",\"customUrl\":").Append(JobResult.Quote(api.CustomServerUrl))
              .Append(",\"serverUrl\":").Append(JobResult.Quote(api.ServerUrl))
              .Append(",\"linked\":").Append(Json(api.IsLinked))
              .Append(",\"username\":").Append(JobResult.Quote(mod?.LinkedUsername ?? ""))
              .Append(",\"notifications\":").Append(Json(api.NotificationsEnabled))
              .Append(",\"checkpointPhotos\":").Append(Json(api.CheckpointPhotosEnabled))
              .Append(",\"dataGathering\":").Append(Json(api.DataGatheringEnabled))
              .Append(",\"updateRequired\":").Append(Json(mod?.UpdateRequired == true))
              .Append(",\"modVersion\":").Append(JobResult.Quote(ModVersion.Current))
              .Append(",\"serverChanged\":").Append(Json(serverChanged))
              .Append('}');
            return JobResult.Json(sb.ToString());
        }

        private JobResult Unlink()
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) return JobResult.Error(503, "Mod not ready.");

            mod.Api.ClearToken();
            // Hand the player back to the in-game link flow: linking needs a code typed
            // in KSP and a Discord approval, neither of which belongs in a browser tab.
            mod.ShowLinkWindow = true;
            return JobResult.Json("{\"ok\":true}");
        }

        /// <summary>
        /// Pushes the server's authoritative unread count back into the mod.
        ///
        /// Marking a notification read from the browser goes straight through the proxy
        /// to the API — the mod's own UnreadNotifications counter, which drives both the
        /// in-game toolbar badge and /gk/state, never sees it and goes stale. Rather
        /// than have the proxy sniff response bodies for specific routes, the page tells
        /// us the count it just read. One source of truth (the server), reported
        /// explicitly, and it keeps the classic UI correct if the player switches back.
        /// </summary>
        private void HandleUnreadSync(HttpListenerContext ctx)
        {
            int count = -1;
            try
            {
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    var dict = MiniJSON.DeserializeDict(reader.ReadToEnd());
                    if (dict != null) count = MiniJSON.GetInt(dict, "count");
                }
            }
            catch (Exception) { }

            if (count < 0)
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"bad_count\"}");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                var mod = GeneKermanMod.Instance;
                if (mod != null) mod.UnreadNotifications = count;
                return JobResult.Json("{\"ok\":true}");
            }));
        }

        // ── Craft install (async job) ───────────────────────────────────────

        /// <summary>
        /// Ids we will interpolate into an upstream URL. CraftDelivery builds
        /// "/api/v1/craft/download/{id}" and calls ApiClient.Get directly — it does NOT
        /// pass through ApiProxy's allow-list — so an id containing "../" would redirect
        /// that authenticated request to an endpoint of the page's choosing. Same charset
        /// the proxy's own contract patterns use.
        /// </summary>
        private static readonly Regex SafeId = new Regex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        private void HandleInstallCraft(HttpListenerContext ctx)
        {
            string contractId = null, ownerName = "";
            try
            {
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    var dict = MiniJSON.DeserializeDict(reader.ReadToEnd());
                    if (dict != null)
                    {
                        contractId = MiniJSON.GetString(dict, "contract_id");
                        ownerName = MiniJSON.GetString(dict, "owner_name");
                    }
                }
            }
            catch (Exception) { }

            if (string.IsNullOrEmpty(contractId) || !SafeId.IsMatch(contractId))
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"bad_contract_id\"}");
                return;
            }

            string jobId = server.Jobs.Begin();

            // Fire and forget on the main thread: this makes two network round trips and
            // spawns vessels, so holding the request open would hit the 30s timeout and
            // leave the tab looking hung. The page follows the job instead.
            queue.RunSync(() =>
            {
                GeneKermanMod.Instance.RunCoroutine(
                    CraftDelivery.Deliver(contractId, ownerName, (ok, msg) =>
                    {
                        server.Jobs.Complete(jobId, ok, msg);
                        // Push the outcome so the page does not have to poll.
                        server.Broadcast("job", server.Jobs.Get(jobId).ToJson());
                    }));
                return JobResult.Json("{}");
            });

            LocalServer.Respond(ctx, 202, "application/json",
                "{\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        /// <summary>Polling fallback for when the SSE stream dropped mid-job.</summary>
        private void HandleJobStatus(HttpListenerContext ctx, string id)
        {
            var rec = server.Jobs.Get(id);
            if (rec == null)
            {
                LocalServer.Respond(ctx, 404, "application/json", "{\"error\":\"unknown_job\"}");
                return;
            }
            LocalServer.Respond(ctx, 200, "application/json", rec.ToJson());
        }

        // ── Tools ───────────────────────────────────────────────────────────

        /// <summary>
        /// What the game currently has available to send or export. The browser cannot
        /// see craft files, so it asks the game what it is holding and renders the
        /// options from that.
        /// </summary>
        /// <summary>
        /// The craft situation, serialized. The reading of it lives in ToolActions,
        /// because the sidebar's Tools panel needs exactly the same answers and a
        /// second copy of "is this craft saved" would drift.
        /// </summary>
        private JobResult CurrentCraft()
        {
            var state = ToolActions.ReadCraftState();

            var sb = new StringBuilder();
            sb.Append("{\"scene\":").Append(JobResult.Quote(SceneName()))
              .Append(",\"editorCraft\":").Append(JobResult.Quote(state.EditorCraft))
              .Append(",\"editorType\":").Append(JobResult.Quote(state.EditorType))
              .Append(",\"editorParts\":").Append(state.EditorParts)
              // Unsaved crafts cannot be sent or exported — there is no file to read.
              // The path itself never leaves the mod.
              .Append(",\"editorSaved\":").Append(Json(state.EditorSaved))
              .Append(",\"activeVessel\":").Append(JobResult.Quote(state.ActiveVessel))
              .Append('}');

            return JobResult.Json(sb.ToString());
        }

        private JobResult ExportCraft()
        {
            bool ok = ToolActions.ExportCurrentCraft(out string message);
            return ok
                ? JobResult.Json("{\"ok\":true,\"path\":" + JobResult.Quote(message) + "}")
                : JobResult.Error(409, message);
        }

        private void HandleImportFlag(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            string url = MiniJSON.GetString(body, "url");
            string name = MiniJSON.GetString(body, "name");

            if (string.IsNullOrEmpty(url))
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"missing_url\"}");
                return;
            }

            StartJob(ctx, done => ToolActions.ImportFlag(url, name, done));
        }

        private void HandleQuicksend(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            string recipientId = MiniJSON.GetString(body, "recipient_id");
            string recipientName = MiniJSON.GetString(body, "recipient_name");
            string kind = MiniJSON.GetString(body, "kind");

            if (string.IsNullOrEmpty(recipientId) || (kind != "craft" && kind != "vessel"))
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"bad_request\"}");
                return;
            }

            // The craft path is resolved on the main thread inside the job — the browser
            // must never supply a filesystem path.
            StartJob(ctx, done => ToolActions.QuicksendCurrent(recipientId, recipientName, kind, done));
        }

        // ── Favorites ───────────────────────────────────────────────────────

        private static JobResult ListFavorites()
        {
            var sb = new StringBuilder("{\"favorites\":[");
            bool first = true;
            foreach (string id in Favorites.Ids)
            {
                if (!first) sb.Append(',');
                sb.Append(JobResult.Quote(id));
                first = false;
            }
            return JobResult.Json(sb.Append("]}").ToString());
        }

        private void HandleFavoriteSet(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            string userId = MiniJSON.GetString(body, "user_id");
            bool favorite = MiniJSON.GetBool(body, "favorite", false);

            if (string.IsNullOrEmpty(userId))
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"missing_user_id\"}");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                bool now = Favorites.Set(userId, favorite);
                return JobResult.Json("{\"favorite\":" + Json(now) + "}");
            }));
        }

        // ── Contract creation ───────────────────────────────────────────────

        /// <summary>
        /// What the create-contract form needs from the running game and cannot get from
        /// the API: which celestial bodies exist in this save, whether a part filter is
        /// readable right now, and — in flight — the vessel a rescue would hand over.
        /// </summary>
        private JobResult ContractContext()
        {
            var ctx = ContractCreation.ScanRescueContext();

            var sb = new StringBuilder();
            sb.Append("{\"scene\":").Append(JobResult.Quote(SceneName()))
              .Append(",\"janitorsCloset\":").Append(Json(ContractCreation.IsJanitorsClosetAvailable()))
              .Append(",\"editorFilterReadable\":").Append(Json(ContractCreation.IsEditorFilterReadable()))
              .Append(",\"minMarginOrbitKm\":").Append(Num(ContractCreation.MinMarginOrbitKm))
              .Append(",\"minMarginSurfaceDeg\":").Append(Num(ContractCreation.MinMarginSurfaceDeg))
              .Append(",\"bodies\":[");

            for (int i = 0; i < ctx.Bodies.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(JobResult.Quote(ctx.Bodies[i].Name))
                  .Append(",\"modded\":").Append(Json(ctx.Bodies[i].Modded)).Append('}');
            }

            sb.Append("],\"rescue\":{\"available\":").Append(Json(ctx.Available))
              .Append(",\"vessel\":").Append(JobResult.Quote(ctx.VesselName))
              .Append(",\"body\":").Append(JobResult.Quote(ctx.Body))
              .Append(",\"apKm\":").Append(Num(ctx.ApKm))
              .Append(",\"peKm\":").Append(Num(ctx.PeKm))
              .Append(",\"lat\":").Append(Num(ctx.Lat))
              .Append(",\"lon\":").Append(Num(ctx.Lon))
              .Append(",\"crew\":[");

            for (int i = 0; i < ctx.Crew.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JobResult.Quote(ctx.Crew[i]));
            }

            return JobResult.Json(sb.Append("]}}").ToString());
        }

        /// <summary>
        /// Issues a contract, auction or rescue.
        ///
        /// A job rather than a synchronous reply: creating one is a network round trip,
        /// and a rescue additionally gzips and uploads the whole vessel, which runs well
        /// past the bridge's 30 s request budget on a large ship.
        ///
        /// Note what the page cannot say here. It names a part-restriction *mode*, not a
        /// mod list, and it does not name the rescue vessel at all — both are read from
        /// the running game inside ContractCreation, on the main thread, at send time.
        /// </summary>
        private void HandleCreateContract(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            if (body == null)
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"bad_body\"}");
                return;
            }

            var req = new ContractCreation.Request
            {
                Kind = MiniJSON.GetString(body, "kind", "contract"),
                ContractorId = MiniJSON.GetString(body, "contractor_id", ""),
                ContractorName = MiniJSON.GetString(body, "contractor_name", ""),
                Mission = MiniJSON.GetString(body, "mission", ""),
                Payment = MiniJSON.GetInt(body, "payment", 0),
                Fine = MiniJSON.GetInt(body, "fine", 0),
                DueDate = MiniJSON.GetString(body, "due_date", ""),
                ContractType = MiniJSON.GetString(body, "contract_type", "craft_build"),
                ModlistMode = MiniJSON.GetString(body, "modlist_mode", ContractCreation.ModlistNone),
                DurationHours = MiniJSON.GetInt(body, "duration_hours", 24),
            };

            var rescue = MiniJSON.GetDict(body, "rescue");
            if (rescue != null)
            {
                req.RescueMode = MiniJSON.GetString(rescue, "mode", "orbit");
                req.RescueRecovery = MiniJSON.GetString(rescue, "recovery", ContractCreation.RecoveryCrew);
                req.MinDvMs = MiniJSON.GetDouble(rescue, "min_dv", 0);
                req.RescueBody = MiniJSON.GetString(rescue, "body", "");
                req.ApKm = MiniJSON.GetDouble(rescue, "ap_km", 0);
                req.PeKm = MiniJSON.GetDouble(rescue, "pe_km", 0);
                req.MarginAltKm = MiniJSON.GetDouble(rescue, "margin_alt_km", ContractCreation.MinMarginOrbitKm);
                req.Lat = MiniJSON.GetDouble(rescue, "lat", 0);
                req.Lon = MiniJSON.GetDouble(rescue, "lon", 0);
                req.MarginPosDeg = MiniJSON.GetDouble(rescue, "margin_pos_deg", ContractCreation.MinMarginSurfaceDeg);
            }

            StartJob(ctx, onDone => ContractCreation.Create(req, onDone));
        }

        // ── Submission hand-off ─────────────────────────────────────────────

        /// <summary>
        /// Raises the in-game submit window for a contract.
        ///
        /// Submission stays IMGUI permanently: it waits for physics to settle, measures
        /// live distance between vessels, and captures a screenshot with the HUD hidden —
        /// all of which need *the game* focused, not a browser tab on another monitor.
        /// So the page's Submit button does not submit; it puts the real window in front
        /// of the player and tells them to switch to KSP.
        ///
        /// The page sends only a contract id. Everything the window enforces — required
        /// situation, body, mod list, part/mass/ΔV constraints, the rescue target — is
        /// re-read from the server here, because those are the terms of the contract and
        /// a page that could supply them could quietly relax its own requirements.
        /// </summary>
        private void HandleOpenSubmit(HttpListenerContext ctx)
        {
            var body = ReadJson(ctx);
            string contractId = body == null ? null : MiniJSON.GetString(body, "contract_id", "");

            // Same shape check as install-craft: this id is interpolated into an upstream
            // path by callers downstream, and a lookup key has no business containing
            // path syntax.
            if (string.IsNullOrEmpty(contractId) || !SafeId.IsMatch(contractId))
            {
                LocalServer.Respond(ctx, 400, "application/json", "{\"error\":\"bad_contract_id\"}");
                return;
            }

            StartJob(ctx, onDone => OpenSubmitRoutine(contractId, onDone));
        }

        /// <summary>
        /// Re-reads the contract from the API and raises the IMGUI submit window for it.
        /// Internal because the website's `open_submit` command routes here too: the
        /// terms (mission type, situation, body, mod list, constraints, rescue target)
        /// are fetched here rather than supplied by the caller, so neither front end can
        /// relax a contract's own requirements. Two callers, one place that decides.
        /// </summary>
        internal static IEnumerator OpenSubmitRoutine(string contractId, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            Dictionary<string, object> found = null;
            yield return mod.Api.GetActiveContracts((ok, data, error) =>
            {
                if (!ok || data == null) return;
                var list = MiniJSON.GetList(data, "contracts");
                if (list == null) return;
                foreach (var item in list)
                {
                    var c = item as Dictionary<string, object>;
                    if (c != null && MiniJSON.GetString(c, "contract_id", "") == contractId)
                    {
                        found = c;
                        return;
                    }
                }
            });

            if (found == null)
            {
                onDone(false, "That contract is no longer available to submit.");
                yield break;
            }

            string status = MiniJSON.GetString(found, "status", "");
            if (status != "active")
            {
                onDone(false, $"This contract is {status}, so there is nothing to submit.");
                yield break;
            }

            string missionType = MiniJSON.GetString(found, "mission_type", "active_vessel");
            RescueTargetSpec rescueSpec = null;
            List<string> rescueKerbals = null;
            if (missionType == "rescue")
            {
                rescueSpec = RescueTargetSpec.FromDict(MiniJSON.GetDict(found, "rescue_target"));
                rescueKerbals = new List<string>();
                var raw = MiniJSON.GetList(found, "rescue_kerbals");
                if (raw != null)
                    foreach (var o in raw)
                        if (o != null) rescueKerbals.Add(o.ToString());
            }

            mod.OpenSubmitWindow(
                contractId,
                MiniJSON.GetString(found, "mission", ""),
                missionType,
                MiniJSON.GetString(found, "required_situation", ""),
                MiniJSON.GetString(found, "required_body", ""),
                MiniJSON.GetString(found, "modlist", ""),
                rescueSpec,
                rescueKerbals,
                ContractConstraints.Parse(MiniJSON.GetDict(found, "constraints")));

            onDone(true, "The submit window is open in KSP — switch to the game to finish.");
        }

        /// <summary>Starts a tracked job and answers 202 with its id.</summary>
        private void StartJob(HttpListenerContext ctx, Func<Action<bool, string>, IEnumerator> makeRoutine)
        {
            string jobId = server.Jobs.Begin();

            queue.RunSync(() =>
            {
                GeneKermanMod.Instance.RunCoroutine(makeRoutine((ok, msg) =>
                {
                    server.Jobs.Complete(jobId, ok, msg);
                    server.Broadcast("job", server.Jobs.Get(jobId).ToJson());
                }));
                return JobResult.Json("{}");
            });

            LocalServer.Respond(ctx, 202, "application/json",
                "{\"job_id\":" + JobResult.Quote(jobId) + "}");
        }

        private static Dictionary<string, object> ReadJson(HttpListenerContext ctx)
        {
            try
            {
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    return MiniJSON.DeserializeDict(reader.ReadToEnd());
            }
            catch (Exception) { return null; }
        }

        private void HandleOpenUrl(HttpListenerContext ctx)
        {
            string url = null;
            try
            {
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    var dict = MiniJSON.DeserializeDict(reader.ReadToEnd());
                    if (dict != null) url = MiniJSON.GetString(dict, "url");
                }
            }
            catch (Exception) { }

            string target = url;
            bool allowed = !string.IsNullOrEmpty(target) &&
                           (OpenUrlAllowList.Contains(target) ||
                            target == (GeneKermanMod.Instance?.Api?.MarketplaceUrl ?? "\0"));

            if (!allowed)
            {
                Debug.LogWarning("[GeneKerman] Bridge refused open-url for a non-allow-listed target.");
                LocalServer.Respond(ctx, 403, "application/json", "{\"error\":\"url_not_allowed\"}");
                return;
            }

            Respond(ctx, queue.RunSync(() =>
            {
                Application.OpenURL(target);
                return JobResult.Json("{\"ok\":true}");
            }));
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static string Json(bool b) => b ? "true" : "false";

        /// <summary>A double as valid JSON. Invariant, because a comma decimal separator
        /// would silently split the value into two array elements; and NaN/Infinity —
        /// which an orbit read can genuinely produce on an escape trajectory — become 0,
        /// because neither is representable in JSON and both break the whole parse.</summary>
        private static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SceneName()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception) { return "unknown"; }
        }

        private static void Respond(HttpListenerContext ctx, JobResult r) =>
            LocalServer.Respond(ctx, r.Status, r.ContentType, r.Body);

        private static void Unauthorized(HttpListenerContext ctx) =>
            LocalServer.Respond(ctx, 401, "application/json", "{\"error\":\"unauthorized\"}");

        private static void MethodNotAllowed(HttpListenerContext ctx) =>
            LocalServer.Respond(ctx, 405, "application/json", "{\"error\":\"method_not_allowed\"}");
    }
}
