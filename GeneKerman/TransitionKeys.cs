using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Security.Cryptography;

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

        /// <summary>
        /// The `fetchedAtWc` of the key set we have already spent a re-fetch on
        /// after a failed verification, so each set buys exactly one.
        /// </summary>
        private static double retriedForFetchAt = double.NegativeInfinity;

        /// <summary>An RSA public key, as JWKS carries it: modulus and exponent.</summary>
        private struct PubKey
        {
            public byte[] Modulus;
            public byte[] Exponent;
        }

        /// <summary>server_id -> public key. An empty id is the account service's own key.</summary>
        private readonly Dictionary<string, PubKey> keys = new Dictionary<string, PubKey>();

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

        /// <summary>
        /// Drop a cached key set that has just failed to verify a token, and go
        /// and get the current one. Returns whether a refresh was actually started.
        ///
        /// **A rotated key is indistinguishable from a forged token here**, and
        /// that is the whole problem: both present as a signature that does not
        /// check out. The set is cached for six hours while keys rotate on ninety
        /// days, so the rotation window is narrow — but landing inside it used to
        /// mean every sanctioned transition for the rest of the session came back
        /// `Rejected`, and the symptom (craft tainted by the watchdog for moves
        /// that were entirely legitimate) points nowhere near a stale key. It was
        /// hit routinely in the dev rig, where restarting the stack mints a new
        /// signing key and only restarting KSP cleared it.
        ///
        /// Two things keep this from being a lever. Each fetched set buys **one**
        /// retry, so a genuinely forged token cannot loop: the second failure
        /// against the same set is a refusal and stays one. And the caller is told
        /// `Pending`, never `Accepted` — the transition is *held*, the nonce is
        /// not spent, and a forger gains a delay rather than a sanction. Failing
        /// closed is unchanged; this only stops us failing closed permanently on
        /// the strength of a key we should have thrown away.
        /// </summary>
        internal static bool RetryOnVerifyFailure()
        {
            if (current == null) return false;
            if (fetchedAtWc == retriedForFetchAt) return false;
            retriedForFetchAt = fetchedAtWc;
            current = null;
            fetchedAtWc = 0.0;
            // The one retry that matters must not be swallowed by the ordinary
            // backoff; the once-per-set guard above is what bounds it instead.
            lastAttemptWc = 0.0;
            Debug.Log("[GeneKerman] a transition token failed to verify; "
                    + "refetching the signing keys in case they rotated");
            BeginFetch();
            return true;
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

                // RSA only, and an Ed25519 key in this set is skipped rather than
                // half-understood. The two token families deliberately use
                // different algorithms (see Verify), so an OKP key appearing here
                // is a player-token key that this mod has no business verifying.
                if (Str(k, "kty") != "RSA") continue;
                string alg = Str(k, "alg");
                if (alg.Length > 0 && alg != "RS256") continue;

                string n = Str(k, "n"), e = Str(k, "e");
                if (n.Length == 0 || e.Length == 0) continue;
                string owner = Str(k, "server_id");      // "" = the account service's own
                try { set.keys[owner] = new PubKey { Modulus = Base64Url(n), Exponent = Base64Url(e) }; }
                catch { continue; }
            }
            return set;
        }

        /// <summary>
        /// Check a payload's signature. **RS256** — RSA PKCS#1 v1.5 over SHA-256.
        ///
        /// Not the Ed25519 the player tokens use, and the difference is
        /// deliberate rather than an inconsistency waiting to be tidied up. The
        /// two token families have different verifiers with different
        /// capabilities: a player token is verified by a Python game server,
        /// which has Ed25519 readily; a transition token is verified *here*, in
        /// KSP's Mono, which carries no Ed25519 anywhere in `Managed/`. Matching
        /// the algorithms would mean shipping a signature implementation into a
        /// security-boundary hook, which is the wrong trade — and it would buy
        /// nothing, because both of the usual arguments run backwards on this
        /// path. Verification is the only operation that happens here, and RSA
        /// with e=65537 verifies *faster* than Ed25519; and the token never
        /// leaves the process, so its size is irrelevant.
        ///
        /// PKCS#1 v1.5 rather than PSS because `RSACryptoServiceProvider` is what
        /// this runtime has and it does v1.5; PSS needs `RSACng`, which it does
        /// not. That makes the wire format exactly JWS `RS256`, which the signing
        /// side gets from any standard library.
        ///
        /// The key is selected by the token's own `srv` field, which is safe only
        /// because the *set* of keys came from the pinned origin: a token naming a
        /// server we have no published key for verifies against nothing and is
        /// refused, so naming a server is a lookup, never an assertion.
        /// </summary>
        internal bool Verify(string payload, string signature, string serverId)
        {
            PubKey key;
            if (!keys.TryGetValue(serverId ?? "", out key)) return false;
            if (key.Modulus == null || key.Exponent == null) return false;

            byte[] sig;
            try { sig = Base64Url(signature); }
            catch { return false; }

            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = key.Modulus,
                        Exponent = key.Exponent,
                    });
                    // The signed bytes are the canonical payload as UTF-8 — the
                    // exact string that was parsed, never a re-serialisation of
                    // the parsed fields, so a parser disagreement can never
                    // become a signature that checks out over different data.
                    byte[] data = Encoding.UTF8.GetBytes(payload);
                    return rsa.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), sig);
                }
            }
            catch (CryptographicException)
            {
                // A malformed key or signature is a refusal, not an exception the
                // caller has to handle: every failure here means "judge it as you
                // would have without a token".
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] transition signature check failed: " + ex.Message);
                return false;
            }
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
