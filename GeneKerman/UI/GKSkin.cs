/*
 * UI/GKSkin.cs – Shared GUI texture cache and style factory.
 *
 * Unity destroys Texture2D objects on scene transitions in KSP.
 * This class caches textures by color key and recreates them when
 * the native texture handle is destroyed (texture becomes null/invalid).
 *
 * All UI windows should use GKSkin.MakeTex() and GKSkin.CheckStyles()
 * instead of creating their own textures.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI
{
    public static class GKSkin
    {
        // Cache textures by their RGBA color key
        private static readonly Dictionary<uint, Texture2D> texCache = new Dictionary<uint, Texture2D>();

        // Sentinel texture to detect scene-change destruction
        private static Texture2D sentinel;

        /// <summary>
        /// Create or retrieve a solid-color texture that survives scene changes.
        /// </summary>
        public static Texture2D MakeTex(int w, int h, Color col)
        {
            uint key = ColorKey(col);

            Texture2D tex;
            if (texCache.TryGetValue(key, out tex) && tex != null)
                return tex;

            // Create new texture and mark it persistent
            tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            tex.hideFlags = HideFlags.HideAndDontSave; // Survive scene loads
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            tex.SetPixels(pix);
            tex.Apply();

            texCache[key] = tex;
            return tex;
        }

        /// <summary>
        /// Check if textures have been destroyed (scene transition).
        /// Returns true if styles need to be rebuilt.
        /// </summary>
        public static bool NeedsRebuild()
        {
            if (sentinel == null)
            {
                sentinel = MakeTex(1, 1, new Color(0.999f, 0.001f, 0.999f, 0.001f));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Force all windows to rebuild their styles next frame.
        /// Called on scene transitions.
        /// </summary>
        public static void Invalidate()
        {
            // Clear the cache — textures with HideAndDontSave should survive,
            // but if they don't (some KSP versions), they'll be recreated.
            var keysToRemove = new List<uint>();
            foreach (var kvp in texCache)
            {
                if (kvp.Value == null)
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
                texCache.Remove(key);

            sentinel = null; // Force rebuild check
        }

        private static uint ColorKey(Color c)
        {
            uint r = (uint)(c.r * 255) & 0xFF;
            uint g = (uint)(c.g * 255) & 0xFF;
            uint b = (uint)(c.b * 255) & 0xFF;
            uint a = (uint)(c.a * 255) & 0xFF;
            return (r << 24) | (g << 16) | (b << 8) | a;
        }
    }
}
