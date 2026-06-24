/*
 * GeneKermanScale.cs – A version-independent part-rescaler ("applicator").
 *
 * THE PROBLEM. TweakScale rescales parts by storing a scale *factor* on the part and
 * recomputing every derived value (model size, attach-node offsets, mass, thrust …)
 * at load time from its own ScaleExponent config. Two players on different TweakScale
 * versions/forks reconstruct the SAME factor into DIFFERENT crafts — the parts drift,
 * the stats diverge, and a player without TweakScale loads everything at stock size.
 *
 * THE FIX. We don't recompute anything. On the SENDER (who has TweakScale and a working
 * scaled craft) ScaleBridge snapshots the ALREADY-COMPUTED absolute values — the linear
 * model-scale factor TweakScale actually applied, the final dry mass, and a few module
 * stats — into this module's persistent fields. On every receiver this module simply
 * RE-APPLIES those absolute values: scale the model by `gkLinear`, offset the attach
 * nodes, force the stored mass, paste the stored module stats. No exponent table is ever
 * consulted, so the craft is identical for everyone regardless of which TweakScale (if
 * any) they run.
 *
 * The module is added to every part prefab (dormant, gkLinear=1) by the ModuleManager
 * patch in GameData/BoundlessMissions/Patches. A dormant instance is a pure no-op; only a craft
 * that carried snapshot data (gkActive=true) does anything.
 *
 * RESOURCES are deliberately NOT handled here: resource maxAmount is a persistent field
 * that already travels in the .craft / ProtoVessel serialization, so it reconstructs
 * correctly with no help from us.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public class GeneKermanScale : PartModule
    {
        // ── Persistent snapshot (filled in by ScaleBridge on the sender) ─────────

        /// <summary>False on a stock/dormant instance → this module does nothing.</summary>
        [KSPField(isPersistant = true)] public bool gkActive = false;

        /// <summary>Linear model-scale multiplier the sender actually applied
        /// (final model localScale ÷ prefab model localScale). 1 = unscaled.</summary>
        [KSPField(isPersistant = true)] public float gkLinear = 1f;

        /// <summary>Final dry mass (tonnes) to force, or ≤0 to leave the prefab/stock mass.</summary>
        [KSPField(isPersistant = true)] public float gkMass = -1f;

        /// <summary>Curated module stats to force, encoded as
        /// "TypeName:index:field=value|TypeName:index:field=value|…" (no spaces).
        /// See <see cref="StatFields"/> for which fields travel.</summary>
        [KSPField(isPersistant = true)] public string gkFields = "";

        // ── Persistent EDITOR layout pin (filled in by ScaleBridge on the sender) ──
        //
        // Surface attachment to a rescaled parent only reconstructs correctly with KSP-Recall
        // (or matching TweakScale) re-seating the child off the scaled collider. Without it,
        // KSP re-seats the child off the PREFAB (unscaled) node and the part collapses inward
        // or flings off (radial structures especially). Baking the part's correct `pos` into
        // the craft is not enough: KSP's re-seat overrides `pos` a few frames after load and on
        // every undo/redo. So we also record each part's correct position in ROOT-LOCAL space
        // (frame-independent) and re-assert it over the settle window, pinning the layout the
        // sender actually had regardless of how KSP re-seats. Independent of gkActive so an
        // unscaled child of a scaled parent can be pinned too. Editor-only.

        /// <summary>True if this part carries a baked editor-layout pin.</summary>
        [KSPField(isPersistant = true)] public bool gkPin = false;

        /// <summary>The part's correct position relative to the ship root, expressed in the
        /// root part's local frame (so it survives the ship being placed differently on the
        /// receiver). Applied by <see cref="ApplyPin"/>.</summary>
        [KSPField(isPersistant = true)] public Vector3 gkPinPos = Vector3.zero;

        // ── Which module fields we snapshot & restore ────────────────────────────
        //
        // We carry only simple scalar (float) fields whose value TweakScale scales.
        // The KEY is the runtime type name (Module*.GetType().Name); the VALUE is the
        // list of public float fields to copy. This table is the single source of truth
        // shared with ScaleBridge's snapshot side. Extend it to cover more part types —
        // nothing else needs to change.

        public static readonly Dictionary<string, string[]> StatFields =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "ModuleEngines",       new[] { "maxThrust", "minThrust" } },
            { "ModuleEnginesFX",     new[] { "maxThrust", "minThrust" } },
            { "ModuleRCSFX",         new[] { "thrusterPower" } },
            { "ModuleRCS",           new[] { "thrusterPower" } },
            { "ModuleReactionWheel", new[] { "PitchTorque", "YawTorque", "RollTorque" } },
        };

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            if (!gkActive && !gkPin) return;

            // Apply the GEOMETRY (model + attach nodes) as early as possible. OnLoad runs
            // before KSP builds the physics joints between stacked parts in flight, so the
            // joints anchor on the already-scaled node positions instead of compressing the
            // structure inward (which would then get persisted into orgPos on save). OnStart
            // re-applies everything (incl. mass/stats/drag, and to win over TweakScale's
            // module ordering); the geometry calls here are idempotent so re-running is safe.
            try
            {
                if (gkActive && gkLinear > 0f) { ScaleModel(); ScaleNodes(); }
                ApplyPin();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GeneKermanScale.OnLoad scale failed on '{part?.partInfo?.name}': {ex.Message}");
            }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!gkActive && !gkPin) return;

            ApplyScale();

            // TweakScale (if present) and KSP's own surface-attach re-projection both run on
            // start, the latter finishing a few frames AFTER load — so a single re-apply can be
            // partially overwritten (the panel moves out but not all the way). Re-assert the
            // geometry over several frames so we settle last. Idempotent, so this just converges.
            try { part.StartCoroutine(ReapplyGeometry()); }
            catch { /* no coroutine host (prefab compile) — the OnStart pass is enough */ }
        }

        private IEnumerator ReapplyGeometry()
        {
            for (int i = 0; i < 12; i++)
            {
                yield return new WaitForFixedUpdate();
                ApplyGeometry();
            }
        }

        /// <summary>Re-assert the scaled geometry after the editor rebuilds the ship (undo/redo).
        /// That rebuild re-runs KSP's surface-attach re-projection without re-triggering OnStart,
        /// so a surface-attached child reverts onto its prefab/unscaled node positions and gets
        /// flung off the vessel. Restart the same multi-frame settle OnStart uses so we converge
        /// last. No-op on dormant/unscaled parts; driven by <see cref="ScaleEditorReapply"/>.</summary>
        public void RequestGeometryReassert()
        {
            if (!gkActive && !gkPin) return;
            try { StartCoroutine(ReapplyGeometry()); }
            catch { ApplyGeometry(); } // no coroutine host — single best-effort pass
        }

        // ── The applicator ───────────────────────────────────────────────────────

        /// <summary>Re-apply the stored absolute values. Idempotent: every quantity is
        /// derived from the prefab/original baseline (never from the current live value),
        /// so calling it repeatedly converges instead of compounding.</summary>
        public void ApplyScale()
        {
            if (!gkActive && !gkPin) return;
            try
            {
                ApplyGeometry();
                if (gkActive && gkLinear > 0f)
                {
                    if (gkMass > 0f) part.mass = gkMass;
                    ApplyStatFields();
                    RebuildDragCubes();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GeneKermanScale.ApplyScale failed on '{part?.partInfo?.name}': {ex.Message}");
            }
        }

        /// <summary>The cheap, repeatable part of the apply: model scale, attach-node offsets,
        /// and the editor-layout pin. Safe to call every frame (idempotent); excludes the
        /// one-shot mass/stat/drag-cube work in <see cref="ApplyScale"/>.</summary>
        private void ApplyGeometry()
        {
            if (gkActive && gkLinear > 0f)
            {
                ScaleModel();
                ScaleNodes();
            }
            ApplyPin();
        }

        /// <summary>Force the part back to the baked layout position the sender had, re-anchored
        /// on the live ship root so it's independent of where the ship sits on the receiver.
        /// Counters KSP's surface-attach re-seat (which otherwise pulls a child off a rescaled
        /// parent). Editor-only; in flight physics owns the layout. Each part is pinned on its
        /// own — moving a parent does not move its children's transforms in the editor — so the
        /// whole layout reconstructs exactly when every part re-asserts.</summary>
        private void ApplyPin()
        {
            if (!gkPin || !HighLogic.LoadedSceneIsEditor) return;

            Part root = part.localRoot;
            if (root == null || root == part) return; // the root is its own anchor — nothing to do

            Transform rt = root.transform;
            part.transform.position = rt.position + rt.rotation * gkPinPos;
        }

        private void ScaleModel()
        {
            Transform model = part.transform.Find("model");
            Transform prefabModel = part.partInfo?.partPrefab?.transform?.Find("model");
            if (model == null || prefabModel == null) return;

            // Scale the MODEL child, never part.transform — the latter would also scale
            // attached child parts (that's why TweakScale does the same).
            model.localScale = prefabModel.localScale * gkLinear;
            try { part.transform.hasChanged = true; } catch { }
        }

        private void ScaleNodes()
        {
            // Offset attach nodes so the scaled model's faces and the connection points stay
            // coincident. We must scale from the PREFAB node position, NOT the live instance's
            // originalPosition: by the time we run, other handlers (B9PartSwitch, KSPCF, KSP's
            // own craft load) have already re-baselined the live originalPosition to the SCALED
            // value, so `originalPosition * gkLinear` double-applies — the node lands at
            // prefab*gkLinear² and floats far off the model (and can compound across the
            // multi-frame re-apply). The prefab is never scaled, so prefab*gkLinear is the
            // correct single-scale target in every load path.
            Part prefab = part.partInfo?.partPrefab;

            if (part.attachNodes != null)
                foreach (AttachNode n in part.attachNodes)
                {
                    if (n == null) continue;
                    AttachNode pn = FindPrefabNode(prefab, n.id);
                    n.position = (pn != null ? pn.originalPosition : n.originalPosition) * gkLinear;
                }

            if (part.srfAttachNode != null)
            {
                Vector3 baseSrf = prefab?.srfAttachNode != null
                    ? prefab.srfAttachNode.originalPosition
                    : part.srfAttachNode.originalPosition;
                part.srfAttachNode.position = baseSrf * gkLinear;
            }

            LogNodeGeometryOnce();
        }

        /// <summary>The prefab's attach node with the given id (the immutable, never-scaled
        /// baseline), or null if the prefab/node can't be found.</summary>
        private static AttachNode FindPrefabNode(Part prefab, string id)
        {
            if (prefab?.attachNodes == null || string.IsNullOrEmpty(id)) return null;
            foreach (AttachNode pn in prefab.attachNodes)
                if (pn != null && pn.id == id) return pn;
            return null;
        }

        // ── Diagnostics ──────────────────────────────────────────────────────────
        //
        // Temporary instrumentation for the "floating / hidden attach node on a heavily
        // rescaled part" report. Fires once per part name so the log isn't spammed by the
        // multi-frame re-apply. Dumps, per node: the prefab baseline we scale from
        // (originalPosition), what we wrote (position), and whether the node is bound to a
        // model transform (nodeTransform != null) — KSP recomputes those from the scaled
        // model itself, so our manual multiply can double-apply (node flies out) or, if the
        // transform sits outside "model", read ~1× (node buried inside the hull). Remove once
        // the node-positioning fix lands.

        private static readonly HashSet<string> _loggedNodes = new HashSet<string>(StringComparer.Ordinal);

        private void LogNodeGeometryOnce()
        {
            try
            {
                string key = part?.partInfo?.name ?? "?";
                if (!_loggedNodes.Add(key)) return;

                Transform model = part.transform.Find("model");
                Transform prefabModel = part.partInfo?.partPrefab?.transform?.Find("model");
                var sb = new StringBuilder();
                sb.Append($"[GeneKerman] node-geometry '{key}' gkLinear={gkLinear} ")
                  .Append($"modelScale={(model != null ? model.localScale.ToString("F3") : "?")} ")
                  .Append($"prefabModelScale={(prefabModel != null ? prefabModel.localScale.ToString("F3") : "?")}");

                Part prefab = part.partInfo?.partPrefab;
                if (part.attachNodes != null)
                    foreach (AttachNode n in part.attachNodes)
                        if (n != null)
                        {
                            AttachNode pn = FindPrefabNode(prefab, n.id);
                            sb.Append($"\n  node '{n.id}' prefab={(pn != null ? pn.originalPosition.ToString("F3") : "?")} ")
                              .Append($"liveOrig={n.originalPosition.ToString("F3")} ")
                              .Append($"pos={n.position.ToString("F3")} ")
                              .Append($"xformBound={(n.nodeTransform != null)}");
                        }

                if (part.srfAttachNode != null)
                    sb.Append($"\n  srfNode orig={part.srfAttachNode.originalPosition.ToString("F3")} ")
                      .Append($"pos={part.srfAttachNode.position.ToString("F3")}");

                Debug.Log(sb.ToString());
            }
            catch { /* diagnostics must never break the apply */ }
        }

        private void ApplyStatFields()
        {
            if (string.IsNullOrEmpty(gkFields)) return;

            foreach (string entry in gkFields.Split('|'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                // "TypeName:index:field=value"
                int colon1 = entry.IndexOf(':');
                int colon2 = colon1 >= 0 ? entry.IndexOf(':', colon1 + 1) : -1;
                int eq = entry.IndexOf('=', colon2 + 1);
                if (colon1 < 0 || colon2 < 0 || eq < 0) continue;

                string typeName = entry.Substring(0, colon1);
                if (!int.TryParse(entry.Substring(colon1 + 1, colon2 - colon1 - 1), out int idx)) continue;
                string field = entry.Substring(colon2 + 1, eq - colon2 - 1);
                if (!float.TryParse(entry.Substring(eq + 1), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out float val)) continue;

                PartModule target = FindModule(typeName, idx);
                if (target == null) continue;

                FieldInfo fi = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
                if (fi != null && fi.FieldType == typeof(float))
                    fi.SetValue(target, val);
            }
        }

        /// <summary>The <paramref name="idx"/>-th module whose runtime type name is
        /// <paramref name="typeName"/> (matches the ordering ScaleBridge snapshotted in).</summary>
        private PartModule FindModule(string typeName, int idx)
        {
            int seen = 0;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule pm = part.Modules[i];
                if (pm == null || pm.GetType().Name != typeName) continue;
                if (seen == idx) return pm;
                seen++;
            }
            return null;
        }

        private void RebuildDragCubes()
        {
            // Drag cubes are rendered from the model silhouette, so a scaled model needs
            // them rebuilt or aero/reentry use the wrong size. Best-effort: the model must
            // be present and the call is comparatively expensive, so failures are non-fatal.
            try
            {
                if (part.DragCubes != null)
                    part.DragCubes.ForceUpdate(true, true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GeneKermanScale: drag-cube rebuild skipped on '{part?.partInfo?.name}': {ex.Message}");
            }
        }

        // ── Snapshot encoding helper (used by ScaleBridge) ───────────────────────

        /// <summary>Encode one stat field into the gkFields wire format.</summary>
        public static string EncodeStat(string typeName, int idx, string field, float value)
            => new StringBuilder()
                .Append(typeName).Append(':').Append(idx).Append(':')
                .Append(field).Append('=')
                .Append(value.ToString("R", CultureInfo.InvariantCulture))
                .ToString();
    }
}
