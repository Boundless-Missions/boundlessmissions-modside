/*
 * BridgeAuth.cs – Who is allowed to talk to the loopback bridge.
 *
 * The bridge holds a 30-day session token and will attach it to anything it proxies,
 * so "only our page may call this" has to be airtight. Five layers, none sufficient
 * alone (layers 1, 2 and 5 live in LocalServer; this file owns 3 and 4):
 *
 *   1. Bound to 127.0.0.1 only — nothing on the LAN can reach it.
 *   2. Random ephemeral port per session — defence in depth ONLY. A local process
 *      can scan 65k ports in under a second; never treat this as security.
 *   3. One-time launch nonce, 15s TTL, in the URL we hand to the browser. ← here
 *   4. HttpOnly SameSite=Strict session cookie + a CSRF token in a custom header. ← here
 *   5. Exact Host match (DNS-rebinding defence) + Origin + Sec-Fetch-Site.
 *
 * Known residual risk: Application.OpenURL shells out to xdg-open on Linux, so the
 * launch URL — nonce included — is briefly visible in /proc and `ps aux`. The 15s TTL
 * plus single use plus the browser consuming it in ~200ms makes the window narrow, and
 * a hostile local user on the same account could already just read PluginData/session.token.
 * Documented, not pretended away.
 */

using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace GeneKerman.Web
{
    public sealed class BridgeAuth
    {
        private const string CookieName = "gk";
        public const string CsrfHeader = "X-GK-CSRF";

        /// <summary>Long enough to survive a slow browser launch, short enough that a
        /// leaked process listing is stale before it is useful.</summary>
        private const int NonceTtlMs = 15_000;

        private string sessionKey;
        private string csrfToken;

        private string launchNonce;
        private int nonceTicks;
        private int nonceConsumed; // Interlocked: exactly one caller may burn it.

        public string CsrfToken => csrfToken;

        /// <summary>
        /// Starts a fresh UI session and returns the first launch nonce. Rotating the
        /// keys on every server Start() means a nonce or cookie from a previous session
        /// is worthless.
        /// </summary>
        public string BeginSession()
        {
            sessionKey = RandomToken(32);
            csrfToken = RandomToken(16);
            return IssueNonce();
        }

        /// <summary>
        /// Mints a replacement launch nonce without disturbing the session keys, so
        /// "Reopen in browser" can hand a second tab a valid handshake while any tab
        /// already open keeps working. Only the newest nonce is ever valid.
        /// </summary>
        public string IssueNonce()
        {
            launchNonce = RandomToken(16);
            nonceTicks = Environment.TickCount;
            Interlocked.Exchange(ref nonceConsumed, 0);
            return launchNonce;
        }

        public void EndSession()
        {
            sessionKey = null;
            csrfToken = null;
            launchNonce = null;
            Interlocked.Exchange(ref nonceConsumed, 1);
        }

        /// <summary>
        /// Burns the launch nonce and returns the Set-Cookie value for the session, or
        /// null if the nonce is wrong, expired, or already used. Single use is enforced
        /// with Interlocked, so a second tab racing the first loses.
        /// </summary>
        public string RedeemNonce(string presented, out string csrf)
        {
            csrf = null;
            if (string.IsNullOrEmpty(presented) || launchNonce == null) return null;

            // No live session (EndSession ran, or BeginSession never did): refuse rather
            // than mint a cookie for a null key. Nothing would validate against it — see
            // HasValidCookie — but handing one out at all invites confusion later.
            if (sessionKey == null || csrfToken == null) return null;

            if (unchecked(Environment.TickCount - nonceTicks) > NonceTtlMs) return null;
            if (!FixedTimeEquals(presented, launchNonce)) return null;
            if (Interlocked.Exchange(ref nonceConsumed, 1) != 0) return null;

            csrf = csrfToken;
            // No Secure attribute: this is plain http on loopback, and Secure would stop
            // the browser storing the cookie at all. SameSite=Strict is what makes the
            // cookie unreachable from any other site.
            return $"{CookieName}={sessionKey}; Path=/; HttpOnly; SameSite=Strict";
        }

        /// <summary>
        /// Full check for /gk/* and /api/*: valid session cookie AND matching CSRF header.
        /// </summary>
        public bool IsAuthorized(HttpListenerRequest req) =>
            HasValidCookie(req) && FixedTimeEquals(req.Headers[CsrfHeader], csrfToken);

        /// <summary>
        /// Cookie-only check, for GET /gk/events.
        ///
        /// EventSource cannot set custom headers, so SSE can't carry the CSRF token.
        /// That is safe here because the cookie is SameSite=Strict (never sent from
        /// another site) and LocalServer has already enforced Host/Origin/Sec-Fetch-Site,
        /// so a cross-origin EventSource arrives with no cookie and is rejected.
        /// Do not widen this to non-GET routes.
        /// </summary>
        public bool IsAuthorizedCookieOnly(HttpListenerRequest req) => HasValidCookie(req);

        private bool HasValidCookie(HttpListenerRequest req)
        {
            if (sessionKey == null) return false;
            var cookie = req.Cookies[CookieName];
            return cookie != null && FixedTimeEquals(cookie.Value, sessionKey);
        }

        // ── Primitives ──────────────────────────────────────────────────────

        /// <summary>Base64url so the value is safe in a cookie, a header and a URL.</summary>
        private static string RandomToken(int bytes)
        {
            var buf = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(buf);
            return Convert.ToBase64String(buf)
                          .TrimEnd('=')
                          .Replace('+', '-')
                          .Replace('/', '_');
        }

        /// <summary>
        /// Length-independent comparison. Overkill against a local attacker who can
        /// simply retry, but these are short secrets compared on every request and
        /// constant time costs nothing here.
        /// </summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
