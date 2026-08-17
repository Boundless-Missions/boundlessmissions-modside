/*
 * ScaleBridge.cs – Snapshot (send) and neutralize (receive) for GeneKermanScale.
 *
 * SEND: SnapshotIntoVesselNode() runs on the sender right after a vessel is serialized.
 * For each part TweakScale actually rescaled it reads the FINAL, already-computed values
 * off the live Part — the linear model-scale factor, the dry mass, and the curated module
 * stats in GeneKermanScale.StatFields — and writes them into that part's (dormant)
 * GeneKermanScale MODULE node. No exponent math: we copy results, not formulas.
 *
 * RECEIVE: NeutralizeTweakScaleForImport() runs on import. For any part that carries an
 * active snapshot it removes the part's TweakScale MODULE node, so the receiver's
 * TweakScale (whatever version, if any) stays at default 1× and our GeneKermanScale is
 * the single authority. That is what makes a transferred craft identical for everyone,
 * which TweakScale's factor+exponent scheme cannot guarantee across versions/forks.
 *
 * Both transfer paths are covered, and both read values off LIVE parts (TweakScale has
 * already computed them there):
 *   • Live-vessel (ProtoVessel) transfer — SnapshotIntoVesselNode on the flight parts,
 *     with TweakScale neutralized on import (NeutralizeTweakScaleForImport).
 *   • .craft blueprint transfer — SnapshotIntoCraftBytes on the editor/flight parts at
 *     submit time, which both snapshots and neutralizes TweakScale in one pass (a blueprint
 *     has no separate import step). Parts are matched by craftID.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class ScaleBridge
    {
        private const string MODULE_NAME = "GeneKermanScale";
        private const string TWEAKSCALE_MODULE = "TweakScale";

        // A part whose model scale is within this of 1.0 is treated as unscaled.
        private const float SCALE_EPSILON = 0.001f;

        // ── SEND ─────────────────────────────────────────────────────────────────

        /// <summary>Snapshot every rescaled part of <paramref name="vessel"/> into the
        /// matching PART node of <paramref name="vesselNode"/> (a serialized "VESSEL").
        /// No-op for unscaled parts. Best-effort: any single part that can't be read is
        /// skipped without aborting the rest.</summary>
        public static void SnapshotIntoVesselNode(Vessel vessel, ConfigNode vesselNode)
        {
            if (vessel == null || vessel.parts == null || vesselNode == null) return;

            try
            {
                ConfigNode[] partNodes = vesselNode.GetNodes("PART");
                // ProtoVessel.Save emits PART nodes in vessel.parts order; pair by index
                // and sanity-check the part name so a mismatch can never write to the
                // wrong part.
                int n = Math.Min(partNodes.Length, vessel.parts.Count);
                int scaled = 0;

                for (int i = 0; i < n; i++)
                {
                    Part part = vessel.parts[i];
                    ConfigNode partNode = partNodes[i];
                    if (part?.partInfo == null) continue;

                    string nodeName = partNode.GetValue("name");
                    if (!string.IsNullOrEmpty(nodeName) && nodeName != part.partInfo.name)
                        continue; // ordering assumption broke — skip rather than corrupt

                    if (SnapshotPart(part, partNode)) scaled++;
                }

                if (scaled > 0)
                    Debug.Log($"[GeneKerman] ScaleBridge: snapshotted {scaled} rescaled part(s) on '{vessel.vesselName}'.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ScaleBridge.SnapshotIntoVesselNode failed: {ex.Message}");
            }
        }

        /// <summary>Read the live scale of one part and write it into its GeneKermanScale
        /// node. Returns true if the part was scaled (and a snapshot was written).</summary>
        private static bool SnapshotPart(Part part, ConfigNode partNode)
        {
            float linear = LinearScaleOf(part);
            if (Math.Abs(linear - 1f) < SCALE_EPSILON) return false; // unscaled

            ConfigNode mod = FindOrAddModuleNode(partNode, MODULE_NAME);

            mod.SetValue("gkActive", "True", true);
            mod.SetValue("gkLinear", linear.ToString("R", CultureInfo.InvariantCulture), true);
            mod.SetValue("gkMass", part.mass.ToString("R", CultureInfo.InvariantCulture), true);
            mod.SetValue("gkFields", EncodeStatFields(part), true);
            return true;
        }

        /// <summary>Final model localScale ÷ prefab model localScale (uniform-scale
        /// assumption — TweakScale's common case). 1 if it can't be determined.</summary>
        private static float LinearScaleOf(Part part)
        {
            try
            {
                // Primary: the linear factor actually baked onto the model transform.
                Transform model = part.transform.Find("model");
                Transform prefabModel = part.partInfo?.partPrefab?.transform?.Find("model");
                if (model != null && prefabModel != null)
                {
                    float baseScale = prefabModel.localScale.x;
                    if (Math.Abs(baseScale) >= 1e-6f)
                    {
                        float fromModel = model.localScale.x / baseScale;
                        if (Math.Abs(fromModel - 1f) >= SCALE_EPSILON) return fromModel;
                    }
                }

                // Fallback: some parts (expandable/animated — e.g. the SSPX centrifuge) don't
                // carry TweakScale's factor on the "model" transform, so the ratio above reads
                // ~1 even though the part is scaled. Read the factor straight off the live
                // TweakScale module (currentScale / defaultScale): a pure linear number,
                // version-independent for geometry.
                float fromTweakScale = TweakScaleFactor(part);
                if (Math.Abs(fromTweakScale - 1f) >= SCALE_EPSILON) return fromTweakScale;

                return 1f;
            }
            catch { return 1f; }
        }

        /// <summary>The linear factor TweakScale applied, read as currentScale / defaultScale off
        /// the live module via reflection (1 if absent/unreadable). The fields are floating-point
        /// — double in Lisias' fork, float in others — so we accept either.</summary>
        private static float TweakScaleFactor(Part part)
        {
            if (part?.Modules == null) return 1f;
            foreach (PartModule pm in part.Modules)
            {
                if (pm == null || pm.GetType().Name != TWEAKSCALE_MODULE) continue;
                double cur = ReadFloatingField(pm, "currentScale");
                double def = ReadFloatingField(pm, "defaultScale");
                return (def > 1e-6 && cur > 0) ? (float)(cur / def) : 1f;
            }
            return 1f;
        }

        private static double ReadFloatingField(object obj, string name)
        {
            FieldInfo fi = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            object v = fi?.GetValue(obj);
            if (v is double d) return d;
            if (v is float f) return f;
            return double.NaN;
        }

        /// <summary>Encode the curated stat fields (see GeneKermanScale.StatFields) of a
        /// part's live modules into the gkFields wire string.</summary>
        private static string EncodeStatFields(Part part)
        {
            var parts = new List<string>();
            var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (PartModule pm in part.Modules)
            {
                if (pm == null) continue;
                string typeName = pm.GetType().Name;
                if (!GeneKermanScale.StatFields.TryGetValue(typeName, out string[] fields))
                    continue;

                int idx = typeCounts.TryGetValue(typeName, out int c) ? c : 0;
                typeCounts[typeName] = idx + 1;

                foreach (string field in fields)
                {
                    FieldInfo fi = pm.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
                    if (fi == null || fi.FieldType != typeof(float)) continue;
                    float val = (float)fi.GetValue(pm);
                    parts.Add(GeneKermanScale.EncodeStat(typeName, idx, field, val));
                }
            }

            return string.Join("|", parts.ToArray());
        }

        // ── SEND: .craft blueprint path ──────────────────────────────────────────

        /// <summary>Bake the ship currently open in the editor into a blueprint read off
        /// disk. Every path that sends a .craft to another player MUST run this (or
        /// <see cref="SnapshotIntoCraftBytes"/> directly) before embedding anything else —
        /// a blueprint has no import-side scale step, so a craft that leaves unbaked
        /// cannot be repaired on arrival.
        ///
        /// It exists because that step was missed on every export path except contract
        /// submission — quicksend, marketplace listings, export-to-file and the blueprint
        /// attached to a vessel transfer all shipped raw TweakScale data, and the crafts
        /// arrived broken in three separate ways (unbaked scale, un-re-anchored surface
        /// attachments, and no layout pin to re-assert against KSP's re-seat). One call
        /// that finds its own parts is much harder to forget than a two-argument one.
        ///
        /// No-op outside the editor, on an empty ship, or on an unscaled craft.</summary>
        public static byte[] BakeEditorCraft(byte[] craftBytes)
        {
            var parts = EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                ? EditorLogic.fetch.ship.parts : null;
            if (parts == null || parts.Count == 0) return craftBytes;
            return SnapshotIntoCraftBytes(craftBytes, parts);
        }

        /// <summary>Snapshot the live editor/flight parts' TweakScale-computed scale into
        /// the matching PART nodes of a .craft blueprint, and neutralize TweakScale on those
        /// parts so the blueprint reconstructs identically for every receiver. Parts are
        /// matched by craftID (the `part = name_&lt;craftID&gt;` suffix), so part ordering is
        /// irrelevant. Returns the rewritten bytes, or the input unchanged if nothing was
        /// scaled / on any failure.</summary>
        public static byte[] SnapshotIntoCraftBytes(byte[] craftBytes, IList<Part> liveParts)
        {
            if (craftBytes == null || craftBytes.Length == 0 || liveParts == null) return craftBytes;
            try
            {
                ConfigNode root = LoadBareCraft(craftBytes);
                ConfigNode[] partNodes = root?.GetNodes("PART");
                if (partNodes == null || partNodes.Length == 0) return craftBytes;

                var byId = new Dictionary<uint, Part>();
                foreach (Part p in liveParts)
                    if (p != null) byId[p.craftID] = p;

                var matched = new Part[partNodes.Length]; // node → live part (for the pos rewrite)
                int scaled = 0;
                for (int i = 0; i < partNodes.Length; i++)
                {
                    Part part = MatchCraftPart(partNodes[i], byId, liveParts, i);
                    matched[i] = part;
                    if (part?.partInfo == null) continue;

                    if (SnapshotPart(part, partNodes[i]))
                    {
                        // On the sender we strip TweakScale right here (no separate import
                        // step exists for a blueprint), making GeneKermanScale authoritative.
                        RemoveModuleNode(partNodes[i], TWEAKSCALE_MODULE);
                        scaled++;
                    }
                }

                if (scaled == 0) return craftBytes; // unscaled craft — leave it byte-for-byte

                RewritePositionsFromLive(partNodes, matched, liveParts);

                Debug.Log($"[GeneKerman] ScaleBridge: snapshotted {scaled} rescaled part(s) into craft blueprint.");
                return Encoding.UTF8.GetBytes(SerializeBareCraft(root));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ScaleBridge.SnapshotIntoCraftBytes failed: {ex.Message}");
                return craftBytes;
            }
        }

        /// <summary>Overwrite each artifacted part's craft <c>pos</c> with its true live
        /// position. KSP serializes a surface-attached part on a SCALED parent using a
        /// KSP-Recall-dependent attachment encoding, so the raw craft <c>pos</c> only renders
        /// correctly WITH KSP-Recall installed; the live editor transform is the actually-
        /// correct layout. We re-anchor every part on the (artifact-free) root part and rewrite
        /// ONLY parts whose live position disagrees with the craft — correctly-placed parts are
        /// left byte-identical, so this is surgical: it touches the misplaced parts and nothing
        /// else.</summary>
        private static void RewritePositionsFromLive(ConfigNode[] partNodes, Part[] matched, IList<Part> liveParts)
        {
            try
            {
                Part liveRoot = null;
                foreach (Part p in liveParts)
                    if (p != null && p.parent == null) { liveRoot = p; break; }
                if (liveRoot == null) return;

                // Root's craft pos is the anchor shared by the craft frame and the live frame.
                // If we can't locate it, bail rather than risk mis-anchoring the whole craft.
                bool haveRoot = false;
                Vector3 rootCraftPos = Vector3.zero;
                for (int i = 0; i < matched.Length; i++)
                    if (matched[i] == liveRoot && TryParseVec3(partNodes[i].GetValue("pos"), out rootCraftPos))
                    { haveRoot = true; break; }
                if (!haveRoot) return;

                Vector3 liveRootPos = liveRoot.transform.position;
                Quaternion rootInv = Quaternion.Inverse(liveRoot.transform.rotation);
                int fixedCount = 0;
                int pinnedCount = 0;
                for (int i = 0; i < partNodes.Length; i++)
                {
                    Part part = matched[i];
                    if (part == null) continue;

                    Vector3 liveOffset = part.transform.position - liveRootPos;

                    // Bake a ROOT-LOCAL layout pin into every part. The `pos` rewrite below makes
                    // the craft LOAD correct, but KSP's surface-attach re-seat overrides `pos` a
                    // few frames later and on every undo; the pin lets GeneKermanScale re-assert
                    // the true layout at runtime. Root-local so it survives the receiver placing
                    // the ship differently. Skip the root itself (it is its own anchor).
                    if (part != liveRoot)
                    {
                        WritePin(partNodes[i], rootInv * liveOffset);
                        pinnedCount++;
                    }

                    if (!TryParseVec3(partNodes[i].GetValue("pos"), out Vector3 craftPos)) continue;

                    Vector3 craftOffset = craftPos - rootCraftPos;
                    if ((liveOffset - craftOffset).sqrMagnitude < 0.0025f) continue; // <0.05m: already correct

                    partNodes[i].SetValue("pos", FormatVec3(rootCraftPos + liveOffset), true);
                    fixedCount++;
                }

                if (fixedCount > 0 || pinnedCount > 0)
                    Debug.Log($"[GeneKerman] ScaleBridge: re-anchored {fixedCount} part position(s) and pinned {pinnedCount} to the live layout.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ScaleBridge.RewritePositionsFromLive failed: {ex.Message}");
            }
        }

        private static bool TryParseVec3(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            string[] a = s.Split(',');
            if (a.Length != 3) return false;
            if (float.TryParse(a[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(a[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(a[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            { v = new Vector3(x, y, z); return true; }
            return false;
        }

        private static string FormatVec3(Vector3 v)
            => v.x.ToString("R", CultureInfo.InvariantCulture) + "," +
               v.y.ToString("R", CultureInfo.InvariantCulture) + "," +
               v.z.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Resolve a .craft PART node to its live Part by craftID (exact), falling
        /// back to index alignment if the id can't be parsed.</summary>
        private static Part MatchCraftPart(ConfigNode partNode, Dictionary<uint, Part> byId,
                                           IList<Part> liveParts, int index)
        {
            string pv = partNode.GetValue("part"); // "mk1pod_4294880972"
            if (!string.IsNullOrEmpty(pv))
            {
                int us = pv.LastIndexOf('_');
                if (us > 0 && uint.TryParse(pv.Substring(us + 1), out uint id) &&
                    byId.TryGetValue(id, out Part p))
                    return p;
            }
            return index < liveParts.Count ? liveParts[index] : null;
        }

        /// <summary>Load .craft bytes (a node-less top-level ConfigNode) via a temp file —
        /// the only reliable parse, matching FlagTransfer's approach.</summary>
        private static ConfigNode LoadBareCraft(byte[] bytes)
        {
            string tempPath = System.IO.Path.Combine(
                KSPUtil.ApplicationRootPath, "PluginData", "GeneKerman_scale_tmp.cfg");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempPath));
            System.IO.File.WriteAllBytes(tempPath, bytes);
            ConfigNode node = ConfigNode.Load(tempPath);
            try { System.IO.File.Delete(tempPath); } catch { }
            return node;
        }

        /// <summary>Re-serialize a .craft's root node WITHOUT the enclosing `root { }` that
        /// ConfigNode.ToString() would add (which KSP's craft loader rejects): write the
        /// top-level values first, then each child node. PART nodes serialize themselves.</summary>
        private static string SerializeBareCraft(ConfigNode root)
        {
            var sb = new StringBuilder();
            foreach (ConfigNode.Value v in root.values)
                sb.Append(v.name).Append(" = ").Append(v.value).Append('\n');
            foreach (ConfigNode child in root.nodes)
                sb.Append(child.ToString());
            return sb.ToString();
        }

        // ── RECEIVE ────────────────────────────────────────────────────────────────

        /// <summary>For every PART in <paramref name="vesselNode"/> that carries an active
        /// GeneKermanScale snapshot, strip its TweakScale MODULE node so TweakScale stays
        /// at default 1× and GeneKermanScale alone drives the part. No-op for parts without
        /// a snapshot (so pre-feature crafts and unscaled parts are untouched).</summary>
        public static void NeutralizeTweakScaleForImport(ConfigNode vesselNode)
        {
            if (vesselNode == null) return;
            try
            {
                int stripped = 0;
                foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
                {
                    if (!HasActiveSnapshot(partNode)) continue;
                    if (RemoveModuleNode(partNode, TWEAKSCALE_MODULE)) stripped++;
                }
                if (stripped > 0)
                    Debug.Log($"[GeneKerman] ScaleBridge: neutralized TweakScale on {stripped} rescaled part(s) — GeneKermanScale is authoritative.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ScaleBridge.NeutralizeTweakScaleForImport failed: {ex.Message}");
            }
        }

        private static bool HasActiveSnapshot(ConfigNode partNode)
        {
            foreach (ConfigNode mod in partNode.GetNodes("MODULE"))
            {
                if (mod.GetValue("name") != MODULE_NAME) continue;
                bool active;
                return bool.TryParse(mod.GetValue("gkActive"), out active) && active;
            }
            return false;
        }

        // ── ConfigNode helpers ───────────────────────────────────────────────────

        /// <summary>Record a root-local layout pin into a part node's GeneKermanScale MODULE so
        /// the receiver can re-assert the true position over KSP's surface-attach re-seat. The
        /// node exists on every part (ModuleManager patch); we add one only if it somehow doesn't.</summary>
        private static void WritePin(ConfigNode partNode, Vector3 rootLocalPos)
        {
            ConfigNode mod = FindOrAddModuleNode(partNode, MODULE_NAME);
            mod.SetValue("gkPin", "True", true);
            mod.SetValue("gkPinPos", FormatVec3(rootLocalPos), true);
        }

        private static ConfigNode FindOrAddModuleNode(ConfigNode partNode, string moduleName)
        {
            foreach (ConfigNode mod in partNode.GetNodes("MODULE"))
                if (mod.GetValue("name") == moduleName) return mod;

            // The ModuleManager patch normally guarantees the node exists; add one if a
            // part somehow lacks it (e.g. a part excluded from the patch).
            ConfigNode added = partNode.AddNode("MODULE");
            added.AddValue("name", moduleName);
            return added;
        }

        private static bool RemoveModuleNode(ConfigNode partNode, string moduleName)
        {
            bool removed = false;
            // Rebuild: ConfigNode has no remove-by-predicate, and removing while iterating
            // GetNodes() is unsafe, so collect survivors and re-add.
            var survivors = new List<ConfigNode>();
            foreach (ConfigNode mod in partNode.GetNodes("MODULE"))
            {
                if (mod.GetValue("name") == moduleName) { removed = true; continue; }
                survivors.Add(mod);
            }
            if (!removed) return false;

            while (partNode.GetNode("MODULE") != null)
                partNode.RemoveNode("MODULE");
            foreach (ConfigNode mod in survivors)
                partNode.AddNode(mod);
            return true;
        }
    }
}
