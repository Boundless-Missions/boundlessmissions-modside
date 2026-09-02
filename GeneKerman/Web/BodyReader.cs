using System;
using System.IO;
using System.Net;
using System.Text;

namespace GeneKerman.Web
{
    /// <summary>Reads a request body with a hard ceiling and a read timeout.
    ///
    /// One reader for both listeners, because the asymmetry it replaces looked like an
    /// oversight rather than a decision: <see cref="ApiProxy"/> capped its body twice
    /// over (before the read and during it), while every body read in
    /// <see cref="GkRoutes"/> used a bare <c>StreamReader.ReadToEnd()</c> — including
    /// <c>POST /gk/session</c>, which is the ONE mutating route reachable with no
    /// credential at all.
    ///
    /// Two costs, both borne by a thread from the pool KSP itself uses. The string grows
    /// to whatever length the caller sends. And a body dribbled a byte at a time parks
    /// the thread for as long as the socket stays open — the failure
    /// <c>EventStream</c>'s own comment calls out as "the game getting mysteriously
    /// sluggish, miserable to diagnose". Hence the timeout as well as the cap: a cap
    /// alone does not bound TIME.
    ///
    /// Not reachable from a web page (the Origin / Sec-Fetch-Site checks refuse a
    /// cross-origin POST before the body is touched), so the caller is another local
    /// process. That is a real caller — the port is trivially scannable — and it is
    /// precisely the one that holds no credential.
    /// </summary>
    internal static class BodyReader
    {
        /// <summary>Generous for every body this surface actually takes: the largest is a
        /// contract form of a few hundred bytes. Deliberately far below ApiProxy's 1 MB,
        /// which has to carry an upload.</summary>
        internal const int DefaultMaxBytes = 64 * 1024;

        /// <summary>Long enough for a slow loopback write, short enough that a stalled
        /// client cannot hold a pool thread. Loopback has no network latency to allow for.</summary>
        private const int ReadTimeoutMs = 10_000;

        /// <summary>The body as UTF-8 text, or null when there is none. Returns null
        /// rather than throwing when the body is oversized or stalls: every caller here
        /// treats a null/unparseable body as a failed request, which is the same answer
        /// and does not need a second error path.</summary>
        internal static string Read(HttpListenerRequest req, int maxBytes = DefaultMaxBytes)
        {
            if (req == null || !req.HasEntityBody) return null;
            if (req.ContentLength64 > maxBytes) return null;

            try
            {
                // Mono's HttpListener stream may not support timeouts; ask, and carry on
                // with the size cap alone if it refuses.
                try { req.InputStream.ReadTimeout = ReadTimeoutMs; }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }

                using (var ms = new MemoryStream())
                {
                    var buf = new byte[8192];
                    int total = 0, read;
                    while ((read = req.InputStream.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += read;
                        // Re-checked while reading: ContentLength64 is client-supplied,
                        // and a chunked request does not set it at all.
                        if (total > maxBytes) return null;
                        ms.Write(buf, 0, read);
                    }
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (Exception)
            {
                // A timed-out or broken read is a failed request, not a crash.
                return null;
            }
        }
    }
}
