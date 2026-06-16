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
 * patch in GameData/GeneKerman/Patches. A dormant instance is a pure no-op; only a craft
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

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!gkActive) return;

            ApplyScale();

            // TweakScale (if present on this part) also runs on start; whichever module
            // applies LAST wins. Re-assert a frame later so we are authoritative no matter
            // the module order. `part` is a MonoBehaviour, so it can host the coroutine.
            try { part.StartCoroutine(ReapplyNextFrame()); }
            catch { /* no coroutine host (prefab compile) — the OnStart pass is enough */ }
        }

        private IEnumerator ReapplyNextFrame()
        {
            yield return new WaitForFixedUpdate();
            ApplyScale();
        }

        // ── The applicator ───────────────────────────────────────────────────────

        /// <summary>Re-apply the stored absolute values. Idempotent: every quantity is
        /// derived from the prefab/original baseline (never from the current live value),
        /// so calling it repeatedly converges instead of compounding.</summary>
        public void ApplyScale()
        {
            if (!gkActive || gkLinear <= 0f) return;
            try
            {
                ScaleModel();
                ScaleNodes();
                if (gkMass > 0f) part.mass = gkMass;
                ApplyStatFields();
                RebuildDragCubes();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GeneKermanScale.ApplyScale failed on '{part?.partInfo?.name}': {ex.Message}");
            }
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
            // Offset attach nodes from their ORIGINAL (prefab) position so the scaled
            // model's faces and the connection points stay coincident.
            if (part.attachNodes != null)
                foreach (AttachNode n in part.attachNodes)
                    if (n != null) n.position = n.originalPosition * gkLinear;

            if (part.srfAttachNode != null)
                part.srfAttachNode.position = part.srfAttachNode.originalPosition * gkLinear;
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
