/*
 * DecalCapture.cs – Make ConformalDecals' decals appear in blueprint renders.
 *
 * A ConformalDecals decal has no Renderer of its own. ModuleConformalDecal (and its
 * ModuleConformalFlag / ModuleConformalText subclasses) hooks Camera.onPreCull and,
 * for every camera about to render, issues a Graphics.DrawMesh of the *target* part's
 * mesh with the decal's projection material — on a HARDCODED layer 0. That is the one
 * thing VesselRenderer's isolation cannot follow: it moves the craft's GameObjects to
 * layer 30 and gives the capture camera cullingMask 1<<30, so the decal draws are
 * culled and every decal — image, flag and text alike — is missing from the blueprint
 * and from the shared-craft thumbnail, while KSP's own thumbnail camera (which sees
 * layer 0) shows them. CollectRenderers cannot see them either: there is no
 * MeshRenderer to find, only a per-camera draw call.
 *
 * So the fix is to re-issue that same draw call for the capture camera on the
 * isolation layer, reading the values the module has already computed (projection
 * mesh, decal material, per-target property block) rather than deriving anything.
 * The decal's own onPreCull draw still goes to layer 0 and is still culled; ours is
 * the one the camera sees, so nothing is drawn twice.
 *
 * All reflection — the build references no ConformalDecals assembly, exactly like the
 * LifeSupport adapters and TweakScaleGuard. Without the mod every entry point here is
 * a no-op.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GeneKerman
{
    internal static class DecalCapture
    {
        private const string ASSEMBLY = "ConformalDecals";

        private const BindingFlags MEMBERS =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ModuleConformalDecal
        private static Type _moduleType;
        private static FieldInfo _fIsAttached, _fTargets, _fDecalMaterial;
        // ProjectionTarget (one per part the decal projects onto)
        private static FieldInfo _fProjectionEnabled, _fTargetMesh, _fTarget, _fDecalMpb;
        // ModuleConformalText
        private static Type _textType;
        private static FieldInfo _fCurrentText;
        private static MethodInfo _mUpdateText;

        private static bool _probed;

        // The part's rim lighting is copied into the decal's property block on every
        // draw so a decal shades with the surface it sits on — same two properties
        // ConformalDecals itself forwards.
        private static readonly int RimFalloff = Shader.PropertyToID("_RimFalloff");
        private static readonly int RimColor   = Shader.PropertyToID("_RimColor");

        private struct DecalDraw
        {
            public Mesh mesh;              // the target part's mesh, in its own space
            public Transform target;       // …and the transform that places it
            public Material material;      // the decal's projection material
            public MaterialPropertyBlock mpb;
            public Part part;              // for rim properties
        }

        private static readonly List<DecalDraw> _draws = new List<DecalDraw>();
        private static Camera _camera;
        private static int _layer;
        private static bool _hooked;
        private static bool _warnedOnDraw;

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>Start redrawing the decals of <paramref name="parts"/> onto
        /// <paramref name="layer"/> for <paramref name="cam"/>, for as long as the
        /// capture runs. Must be paired with <see cref="EndCapture"/>. A no-op when
        /// ConformalDecals isn't installed or the craft carries no decals.</summary>
        public static void BeginCapture(Camera cam, int layer, List<Part> parts)
        {
            EndCapture();  // never leave a stale hook behind
            Probe();
            if (_moduleType == null || cam == null || parts == null) return;

            _camera = cam;
            _layer = layer;
            _warnedOnDraw = false;
            Collect(parts);

            if (_draws.Count == 0) { _camera = null; return; }

            Camera.onPreCull += OnPreCull;
            _hooked = true;
        }

        /// <summary>Stop redrawing and drop the collected draws. Safe to call twice,
        /// and safe to call before the capture camera is destroyed (it must be).</summary>
        public static void EndCapture()
        {
            if (_hooked)
            {
                Camera.onPreCull -= OnPreCull;
                _hooked = false;
            }
            _draws.Clear();
            _camera = null;
        }

        // ── Collection ──────────────────────────────────────────────────────

        private static void Collect(List<Part> parts)
        {
            foreach (Part p in parts)
            {
                if (p == null || p.Modules == null) continue;
                foreach (PartModule m in p.Modules)
                {
                    if (m == null || !_moduleType.IsInstanceOfType(m)) continue;
                    RefreshTextIfNeverRendered(m);
                    AddTargets(p, m);
                }
            }
        }

        private static void AddTargets(Part part, PartModule module)
        {
            try
            {
                // An unattached decal (still on the cursor, or a stripped-out preview)
                // projects nothing — the module's own guard.
                if (_fIsAttached != null && _fIsAttached.GetValue(module) is bool attached && !attached)
                    return;

                var material = _fDecalMaterial.GetValue(module) as Material;
                if (material == null) return;

                if (!(_fTargets.GetValue(module) is IEnumerable targets)) return;
                foreach (object t in targets)
                {
                    if (t == null) continue;
                    if (_fProjectionEnabled != null &&
                        _fProjectionEnabled.GetValue(t) is bool projecting && !projecting) continue;

                    var mesh = _fTargetMesh.GetValue(t) as Mesh;
                    var tr = _fTarget.GetValue(t) as Transform;
                    if (mesh == null || tr == null) continue;

                    _draws.Add(new DecalDraw
                    {
                        mesh = mesh,
                        target = tr,
                        material = material,
                        mpb = _fDecalMpb.GetValue(t) as MaterialPropertyBlock,
                        part = part,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] DecalCapture: reading '{module.moduleName}' failed: {ex.Message}");
            }
        }

        /// <summary>A text decal's texture is generated at runtime, and only by
        /// UpdateText — the module's fields carry the string, the font and the colours,
        /// but nothing is drawn until that has run once (OnLoad defers it to a
        /// coroutine, which never runs for a part loaded while its GameObject is
        /// inactive). Generate it here for any text decal that has never rendered, so a
        /// blueprint shows the lettering rather than a blank decal. Idempotent: a decal
        /// that has already rendered is left alone.</summary>
        private static void RefreshTextIfNeverRendered(PartModule module)
        {
            if (_textType == null || _fCurrentText == null || _mUpdateText == null) return;
            if (!_textType.IsInstanceOfType(module)) return;
            try
            {
                if (_fCurrentText.GetValue(module) != null) return;  // already rendered
                _mUpdateText.Invoke(module, new object[] { false });
                Debug.Log($"[GeneKerman] DecalCapture: rendered the pending text decal on '{module.part?.partInfo?.name}'.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] DecalCapture: refreshing a text decal failed: {ex.Message}");
            }
        }

        // ── Drawing ─────────────────────────────────────────────────────────

        private static void OnPreCull(Camera cam)
        {
            if (cam != _camera) return;
            try
            {
                for (int i = 0; i < _draws.Count; i++)
                {
                    DecalDraw d = _draws[i];
                    if (d.mesh == null || d.target == null || d.material == null) continue;

                    CopyRimProperties(d);
                    Graphics.DrawMesh(d.mesh, d.target.localToWorldMatrix, d.material,
                        _layer, cam, 0, d.mpb,
                        UnityEngine.Rendering.ShadowCastingMode.Off, true);
                }
            }
            catch (Exception ex)
            {
                if (!_warnedOnDraw)
                {
                    _warnedOnDraw = true;  // this runs per view per pass — warn once
                    Debug.LogWarning($"[GeneKerman] DecalCapture: drawing decals failed: {ex.Message}");
                }
            }
        }

        private static void CopyRimProperties(DecalDraw d)
        {
            if (d.mpb == null || d.part == null) return;
            MaterialPropertyBlock partMpb = d.part.mpb;
            if (partMpb == null) return;
            d.mpb.SetFloat(RimFalloff, partMpb.GetFloat(RimFalloff));
            d.mpb.SetColor(RimColor, partMpb.GetColor(RimColor));
        }

        // ── Reflection setup ────────────────────────────────────────────────

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _moduleType = LsReflect.FindType(ASSEMBLY, "ConformalDecals.ModuleConformalDecal");
                if (_moduleType == null) return;

                _fIsAttached    = _moduleType.GetField("_isAttached", MEMBERS);
                _fTargets       = _moduleType.GetField("_targets", MEMBERS);
                _fDecalMaterial = _moduleType.GetField("_decalMaterial", MEMBERS);

                Type targetType = LsReflect.FindType(ASSEMBLY, "ConformalDecals.ProjectionTarget");
                if (targetType != null)
                {
                    _fProjectionEnabled = targetType.GetField("_projectionEnabled", MEMBERS);
                    _fTargetMesh        = targetType.GetField("_targetMesh", MEMBERS);
                    _fTarget            = targetType.GetField("target", MEMBERS);
                    _fDecalMpb          = targetType.GetField("_decalMPB", MEMBERS);
                }

                // The text half is optional: without it image and flag decals still
                // render, they just don't get the pending-text refresh.
                _textType = LsReflect.FindType(ASSEMBLY, "ConformalDecals.ModuleConformalText");
                if (_textType != null)
                {
                    _fCurrentText = _textType.GetField("_currentText", MEMBERS);
                    _mUpdateText = _textType.GetMethod("UpdateText", MEMBERS, null,
                        new[] { typeof(bool) }, null);
                }

                if (_fTargets == null || _fDecalMaterial == null ||
                    _fTargetMesh == null || _fTarget == null || _fDecalMpb == null)
                {
                    Debug.LogWarning("[GeneKerman] ConformalDecals is installed but its internals "
                        + "don't match what DecalCapture expects — blueprint decals disabled.");
                    _moduleType = null;
                    return;
                }

                Debug.Log("[GeneKerman] ConformalDecals detected — decals will be redrawn onto the capture layer.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] DecalCapture probe failed: {ex.Message}");
                _moduleType = null;
            }
        }
    }
}
