/*
 * ApiClient.cs – HTTP client for KSP mod ↔ server communication.
 *
 * Uses Unity's UnityWebRequest (the only HTTP client available in KSP's runtime).
 * All requests are coroutine-based. Handles:
 *   - JSON GET/POST with auth tokens
 *   - Multipart file uploads (craft + screenshots)
 *   - Session token persistence in PluginData
 *   - Gzip compression for craft files
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace GeneKerman
{
    public class ApiClient
    {
        /// <summary>Address of the official BM server, used when "Official Server" is selected.</summary>
        public const string OfficialServerUrl = "https://mainserver.boundlessmissions.com";

        private string serverUrl;
        private string customServerUrl = "http://localhost:5022"; // last custom URL, preserved when official is active
        private bool useOfficialServer;
        private bool notificationsEnabled = true;
        private bool checkpointPhotosEnabled = true;
        private string sessionToken;
        private readonly string tokenPath;

        // Callbacks for async operations
        public delegate void ApiCallback(bool success, string responseBody, long statusCode);
        public delegate void ApiCallback<T>(bool success, T data, string error);

        public bool IsLinked => !string.IsNullOrEmpty(sessionToken);
        public string ServerUrl => serverUrl;
        public string Token => sessionToken;

        /// <summary>True when connecting to the official server; false for a custom IP.</summary>
        public bool UseOfficialServer => useOfficialServer;
        /// <summary>The last custom server URL entered by the user.</summary>
        public string CustomServerUrl => customServerUrl;
        /// <summary>Whether live notifications (toast popups + socket/poll) are enabled.</summary>
        public bool NotificationsEnabled => notificationsEnabled;
        /// <summary>Whether milestone hero-shot prompts (rendezvous/flyby/asteroid) are enabled.</summary>
        public bool CheckpointPhotosEnabled => checkpointPhotosEnabled;

        /// <summary>
        /// WebSocket URL for the live notification stream, with the session token
        /// in the query string (UnityWebRequest cannot set headers on a WS handshake).
        /// Derives ws/wss from the configured http/https server URL. Empty if unlinked.
        /// </summary>
        public string NotificationsWebSocketUrl
        {
            get
            {
                if (string.IsNullOrEmpty(sessionToken)) return "";
                string wsBase = serverUrl;
                if (wsBase.StartsWith("https://")) wsBase = "wss://" + wsBase.Substring(8);
                else if (wsBase.StartsWith("http://")) wsBase = "ws://" + wsBase.Substring(7);
                return wsBase + "/ws/v1/notifications?token=" + Uri.EscapeDataString(sessionToken);
            }
        }

        public ApiClient()
        {
            tokenPath = Path.Combine(GeneKermanMod.PluginDataPath, "session.token");
            LoadSettings();
            LoadToken();
        }

        // ── Settings ────────────────────────────────────────────────────────

        private void LoadSettings()
        {
            string settingsPath = Path.Combine(GeneKermanMod.PluginDataPath, "settings.cfg");
            if (File.Exists(settingsPath))
            {
                var node = ConfigNode.Load(settingsPath);
                if (node != null)
                {
                    var gk = node.GetNode("GeneKerman");
                    if (gk != null)
                    {
                        bool.TryParse(gk.GetValue("useOfficialServer") ?? "false", out useOfficialServer);
                        bool.TryParse(gk.GetValue("enableNotifications") ?? "true", out notificationsEnabled);
                        bool.TryParse(gk.GetValue("enableCheckpointPhotos") ?? "true", out checkpointPhotosEnabled);

                        // Store host and port separately because ConfigNode
                        // treats // as a comment delimiter, mangling URLs.
                        string host = gk.GetValue("serverHost") ?? "localhost";
                        string port = gk.GetValue("serverPort") ?? "5022";
                        string protocol = gk.GetValue("serverProtocol") ?? "http";
                        customServerUrl = $"{protocol}://{host}:{port}";

                        // When the official server is selected, ignore the stored custom
                        // host/port (kept so toggling back to custom restores it).
                        serverUrl = useOfficialServer ? OfficialServerUrl : customServerUrl;
                        Debug.Log($"[GeneKerman] Settings loaded — server: {serverUrl} (official={useOfficialServer}, notifications={notificationsEnabled})");
                        return;
                    }
                }
            }
            // First setup (no settings.cfg yet): default to the official server.
            // The choice is persisted so a later switch to a custom server sticks.
            customServerUrl = "http://localhost:5022";
            useOfficialServer = true;
            notificationsEnabled = true;
            serverUrl = OfficialServerUrl;
            SaveSettings();
            Debug.Log($"[GeneKerman] First setup — defaulting to official server: {serverUrl}");
        }

        /// <summary>Switch to the official server and persist the choice.</summary>
        public void SetOfficialServer()
        {
            useOfficialServer = true;
            serverUrl = OfficialServerUrl;
            SaveSettings();
        }

        /// <summary>Switch to a custom server URL and persist it.</summary>
        public void SetCustomServer(string url)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "http://" + url;
            }
            useOfficialServer = false;
            customServerUrl = url;
            serverUrl = url;
            SaveSettings();
        }

        // Backwards-compatible alias: treat a manual URL set as a custom server.
        public void SetServerUrl(string url) => SetCustomServer(url);

        /// <summary>Enable or disable live notifications and persist the choice.</summary>
        public void SetNotificationsEnabled(bool enabled)
        {
            notificationsEnabled = enabled;
            SaveSettings();
        }

        /// <summary>Enable or disable milestone hero-shot prompts and persist the choice.</summary>
        public void SetCheckpointPhotosEnabled(bool enabled)
        {
            checkpointPhotosEnabled = enabled;
            SaveSettings();
        }

        private void SaveSettings()
        {
            // Persist the custom URL components (ConfigNode-safe), even when the
            // official server is currently active, so the custom value is preserved.
            string protocol = "http";
            string host = "localhost";
            string port = "5022";

            try
            {
                var uri = new Uri(customServerUrl);
                protocol = uri.Scheme;
                host = uri.Host;
                port = uri.Port.ToString();
            }
            catch
            {
                Debug.LogWarning("[GeneKerman] Failed to parse custom server URL, using defaults.");
            }

            string settingsPath = Path.Combine(GeneKermanMod.PluginDataPath, "settings.cfg");
            var node = new ConfigNode();
            var gk = node.AddNode("GeneKerman");
            gk.AddValue("useOfficialServer", useOfficialServer);
            gk.AddValue("enableNotifications", notificationsEnabled);
            gk.AddValue("enableCheckpointPhotos", checkpointPhotosEnabled);
            gk.AddValue("serverProtocol", protocol);
            gk.AddValue("serverHost", host);
            gk.AddValue("serverPort", port);
            node.Save(settingsPath);
            Debug.Log($"[GeneKerman] Settings saved — server: {serverUrl} (official={useOfficialServer}, notifications={notificationsEnabled})");
        }

        // ── Token Management ────────────────────────────────────────────────

        private void LoadToken()
        {
            if (File.Exists(tokenPath))
            {
                sessionToken = File.ReadAllText(tokenPath).Trim();
                Debug.Log("[GeneKerman] Session token loaded.");
            }
        }

        public void SetToken(string token)
        {
            sessionToken = token;
            Directory.CreateDirectory(Path.GetDirectoryName(tokenPath));
            File.WriteAllText(tokenPath, token);
            Debug.Log("[GeneKerman] Session token saved.");
        }

        public void ClearToken()
        {
            sessionToken = null;
            if (File.Exists(tokenPath))
                File.Delete(tokenPath);
            Debug.Log("[GeneKerman] Session token cleared.");
        }

        // ── Core HTTP Methods ───────────────────────────────────────────────

        /// Attach the device id (always — so the link request can bind it) and the
        /// bearer token (when linked) to a request.
        private void ApplyHeaders(UnityWebRequest req)
        {
            req.SetRequestHeader("X-Device-Id", DeviceId.Current);
            // Sent on every request so the server can hard-block outdated/modified
            // DLLs (server-enforced version gate), not just the explicit version check.
            req.SetRequestHeader("X-Mod-Hash", ModVersion.Sha256);
            if (IsLinked)
                req.SetRequestHeader("Authorization", "Bearer " + sessionToken);
        }

        /// Post-response gate hook. Handles both the server-enforced version block
        /// (426 update_required) and the device-binding block (403 device_unverified).
        /// Returns true if the response was one of those gates.
        private bool HandleDeviceGate(long status, string body)
        {
            if (HandleVersionGate(status, body)) return true;
            if (status != 403 || string.IsNullOrEmpty(body)) return false;
            var data = MiniJSON.DeserializeDict(body);
            // FastAPI wraps the payload under "detail".
            object detailObj;
            if (data != null && data.TryGetValue("detail", out detailObj)
                && detailObj is Dictionary<string, object> detail
                && MiniJSON.GetString(detail, "code") == "device_unverified")
            {
                string challengeId = MiniJSON.GetString(detail, "challenge_id");
                if (GeneKermanMod.Instance != null)
                    GeneKermanMod.Instance.OnDeviceGate(challengeId);
                return true;
            }
            return false;
        }

        /// If a response is the server-enforced version block (426 update_required),
        /// raise the in-game "update required" window. Returns true if it was.
        private bool HandleVersionGate(long status, string body)
        {
            if (status != 426 || string.IsNullOrEmpty(body)) return false;
            var data = MiniJSON.DeserializeDict(body);
            object detailObj;
            if (data != null && data.TryGetValue("detail", out detailObj)
                && detailObj is Dictionary<string, object> detail
                && MiniJSON.GetString(detail, "code") == "update_required")
            {
                if (GeneKermanMod.Instance != null)
                    GeneKermanMod.Instance.OnVersionGate(
                        MiniJSON.GetString(detail, "latest_version"),
                        MiniJSON.GetString(detail, "download_url"));
                return true;
            }
            return false;
        }

        public IEnumerator Get(string endpoint, ApiCallback callback)
        {
            string url = serverUrl + endpoint;
            using (var req = UnityWebRequest.Get(url))
            {
                ApplyHeaders(req);
                req.SetRequestHeader("Accept", "application/json");
                req.timeout = 15;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] GET {endpoint} failed: {req.error} ({req.responseCode})");
            }
        }

        public IEnumerator Post(string endpoint, string jsonBody, ApiCallback callback)
        {
            string url = serverUrl + endpoint;
            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                ApplyHeaders(req);
                req.timeout = 15;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] POST {endpoint} failed: {req.error} ({req.responseCode})");
            }
        }

        public IEnumerator Delete(string endpoint, ApiCallback callback)
        {
            string url = serverUrl + endpoint;
            using (var req = new UnityWebRequest(url, "DELETE"))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                ApplyHeaders(req);
                req.timeout = 15;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] DELETE {endpoint} failed: {req.error} ({req.responseCode})");
            }
        }

        // ── Multipart Upload (craft + screenshots) ──────────────────────────

        public IEnumerator SubmitContract(
            string contractId,
            byte[] craftFileData, string craftFileName,
            string loadmetaContent,
            string vesselDataJson,
            string vesselNodeData,
            List<byte[]> screenshots, List<string> screenshotNames,
            string modlist,
            string usedModlist,
            string usedParts,
            string deltaVVac,
            ApiCallback callback)
        {
            string url = serverUrl + "/api/v1/contracts/" + contractId + "/submit";

            var form = new List<IMultipartFormSection>();

            // Craft file (gzip compressed for bandwidth savings)
            if (craftFileData != null && craftFileData.Length > 0)
            {
                byte[] compressed = GzipCompress(craftFileData);
                form.Add(new MultipartFormFileSection("craft_file", compressed,
                    craftFileName ?? "vessel.craft", "application/gzip"));
            }

            // Loadmeta as form field
            if (!string.IsNullOrEmpty(loadmetaContent))
                form.Add(new MultipartFormDataSection("loadmeta", loadmetaContent));

            // Vessel telemetry data
            if (!string.IsNullOrEmpty(vesselDataJson))
                form.Add(new MultipartFormDataSection("vessel_data", vesselDataJson));

            // Full vessel state (ConfigNode) for vessel transfer — gzip compressed
            if (!string.IsNullOrEmpty(vesselNodeData))
            {
                byte[] nodeBytes = System.Text.Encoding.UTF8.GetBytes(vesselNodeData);
                byte[] compressed = GzipCompress(nodeBytes);
                form.Add(new MultipartFormFileSection("vessel_node", compressed,
                    "vessel.cfg", "application/gzip"));
            }

            // Screenshots — one render per submitted craft. Sent as a repeated
            // "screenshots" field so the count isn't capped (a multi-vessel submission
            // sends the active craft plus one render for each selected extra).
            if (screenshots != null)
            {
                for (int i = 0; i < screenshots.Count; i++)
                {
                    if (screenshots[i] == null || screenshots[i].Length == 0) continue;
                    string name = (screenshotNames != null && i < screenshotNames.Count)
                        ? screenshotNames[i] : $"render_{i}.png";
                    form.Add(new MultipartFormFileSection("screenshots", screenshots[i],
                        name, "image/png"));
                }
            }

            // Modlist (contractor's installed mods — informational)
            if (!string.IsNullOrEmpty(modlist))
                form.Add(new MultipartFormDataSection("modlist", modlist));

            // Mod folders actually used by the submitted craft — server validates
            // these against the contract's required modlist.
            if (!string.IsNullOrEmpty(usedModlist))
                form.Add(new MultipartFormDataSection("used_modlist", usedModlist));

            // Per-part classification of the craft — server re-checks the contract's
            // mission-limit constraints (forbidden/required parts, fuels, engine types).
            if (!string.IsNullOrEmpty(usedParts))
                form.Add(new MultipartFormDataSection("used_parts", usedParts));

            // Craft's vacuum Δv (m/s) — server re-checks the contract's min/max-Δv limit.
            if (!string.IsNullOrEmpty(deltaVVac))
                form.Add(new MultipartFormDataSection("delta_v_vac", deltaVVac));

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 60; // File uploads can take longer

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Submit failed: {req.error} ({req.responseCode})");
            }
        }

        // ── Checkpoint Hero Shot Upload ─────────────────────────────────────

        /// <summary>
        /// Upload a milestone hero shot (PNG) to the server, which posts it to the
        /// community checkpoint-photos channel. Metadata fields are informational and
        /// shown in the Discord embed.
        /// </summary>
        public IEnumerator UploadCheckpointPhoto(
            byte[] pngData, string kind, string vesselName,
            string body, string targetName, ApiCallback callback)
        {
            string url = serverUrl + "/api/v1/checkpoint-photo";

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("photo", pngData, "checkpoint.png", "image/png"),
                new MultipartFormDataSection("kind", kind ?? "checkpoint"),
                new MultipartFormDataSection("vessel_name", vesselName ?? ""),
                new MultipartFormDataSection("body", body ?? ""),
                new MultipartFormDataSection("target_name", targetName ?? ""),
            };

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 60;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback?.Invoke(ok, req.downloadHandler?.text, req.responseCode);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Checkpoint photo upload failed: {req.error} ({req.responseCode})");
            }
        }

        // ── Typed API Methods ───────────────────────────────────────────────

        public IEnumerator LinkAccount(string code, ApiCallback<Dictionary<string, object>> callback)
        {
            var body = new Dictionary<string, object> { { "code", code } };
            string json = MiniJSON.Serialize(body);

            yield return Post("/api/v1/auth/link", json, (ok, resp, status) =>
                HandleLinkResponse(ok, resp, callback));
        }

        /// Poll the server until the user approves the login in their Discord DM.
        /// The user pressed /g linkcode → entered the code here → the bot DM'd them
        /// a Log-in button; this waits for that press. Calls back exactly once:
        /// success with the linked data (token stored), or failure with a message.
        public IEnumerator PollLoginApproval(string challengeId,
            ApiCallback<Dictionary<string, object>> callback)
        {
            string body = MiniJSON.Serialize(
                new Dictionary<string, object> { { "challenge_id", challengeId } });
            const int maxAttempts = 95;   // ~3 min at 2s spacing, matches server TTL

            for (int i = 0; i < maxAttempts; i++)
            {
                bool requestOk = false;
                long code = 0;
                string resp = null;
                yield return Post("/api/v1/auth/link/poll", body, (ok, r, status) =>
                {
                    requestOk = ok; resp = r; code = status;
                });

                var data = !string.IsNullOrEmpty(resp) ? MiniJSON.DeserializeDict(resp) : null;

                if (requestOk && data != null)
                {
                    string token = MiniJSON.GetString(data, "token");
                    if (!string.IsNullOrEmpty(token))
                    {
                        SetToken(token);
                        callback(true, data, null);
                        yield break;
                    }
                    // status == "pending" → fall through and keep waiting.
                }
                else if (code >= 400 && code < 500)
                {
                    // Denied or expired — terminal. Surface the server's detail.
                    string error = data != null
                        ? MiniJSON.GetString(data, "detail", "Login was not approved.")
                        : "Login was not approved.";
                    callback(false, null, error);
                    yield break;
                }
                // Pending, or a transient network/5xx blip → wait and retry.
                yield return new WaitForSeconds(2f);
            }

            callback(false, null, "Timed out waiting for Discord approval. Try again.");
        }

        /// Poll a device-approval challenge while this device is blocked. Calls back
        /// repeatedly is avoided — it loops internally and calls back once with the
        /// terminal outcome: approved, denied (with optional reportId), or expired.
        public delegate void DevicePollCallback(string state, string reportId);

        /// onPing fires (on the polling/blocked device) when the account owner pressed
        /// "🔔 Ping this PC" in their Discord DM, so we can flash an "is this you?" alert.
        public IEnumerator PollDeviceApproval(string challengeId, DevicePollCallback callback,
            System.Action onPing = null)
        {
            string body = MiniJSON.Serialize(
                new Dictionary<string, object> { { "challenge_id", challengeId } });

            while (true)
            {
                bool requestOk = false;
                string resp = null;
                yield return Post("/api/v1/auth/device/poll", body, (ok, r, status) =>
                {
                    requestOk = ok; resp = r;
                });

                if (requestOk && !string.IsNullOrEmpty(resp))
                {
                    var data = MiniJSON.DeserializeDict(resp);
                    string state = MiniJSON.GetString(data, "status");
                    if (state == "pending")
                    {
                        if (onPing != null && MiniJSON.GetBool(data, "ping"))
                            onPing();
                        yield return new WaitForSeconds(3f);
                        continue;
                    }
                    callback(state, MiniJSON.GetString(data, "report_id"));
                    yield break;
                }
                // Transient failure — wait and retry rather than giving up the gate.
                yield return new WaitForSeconds(3f);
            }
        }

        /// Upload this device's diagnostics (MAC + KSP.log) for a moderation report
        /// the user opened. Best-effort; failure just means the ticket stays partial.
        public IEnumerator UploadDeviceReport(string reportId, ApiCallback callback)
        {
            string url = serverUrl + "/api/v1/device/report/" + reportId;
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("mac", DeviceId.GetMacAddress() ?? ""),
            };
            byte[] logBytes = DeviceId.GetKspLog();
            if (logBytes != null && logBytes.Length > 0)
                form.Add(new MultipartFormFileSection("ksp_log", logBytes, "KSP.log", "text/plain"));

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 60;
                yield return req.SendWebRequest();
                bool ok = !req.isNetworkError && !req.isHttpError;
                callback?.Invoke(ok, req.downloadHandler?.text, req.responseCode);
                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Device report upload failed: {req.error} ({req.responseCode})");
            }
        }

        // Handles the first link step. A token means we're linked (store it). An
        // "approval_required" status means the user must approve in Discord — the
        // response carries challenge_id and the caller polls via PollLoginApproval.
        // Anything else is an error, surfacing the server's detail when present.
        private void HandleLinkResponse(bool ok, string resp,
            ApiCallback<Dictionary<string, object>> callback)
        {
            if (ok && !string.IsNullOrEmpty(resp))
            {
                var data = MiniJSON.DeserializeDict(resp);
                string token = MiniJSON.GetString(data, "token");
                if (!string.IsNullOrEmpty(token))
                {
                    SetToken(token);
                    callback(true, data, null);
                    return;
                }
                if (MiniJSON.GetString(data, "status") == "approval_required")
                {
                    callback(true, data, null);   // not linked yet — awaiting approval
                    return;
                }
            }
            string error = "Link failed";
            if (!string.IsNullOrEmpty(resp))
            {
                var errData = MiniJSON.DeserializeDict(resp);
                error = MiniJSON.GetString(errData, "detail", error);
            }
            callback(false, null, error);
        }

        /// Log out of every device linked to this account. On success the server
        /// has invalidated all of this user's tokens, so we drop our own token too
        /// and the caller returns to the link screen.
        public IEnumerator LogoutAllDevices(ApiCallback callback)
        {
            yield return Post("/api/v1/auth/logout_all", "{}", (ok, resp, status) =>
            {
                if (ok)
                    ClearToken();
                callback(ok, resp, status);
            });
        }

        /// <summary>
        /// Ask the server whether this client's DLL is the published latest. Sends
        /// our SHA256 + version label (unauthenticated endpoint, works before linking).
        /// On a failed request the callback's data is null — callers must fail open
        /// (never block the player on a check that didn't reach the server).
        /// </summary>
        public IEnumerator CheckVersion(ApiCallback<Dictionary<string, object>> callback)
        {
            string endpoint = "/api/v1/version/check"
                + "?hash=" + Uri.EscapeDataString(ModVersion.Sha256)
                + "&version=" + Uri.EscapeDataString(ModVersion.Current);
            yield return Get(endpoint, (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "version check failed");
            });
        }

        /// <summary>
        /// Challenge-response attestation: fetch a nonce + DLL byte-window, hash our
        /// on-disk DLL over it, and send the digest back. The server flags a mismatch
        /// to moderators — there's nothing for the client to act on, so this is
        /// best-effort and silent. No-op unless linked (the endpoint needs a token).
        /// </summary>
        public IEnumerator RunAttestation()
        {
            if (!IsLinked) yield break;

            bool ok = false; string resp = null;
            yield return Get("/api/v1/attest/challenge", (o, r, s) => { ok = o; resp = r; });
            if (!ok || string.IsNullOrEmpty(resp)) yield break;

            var data = MiniJSON.DeserializeDict(resp);
            if (data == null || !MiniJSON.GetBool(data, "enabled")) yield break;  // not stored → skip

            string attestId = MiniJSON.GetString(data, "attest_id");
            string nonce = MiniJSON.GetString(data, "nonce");
            int offset = MiniJSON.GetInt(data, "offset");
            int length = MiniJSON.GetInt(data, "length");
            if (string.IsNullOrEmpty(attestId) || string.IsNullOrEmpty(nonce)) yield break;

            string digest = ModVersion.AttestDigest(nonce, offset, length);
            if (string.IsNullOrEmpty(digest)) yield break;

            string body = MiniJSON.Serialize(new Dictionary<string, object>
            {
                { "attest_id", attestId },
                { "digest", digest },
            });
            yield return Post("/api/v1/attest/respond", body, (o, r, s) => { });
        }

        public IEnumerator GetProfile(ApiCallback<Dictionary<string, object>> callback)
        {
            yield return Get("/api/v1/user/profile", (ok, resp, status) =>
            {
                if (status == 401) { ClearToken(); }
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "Failed to fetch profile");
            });
        }

        public IEnumerator GetWeeklyMissions(ApiCallback<Dictionary<string, object>> callback)
        {
            yield return Get("/api/v1/missions/weekly", (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "Failed to fetch missions");
            });
        }

        public IEnumerator SelectMission(int missionId, ApiCallback<Dictionary<string, object>> callback)
        {
            var body = new Dictionary<string, object> { { "mission_id", missionId } };
            yield return Post("/api/v1/missions/select", MiniJSON.Serialize(body), (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                {
                    string error = "Failed";
                    if (!string.IsNullOrEmpty(resp))
                    {
                        var d = MiniJSON.DeserializeDict(resp);
                        error = MiniJSON.GetString(d, "message", MiniJSON.GetString(d, "detail", error));
                    }
                    callback(false, null, error);
                }
            });
        }

        public IEnumerator GetActiveContracts(ApiCallback<Dictionary<string, object>> callback)
        {
            yield return Get("/api/v1/contracts/active", (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "Failed to fetch contracts");
            });
        }

        public IEnumerator GetNotifications(ApiCallback<Dictionary<string, object>> callback)
        {
            yield return Get("/api/v1/user/notifications", (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "Failed to fetch notifications");
            });
        }

        public IEnumerator MarkNotificationsRead(ApiCallback callback)
        {
            yield return Post("/api/v1/user/notifications/mark_read", "{}", callback);
        }

        public IEnumerator MarkNotificationRead(string notifId, ApiCallback callback)
        {
            yield return Post($"/api/v1/user/notifications/{notifId}/mark_read", "{}", callback);
        }

        public IEnumerator DismissNotification(string notifId, ApiCallback callback)
        {
            yield return Delete($"/api/v1/user/notifications/{notifId}", callback);
        }

        public IEnumerator GetCorps(ApiCallback<Dictionary<string, object>> callback)
        {
            yield return Get("/api/v1/corps/list", (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "Failed to fetch corporations");
            });
        }

        public IEnumerator CreateContract(string contractorId, string mission,
            int payment, int fine, string dueDate, string modlist,
            ApiCallback<Dictionary<string, object>> callback, string contractType = "auto")
        {
            var body = new Dictionary<string, object>
            {
                { "contractor_id", contractorId },
                { "mission", mission },
                { "payment", payment },
                { "fine", fine },
                { "due_date", dueDate },
                { "contract_type", string.IsNullOrEmpty(contractType) ? "auto" : contractType },
            };
            if (!string.IsNullOrEmpty(modlist))
                body.Add("modlist", modlist);

            yield return Post("/api/v1/contracts/create", MiniJSON.Serialize(body), (ok, resp, status) =>
            {
                if (!string.IsNullOrEmpty(resp))
                {
                    var data = MiniJSON.DeserializeDict(resp);
                    bool success = MiniJSON.GetBool(data, "success", false);
                    string msg = MiniJSON.GetString(data, "message", ok ? "Success" : "Failed");
                    callback(success, data, success ? null : msg);
                }
                else
                    callback(false, null, "No response from server");
            });
        }

        /// <summary>
        /// Open a reverse (Dutch) auction. No contractor — it's posted to Discord
        /// where anyone bids the price down; the lowest bid wins. start_value is
        /// escrowed up front. modlist is the mods required / limited to (optional).
        /// </summary>
        public IEnumerator CreateAuction(string mission, int startValue, int fine,
            string dueDate, int durationHours, string modlist, string contractType,
            ApiCallback<Dictionary<string, object>> callback)
        {
            var body = new Dictionary<string, object>
            {
                { "mission", mission },
                { "start_value", startValue },
                { "fine", fine },
                { "due_date", dueDate },
                { "duration_hours", durationHours },
            };
            if (!string.IsNullOrEmpty(modlist))
                body.Add("modlist", modlist);
            if (!string.IsNullOrEmpty(contractType))
                body.Add("contract_type", contractType);

            yield return Post("/api/v1/auctions/create", MiniJSON.Serialize(body), (ok, resp, status) =>
            {
                if (!string.IsNullOrEmpty(resp))
                {
                    var data = MiniJSON.DeserializeDict(resp);
                    bool success = MiniJSON.GetBool(data, "success", false);
                    string msg = MiniJSON.GetString(data, "message", ok ? "Success" : "Failed");
                    callback(success, data, success ? null : msg);
                }
                else
                    callback(false, null, "No response from server");
            });
        }

        /// <summary>
        /// Create a rescue contract. Uploads the issuer's snapshotted vessel (the
        /// wreck, crew already renamed) as a gzipped ConfigNode alongside the target
        /// location and rename map. Multipart because of the file upload.
        /// </summary>
        public IEnumerator CreateRescueContract(
            string contractorId, string mission, int payment, int fine, string dueDate,
            string modlist, string body, string mode,
            double ap, double pe, double lat, double lon,
            double marginAlt, double marginPos, bool isModded,
            string rescuePid, string kerbalsJson, string vesselNodeData,
            ApiCallback<Dictionary<string, object>> callback)
        {
            string url = serverUrl + "/api/v1/contracts/create_rescue";
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("contractor_id", contractorId ?? ""),
                new MultipartFormDataSection("mission", mission ?? ""),
                new MultipartFormDataSection("payment", payment.ToString(inv)),
                new MultipartFormDataSection("fine", fine.ToString(inv)),
                new MultipartFormDataSection("due_date", dueDate ?? ""),
                new MultipartFormDataSection("body", body ?? ""),
                new MultipartFormDataSection("mode", mode ?? "orbit"),
                new MultipartFormDataSection("ap", ap.ToString("G17", inv)),
                new MultipartFormDataSection("pe", pe.ToString("G17", inv)),
                new MultipartFormDataSection("lat", lat.ToString("G17", inv)),
                new MultipartFormDataSection("lon", lon.ToString("G17", inv)),
                new MultipartFormDataSection("margin_alt", marginAlt.ToString("G17", inv)),
                new MultipartFormDataSection("margin_pos", marginPos.ToString("G17", inv)),
                new MultipartFormDataSection("is_modded", isModded ? "true" : "false"),
                new MultipartFormDataSection("kerbals", string.IsNullOrEmpty(kerbalsJson) ? "[]" : kerbalsJson),
            };
            if (!string.IsNullOrEmpty(modlist))
                form.Add(new MultipartFormDataSection("modlist", modlist));
            if (!string.IsNullOrEmpty(rescuePid))
                form.Add(new MultipartFormDataSection("rescue_pid", rescuePid));

            byte[] nodeBytes = Encoding.UTF8.GetBytes(vesselNodeData ?? "");
            byte[] compressed = GzipCompress(nodeBytes);
            form.Add(new MultipartFormFileSection("vessel_node", compressed, "vessel.cfg", "application/gzip"));

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 60;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                string resp = req.downloadHandler?.text;
                if (!string.IsNullOrEmpty(resp))
                {
                    var data = MiniJSON.DeserializeDict(resp);
                    bool success = MiniJSON.GetBool(data, "success", false);
                    string msg = MiniJSON.GetString(data, "message", ok ? "Success" : "Failed");
                    callback(success, data, success ? null : msg);
                }
                else
                {
                    callback(false, null, "No response from server");
                }
                if (!ok)
                    Debug.LogWarning($"[GeneKerman] CreateRescue failed: {req.error} ({req.responseCode})");
            }
        }

        // ── Marketplace ─────────────────────────────────────────────────────

        /// <summary>
        /// List a craft for sale on the marketplace. The craft file is gzip
        /// compressed before upload (the server decompresses and stores raw).
        /// </summary>
        public IEnumerator ListCraftForSale(
            byte[] craftFileData, string craftFileName,
            string craftName, string craftType, int partCount,
            float mass, float cost, int price,
            byte[] blueprintData,
            ApiCallback callback)
        {
            string url = serverUrl + "/api/v1/marketplace/list";

            var form = new List<IMultipartFormSection>();

            byte[] compressed = GzipCompress(craftFileData);
            form.Add(new MultipartFormFileSection("craft_file", compressed,
                craftFileName ?? "craft.craft", "application/gzip"));

            // Rendered blueprint image — shown publicly on the Discord listing.
            if (blueprintData != null && blueprintData.Length > 0)
                form.Add(new MultipartFormFileSection("blueprint", blueprintData,
                    "blueprint.png", "image/png"));

            form.Add(new MultipartFormDataSection("craft_name", craftName ?? "Untitled"));
            form.Add(new MultipartFormDataSection("craft_type", craftType ?? "VAB"));
            form.Add(new MultipartFormDataSection("part_count", partCount.ToString()));
            form.Add(new MultipartFormDataSection("mass", mass.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            form.Add(new MultipartFormDataSection("cost", cost.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            form.Add(new MultipartFormDataSection("price", price.ToString()));

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 60;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Marketplace list failed: {req.error} ({req.responseCode})");
            }
        }

        /// <summary>
        /// Quicksend a craft/vessel to another player. <paramref name="kind"/> is
        /// "vessel" (a live ConfigNode the recipient spawns in-save) or "craft" (a
        /// .craft blueprint installed to their Ships folder). The payload is gzip
        /// compressed before upload; the server decompresses and stores it raw.
        /// </summary>
        public IEnumerator SendCraftToFriend(
            string recipientId, string kind, string craftName,
            byte[] fileData, string fileName, ApiCallback callback)
        {
            string url = serverUrl + "/api/v1/craft/send";

            var form = new List<IMultipartFormSection>();
            byte[] compressed = GzipCompress(fileData);
            form.Add(new MultipartFormFileSection("file", compressed,
                fileName ?? "payload.dat", "application/gzip"));
            form.Add(new MultipartFormDataSection("recipient_id", recipientId ?? ""));
            form.Add(new MultipartFormDataSection("kind", kind ?? "craft"));
            form.Add(new MultipartFormDataSection("craft_name", craftName ?? "Craft"));

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 60;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Quicksend failed: {req.error} ({req.responseCode})");
            }
        }

        public IEnumerator GetMarketplaceListings(ApiCallback<Dictionary<string, object>> callback)
        {
            yield return Get("/api/v1/marketplace/listings", (ok, resp, status) =>
            {
                if (ok && !string.IsNullOrEmpty(resp))
                    callback(true, MiniJSON.DeserializeDict(resp), null);
                else
                    callback(false, null, "Failed to fetch marketplace listings");
            });
        }

        public IEnumerator DelistCraft(string listingId, ApiCallback callback)
        {
            yield return Post($"/api/v1/marketplace/{listingId}/delist", "{}", callback);
        }

        /// <summary>
        /// Download a raw file from a URL (for craft files from Firebase Storage).
        /// </summary>
        public IEnumerator DownloadFile(string url, System.Action<bool, byte[]> callback)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 30;
                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, ok ? req.downloadHandler.data : null);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Download failed: {req.error}");
            }
        }

        // ── Utility ─────────────────────────────────────────────────────────

        private static byte[] GzipCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }
    }
}
