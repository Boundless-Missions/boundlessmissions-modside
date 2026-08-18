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

        /// <summary>Public Privacy Policy / Terms of Service pages linked from the consent gate.</summary>
        public const string PrivacyPolicyUrl = "https://boundlessmissions.com/pp";
        public const string TermsOfServiceUrl = "https://boundlessmissions.com/tos";

        /// <summary>How much of KSP.log a bug report carries: the first 2 MB (loaded
        /// assemblies, mod list, system specs) and the last 7 MB (whatever just went
        /// wrong). Matches _LOG_HEAD_BYTES / _LOG_TAIL_BYTES in the server's
        /// api_server.py, which trims to the same shape as a backstop.</summary>
        private const int LogHeadBytes = 2000000;
        private const int LogTailBytes = 7000000;

        /// <summary>Default marketplace website, opened from the Market tab. Overridable
        /// via the <c>marketplaceProtocol</c> / <c>marketplaceAddress</c> keys in
        /// settings.cfg — split for the same reason serverHost/serverProtocol are,
        /// see LoadMarketplaceUrl.</summary>
        public const string DefaultMarketplaceUrl = "https://boundlessmissions.com/marketplace";

        private string serverUrl;
        private string marketplaceUrl = DefaultMarketplaceUrl; // marketplace website (settings.cfg overridable)
        private string customServerUrl = "http://localhost:5022"; // last custom URL, preserved when official is active
        private bool useOfficialServer;
        private bool notificationsEnabled = true;
        private bool checkpointPhotosEnabled = true;
        // Master data-sharing switch (KSP add-on rule 8.2). While false the user has
        // opted out: every outbound request is short-circuited (see TransmissionBlocked)
        // and the mod runs inert until they re-enable it. Defaults on once consent is given.
        private bool dataGatheringEnabled = true;
        // UI mode. False = the classic in-game IMGUI windows; true = the browser UI
        // served by the loopback bridge. Classic is the default and stays a permanent,
        // fully functional fallback — single-monitor and Steam Deck players are better
        // served by it, and it is the only UI available if the bridge cannot start.
        private bool webUiEnabled;
        // Emergency freeze for rescue crew (see RescueImmunityGuardian). On by default:
        // without it a stranded crew starves in whatever time the rescuer takes, and a
        // wreck built for another life-support mod is unrescuable. Off leaves the crew
        // seated and their life support running normally.
        private bool emergencyFreezeEnabled = true;
        // Days of the local LS mod's resources stowed aboard a rescue wreck, per rescued
        // kerbal. 0 disables the ration kit; the freeze itself is unaffected.
        private int emergencyRationDays = 3;
        // Swap parts a received craft asks for but this install doesn't have for the
        // equivalent it does have (see PartAliases). On by default: the swap only ever
        // engages on a craft that would otherwise refuse to load, and every substitution
        // is reported. Off keeps received crafts byte-for-byte as the sender built them.
        private bool partSubstitutionEnabled = true;
        // Carry a craft's Textures Unlimited paint job across, and drop the recolour
        // modules this install can't accept so an unpainted load is a clean one (see
        // TextureTransfer). On by default: it only ever engages on a craft that was
        // painted, and never on the parts themselves. Off leaves the recolour modules
        // exactly as the sender wrote them — KSP ignores the ones it can't match.
        private bool textureTransferEnabled = true;
        // Carry a craft's RealFuels/RO fuel-and-engine configuration manifest, and for a
        // recipient without RealFuels drop the RF modules plus locally-undefined
        // propellants so the craft loads in local fuels (see RealFuelsTransfer). On by
        // default for the same reason as part substitution: it only engages on a craft
        // that would otherwise arrive broken or misleading. Off leaves the nodes exactly
        // as the sender wrote them; warnings still post.
        private bool fuelConfigTransferEnabled = true;
        private string sessionToken;
        private readonly string tokenPath;

        // Session tokens, keyed by the server that issued them.
        //
        // A token only means anything to the server that minted it, so switching servers
        // must not carry the active one across: it would 401 anyway, and on the way it
        // would hand this account's bearer token to whatever host was just typed in.
        // Remembering the old one is what lets a self-hoster (or a developer) flip
        // between the official server and a local bot without re-linking each time —
        // linking costs a 6-digit code and a Discord approval every round trip.
        //
        // Same exposure as session.token, which sits beside it: plaintext on disk, N
        // tokens instead of one. Anyone who can read this directory could already read
        // that file, so this widens the blast radius but not the threat model.
        private readonly Dictionary<string, string> tokensByServer =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string sessionsPath;

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
        /// <summary>The marketplace website opened from the Market tab.</summary>
        public string MarketplaceUrl =>
            string.IsNullOrEmpty(marketplaceUrl) ? DefaultMarketplaceUrl : marketplaceUrl;
        /// <summary>Whether live notifications (toast popups + socket/poll) are enabled.</summary>
        public bool NotificationsEnabled => notificationsEnabled;
        /// <summary>Whether milestone hero-shot prompts (rendezvous/flyby/asteroid) are enabled.</summary>
        public bool CheckpointPhotosEnabled => checkpointPhotosEnabled;
        /// <summary>Master opt-in: whether the mod may collect and transmit any data at
        /// all. False means the user opted out — the mod is inert and sends nothing.</summary>
        public bool DataGatheringEnabled => dataGatheringEnabled;
        /// <summary>Whether the UI opens in a browser (true) or as classic in-game windows (false).</summary>
        public bool WebUiEnabled => webUiEnabled;
        /// <summary>Whether stranded rescue crew are held in emergency freeze until reached.</summary>
        public bool EmergencyFreezeEnabled => emergencyFreezeEnabled;
        /// <summary>Days of local life support stowed aboard a rescue wreck per kerbal (0 = off).</summary>
        public int EmergencyRationDays => emergencyRationDays;
        /// <summary>Whether a received craft's unavailable parts are swapped for installed equivalents.</summary>
        public bool PartSubstitutionEnabled => partSubstitutionEnabled;
        /// <summary>Whether a craft's Textures Unlimited paint job is carried across transfers.</summary>
        public bool TextureTransferEnabled => textureTransferEnabled;
        /// <summary>Whether a craft's RealFuels/RO fuel-and-engine configuration is carried
        /// (and reconciled away for a recipient without RealFuels).</summary>
        public bool FuelConfigTransferEnabled => fuelConfigTransferEnabled;

        /// <summary>True when no request may leave this PC — either because the user
        /// has not yet given first-run consent (rule 8.1) or has opted out of data
        /// sharing (rule 8.2). Logged once per suppressed call for diagnostics.</summary>
        public bool TransmissionBlocked
        {
            get
            {
                // Hard precondition: nothing is transmitted until the user has accepted
                // the privacy policy, terms, and data-collection consent.
                if (!Consent.Accepted)
                {
                    Debug.Log("[GeneKerman] Outbound request suppressed — privacy consent not given.");
                    return true;
                }
                if (!dataGatheringEnabled)
                {
                    Debug.Log("[GeneKerman] Outbound request suppressed — data sharing is off.");
                    return true;
                }
                return false;
            }
        }

        /// <summary>ws/wss base derived from the configured http/https server URL.</summary>
        private string WebSocketBase
        {
            get
            {
                string wsBase = serverUrl;
                if (wsBase.StartsWith("https://")) wsBase = "wss://" + wsBase.Substring(8);
                else if (wsBase.StartsWith("http://")) wsBase = "ws://" + wsBase.Substring(7);
                return wsBase;
            }
        }

        /// <summary>
        /// Legacy WebSocket URL carrying the long-lived session token in the query
        /// string. Deprecated — the token then lands in server/proxy access logs.
        /// Prefer GetWsTicket + WebSocketUrlForTicket. Kept only as a fallback when
        /// the server is too old to issue tickets. Empty if unlinked.
        /// </summary>
        public string NotificationsWebSocketUrl
        {
            get
            {
                if (string.IsNullOrEmpty(sessionToken)) return "";
                return WebSocketBase + "/ws/v1/notifications?token=" + Uri.EscapeDataString(sessionToken);
            }
        }

        /// <summary>WebSocket URL using a short-lived single-use ticket instead of the
        /// session token, so nothing reusable is exposed in the URL (or logs).</summary>
        public string WebSocketUrlForTicket(string ticket)
        {
            return WebSocketBase + "/ws/v1/notifications?ticket=" + Uri.EscapeDataString(ticket ?? "");
        }

        /// <summary>
        /// Exchange the session token for a short-lived single-use WebSocket ticket
        /// over normal authenticated HTTPS, then invoke onTicket with it (null on
        /// failure, so the caller can fall back to the token URL on an old server).
        /// </summary>
        public IEnumerator GetWsTicket(Action<string> onTicket)
        {
            if (string.IsNullOrEmpty(sessionToken)) { onTicket(null); yield break; }
            yield return Post("/api/v1/auth/ws-ticket", "{}", (ok, body, status) =>
            {
                string ticket = null;
                if (ok && !string.IsNullOrEmpty(body))
                {
                    var data = MiniJSON.DeserializeDict(body);
                    if (data != null) ticket = MiniJSON.GetString(data, "ticket");
                }
                onTicket(string.IsNullOrEmpty(ticket) ? null : ticket);
            });
        }

        public ApiClient()
        {
            tokenPath = Path.Combine(GeneKermanMod.PluginDataPath, "session.token");
            sessionsPath = Path.Combine(GeneKermanMod.PluginDataPath, "sessions.cfg");
            LoadSettings();   // establishes serverUrl, which keys everything below
            LoadSessions();
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
                        // Default to the official server when the key is absent (a
                        // shipped/legacy settings.cfg) — a custom IP is only used when
                        // the user explicitly chose it (which writes the key as false).
                        bool.TryParse(gk.GetValue("useOfficialServer") ?? "true", out useOfficialServer);
                        bool.TryParse(gk.GetValue("enableNotifications") ?? "true", out notificationsEnabled);
                        bool.TryParse(gk.GetValue("enableCheckpointPhotos") ?? "true", out checkpointPhotosEnabled);
                        bool.TryParse(gk.GetValue("enableDataGathering") ?? "true", out dataGatheringEnabled);
                        // Absent means classic: an existing install must never be moved
                        // to a different UI by a mod update.
                        bool.TryParse(gk.GetValue("enableWebUi") ?? "false", out webUiEnabled);
                        bool.TryParse(gk.GetValue("enableEmergencyFreeze") ?? "true", out emergencyFreezeEnabled);
                        // Guarded rather than TryParse'd straight in: a hand-edited
                        // negative would size a ration kit backwards.
                        int rationDays;
                        if (int.TryParse(gk.GetValue("emergencyRationDays") ?? "3", out rationDays))
                            emergencyRationDays = rationDays < 0 ? 0 : rationDays;
                        bool.TryParse(gk.GetValue("enablePartSubstitution") ?? "true", out partSubstitutionEnabled);
                        bool.TryParse(gk.GetValue("enableTextureTransfer") ?? "true", out textureTransferEnabled);
                        bool.TryParse(gk.GetValue("enableFuelConfigTransfer") ?? "true", out fuelConfigTransferEnabled);

                        // Store host and port separately because ConfigNode
                        // treats // as a comment delimiter, mangling URLs.
                        string host = gk.GetValue("serverHost") ?? "localhost";
                        string port = gk.GetValue("serverPort") ?? "5022";
                        string protocol = gk.GetValue("serverProtocol") ?? "http";
                        customServerUrl = $"{protocol}://{host}:{port}";

                        // Marketplace website (Market tab "Open Marketplace").
                        marketplaceUrl = LoadMarketplaceUrl(gk);

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

        // ── Marketplace URL ─────────────────────────────────────────────────
        //
        // Stored split into scheme and remainder for exactly the reason
        // serverHost/serverPort/serverProtocol are: **ConfigNode treats `//` as a
        // comment delimiter**, so a line reading
        //
        //     marketplaceUrl = https://boundlessmissions.com/marketplace
        //
        // parses back as the value `https:` — everything from the scheme's double
        // slash on is discarded as a comment. The next SaveSettings then wrote
        // `https:` back to disk, making the loss permanent, and Application.OpenURL
        // received a URL with no host. Splitting the scheme out leaves no `//` in
        // either value; a path's single slashes are fine.

        /// <summary>
        /// Resolve the marketplace URL from settings, preferring the split keys and
        /// falling back to the pre-split single key. Anything that does not come out
        /// as an absolute http(s) URL with a host is discarded for the shipped
        /// default — which is what repairs an install already holding `https:`.
        /// </summary>
        private static string LoadMarketplaceUrl(ConfigNode gk)
        {
            string address = gk.GetValue("marketplaceAddress");
            if (!string.IsNullOrEmpty(address))
            {
                string protocol = gk.GetValue("marketplaceProtocol");
                if (string.IsNullOrEmpty(protocol)) protocol = "https";

                string url;
                if (TryNormalizeMarketplaceUrl(protocol.Trim() + "://" + address.Trim(), out url))
                    return url;

                Debug.LogWarning("[GeneKerman] Ignoring unusable marketplaceAddress in settings.cfg.");
                return DefaultMarketplaceUrl;
            }

            // Written before the split. Almost every one of these is a mangled
            // remnant rather than a real override, so it is validated, not trusted.
            string legacy = gk.GetValue("marketplaceUrl");
            if (string.IsNullOrEmpty(legacy)) return DefaultMarketplaceUrl;

            string migrated;
            if (TryNormalizeMarketplaceUrl(legacy.Trim(), out migrated)) return migrated;

            Debug.LogWarning("[GeneKerman] settings.cfg has a truncated marketplaceUrl (\"" + legacy.Trim() +
                             "\") — ConfigNode reads // as a comment. Using the default; " +
                             "set marketplaceProtocol/marketplaceAddress to override.");
            return DefaultMarketplaceUrl;
        }

        /// <summary>
        /// Accept only an absolute http(s) URL that actually has a host, and return
        /// it in the exact form the bridge's open-url allow-list compares against
        /// (Web/GkRoutes.cs matches this string literally, so it must be stable).
        /// </summary>
        private static bool TryNormalizeMarketplaceUrl(string candidate, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrEmpty(candidate)) return false;

            Uri uri;
            // "https:" fails here outright — there is no authority to parse — which
            // is the mangled case self-healing.
            if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(uri.Host)) return false;

            normalized = candidate.Trim();
            return true;
        }

        /// <summary>
        /// Split a URL into the two settings.cfg values. False when the remainder
        /// would itself contain a `//` (a doubled slash in the path), since
        /// persisting that would reintroduce the very bug the split exists to fix.
        /// </summary>
        private static bool SplitMarketplaceUrl(string url, out string protocol, out string address)
        {
            protocol = null;
            address = null;

            string normalized;
            if (!TryNormalizeMarketplaceUrl(url, out normalized)) return false;

            var uri = new Uri(normalized);
            protocol = uri.Scheme;
            address = uri.Authority + uri.PathAndQuery;
            return !address.Contains("//");
        }

        /// <summary>Switch to the official server and persist the choice.
        /// Returns true when this actually changed the server.</summary>
        public bool SetOfficialServer() => SwitchServer(true, null);

        /// <summary>Switch to a custom server URL and persist it. The address is
        /// canonicalized by <see cref="NormalizeServerUrl"/>; an unusable one is
        /// refused outright and the current server is left alone.
        /// Returns true when this actually changed the server.</summary>
        public bool SetCustomServer(string url)
        {
            string normalized = NormalizeServerUrl(url, out string error);
            if (normalized == null)
            {
                Debug.LogWarning("[GeneKerman] Refused server address: " + error);
                return false;
            }
            return SwitchServer(false, normalized);
        }

        // Backwards-compatible alias: treat a manual URL set as a custom server.
        public void SetServerUrl(string url) => SetCustomServer(url);

        /// <summary>
        /// Canonicalizes a user-typed server address to exactly scheme://host[:port],
        /// or returns null and sets <paramref name="error"/> to a sentence worth showing.
        ///
        /// Anything past the authority — a path, a query string, embedded credentials —
        /// is refused rather than trimmed away. This string is the base of every upstream
        /// request the mod makes, so a stray path segment would silently prefix all of
        /// them, and a user:pass@ would put credentials into KSP.log, which players post
        /// to Discord routinely.
        /// </summary>
        public static string NormalizeServerUrl(string input, out string error)
        {
            error = null;
            string s = (input ?? "").Trim().TrimEnd('/');

            if (s.Length == 0) { error = "Enter a server address."; return null; }
            if (s.Length > 200) { error = "That address is too long."; return null; }
            // Bare "localhost:5022" is what people actually type.
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "http://" + s;

            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri uri))
            { error = "That is not a valid address."; return null; }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            { error = "Only http:// and https:// addresses are supported."; return null; }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            { error = "Remove the username and password from the address."; return null; }

            if (string.IsNullOrEmpty(uri.Host))
            { error = "That address has no host name."; return null; }

            if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            { error = "Use just the host and port, with no path after it."; return null; }

            // GetLeftPart rather than reassembling by hand: it brackets IPv6 literals
            // and drops the port only when it is the scheme's default.
            return uri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>
        /// Points the mod at a different server and moves the session token with it.
        /// Returns true when the address actually changed — callers use that to decide
        /// whether to tear down live state (see GeneKermanMod.OnServerChanged).
        /// </summary>
        private bool SwitchServer(bool official, string url)
        {
            string target = official ? OfficialServerUrl : url;
            bool changed = !string.Equals(target, serverUrl, StringComparison.OrdinalIgnoreCase);

            useOfficialServer = official;
            if (!official) customServerUrl = url;
            serverUrl = target;
            SaveSettings();

            if (!changed) return false;

            // The active token belongs to the server we just left. It is already parked
            // in tokensByServer (SetToken keeps that in step), so this only has to pick
            // up whatever the new server issued us — usually nothing, which correctly
            // leaves the mod unlinked and sends no Authorization header at all.
            sessionToken = tokensByServer.TryGetValue(serverUrl, out string t) ? t : null;
            WriteActiveToken();
            Debug.Log($"[GeneKerman] Server changed to {serverUrl} (linked={IsLinked}).");
            return true;
        }

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

        /// <summary>Turn the rescue emergency freeze on or off and persist the choice.
        /// Only affects wrecks spawned afterwards — crew already frozen stay frozen and
        /// still thaw normally, since abandoning them mid-freeze would strand them in an
        /// LS mod's books.</summary>
        public void SetEmergencyFreezeEnabled(bool enabled)
        {
            emergencyFreezeEnabled = enabled;
            SaveSettings();
        }

        /// <summary>Master opt-out for all data sharing (KSP add-on rule 8.2). When set
        /// false the mod transmits nothing and runs inert until re-enabled.</summary>
        public void SetDataGatheringEnabled(bool enabled)
        {
            dataGatheringEnabled = enabled;
            SaveSettings();
        }

        /// <summary>Switch between the browser UI and the classic in-game windows, and persist it.</summary>
        public void SetWebUiEnabled(bool enabled)
        {
            webUiEnabled = enabled;
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
            gk.AddValue("enableDataGathering", dataGatheringEnabled);
            gk.AddValue("enableWebUi", webUiEnabled);
            gk.AddValue("enableEmergencyFreeze", emergencyFreezeEnabled);
            gk.AddValue("emergencyRationDays", emergencyRationDays);
            gk.AddValue("enablePartSubstitution", partSubstitutionEnabled);
            gk.AddValue("enableTextureTransfer", textureTransferEnabled);
            gk.AddValue("enableFuelConfigTransfer", fuelConfigTransferEnabled);
            gk.AddValue("serverProtocol", protocol);
            gk.AddValue("serverHost", host);
            gk.AddValue("serverPort", port);
            // Split, never as one value — see LoadMarketplaceUrl. The old
            // `marketplaceUrl` key is deliberately no longer written: leaving it
            // behind would keep re-truncating on every load, and load already
            // migrates it.
            string mktProtocol, mktAddress;
            if (!SplitMarketplaceUrl(MarketplaceUrl, out mktProtocol, out mktAddress))
            {
                // Only reachable for a URL with a doubled slash in its path, which
                // cannot be stored in a ConfigNode at all. Say so rather than
                // quietly replacing the player's override with the default.
                Debug.LogWarning("[GeneKerman] Marketplace URL \"" + MarketplaceUrl +
                                 "\" cannot be stored in settings.cfg (a `//` in the path is read as a " +
                                 "comment); persisting the default instead.");
                SplitMarketplaceUrl(DefaultMarketplaceUrl, out mktProtocol, out mktAddress);
            }
            gk.AddValue("marketplaceProtocol", mktProtocol);
            gk.AddValue("marketplaceAddress", mktAddress);
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

            // An install that predates sessions.cfg has a token but no record of which
            // server issued it. It was necessarily the one in settings.cfg, so file it
            // there — otherwise the first server switch would throw it away.
            if (IsLinked && !tokensByServer.ContainsKey(serverUrl))
            {
                tokensByServer[serverUrl] = sessionToken;
                SaveSessions();
            }
        }

        public void SetToken(string token)
        {
            sessionToken = token;
            tokensByServer[serverUrl] = token;
            WriteActiveToken();
            SaveSessions();
            Debug.Log("[GeneKerman] Session token saved.");
        }

        public void ClearToken()
        {
            sessionToken = null;
            tokensByServer.Remove(serverUrl);
            WriteActiveToken();
            SaveSessions();
            Debug.Log("[GeneKerman] Session token cleared.");
        }

        /// <summary>Mirrors the in-memory token to session.token, which stays the single
        /// file holding the *active* session — sessions.cfg is only the parked ones.</summary>
        private void WriteActiveToken()
        {
            try
            {
                if (string.IsNullOrEmpty(sessionToken))
                {
                    if (File.Exists(tokenPath)) File.Delete(tokenPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(tokenPath));
                    File.WriteAllText(tokenPath, sessionToken);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not write session.token: " + e.Message);
            }
        }

        private void LoadSessions()
        {
            tokensByServer.Clear();
            if (!File.Exists(sessionsPath)) return;

            try
            {
                var root = ConfigNode.Load(sessionsPath)?.GetNode("GeneKermanSessions");
                if (root == null) return;

                foreach (var s in root.GetNodes("session"))
                {
                    // Scheme and authority are stored apart because ConfigNode treats //
                    // as a comment delimiter and would eat the rest of a whole URL — the
                    // same reason settings.cfg splits serverHost from serverProtocol.
                    string protocol = s.GetValue("protocol");
                    string address = s.GetValue("address");
                    string token = s.GetValue("token");
                    if (string.IsNullOrEmpty(protocol) || string.IsNullOrEmpty(address) ||
                        string.IsNullOrEmpty(token)) continue;

                    tokensByServer[protocol + "://" + address] = token;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not read sessions.cfg: " + e.Message);
            }
        }

        private void SaveSessions()
        {
            try
            {
                var node = new ConfigNode();
                var root = node.AddNode("GeneKermanSessions");
                foreach (var kv in tokensByServer)
                {
                    int sep = kv.Key.IndexOf("://", StringComparison.Ordinal);
                    if (sep < 0 || string.IsNullOrEmpty(kv.Value)) continue;
                    var s = root.AddNode("session");
                    s.AddValue("protocol", kv.Key.Substring(0, sep));
                    s.AddValue("address", kv.Key.Substring(sep + 3));
                    s.AddValue("token", kv.Value);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(sessionsPath));
                node.Save(sessionsPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not save sessions.cfg: " + e.Message);
            }
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

        /// Post-response gate hook. Handles the server-enforced version block
        /// (426 update_required), the dead-session drop (401) and the device-binding
        /// block (403 device_unverified). Returns true if the response was a gate.
        private bool HandleDeviceGate(long status, string body)
        {
            if (HandleVersionGate(status, body)) return true;
            if (HandleSessionGate(status)) return true;
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

        /// If a response is a 401, this PC's session is finished: the server answers
        /// 401 from one place only (its bearer-token check), so it never means "wrong
        /// code" or "wrong device" — the token is expired, or was revoked by the
        /// user's own "log out of all devices", which invalidates tokens minted before
        /// it. No retry can fix that, so drop straight to the link screen. Left
        /// unhandled, the client sits in a zombie linked state where every action
        /// fails with a generic transport error and the notification socket
        /// reconnects forever, with nothing telling the player to link again.
        /// Returns true if it was a 401.
        private bool HandleSessionGate(long status)
        {
            if (status != 401) return false;
            // Already dropped. A burst of in-flight requests all come back 401
            // together, and IsLinked goes false on the first one, so this is what
            // keeps the unlink (and its popup) to one.
            if (!IsLinked) return true;
            Debug.LogWarning("[GeneKerman] Session rejected by the server (401) — unlinking this PC.");
            if (GeneKermanMod.Instance != null)
                GeneKermanMod.Instance.OnSessionRevoked();
            else
                ClearToken();   // no UI to drive (early startup) — still drop the dead token
            return true;
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
            // Opt-out gate: while data sharing is off, no request leaves this PC.
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
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
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
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
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
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

        // ── Web bridge relay ────────────────────────────────────────────────

        /// <summary>
        /// Forwards one browser request to the API and hands back the raw upstream
        /// status, content type and body, untouched.
        ///
        /// This exists so the loopback bridge (Web/ApiProxy.cs) can serve the in-game
        /// web UI *without the session token ever entering JavaScript* — the page calls
        /// same-origin /api/..., and the token is attached here, in C#.
        ///
        /// It deliberately routes through the same ApplyHeaders / TransmissionBlocked /
        /// HandleDeviceGate path as Get/Post/Delete. Do not "simplify" the proxy by
        /// opening its own HttpWebRequest on a background thread: that would silently
        /// drop the 426 update gate, the 403 device gate, the consent gate and the
        /// device-id header, letting a swapped-out WebUI bundle talk to the server
        /// outside every gate this mod exists to enforce.
        ///
        /// onDone receives (status, contentType, body). status is 0 on a transport
        /// failure and 503 when transmission is blocked by the data-sharing opt-out.
        /// </summary>
        public IEnumerator Relay(string method, string path, string jsonBody,
                                 Action<long, string, string> onDone)
        {
            if (TransmissionBlocked)
            {
                onDone(503, "application/json", "{\"error\":\"transmission_blocked\"}");
                yield break;
            }

            string url = serverUrl + path;
            using (var req = new UnityWebRequest(url, method))
            {
                if (!string.IsNullOrEmpty(jsonBody))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                ApplyHeaders(req);
                req.SetRequestHeader("Accept", "application/json");
                req.timeout = 15;

                yield return req.SendWebRequest();

                string body = req.downloadHandler?.text;
                string contentType = req.GetResponseHeader("Content-Type") ?? "application/json";

                // Gates first: a 426/403 must raise the in-game window even though we
                // also pass the status through to the page.
                HandleDeviceGate(req.responseCode, body);

                if (req.isNetworkError)
                {
                    Debug.LogWarning($"[GeneKerman] Relay {method} {path} transport error: {req.error}");
                    onDone(0, "application/json", "{\"error\":\"upstream_unreachable\"}");
                    yield break;
                }

                onDone(req.responseCode, contentType, body ?? "");
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
            string lifeSupport,
            double lsEnduranceDays,
            int lsCrewCapacity,
            ApiCallback callback)
        {
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
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

            // Life-support flag of the submitted craft (which LS mod it's provisioned for,
            // per-kerbal endurance, crew capacity) — shown on the contract's review embed.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!string.IsNullOrEmpty(lifeSupport))
                form.Add(new MultipartFormDataSection("life_support", lifeSupport));
            if (lsEnduranceDays > 0)
                form.Add(new MultipartFormDataSection("ls_endurance_days", lsEnduranceDays.ToString(inv)));
            if (lsCrewCapacity > 0)
                form.Add(new MultipartFormDataSection("ls_crew_capacity", lsCrewCapacity.ToString()));

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
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
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

        // ── Achievement Hero Shot Upload ────────────────────────────────────

        /// <summary>
        /// Upload a player-composed achievement shot (PNG) for server-side
        /// verification. The server analyses it and grants the matching KSP title
        /// role if it qualifies, returning a human-readable "message" for the
        /// in-game notification.
        /// </summary>
        public IEnumerator UploadAchievementPhoto(
            byte[] pngData, string vesselName, string body,
            string vesselId, string situation, bool review, ApiCallback callback)
        {
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
            string url = serverUrl + "/api/v1/achievement-photo";

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("photo", pngData, "achievement.png", "image/png"),
                new MultipartFormDataSection("vessel_name", vesselName ?? ""),
                new MultipartFormDataSection("body", body ?? ""),
                new MultipartFormDataSection("vessel_id", vesselId ?? ""),
                new MultipartFormDataSection("situation", situation ?? ""),
                // The server treats this string as a bool ("true"/"false").
                new MultipartFormDataSection("review", review ? "true" : "false"),
            };

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                req.timeout = 90;   // Gemini analysis can take a few seconds

                yield return req.SendWebRequest();

                bool ok = !req.isNetworkError && !req.isHttpError;
                callback?.Invoke(ok, req.downloadHandler?.text, req.responseCode);

                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Achievement photo upload failed: {req.error} ({req.responseCode})");
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
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
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

        /// <summary>
        /// File a bug report. The server opens a Discord ticket for it and pings the
        /// bug-report role, so the reply comes back to the player in Discord.
        ///
        /// <paramref name="attachLog"/> is the player's explicit choice, made in the
        /// Tools tab: KSP.log carries their mod list, install paths and system specs,
        /// so it is never attached unless they leave the switch on. It is trimmed to
        /// head+tail here (see DeviceId.GetKspLogCapped) because a modded log is far
        /// larger than anything the API or Discord will accept whole.
        /// </summary>
        public IEnumerator SubmitBugReport(string summary, string details, bool attachLog,
                                           ApiCallback callback)
        {
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
            string url = serverUrl + "/api/v1/bugreport";
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("summary", summary ?? ""),
                new MultipartFormDataSection("details", details ?? ""),
                new MultipartFormDataSection("mod_version", ModVersion.Current ?? ""),
            };
            if (attachLog)
            {
                byte[] logBytes = DeviceId.GetKspLogCapped(LogHeadBytes, LogTailBytes);
                if (logBytes != null && logBytes.Length > 0)
                    form.Add(new MultipartFormFileSection("ksp_log", logBytes, "KSP.log", "text/plain"));
            }

            using (var req = UnityWebRequest.Post(url, form))
            {
                ApplyHeaders(req);
                // Generous: the log is the bulk of it and a player on a slow uplink
                // still deserves to have the report land.
                req.timeout = 120;
                yield return req.SendWebRequest();
                bool ok = !req.isNetworkError && !req.isHttpError;
                callback?.Invoke(ok, req.downloadHandler?.text, req.responseCode);
                HandleDeviceGate(req.responseCode, req.downloadHandler?.text);
                if (!ok)
                    Debug.LogWarning($"[GeneKerman] Bug report failed: {req.error} ({req.responseCode})");
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
                // A 401 here used to be cleared by hand. It is HandleSessionGate's
                // job now, for every endpoint rather than only this one.
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
            string lifeSupport, double lsEnduranceDays, int lsCrewCapacity,
            string recovery, double minDv,
            // Orbit-mode plane/regime requirement. incl < 0 means the issuer asked for
            // no particular plane, and the field is then left off entirely so the server
            // stores its own "any plane" rather than a real 0° (equatorial).
            double incl, double marginIncl, string orbitTypes,
            ApiCallback<Dictionary<string, object>> callback)
        {
            if (TransmissionBlocked) { callback(false, null, "Data sharing is off."); yield break; }
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
                // What has to come back: the crew alone, or the wreck with them. The
                // server derives the wreck's part list from the node uploaded below, so
                // there is nothing extra to send for it.
                new MultipartFormDataSection("recovery", string.IsNullOrEmpty(recovery) ? "crew" : recovery),
                new MultipartFormDataSection("min_dv", minDv.ToString("G17", inv)),
            };
            if (incl >= 0)
            {
                form.Add(new MultipartFormDataSection("inc", incl.ToString("G17", inv)));
                form.Add(new MultipartFormDataSection("margin_inc", marginIncl.ToString("G17", inv)));
            }
            if (!string.IsNullOrEmpty(orbitTypes))
                form.Add(new MultipartFormDataSection("orbit_types", orbitTypes));
            if (!string.IsNullOrEmpty(modlist))
                form.Add(new MultipartFormDataSection("modlist", modlist));
            if (!string.IsNullOrEmpty(rescuePid))
                form.Add(new MultipartFormDataSection("rescue_pid", rescuePid));
            // What the wreck is provisioned for, so the rescuer's client can tell whether
            // its supplies mean anything in their save (and stow rations if not).
            if (!string.IsNullOrEmpty(lifeSupport))
                form.Add(new MultipartFormDataSection("life_support", lifeSupport));
            if (lsEnduranceDays > 0)
                form.Add(new MultipartFormDataSection("ls_endurance_days", lsEnduranceDays.ToString(inv)));
            if (lsCrewCapacity > 0)
                form.Add(new MultipartFormDataSection("ls_crew_capacity", lsCrewCapacity.ToString(inv)));

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
            byte[] blueprintData, byte[] thumbnailData, string mods, string parts,
            string lifeSupport, double lsEnduranceDays, int lsCrewCapacity,
            ApiCallback callback)
        {
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
            string url = serverUrl + "/api/v1/marketplace/list";

            var form = new List<IMultipartFormSection>();

            byte[] compressed = GzipCompress(craftFileData);
            form.Add(new MultipartFormFileSection("craft_file", compressed,
                craftFileName ?? "craft.craft", "application/gzip"));

            // Rendered blueprint image — shown publicly on the Discord listing and in
            // the website's listing detail view.
            if (blueprintData != null && blueprintData.Length > 0)
                form.Add(new MultipartFormFileSection("blueprint", blueprintData,
                    "blueprint.png", "image/png"));

            // Square NW-view thumbnail — the website's listing-card image.
            if (thumbnailData != null && thumbnailData.Length > 0)
                form.Add(new MultipartFormFileSection("thumbnail", thumbnailData,
                    "thumbnail.png", "image/png"));

            // Unity's MultipartFormDataSection throws ArgumentException on an empty
            // body, which would kill this coroutine before the upload (leaving the UI
            // stuck on "listing…"). So skip empty text fields entirely — the server
            // supplies defaults for the optional ones (craft_type="VAB", mods="").
            void AddText(string name, string value)
            {
                if (!string.IsNullOrEmpty(value))
                    form.Add(new MultipartFormDataSection(name, value));
            }

            AddText("craft_name", string.IsNullOrEmpty(craftName) ? "Untitled" : craftName);
            AddText("craft_type", craftType);
            AddText("part_count", partCount.ToString());
            AddText("mass", mass.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddText("cost", cost.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddText("price", price.ToString());
            // Distinct GameData mod folders the craft uses (comma-separated), so the
            // website can filter listings by mod. Empty for stock-only crafts.
            AddText("mods", mods);
            // The craft's exact part names, so the server can warn a buyer about parts
            // they don't have before they pay. Skipped by older clients; the server
            // simply reports "unknown" for a listing that carries none.
            AddText("parts", parts);

            // Life-support flag: which LS mod the craft is provisioned for, how long it
            // lasts per kerbal, and its crew capacity (for the min/max endurance range).
            AddText("life_support", lifeSupport);
            if (lsEnduranceDays > 0)
                AddText("ls_endurance_days", lsEnduranceDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (lsCrewCapacity > 0)
                AddText("ls_crew_capacity", lsCrewCapacity.ToString());

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
        /// <paramref name="blueprintPng"/> is the rendered preview the recipient
        /// judges the offer by — optional, since a render can fail.
        /// </summary>
        public IEnumerator SendCraftToFriend(
            string recipientId, string kind, string craftName,
            byte[] fileData, string fileName, byte[] blueprintPng, ApiCallback callback)
        {
            if (TransmissionBlocked) { callback(false, null, 0); yield break; }
            string url = serverUrl + "/api/v1/craft/send";

            var form = new List<IMultipartFormSection>();
            byte[] compressed = GzipCompress(fileData);
            form.Add(new MultipartFormFileSection("file", compressed,
                fileName ?? "payload.dat", "application/gzip"));
            if (blueprintPng != null && blueprintPng.Length > 0)
                form.Add(new MultipartFormFileSection("blueprint", blueprintPng,
                    "blueprint.png", "image/png"));
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
                    Debug.LogWarning($"[GeneKerman] Download failed ({req.responseCode}): {req.error} — url: {url}");
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
