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
        private string serverUrl;
        private string sessionToken;
        private readonly string tokenPath;

        // Callbacks for async operations
        public delegate void ApiCallback(bool success, string responseBody, long statusCode);
        public delegate void ApiCallback<T>(bool success, T data, string error);

        public bool IsLinked => !string.IsNullOrEmpty(sessionToken);
        public string ServerUrl => serverUrl;

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
                        // Store host and port separately because ConfigNode
                        // treats // as a comment delimiter, mangling URLs.
                        string host = gk.GetValue("serverHost") ?? "localhost";
                        string port = gk.GetValue("serverPort") ?? "5022";
                        string protocol = gk.GetValue("serverProtocol") ?? "http";
                        serverUrl = $"{protocol}://{host}:{port}";
                        Debug.Log($"[GeneKerman] Settings loaded — server: {serverUrl}");
                        return;
                    }
                }
            }
            serverUrl = "http://localhost:5022";
            Debug.Log($"[GeneKerman] Using default server: {serverUrl}");
        }

        public void SetServerUrl(string url)
        {
            url = url.TrimEnd('/');
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "http://" + url;
            }
            serverUrl = url;
            SaveSettings();
        }

        private void SaveSettings()
        {
            // Parse the URL into components for ConfigNode-safe storage
            string protocol = "http";
            string host = "localhost";
            string port = "5022";

            try
            {
                var uri = new Uri(serverUrl);
                protocol = uri.Scheme;
                host = uri.Host;
                port = uri.Port.ToString();
            }
            catch
            {
                Debug.LogWarning("[GeneKerman] Failed to parse server URL, using defaults.");
            }

            string settingsPath = Path.Combine(GeneKermanMod.PluginDataPath, "settings.cfg");
            var node = new ConfigNode();
            var gk = node.AddNode("GeneKerman");
            gk.AddValue("serverProtocol", protocol);
            gk.AddValue("serverHost", host);
            gk.AddValue("serverPort", port);
            node.Save(settingsPath);
            Debug.Log($"[GeneKerman] Settings saved — {protocol}://{host}:{port}");
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

        public IEnumerator Get(string endpoint, ApiCallback callback)
        {
            string url = serverUrl + endpoint;
            using (var req = UnityWebRequest.Get(url))
            {
                if (IsLinked)
                    req.SetRequestHeader("Authorization", "Bearer " + sessionToken);
                req.SetRequestHeader("Accept", "application/json");
                req.timeout = 15;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);

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
                if (IsLinked)
                    req.SetRequestHeader("Authorization", "Bearer " + sessionToken);
                req.timeout = 15;

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] POST {endpoint} failed: {req.error} ({req.responseCode})");
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

            // Screenshots
            if (screenshots != null)
            {
                string[] fieldNames = { "screenshot1", "screenshot2", "screenshot3" };
                for (int i = 0; i < Math.Min(screenshots.Count, 3); i++)
                {
                    string name = (screenshotNames != null && i < screenshotNames.Count)
                        ? screenshotNames[i] : $"screenshot_{i}.png";
                    form.Add(new MultipartFormFileSection(fieldNames[i], screenshots[i],
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

            using (var req = UnityWebRequest.Post(url, form))
            {
                if (IsLinked)
                    req.SetRequestHeader("Authorization", "Bearer " + sessionToken);
                req.timeout = 60; // File uploads can take longer

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback(ok, req.downloadHandler?.text, req.responseCode);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Submit failed: {req.error} ({req.responseCode})");
            }
        }

        // ── Typed API Methods ───────────────────────────────────────────────

        public IEnumerator LinkAccount(string code, ApiCallback<Dictionary<string, object>> callback)
        {
            var body = new Dictionary<string, object> { { "code", code } };
            string json = MiniJSON.Serialize(body);

            yield return Post("/api/v1/auth/link", json, (ok, resp, status) =>
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
                }
                string error = "Link failed";
                if (!string.IsNullOrEmpty(resp))
                {
                    var errData = MiniJSON.DeserializeDict(resp);
                    error = MiniJSON.GetString(errData, "detail", error);
                }
                callback(false, null, error);
            });
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
            int payment, int fine, string dueDate, string modlist, ApiCallback<Dictionary<string, object>> callback)
        {
            var body = new Dictionary<string, object>
            {
                { "contractor_id", contractorId },
                { "mission", mission },
                { "payment", payment },
                { "fine", fine },
                { "due_date", dueDate },
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
