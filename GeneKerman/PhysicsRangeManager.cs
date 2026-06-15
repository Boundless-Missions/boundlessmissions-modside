/*
 * PhysicsRangeManager.cs – Temporarily disable Physics Range Extender (PRE) during
 * a multi-craft submission.
 *
 * Why: PRE inflates the physics bubble so many distant vessels stay loaded at once.
 * When packing up "everything in physics range" for a submission that's unstable and
 * spam-loads craft. So before capture we turn PRE's override off, let KSP collapse
 * back to the stock ~2.5 km bubble (far vessels unload), capture what remains, then —
 * if (and only if) we were the one who disabled it — turn PRE back on afterward.
 *
 * PRE is an optional third-party mod, so all access is reflection-based: if PRE isn't
 * installed, or its API differs from what we probe for, every call is a safe no-op.
 * The exact static enable flag varies between PRE builds, so we search a few likely
 * member names rather than hard-binding one.
 */

using System;
using System.Reflection;
using UnityEngine;

namespace GeneKerman
{
    public static class PhysicsRangeManager
    {
        // Candidate names for PRE's static "is the override active" toggle, in order
        // of preference. Different PRE releases have used different names.
        private static readonly string[] EnableMemberNames =
            { "ModEnabled", "Enabled", "Active", "IsEnabled", "enabled" };

        private static bool _disabledByUs;
        private static MemberWrapper _enableMember;   // cached PRE toggle, once resolved

        /// <summary>True while PRE's override is suppressed by us.</summary>
        public static bool IsSuppressed { get { return _disabledByUs; } }

        /// <summary>
        /// If PRE is installed and currently extending the range, turn its override off
        /// and collapse loaded vessels back to stock ranges. Returns true only when we
        /// actually changed PRE's state (so the caller knows to re-enable it later).
        /// A no-op — returning false — when PRE is absent or already off.
        /// </summary>
        public static bool TryDisable()
        {
            try
            {
                if (_disabledByUs) return true;   // already suppressed

                MemberWrapper toggle = ResolveEnableMember();
                if (toggle == null)
                {
                    Debug.Log("[GeneKerman] PhysicsRangeManager: PRE not detected — nothing to disable.");
                    return false;
                }

                bool current = toggle.GetBool();
                if (!current)
                {
                    // PRE present but already off; leave it, don't claim ownership.
                    return false;
                }

                toggle.SetBool(false);
                _disabledByUs = true;
                Debug.Log("[GeneKerman] PhysicsRangeManager: PRE override disabled for submission.");

                ResetRangesToStock();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PhysicsRangeManager.TryDisable failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Re-enable PRE's override if (and only if) <see cref="TryDisable"/> turned it
        /// off. PRE re-applies its extended ranges on its own once the flag is back on.
        /// </summary>
        public static void Reenable()
        {
            try
            {
                if (!_disabledByUs) return;

                MemberWrapper toggle = ResolveEnableMember();
                if (toggle != null)
                {
                    toggle.SetBool(true);
                    Debug.Log("[GeneKerman] PhysicsRangeManager: PRE override restored.");
                }
                _disabledByUs = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PhysicsRangeManager.Reenable failed: {ex.Message}");
                _disabledByUs = false;
            }
        }

        // ── Stock range reset ────────────────────────────────────────────────

        /// <summary>Shrink every loaded vessel's ranges back to the stock defaults so
        /// KSP unloads anything outside the normal bubble. Best-effort; any failure is
        /// swallowed (worst case PRE's last ranges linger until it's re-enabled).</summary>
        private static void ResetRangesToStock()
        {
            try
            {
                if (FlightGlobals.VesselsLoaded == null) return;

                VesselRanges stock = null;
                try { stock = PhysicsGlobals.Instance != null ? PhysicsGlobals.Instance.VesselRangesDefault : null; }
                catch { stock = null; }

                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null) continue;
                    try { v.vesselRanges = stock != null ? new VesselRanges(stock) : new VesselRanges(); }
                    catch { /* per-vessel failure is non-fatal */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PhysicsRangeManager.ResetRangesToStock failed: {ex.Message}");
            }
        }

        // ── PRE reflection plumbing ──────────────────────────────────────────

        private static MemberWrapper ResolveEnableMember()
        {
            if (_enableMember != null) return _enableMember;

            Assembly preAsm = FindPreAssembly();
            if (preAsm == null) return null;

            const BindingFlags Flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            foreach (var type in SafeGetTypes(preAsm))
            {
                if (type == null) continue;
                foreach (var name in EnableMemberNames)
                {
                    PropertyInfo pi = type.GetProperty(name, Flags);
                    if (pi != null && pi.PropertyType == typeof(bool) &&
                        pi.CanRead && pi.CanWrite)
                    {
                        _enableMember = MemberWrapper.From(pi);
                        return _enableMember;
                    }

                    FieldInfo fi = type.GetField(name, Flags);
                    if (fi != null && fi.FieldType == typeof(bool))
                    {
                        _enableMember = MemberWrapper.From(fi);
                        return _enableMember;
                    }
                }
            }

            Debug.LogWarning("[GeneKerman] PhysicsRangeManager: PRE assembly found but no enable flag matched.");
            return null;
        }

        private static Assembly FindPreAssembly()
        {
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    string n = la?.assembly?.GetName().Name;
                    if (!string.IsNullOrEmpty(n) &&
                        n.IndexOf("PhysicsRangeExtender", StringComparison.OrdinalIgnoreCase) >= 0)
                        return la.assembly;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] PhysicsRangeManager: PRE lookup failed: {ex.Message}");
            }
            return null;
        }

        private static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types ?? new Type[0]; }
            catch { return new Type[0]; }
        }

        /// <summary>Uniform read/write over a static bool property or field.</summary>
        private class MemberWrapper
        {
            private PropertyInfo _prop;
            private FieldInfo _field;

            public static MemberWrapper From(PropertyInfo pi) { return new MemberWrapper { _prop = pi }; }
            public static MemberWrapper From(FieldInfo fi) { return new MemberWrapper { _field = fi }; }

            public bool GetBool()
            {
                if (_prop != null) return (bool)_prop.GetValue(null, null);
                if (_field != null) return (bool)_field.GetValue(null);
                return false;
            }

            public void SetBool(bool value)
            {
                if (_prop != null) _prop.SetValue(null, value, null);
                else if (_field != null) _field.SetValue(null, value);
            }
        }
    }
}
