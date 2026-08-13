/*
 * EventStream.cs – Server-Sent Events push from the game to the browser UI.
 *
 * SSE rather than WebSocket, deliberately: HttpListener.AcceptWebSocketAsync exists
 * in KSP's System.dll metadata but is a NotImplementedException trap in Mono. SSE
 * needs nothing but a chunked response we write to forever, and the browser side is
 * a one-liner (new EventSource("/gk/events")).
 *
 * Mono's chunked encoding was measured to genuinely stream rather than buffer (frames
 * arrived 1.00s apart, in game and out), so SSE stands. If that ever regresses on an
 * older runtime the fallback is a long-poll endpoint with a cursor; the client contract
 * stays the same either way.
 *
 * Each connection owns a dedicated background thread — see Accept() for why it must not
 * be a ThreadPool thread.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Text;
using UnityEngine;

namespace GeneKerman.Web
{
    public sealed class EventStream
    {
        /// <summary>
        /// Comment-only ping. Keeps proxies and the browser from idling the connection
        /// out, and — more usefully — surfaces a dead peer as a write exception so the
        /// client gets cleaned up instead of leaking a thread.
        /// </summary>
        private const int HeartbeatMs = 15_000;

        private sealed class Client
        {
            public readonly BlockingCollection<string> Outbox =
                new BlockingCollection<string>(new ConcurrentQueue<string>(), 256);
        }

        private readonly List<Client> clients = new List<Client>();
        private readonly object gate = new object();

        public int ClientCount { get { lock (gate) return clients.Count; } }

        /// <summary>
        /// Called from the main thread (GeneKermanMod), so notifications tee to the
        /// browser without disturbing the in-game toast path.
        /// </summary>
        public void Broadcast(string eventName, string jsonPayload)
        {
            string frame = $"event: {eventName}\ndata: {jsonPayload}\n\n";
            lock (gate)
            {
                foreach (var c in clients)
                {
                    // Drop rather than block: a stalled reader must never back-pressure
                    // the game's main thread.
                    if (!c.Outbox.TryAdd(frame))
                        Debug.LogWarning("[GeneKerman] SSE outbox full; dropped an event.");
                }
            }
        }

        /// <summary>
        /// Takes over an SSE request. Returns immediately.
        ///
        /// The pump loop below blocks for the entire life of the connection, which must
        /// NOT happen on a ThreadPool thread: the pool starts at roughly one thread per
        /// core and grows slowly, KSP shares it, and a couple of open browser tabs would
        /// permanently retire several of its threads. The symptom would be the game
        /// getting mysteriously sluggish — miserable to diagnose. A dedicated background
        /// thread per connection costs ~1 MB of stack and is honest about what it does.
        /// </summary>
        public void Accept(HttpListenerContext ctx)
        {
            var t = new Thread(() => Pump(ctx))
            {
                IsBackground = true, // must not keep the process alive on quit
                Name = "GK-SSE",
            };
            t.Start();
        }

        private void Pump(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            var client = new Client();

            try
            {
                res.StatusCode = 200;
                res.ContentType = "text/event-stream";
                res.Headers["Cache-Control"] = "no-cache";
                res.Headers["X-Accel-Buffering"] = "no"; // harmless here, matters behind a proxy
                LocalServer.ApplySecurityHeaders(res, false);

                // Chunked and ContentLength64 are mutually exclusive — setting the latter
                // (even to 0) turns the stream into a fixed-length response and SSE dies.
                res.SendChunked = true;
                res.KeepAlive = true;

                lock (gate) clients.Add(client);

                // Retry hint + an immediate comment so the browser fires onopen right
                // away rather than after the first real event.
                Write(res, ": connected\nretry: 3000\n\n");

                while (true)
                {
                    string frame;
                    if (client.Outbox.TryTake(out frame, HeartbeatMs))
                        Write(res, frame);
                    else
                        Write(res, ": ping\n\n");
                }
            }
            catch (Exception)
            {
                // Normal termination: the tab closed, or CloseAll() completed the outbox.
            }
            finally
            {
                lock (gate) clients.Remove(client);
                try { client.Outbox.Dispose(); } catch { }
                try { res.Close(); } catch { }
            }
        }

        private static void Write(HttpListenerResponse res, string s)
        {
            byte[] buf = Encoding.UTF8.GetBytes(s);
            res.OutputStream.Write(buf, 0, buf.Length);
            // Without this the frame sits in Mono's buffer and the whole point is lost.
            res.OutputStream.Flush();
        }

        /// <summary>Unblocks every parked reader so shutdown doesn't wait on heartbeats.</summary>
        public void CloseAll()
        {
            lock (gate)
            {
                foreach (var c in clients)
                {
                    try { c.Outbox.CompleteAdding(); } catch { }
                }
            }
        }
    }
}
