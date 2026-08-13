/*
 * Favorites.cs – The player's starred players, for the quicksend picker.
 *
 * This lives mod-side rather than in the browser on purpose. The bridge binds a fresh
 * ephemeral port every session, so http://127.0.0.1:<port> is a *different origin* each
 * time KSP starts — localStorage would be empty on every launch and the feature would
 * quietly never work. PluginData is the only storage that survives a restart.
 *
 * Stored as a flat ConfigNode of repeated `id = <discord user id>` values.
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

        // Discord snowflakes. Digits only, so nothing that reaches the file can be
        // mistaken for a key, a node name, or a comment when it is read back.
        private static bool IsPlausibleId(string s)
        {
            if (s.Length == 0 || s.Length > 24) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
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
