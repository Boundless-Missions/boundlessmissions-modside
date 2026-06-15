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
using System.IO;
using UnityEngine;

namespace GeneKerman.UI
{
    public static class GKSkin
    {
        // Cache textures by their RGBA color key
        private static readonly Dictionary<uint, Texture2D> texCache = new Dictionary<uint, Texture2D>();

        // Cache icon textures loaded from disk, keyed by filename (e.g. "Refresh")
        private static readonly Dictionary<string, Texture2D> iconCache = new Dictionary<string, Texture2D>();

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
        /// Load a PNG icon from GameData/GeneKerman/Textures/{name}.png and
        /// scale it to <paramref name="size"/> pixels (default 14).
        /// Cached and marked HideAndDontSave to survive scene changes.
        /// Returns null if the file doesn't exist.
        /// </summary>
        public static Texture2D LoadIcon(string name, int size = 14)
        {
            string cacheKey = name + "_" + size;
            Texture2D tex;
            if (iconCache.TryGetValue(cacheKey, out tex) && tex != null)
                return tex;

            string path = Path.Combine(GeneKermanMod.ModPath, "Textures", name + ".png");
            if (!File.Exists(path))
            {
                Debug.LogWarning("[GeneKerman] Icon not found: " + path);
                return null;
            }

            // Load at native resolution
            var src = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            src.LoadImage(File.ReadAllBytes(path));

            // Scale down via RenderTexture blit
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            Graphics.Blit(src, rt);

            tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(src);

            iconCache[cacheKey] = tex;
            return tex;
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

            // Also purge destroyed icon textures so they reload from disk
            var iconKeysToRemove = new List<string>();
            foreach (var kvp in iconCache)
            {
                if (kvp.Value == null)
                    iconKeysToRemove.Add(kvp.Key);
            }
            foreach (var key in iconKeysToRemove)
                iconCache.Remove(key);

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
