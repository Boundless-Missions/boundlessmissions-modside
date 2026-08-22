/*
 * ClickThroughHelper.cs – Optional Click Through Blocker (CTB) integration.
 *
 * Click Through Blocker (linuxgurugamer's "ClickThroughBlocker") stops mouse
 * input over a mod window from also reaching the game underneath — without it,
 * clicking a button in our UI can also place/remove a part in the editor or
 * interact with the flight scene behind the window.
 *
 * When CTB is installed we draw our windows through its GUILayoutWindow; when it
 * isn't, we fall back to the stock GUILayout.Window and behave exactly as before.
 * Everything is reached by reflection, so there is no compile-time or hard runtime
 * dependency — CTB stays a soft, optional dependency.
 */

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GeneKerman
{
    public static class ClickThroughHelper
    {
        // Matches CTB's static:
        //   Rect ClickThroughFix.ClickThruBlocker.GUILayoutWindow(
        //       int id, Rect screenRect, GUI.WindowFunction func,
        //       string text, GUIStyle style, params GUILayoutOption[] options)
        private delegate Rect GUILayoutWindowFn(int id, Rect screenRect, GUI.WindowFunction func,
            string text, GUIStyle style, GUILayoutOption[] options);

        private static bool _resolved;
        private static GUILayoutWindowFn _ctbWindow;

        /// <summary>True when Click Through Blocker is installed and wired up.</summary>
        public static bool IsAvailable
        {
            get { Resolve(); return _ctbWindow != null; }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var asm = AssemblyLoader.loadedAssemblies
                    .Select(a => a.assembly)
                    .FirstOrDefault(a => a.GetName().Name == "ClickThroughBlocker");
                if (asm == null)
                {
                    Debug.Log("[GeneKerman] Click Through Blocker not found — using stock windows.");
                    return;
                }

                // Namespace is ClickThroughFix, but the class is ClickThruBlocker
                // ("Thru", not "Through") — a deliberate naming quirk in CTB.
                var type = asm.GetType("ClickThroughFix.ClickThruBlocker");
                if (type == null)
                {
                    Debug.LogWarning("[GeneKerman] CTB assembly found but ClickThruBlocker type missing — using stock windows.");
                    return;
                }

                var method = type.GetMethod("GUILayoutWindow",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[]
                    {
                        typeof(int), typeof(Rect), typeof(GUI.WindowFunction),
                        typeof(string), typeof(GUIStyle), typeof(GUILayoutOption[])
                    },
                    null);
                if (method == null)
                {
                    Debug.LogWarning("[GeneKerman] CTB found but GUILayoutWindow signature not matched — using stock windows.");
                    return;
                }

                _ctbWindow = (GUILayoutWindowFn)Delegate.CreateDelegate(typeof(GUILayoutWindowFn), method);
                Debug.Log("[GeneKerman] Click Through Blocker integration active.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CTB integration failed ({ex.Message}) — using stock windows.");
                _ctbWindow = null;
            }
        }

        /// <summary>
        /// Drop-in replacement for GUILayout.Window. Routes through Click Through
        /// Blocker when available, otherwise calls the stock window unchanged.
        /// </summary>
        public static Rect Window(int id, Rect screenRect, GUI.WindowFunction func,
            string text, GUIStyle style, params GUILayoutOption[] options)
        {
            Resolve();
            if (_ctbWindow != null)
            {
                try
                {
                    return _ctbWindow(id, screenRect, func, text, style, options);
                }
                catch (Exception ex)
                {
                    // If CTB throws for any reason, disable it for the rest of the
                    // session and keep the UI usable via the stock window.
                    Debug.LogWarning($"[GeneKerman] CTB window call failed ({ex.Message}); reverting to stock windows.");
                    _ctbWindow = null;
                }
            }
            return GUILayout.Window(id, screenRect, func, text, style, options);
        }
    }
}
