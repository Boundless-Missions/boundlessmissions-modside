/*
 * KVVIntegration.cs – Vessel capture manager.
 *
 * Uses our built-in VesselRenderer for orthographic renders.
 * KVV detection is kept for informational purposes only.
 */

using System;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public static class KVVIntegration
    {
        private static bool _checked;
        private static bool _kvvInstalled;

        /// <summary>
        /// Whether KVV (KronalUtils) is installed.
        /// We always use our built-in renderer regardless, but report
        /// KVV's presence for informational purposes.
        /// </summary>
        public static bool IsKVVInstalled
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    try
                    {
                        _kvvInstalled = AssemblyLoader.loadedAssemblies
                            .Select(a => a.assembly)
                            .Any(a => a.GetName().Name == "KronalUtils");

                        if (_kvvInstalled)
                            Debug.Log("[GeneKerman] KVV detected (not used — built-in renderer active).");
                    }
                    catch { _kvvInstalled = false; }
                }
                return _kvvInstalled;
            }
        }

        /// <summary>
        /// Always true — we have our own built-in renderer.
        /// </summary>
        public static bool IsAvailable => true;

        /// <summary>
        /// Capture an orthographic vessel render using the built-in renderer. When a
        /// vessel is supplied that specific craft is rendered (for multi-vessel
        /// submissions); otherwise the active vessel / editor ship is auto-detected.
        /// </summary>
        public static string CaptureWithFallback(Vessel vessel = null)
        {
            return VesselRenderer.CaptureVessel(vessel);
        }
    }
}
