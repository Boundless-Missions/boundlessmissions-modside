/*
 * JobRegistry.cs – Tracks game operations that outlive an HTTP request.
 *
 * Most bridge routes finish in milliseconds and can just block the request thread until
 * MainThreadQueue runs them. Craft installs cannot: they make two network round trips,
 * spawn vessels, and write files, and they must survive a scene load. Holding an HTTP
 * request open for that means a 30s timeout and a browser tab that looks hung.
 *
 * So those routes answer 202 with a job id immediately, and the page follows the job —
 * either by listening on SSE (the completion is broadcast) or by polling /gk/jobs/{id}
 * if the stream dropped.
 *
 * Records are written on the main thread and read from request threads, hence the
 * concurrent dictionary.
 */

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace GeneKerman.Web
{
    public sealed class JobRegistry
    {
        /// <summary>
        /// How long a finished job stays readable. Long enough that a page reloading
        /// mid-install still learns the outcome; short enough that the dictionary cannot
        /// grow without bound across a long session.
        /// </summary>
        private const int RetentionMs = 10 * 60 * 1000;

        public sealed class Record
        {
            public string Id;
            public string State;    // "running" | "done" | "error"
            public string Message;
            public int Stamp;       // Environment.TickCount at last write

            public string ToJson() =>
                "{\"id\":" + JobResult.Quote(Id) +
                ",\"state\":" + JobResult.Quote(State) +
                ",\"message\":" + JobResult.Quote(Message ?? "") + "}";
        }

        private readonly ConcurrentDictionary<string, Record> jobs =
            new ConcurrentDictionary<string, Record>();

        public string Begin()
        {
            Expire();
            var r = new Record
            {
                Id = NewId(),
                State = "running",
                Message = "",
                Stamp = Environment.TickCount,
            };
            jobs[r.Id] = r;
            return r.Id;
        }

        /// <summary>Main thread, from the coroutine that owns the job.</summary>
        public void Complete(string id, bool ok, string message)
        {
            if (id == null) return;
            if (!jobs.TryGetValue(id, out Record r)) return;
            r.State = ok ? "done" : "error";
            r.Message = message ?? "";
            r.Stamp = Environment.TickCount;
        }

        public Record Get(string id) =>
            id != null && jobs.TryGetValue(id, out Record r) ? r : null;

        private void Expire()
        {
            int now = Environment.TickCount;
            foreach (var kv in jobs)
            {
                // Only finished jobs expire — a long install must never be swept out
                // from under the page that is still watching it.
                if (kv.Value.State != "running" && unchecked(now - kv.Value.Stamp) > RetentionMs)
                    jobs.TryRemove(kv.Key, out _);
            }
        }

        private static string NewId()
        {
            var buf = new byte[12];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(buf);
            return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
