/*
 * StreamerMode.cs – Answers one question: is broadcasting software running on
 * this PC right now?
 *
 * The player lists (quicksend, contract creation) draw a Discord avatar and the
 * player's corporation under their display name. That is exactly the material a
 * streamer does not want on screen — someone else's face, and a corp name that is
 * often a real name with "Space Agency" after it. `hidePlayerDetails` in
 * settings.cfg turns both off by hand; this file is the second way to turn them
 * off, the one that does not depend on remembering before going live.
 *
 * ── How Discord does it, and why we do the same
 *
 * Discord's Streamer Mode auto-toggle is process-name matching: the client already
 * enumerates running processes for game detection, and streamer mode reuses that
 * list, matching it against a handful of known broadcasting apps (OBS, XSplit,
 * Streamlabs). It does not hook the capture APIs and it cannot tell whether you are
 * actually live — "OBS is open" is the whole signal.
 *
 * We copy that because there is nothing better to copy. No OS exposes "this window
 * is being captured" (a capture is a screen read, and screen reads are not
 * announced), so any detector is a heuristic over what is *running*. The honest
 * consequence is stated in the settings screen: this is "OBS is open", not "you are
 * live", which is why the manual switch stays and this one only ever turns hiding
 * *on* top of it.
 *
 * ── Three scans, because one is not enough on Linux
 *
 *  1. The managed process list (System.Diagnostics.Process). Covers Windows,
 *     native Linux and macOS.
 *  2. A /proc scan. On a Proton install — which is how KSP runs on this project's
 *     dev machines — scan 1 sees only what lives inside the Wine prefix, and OBS
 *     is a native Linux program outside it. It is invisible there and visible in
 *     the host's /proc, which Wine hands us as `Z:\proc` (Z: maps to /). Native
 *     Linux reads plain `/proc`. On Windows neither path exists and the scan is
 *     skipped. Reads `comm` rather than `cmdline`: one short line per process.
 *  3. Our own loaded modules. OBS's Game Capture injects `graphics-hook64.dll`
 *     into what it captures, so finding it in KSP's own module list is the one
 *     signal here that means "OBS is capturing *this game*" rather than "OBS is
 *     open". It also survives a renamed or portable OBS build, which scan 1 would
 *     miss. Windows/Proton only; Linux OBS captures through the compositor and
 *     injects nothing.
 *
 * Each scan is tried only when the one before it found nothing, so the common case
 * (a hit in the process list, or streamer mode switched off) costs the least.
 *
 * ── Two rules this file must keep
 *
 * **Nothing is scanned unless the switch is on.** Reading the list of programs
 * somebody is running is itself an intrusion, so it is not something the mod does
 * quietly in the background on the chance it might be useful. `Tick` returns before
 * touching anything when `StreamerModeEnabled` is false, and the settings copy says
 * so.
 *
 * **It runs off the main thread.** Enumerating processes blocks for as long as the
 * OS takes, and under Wine scan 2 is a few hundred file reads; on the main thread
 * that is a visible hitch every few seconds. `Tick` only schedules — the result
 * lands in a field a later frame reads.
 *
 * Unlike the cheat watchdog, this one prefers **false positives**: hiding avatars
 * that did not need hiding costs nothing, while missing a detection puts somebody's
 * face on a stream. Nothing here is ever reported to the server — it decides what
 * this PC draws, and stops there.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;

namespace GeneKerman
{
    public static class StreamerMode
    {
        /// <summary>How often the scan runs while streamer mode is on. Fast enough
        /// that opening OBS and alt-tabbing back finds the sidebar already hidden,
        /// slow enough that the cost never matters.</summary>
        private const float ScanIntervalSeconds = 5f;

        /// <summary>`/proc/<pid>/comm` is capped at 15 characters, so a longer name
        /// arrives cut short ("Streamlabs Desk"). Only a candidate that is actually
        /// at the cap may match a table entry by prefix — otherwise "obs" would
        /// match anything beginning with it.</summary>
        private const int CommNameCap = 15;

        /// <summary>
        /// Process names → the name to show the player. Keys are lower-case and
        /// without the `.exe`, and are matched whole (see <see cref="Match"/>), so
        /// this list decides exactly what counts and nothing is matched by accident.
        ///
        /// NVIDIA ShadowPlay is deliberately absent: `NVIDIA Share.exe` runs from
        /// boot on any machine with GeForce Experience installed, so matching it
        /// would hide avatars permanently for a large share of players. Discord
        /// leaves it out for the same reason.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] Apps =
        {
            new KeyValuePair<string, string>("obs", "OBS Studio"),
            new KeyValuePair<string, string>("obs64", "OBS Studio"),
            new KeyValuePair<string, string>("obs32", "OBS Studio"),
            new KeyValuePair<string, string>("streamlabs obs", "Streamlabs"),
            new KeyValuePair<string, string>("streamlabs desktop", "Streamlabs"),
            new KeyValuePair<string, string>("slobs", "Streamlabs"),
            new KeyValuePair<string, string>("xsplit.core", "XSplit"),
            new KeyValuePair<string, string>("xsplit.broadcaster", "XSplit"),
            new KeyValuePair<string, string>("twitch studio", "Twitch Studio"),
            new KeyValuePair<string, string>("wirecast", "Wirecast"),
            new KeyValuePair<string, string>("vmix", "vMix"),
            new KeyValuePair<string, string>("vmix64", "vMix"),
            new KeyValuePair<string, string>("prism live studio", "PRISM Live Studio"),
            new KeyValuePair<string, string>("prismlivestudio", "PRISM Live Studio"),
        };

        /// <summary>OBS Game Capture's injected hook, by module-name prefix
        /// (`graphics-hook32.dll` / `graphics-hook64.dll`).</summary>
        private const string CaptureHookPrefix = "graphics-hook";

        // Written by the scan thread, read by the main one. `volatile` on both:
        // a torn read is impossible for either type, but a *stale* one would leave
        // the UI a frame or two behind a scan that has already finished.
        private static volatile string detected;   // display name, or null for none
        private static volatile bool scanning;

        private static float nextScan;

        /// <summary>The broadcasting app that was found, or null when none is
        /// running (or streamer mode is off, which is the same thing here — nothing
        /// is looked for).</summary>
        public static string DetectedApp => detected;

        /// <summary>True while a recognised broadcasting app is running.</summary>
        public static bool Broadcasting => detected != null;

        /// <summary>
        /// **The one property a UI may read.** Folds the manual switch together with
        /// streamer mode, so no drawing code has to know that there are two ways to
        /// arrive at the same answer. <see cref="ApiClient.HidePlayerDetails"/> is
        /// the stored preference and is for the settings screen only.
        /// </summary>
        public static bool HideDetails
        {
            get
            {
                var api = GeneKermanMod.Instance?.Api;
                if (api == null) return false;
                return api.HidePlayerDetails || (api.StreamerModeEnabled && detected != null);
            }
        }

        /// <summary>
        /// Pumped once a frame from <c>GeneKermanMod.Update</c>. Schedules a scan at
        /// most every <see cref="ScanIntervalSeconds"/>, and scans nothing at all
        /// while streamer mode is off.
        /// </summary>
        public static void Tick()
        {
            var api = GeneKermanMod.Instance?.Api;
            if (api == null || !api.StreamerModeEnabled)
            {
                // Switched off mid-session: drop the last answer, or the UI would go
                // on hiding on the strength of a scan nobody asked for.
                Forget();
                return;
            }

            if (scanning || Time.unscaledTime < nextScan) return;

            nextScan = Time.unscaledTime + ScanIntervalSeconds;
            scanning = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { detected = Scan(); }
                catch (Exception e)
                {
                    // A scan that throws must not take the game's thread pool with it,
                    // and must not leave `detected` asserting something stale.
                    detected = null;
                    UnityEngine.Debug.LogWarning("[GeneKerman] Streamer-mode scan failed: " + e.Message);
                }
                finally { scanning = false; }
            });
        }

        /// <summary>Forget the last detection and let the next <see cref="Tick"/>
        /// scan immediately. Called when the switch is flipped, so neither turning
        /// streamer mode on nor off waits out the interval.</summary>
        public static void Forget()
        {
            detected = null;
            nextScan = 0f;
        }

        // ── The scan (background thread only) ───────────────────────────────

        private static string Scan()
        {
            string hit = ScanProcessList();
            if (hit == null) hit = ScanProcFs();
            if (hit == null && CaptureHookLoaded()) hit = "OBS Studio";
            return hit;
        }

        /// <summary>Scan 1: whatever this runtime can see. Every process is disposed —
        /// each one is a live handle on Windows, and leaking a few hundred of them
        /// every five seconds is its own bug.</summary>
        private static string ScanProcessList()
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }

            try
            {
                foreach (var p in all)
                {
                    string name;
                    // A process that exited between the snapshot and this read throws
                    // rather than answering; it is also, definitionally, not running.
                    try { name = p.ProcessName; }
                    catch { continue; }

                    string app = Match(name);
                    if (app != null) return app;
                }
            }
            finally
            {
                foreach (var p in all)
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return null;
        }

        /// <summary>Scan 2: the host's process table, which is the only place a Wine
        /// prefix can see a native Linux OBS.</summary>
        private static string ScanProcFs()
        {
            string root = ProcRoot();
            if (root == null) return null;

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return null; }

            foreach (var dir in dirs)
            {
                // /proc holds plenty that is not a process (self, sys, net…); a pid
                // directory is the ones whose name is a number.
                string leaf = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(leaf) || leaf[0] < '0' || leaf[0] > '9') continue;

                string comm;
                // Unreadable or already gone — both mean "not the app we are after".
                try { comm = File.ReadAllText(Path.Combine(dir, "comm")); }
                catch { continue; }

                string app = Match(comm);
                if (app != null) return app;
            }
            return null;
        }

        /// <summary>
        /// Where the host's /proc is, from in here: `/proc` when the runtime is on
        /// Linux itself, `Z:\proc` when it is a Windows build under Wine (Wine maps
        /// Z: to /), and nowhere on Windows or macOS. Deliberately not cached — a
        /// player can remap Wine's drives, and this runs every few seconds at most.
        /// </summary>
        private static string ProcRoot()
        {
            try
            {
                if (Directory.Exists("/proc")) return "/proc";
                if (Directory.Exists(@"Z:\proc")) return @"Z:\proc";
            }
            catch { }
            return null;
        }

        /// <summary>Scan 3: is OBS's capture hook loaded into *us*? Answers a
        /// stricter question than the other two and is allowed to fail on any
        /// platform that will not enumerate its own modules.</summary>
        private static bool CaptureHookLoaded()
        {
            try
            {
                using (var self = Process.GetCurrentProcess())
                {
                    foreach (ProcessModule m in self.Modules)
                    {
                        string name;
                        try { name = m.ModuleName; }
                        catch { continue; }

                        if (!string.IsNullOrEmpty(name) &&
                            name.StartsWith(CaptureHookPrefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { /* not supported here, which is not the same as "no hook" — but it is all we can say */ }
            return false;
        }

        /// <summary>One process name against the table, or null for no match.</summary>
        private static string Match(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string name = raw.Trim().ToLowerInvariant();
            if (name.EndsWith(".exe", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 4);
            if (name.Length == 0) return null;

            foreach (var app in Apps)
            {
                if (name == app.Key) return app.Value;
                // Only a name long enough to have been truncated by `comm` may match
                // on a prefix — see CommNameCap.
                if (name.Length >= CommNameCap && app.Key.StartsWith(name, StringComparison.Ordinal))
                    return app.Value;
            }
            return null;
        }
    }
}
