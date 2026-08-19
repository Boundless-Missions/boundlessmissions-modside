/*
 * UI/GKSkin.cs – Shared GUI texture cache and style factory.
 *
 * Unity destroys Texture2D objects on scene transitions in KSP.
 * This class caches textures by color key and recreates them when
 * the native texture handle is destroyed (texture becomes null/invalid).
 *
 * All UI windows should use GKSkin.MakeTex() and GKSkin.CheckStyles()
 * instead of creating their own textures. "All" is now a short list: the gates
 * (consent, update, data-sharing paused, device verify), the link screen, the
 * checkpoint prompt and the browser-UI notice. Everything else is uGUI, on the
 * sidebar's canvas, and gets its colours from UI/Gui/Theme.cs instead.
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

        // GeneKerman's themed skin (see GetSkin). Rebuilt on scene changes.
        private static GUISkin themedSkin;

        /// <summary>
        /// GeneKerman's dark theme for the controls that would otherwise fall back
        /// to the shared <see cref="GUI.skin"/> — bare buttons, text fields and
        /// scroll bars. Built once by cloning the ambient skin and restyling those
        /// few controls; everything else is inherited untouched.
        ///
        /// IMPORTANT: callers MUST scope this. Set <c>GUI.skin</c> to it only while
        /// drawing GeneKerman windows and restore the previous skin afterwards, so
        /// the theme never leaks into other mods' IMGUI (which all share GUI.skin).
        /// </summary>
        public static GUISkin GetSkin(GUISkin baseSkin)
        {
            if (themedSkin != null) return themedSkin;

            themedSkin = Object.Instantiate(baseSkin);
            themedSkin.hideFlags = HideFlags.HideAndDontSave; // survive scene loads

            themedSkin.verticalScrollbar = new GUIStyle(themedSkin.verticalScrollbar) {
                normal = { background = MakeTex(2, 2, new Color(0.08f, 0.08f, 0.1f, 1f)) },
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), fixedWidth = 12
            };
            themedSkin.verticalScrollbarThumb = new GUIStyle(themedSkin.verticalScrollbarThumb) {
                normal = { background = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.25f, 1f)) },
                hover = { background = MakeTex(2, 2, new Color(0.3f, 0.3f, 0.38f, 1f)) },
                active = { background = MakeTex(2, 2, new Color(0.3f, 0.3f, 0.38f, 1f)) },
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), fixedWidth = 12
            };
            themedSkin.button = new GUIStyle(themedSkin.button) {
                normal = { background = MakeTex(2, 2, new Color(0.15f, 0.15f, 0.2f, 1f)) },
                hover = { background = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 1f)) },
                active = { background = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.28f, 1f)) },
                border = new RectOffset(0, 0, 0, 0)
            };
            themedSkin.textField = new GUIStyle(themedSkin.textField) {
                normal = { background = MakeTex(2, 2, new Color(0.08f, 0.08f, 0.11f, 1f)), textColor = Color.white },
                focused = { background = MakeTex(2, 2, new Color(0.1f, 0.15f, 0.2f, 1f)), textColor = Color.white },
                hover = { background = MakeTex(2, 2, new Color(0.09f, 0.1f, 0.14f, 1f)), textColor = Color.white },
                active = { background = MakeTex(2, 2, new Color(0.1f, 0.15f, 0.2f, 1f)), textColor = Color.white },
                border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(5, 5, 5, 5)
            };

            return themedSkin;
        }

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

            // Drop the themed skin so it rebuilds with fresh (non-destroyed) textures.
            themedSkin = null;

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
