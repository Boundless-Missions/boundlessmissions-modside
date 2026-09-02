/*
 * Favorites.cs – The player's starred players, for the quicksend picker.
 *
 * This lives mod-side rather than in the browser on purpose. The bridge binds a fresh
 * ephemeral port every session, so http://127.0.0.1:<port> is a *different origin* each
 * time KSP starts — localStorage would be empty on every launch and the feature would
 * quietly never work. PluginData is the only storage that survives a restart.
 *
 * Stored as a flat ConfigNode of repeated `id = <account id>` values — a Discord
 * snowflake for most players, "a_…" for a website-only Boundless account.
 */

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class Favorites
    {
        private const int MaxFavorites = 100;

        private static HashSet<string> ids;

        private static string FilePath =>
            Path.Combine(GeneKermanMod.PluginDataPath, "favorites.cfg");

        /// <summary>The starred user ids. Never null.</summary>
        public static HashSet<string> Ids
        {
            get { EnsureLoaded(); return ids; }
        }

        public static bool IsFavorite(string userId)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(userId) && ids.Contains(userId);
        }

        /// <summary>Stars or unstars a player. Returns the resulting state.</summary>
        public static bool Set(string userId, bool favorite)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(userId) || !IsPlausibleId(userId)) return false;

            if (favorite)
            {
                // A cap, because these ids are written to disk from a request the page
                // makes: without one, a loop in the page grows the file without bound.
                if (ids.Count >= MaxFavorites && !ids.Contains(userId)) return false;
                ids.Add(userId);
            }
            else ids.Remove(userId);

            Save();
            return favorite;
        }

        // Account ids: a Discord snowflake is digits, a Boundless website account is
        // "a_" + a Firebase uid. Digits alone was the original test and it quietly
        // made every website-only player unstarrable — the id was simply dropped on
        // the way to the file, so the star came back off on the next launch.
        //
        // The character set stays deliberately narrow rather than becoming "anything
        // short": nothing that reaches the file may be mistaken for a key, a node
        // name, or a comment when ConfigNode reads it back.
        private static bool IsPlausibleId(string s)
        {
            if (s.Length == 0 || s.Length > 48) return false;
            foreach (char c in s)
            {
                bool okChar = (c >= '0' && c <= '9')
                           || (c >= 'a' && c <= 'z')
                           || (c >= 'A' && c <= 'Z')
                           || c == '_' || c == '-';
                if (!okChar) return false;
            }
            return true;
        }

        private static void EnsureLoaded()
        {
            if (ids != null) return;
            ids = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                if (!File.Exists(FilePath)) return;
                ConfigNode root = ConfigNode.Load(FilePath);
                ConfigNode node = root?.GetNode("GeneKermanFavorites");
                if (node == null) return;

                foreach (string v in node.GetValues("id"))
                    if (IsPlausibleId(v)) ids.Add(v);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not read favorites.cfg: {ex.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(GeneKermanMod.PluginDataPath);

                var node = new ConfigNode("GeneKermanFavorites");
                foreach (string id in ids) node.AddValue("id", id);

                var root = new ConfigNode();
                root.AddNode(node);
                root.Save(FilePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not save favorites.cfg: {ex.Message}");
            }
        }
    }
}
