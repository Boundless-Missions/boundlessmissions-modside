/*
 * QuicksendLedger.cs — what this client has actually handed over.
 *
 * The "craft_gift_accepted" notification exists as a backstop: a quicksend queues
 * the vessel out of the save at send time, and a quickload or a revert after that
 * restores the hull AND wipes the queued removal from the scenario, while the offer
 * lives on server-side. Quicksends have no contract for ReconcileRescueVessels to
 * re-derive the intent from, so the server's echo of the acceptance — carrying the
 * pid the client reported at send time — is the only thing left that remembers.
 *
 * The trouble is that the handler used to act on that pid with no record of its own.
 * Its precondition was VesselTransfer.VesselExists(pid), which is "this vessel is in
 * the current save" — the opposite of a guard — and the fate it applies is
 * LeavesWithCraft, which kills everyone aboard and REMOVES them from the roster,
 * permanently, with no KSP UI that can put them back. So one notification from a
 * malicious or repointed server destroyed any vessel it could name, one at a time,
 * with no craft transfer involved at all.
 *
 * MaybeHandleRescueRemoval has never had that shape: it looks its pids up in
 * GKContractScenario.PeekRescueSubmissions, so the server chooses which *recorded*
 * hand-over to settle rather than an arbitrary target. This file is the same
 * corroboration for a quicksend, and there are three reasons it is not in the
 * scenario alongside that one:
 *
 *  - The scenario rides the save. A quickload rolls it back — which is precisely the
 *    event this echo exists to repair, so a record kept there would be gone exactly
 *    when it is needed. PluginData is outside the save and survives it.
 *  - It is keyed by save folder as well as pid. A pid is unique within a save and
 *    means nothing across saves, and a client with several careers must not let an
 *    acceptance in one address a hull in another.
 *  - It records the server the send actually went to. Tokens are already per-server
 *    (ApiClient.tokensByServer), settings.cfg supports a custom host by design, and
 *    the loopback bridge can repoint the mod (LB3) — so a hostile server that
 *    acquired a session must still not be able to settle a hand-over made to the
 *    real one. This is the "same origin the token was minted for" test, kept per
 *    entry rather than globally so switching servers legitimately loses nothing.
 *
 * Entries expire rather than being consumed. Consuming looks tidier and is wrong for
 * the same reason it is wrong in data/crew_ledger.py: the echo can arrive more than
 * once (a poll and the live socket both dispatch it, and a rollback can happen after
 * the first one landed), and re-asserting a removal the player already made is a
 * no-op, while having consumed the record is a hand-over that can never be repaired.
 */

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class QuicksendLedger
    {
        private const string RootNode = "GKQUICKSENDS";
        private const string EntryNode = "SEND";

        /// <summary>How long a record is honoured. Long enough that a recipient who
        /// takes a fortnight to open the game still settles the hand-over, short
        /// enough that the file does not grow for the life of an install.</summary>
        private const int TtlDays = 60;

        /// <summary>Hard cap, oldest dropped first. A bound on our own writes, not on
        /// an attacker's: nothing here is peer-controlled.</summary>
        private const int MaxEntries = 200;

        private class Entry
        {
            public string Save;
            public string Pid;
            public string Server;
            public string Name;
            public DateTime Utc;
        }

        private static List<Entry> entries;

        private static string FilePath =>
            Path.Combine(GeneKermanMod.PluginDataPath, "quicksends.cfg");

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Write down that this save handed <paramref name="pid"/> to somebody,
        /// through <paramref name="serverUrl"/>. Called at the moment the removal is
        /// queued, i.e. when the server already holds the snapshot.</summary>
        public static void Record(string pid, string craftName, string serverUrl)
        {
            if (string.IsNullOrEmpty(pid)) return;
            try
            {
                Load();
                string save = SaveFolder();
                entries.RemoveAll(e => e != null && Same(e.Pid, pid) && Same(e.Save, save));
                entries.Add(new Entry
                {
                    Save = save,
                    Pid = pid,
                    Server = serverUrl ?? "",
                    Name = craftName ?? "",
                    Utc = DateTime.UtcNow,
                });
                Prune();
                Save();
                Debug.Log($"[GeneKerman] Quicksend ledger: recorded hand-over of {pid} " +
                          $"('{craftName}') from save '{save}'.");
            }
            catch (Exception ex)
            {
                // A failure here costs the backstop, not the send: the vessel is already
                // queued out and the normal path never needs this record.
                Debug.LogWarning($"[GeneKerman] QuicksendLedger.Record failed: {ex.Message}");
            }
        }

        /// <summary>True when this client really did hand this pid over, out of this
        /// save, to this server, recently enough to still be settling.
        ///
        /// Fails CLOSED, unlike the read in data/craft_bans.py or data/suspensions.py:
        /// the thing on the other side of a false answer here is a vessel deleted and
        /// its crew struck off a roster that has no way to get them back, where the
        /// cost of a false "no" is one hull the player removes themselves.</summary>
        public static bool WasHandedOver(string pid, string serverUrl)
        {
            return Lookup(pid, serverUrl, SaveFolder());
        }

        /// <summary>Whether this pid was handed over from ANY save on this install.
        ///
        /// Only ever asked after <see cref="WasHandedOver"/> has said no, and only to
        /// choose between the two meanings of that no. "Recorded, but in another save"
        /// means the player has a different career open and the instruction should be
        /// asked again later — the same reading the handler gives a vessel it cannot
        /// find, and for the same reason (§3.19 lost a hand-over by calling that case
        /// settled). "Not recorded anywhere" means nothing here ever handed this hull
        /// over, and there is nothing to retry into.</summary>
        public static bool WasHandedOverFromAnySave(string pid, string serverUrl)
        {
            return Lookup(pid, serverUrl, null);
        }

        /// <summary><paramref name="save"/> null matches any save.</summary>
        private static bool Lookup(string pid, string serverUrl, string save)
        {
            if (string.IsNullOrEmpty(pid)) return false;
            try
            {
                Load();
                DateTime cutoff = DateTime.UtcNow.AddDays(-TtlDays);
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry e = entries[i];
                    if (e == null) continue;
                    if (!Same(e.Pid, pid)) continue;
                    if (save != null && !Same(e.Save, save)) continue;
                    // An empty stored server is a record written before this field
                    // existed. Accept it: it was still written by this client, about
                    // this save, which is the corroboration that was missing.
                    if (!string.IsNullOrEmpty(e.Server) && !Same(e.Server, serverUrl)) continue;
                    if (e.Utc < cutoff) continue;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] QuicksendLedger lookup failed: {ex.Message}");
                return false;
            }
        }

        // ── Storage ──────────────────────────────────────────────────────────

        private static string SaveFolder() => HighLogic.SaveFolder ?? "";

        private static bool Same(string a, string b)
            => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        private static void Load()
        {
            if (entries != null) return;
            entries = new List<Entry>();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                ConfigNode file = ConfigNode.Load(path);
                ConfigNode root = file == null ? null
                                : (file.name == RootNode ? file : file.GetNode(RootNode));
                if (root == null) return;
                foreach (ConfigNode n in root.GetNodes(EntryNode))
                {
                    string pid = n.GetValue("pid");
                    if (string.IsNullOrEmpty(pid)) continue;
                    long ticks;
                    long.TryParse(n.GetValue("utc"), out ticks);
                    entries.Add(new Entry
                    {
                        Save = n.GetValue("save") ?? "",
                        Pid = pid,
                        Server = n.GetValue("server") ?? "",
                        Name = n.GetValue("name") ?? "",
                        Utc = TicksToUtc(ticks),
                    });
                }
                Prune();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] QuicksendLedger.Load failed: {ex.Message}");
                entries = new List<Entry>();
            }
        }

        private static DateTime TicksToUtc(long ticks)
        {
            // A hand-edited or truncated value must not throw out of the ctor, and must
            // not read as "now" either — an unreadable timestamp is an expired one.
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return DateTime.MinValue;
            try { return new DateTime(ticks, DateTimeKind.Utc); }
            catch { return DateTime.MinValue; }
        }

        private static void Prune()
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-TtlDays);
            entries.RemoveAll(e => e == null || e.Utc < cutoff);
            if (entries.Count <= MaxEntries) return;
            entries.Sort((a, b) => a.Utc.CompareTo(b.Utc));
            entries.RemoveRange(0, entries.Count - MaxEntries);
        }

        private static void Save()
        {
            try
            {
                var root = new ConfigNode(RootNode);
                foreach (Entry e in entries)
                {
                    if (e == null) continue;
                    ConfigNode n = root.AddNode(EntryNode);
                    n.AddValue("save", e.Save ?? "");
                    n.AddValue("pid", e.Pid ?? "");
                    n.AddValue("server", e.Server ?? "");
                    n.AddValue("name", e.Name ?? "");
                    n.AddValue("utc", e.Utc.Ticks.ToString());
                }
                Directory.CreateDirectory(GeneKermanMod.PluginDataPath);
                root.Save(FilePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] QuicksendLedger.Save failed: {ex.Message}");
            }
        }
    }
}
