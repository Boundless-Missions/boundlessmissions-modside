/*
 * ContractInbox.cs – The inbox's *local* state: which contracts the player has
 * thrown away, and how the list is grouped into weeks.
 *
 * Neither of these is on the server. Trashing a contract hides it from this
 * install's list and nothing else — the contract still exists, the other party
 * still sees it, and the server is never told. That is why it lives in
 * PluginData rather than behind an API call, exactly as Favorites does.
 *
 * Shared because two front ends render the same inbox (the classic window's mail
 * list and the sidebar's ContractsPanel) and a second copy of "which ids are in
 * the bin" would mean trashing something in one and having it reappear in the
 * other. The week key is here for the same reason: a group heading that differs
 * between the two would read as two different inboxes.
 *
 * The file is a flat list of ids, one per line — the format the classic window
 * already wrote, so an existing trash bin survives this being factored out.
 */

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class ContractInbox
    {
        /// <summary>
        /// Statuses a contract may be trashed in. A live contract is still work the
        /// player owes or is owed, and hiding one is how it gets forgotten until the
        /// fine lands — so the bin only takes contracts that are already over.
        /// </summary>
        public static bool IsFinished(string status) =>
            status == "completed" || status == "failed" ||
            status == "declined" || status == "cancelled";

        private static HashSet<string> trashed;

        private static string FilePath =>
            Path.Combine(GeneKermanMod.PluginDataPath, "trashed_contracts.txt");

        /// <summary>The trashed contract ids. Never null.</summary>
        public static HashSet<string> Trashed
        {
            get { EnsureLoaded(); return trashed; }
        }

        public static bool IsTrashed(string contractId)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(contractId) && trashed.Contains(contractId);
        }

        /// <summary>Move a contract to the bin, or put it back. Persists immediately.</summary>
        public static void SetTrashed(string contractId, bool value)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(contractId)) return;

            bool changed = value ? trashed.Add(contractId) : trashed.Remove(contractId);
            if (changed) Save();
        }

        /// <summary>
        /// The Monday-of-week heading a contract is filed under, from its ISO
        /// created_at. Unparseable dates get their own bucket rather than being
        /// dropped — a contract with no date is still a contract, and the classic
        /// window has always shown them under "Unknown Date".
        /// </summary>
        public static string WeekKey(string isoDate)
        {
            if (string.IsNullOrEmpty(isoDate) || isoDate.Length < 10) return "Unknown Date";

            DateTime dt;
            if (!DateTime.TryParse(isoDate, out dt)) return "Unknown Date";

            int diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
            return "Week of " + dt.AddDays(-diff).Date.ToString("MMM d, yyyy");
        }

        private static void EnsureLoaded()
        {
            if (trashed != null) return;
            trashed = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string id = (line ?? "").Trim();
                    if (id.Length > 0) trashed.Add(id);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not read trashed_contracts.txt: {ex.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(GeneKermanMod.PluginDataPath);
                File.WriteAllLines(FilePath, new List<string>(trashed).ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not save trashed_contracts.txt: {ex.Message}");
            }
        }
    }
}
