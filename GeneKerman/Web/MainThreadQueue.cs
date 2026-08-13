/*
 * MainThreadQueue.cs – Marshals work from HTTP request threads onto Unity's main thread.
 *
 * LocalServer answers requests on ThreadPool threads, but every KSP/Unity API
 * (FlightGlobals, HighLogic, VesselDataCollector, coroutines) is main-thread-only.
 * A request thread Posts a job and blocks; GeneKermanMod.Update() drains the queue,
 * runs the job, and signals the waiter.
 *
 * The direction matters and is not symmetric: blocking a ThreadPool thread is fine,
 * blocking the Unity main thread would freeze the game. Never invert this.
 *
 * Same shape as NotificationSocket's ConcurrentQueue + main-thread Tick(), which
 * already proves the pattern in this codebase.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace GeneKerman.Web
{
    /// <summary>What a job hands back to the waiting request thread.</summary>
    public sealed class JobResult
    {
        public int Status = 200;
        public string ContentType = "application/json";
        public string Body = "";

        /// <summary>
        /// Set instead of <see cref="Body"/> for binary responses (the image proxy).
        /// When non-null the caller must write these bytes, not the string.
        /// </summary>
        public byte[] Bytes;

        public static JobResult Json(string body) =>
            new JobResult { Status = 200, ContentType = "application/json", Body = body };

        public static JobResult Error(int status, string message) =>
            new JobResult
            {
                Status = status,
                ContentType = "application/json",
                Body = "{\"error\":" + Quote(message) + "}",
            };

        internal static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// One unit of main-thread work. <see cref="Work"/> receives a completion callback
    /// so a job may finish in the same frame (a plain KSP read) or many frames later
    /// (a coroutine), without the queue caring which.
    /// </summary>
    public sealed class Job
    {
        public Action<Action<JobResult>> Work;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public JobResult Result;

        /// <summary>
        /// Set by the request thread when it gives up waiting. The main thread checks
        /// this before running the job so a timed-out request costs nothing, and the
        /// completion callback drops the result instead of touching a dead response.
        /// </summary>
        public volatile bool Abandoned;

        private int completed; // Interlocked guard: a coroutine may call back twice on error paths.

        internal void Complete(JobResult r)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            Result = r;
            try { Done.Set(); } catch (ObjectDisposedException) { /* waiter already gone */ }
        }
    }

    public sealed class MainThreadQueue
    {
        /// <summary>
        /// How long a request thread waits before giving up. Deliberately generous:
        /// Update() does not run during a KSP scene load, which is 5–40s on a modded
        /// install, and every in-flight request stalls for that whole time.
        /// </summary>
        public const int JobTimeoutMs = 30_000;

        /// <summary>Jobs run per frame. Bounds the cost of a request burst on frame time.</summary>
        private const int BudgetPerFrame = 32;

        private readonly ConcurrentQueue<Job> pending = new ConcurrentQueue<Job>();

        /// <summary>
        /// Environment.TickCount (not Time.*, which is main-thread-only) stamped on every
        /// Pump. Request threads read it to tell "the game is loading" from "the bridge is
        /// dead", so the page can show a loading state instead of spraying failed requests.
        /// </summary>
        private int lastPumpTicks = Environment.TickCount;

        public int MillisSinceLastPump => unchecked(Environment.TickCount - lastPumpTicks);

        /// <summary>Called from a request thread. Blocks until the job completes or times out.</summary>
        public JobResult Run(Action<Action<JobResult>> work)
        {
            var job = new Job { Work = work };
            pending.Enqueue(job);

            if (!job.Done.Wait(JobTimeoutMs))
            {
                job.Abandoned = true;
                return JobResult.Error(504, "Game is busy (loading a scene?). Retry.");
            }
            return job.Result ?? JobResult.Error(500, "Job produced no result.");
        }

        /// <summary>Convenience for jobs that finish synchronously on the main thread.</summary>
        public JobResult RunSync(Func<JobResult> work) => Run(cb => cb(work()));

        /// <summary>
        /// Wraps a coroutine so the queue can run multi-frame work. The caller supplies a
        /// routine that produces the result; we yield it, then complete the job.
        /// </summary>
        public static Action<Action<JobResult>> Coroutine(Func<Action<JobResult>, IEnumerator> routine) =>
            cb => GeneKermanMod.Instance.RunCoroutine(routine(cb));

        /// <summary>
        /// Called from GeneKermanMod.Update(), on the main thread only — never from
        /// OnGUI(), which runs several times per frame.
        /// </summary>
        public void Pump()
        {
            lastPumpTicks = Environment.TickCount;

            int budget = BudgetPerFrame;
            while (budget-- > 0 && pending.TryDequeue(out var job))
            {
                if (job.Abandoned) continue;
                try
                {
                    job.Work(job.Complete);
                }
                catch (Exception e)
                {
                    // A throwing job must never take the frame — or the queue — down with it.
                    Debug.LogError("[GeneKerman] Web job threw: " + e);
                    job.Complete(JobResult.Error(500, "Internal error."));
                }
            }
        }

        /// <summary>Fails every waiter so no request thread hangs for the full timeout on shutdown.</summary>
        public void DrainAndFail()
        {
            while (pending.TryDequeue(out var job))
                job.Complete(JobResult.Error(503, "Bridge shutting down."));
        }
    }
}
