using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace GeneKerman
{
    /// <summary>
    /// The public keys `SanctionedTransition` verifies against, fetched lazily
    /// from a compiled-in origin and cached.
    ///
    /// Lazy on purpose. A standalone install never receives a token, so it never
    /// calls this, so it never fetches a key and never holds one — the mod's
    /// inertness without the multiplayer layer is a consequence of the shape
    /// rather than a check somebody has to remember. It also means a player who
    /// never touches multiplayer is never making a request they did not ask for.
    ///
    /// The origin is pinned in the DLL (`SanctionedTransition.KeyOrigin`), not
    /// read from `settings.cfg`. That file is player-editable, which is correct
    /// for an API host and wrong for a trust root: a key fetched from a host the
    /// player chose would let anyone point verification at a server they control
    /// and sanction their own teleports. What rotates is the key; the place it
    /// comes from does not, so that is the part that can be nailed down.
    /// </summary>
    internal class TransitionKeys
    {
        /// <summary>How long a fetched key set is trusted before refetching. Keys rotate on 90 days.</summary>
        private const double CacheSeconds = 6 * 60 * 60;

        /// <summary>Backoff after a failed fetch, so a dead endpoint is not hammered per transition.</summary>
        private const double RetrySeconds = 20.0;

        private static TransitionKeys current;
        private static double fetchedAtWc;
        private static double lastAttemptWc;
        private static bool fetching;

        /// <summary>server_id -> public key material. An empty id is the account service's own key.</summary>
        private readonly Dictionary<string, byte[]> keys = new Dictionary<string, byte[]>();

        /// <summary>The cached key set, or null if none is usable yet.</summary>
        internal static TransitionKeys Get()
        {
            if (current == null) return null;
            if (Now() - fetchedAtWc > CacheSeconds) return null;
            return current;
        }

        /// <summary>
        /// Start a fetch if one is not already running and the backoff has passed.
        ///
        /// Never blocks: the caller is told `Pending` and holds its transition.
        /// Blocking here would stall the game on a network round trip at exactly
        /// the moment a docking snap is being applied.
        /// </summary>
        internal static void BeginFetch()
        {
            if (fetching) return;
            double now = Now();
            if (now - lastAttemptWc < RetrySeconds) return;
            lastAttemptWc = now;
            fetching = true;
            try { Runner.Ensure().StartCoroutine(Fetch()); }
            catch (Exception ex)
            {
                fetching = false;
                Debug.LogWarning("[GeneKerman] transition key fetch could not start: " + ex.Message);
            }
        }

        private static IEnumerator Fetch()
        {
            UnityWebRequest req = null;
            try { req = UnityWebRequest.Get(SanctionedTransition.KeyOrigin); }
            catch (Exception ex)
            {
                fetching = false;
                Debug.LogWarning("[GeneKerman] transition key request failed to build: " + ex.Message);
                yield break;
            }

            using (req)
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                bool failed;
#if UNITY_2020_1_OR_NEWER
                failed = req.result != UnityWebRequest.Result.Success;
#else
                failed = req.isNetworkError || req.isHttpError;
#endif
                if (failed)
                {
                    fetching = false;
                    // Not an error the player needs to see: the transition is held
                    // and retried, and if it never succeeds the vessel is judged
                    // exactly as it would have been with no multiplayer at all.
                    Debug.Log("[GeneKerman] transition key fetch failed: " + req.error);
                    yield break;
                }

                try
                {
                    var parsed = ParseJwks(req.downloadHandler.text);
                    if (parsed != null && parsed.keys.Count > 0)
                    {
                        current = parsed;
                        fetchedAtWc = Now();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[GeneKerman] transition key parse failed: " + ex.Message);
                }
                fetching = false;
            }
        }

        private static TransitionKeys ParseJwks(string json)
        {
            // The JWKS shape is the account service's (`data/mp_keys.py::jwks`).
            // Deliberately tolerant about unknown fields and strict about the ones
            // it uses: a key it cannot fully understand is skipped rather than
            // guessed at, because a mis-parsed key verifies nothing correctly but
            // may verify something incorrectly.
            var set = new TransitionKeys();
            var root = MiniJSON.Deserialize(json) as Dictionary<string, object>;
            if (root == null) return null;
            object rawKeys;
            if (!root.TryGetValue("keys", out rawKeys)) return null;
            var list = rawKeys as List<object>;
            if (list == null) return null;

            foreach (object o in list)
            {
                var k = o as Dictionary<string, object>;
                if (k == null) continue;
                string use = Str(k, "use");
                if (use.Length > 0 && use != "sig") continue;
                string material = Str(k, "x");           // OKP/Ed25519 public key
                if (material.Length == 0) material = Str(k, "n");   // RSA modulus
                if (material.Length == 0) continue;
                string owner = Str(k, "server_id");      // "" = the account service's own
                byte[] bytes;
                try { bytes = Base64Url(material); }
                catch { continue; }
                set.keys[owner] = bytes;
            }
            return set;
        }

        /// <summary>
        /// Check a payload's signature.
        ///
        /// **This is the one seam the signature algorithm decision lands in, and
        /// it is unresolved — so it refuses everything.**
        ///
        /// The reason is a runtime fact rather than a preference: KSP's Mono
        /// carries no Ed25519 anywhere in `KSP_x64_Data/Managed`, and no
        /// instance here has BouncyCastle or a NaCl port either. What it does
        /// have is `RSACryptoServiceProvider.VerifyData`, `RSAParameters` and
        /// `SHA256Managed`. So verifying an Ed25519 signature in this process
        /// means shipping an implementation of it; verifying an RSA one means
        /// calling what is already present.
        ///
        /// Until that is settled this returns false, which is the safe direction
        /// and not a placeholder that could be mistaken for working code: every
        /// token is rejected, `Submit` reports `Rejected`, and the watchdog judges
        /// every transition exactly as it does today. The mod's behaviour is
        /// therefore identical to its behaviour before this file existed — which
        /// is precisely what "inert" is supposed to mean, and what makes it safe
        /// to have merged this far ahead of the primitive.
        /// </summary>
        internal bool Verify(string payload, string signature, string serverId)
        {
            byte[] key;
            if (!keys.TryGetValue(serverId ?? "", out key) || key == null) return false;
            return false;
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        internal static byte[] Base64Url(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
                case 1: throw new FormatException("bad base64url length");
            }
            return Convert.FromBase64String(t);
        }

        private static double Now()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// A host for the fetch coroutine.
        ///
        /// Created on demand and kept across scenes. It exists because a fetch
        /// needs a MonoBehaviour and this class must not reach into
        /// `GeneKermanMod` — the hook has to work whether or not the player is
        /// linked, and borrowing the mod singleton would tie key verification to
        /// an identity that has nothing to do with it.
        /// </summary>
        private class Runner : MonoBehaviour
        {
            private static Runner instance;

            internal static Runner Ensure()
            {
                if (instance != null) return instance;
                var go = new GameObject("GeneKerman.TransitionKeys");
                UnityEngine.Object.DontDestroyOnLoad(go);
                instance = go.AddComponent<Runner>();
                return instance;
            }
        }
    }
}
