/*
 * ReforgedTransfer.cs – Carry a craft's Reforged Materials Redux paint job between
 * players, and make a craft painted with it load cleanly for a recipient without it.
 *
 * The sibling of TextureTransfer, for the other recolour system this project meets in
 * the wild. Reforged Redux is a Textures Unlimited *addon* — TU supplies the PBR
 * shaders, Reforged supplies the in-editor painter — but it shares none of TU's
 * plumbing, which is why TextureTransfer is blind to it: it defines no KSP_TEXTURE_SET,
 * attaches no KSPTextureSwitch, and keeps the player's choices in its own
 * `ModuleReforged` persistent fields (colour, metal/smoothness, region, zone and dirt
 * stacks). A craft painted gold with Reforged therefore listed as stock and arrived at
 * the recipient with a row of modules nothing on their install could consume.
 *
 * Two differences from TextureTransfer decide the shape of this file:
 *
 *   1. NO SIDE-CHANNEL BLOCK. TU needs one because a texture set resolves to its pack's
 *      GameData folder only on the sender's machine. Reforged has no such indirection:
 *      one mod, one folder, and its paint state already rides in the craft's own MODULE
 *      nodes. So there is nothing to carry, nothing to strip, and the seven export
 *      chains (bake → GKFLAG → GKTSVER → GKTU → GKRF → GKMODS → GKTHUMB) are untouched.
 *
 *   2. CARRYING THE MODULE MEANS NOTHING. Reforged's single ModuleManager patch adds
 *      ModuleReforged to *every* loaded part, painted or not ("parts stay completely
 *      stock until you actually paint them"), so every craft saved on a Reforged install
 *      carries the module on nearly every part. The test for "this craft is painted" is
 *      therefore the strict one — is any field actually off its default — the same
 *      distinction TweakScaleGuard draws between "mentions TweakScale" and "is actually
 *      rescaled", and the opposite of TU's deliberate "carries TU state" (TU's unpainted
 *      baseline is a runtime derivation, Reforged's is written down).
 *
 * The texture FILES are never embedded, for the same reasons as TU: they belong to the
 * pack author and are far too big for a craft transfer. A user-added dirt texture from
 * the recipient's missing `ReforgedRedux/Weather/` is deliberately NOT reported either —
 * the layer stacks are the mod's own packed strings, and a token in one that matches no
 * local file is indistinguishable from a parameter that was never a file name. Guessing
 * would mean warning about textures that don't exist.
 *
 * Gated by the same `enableTextureTransfer` setting as TU: it is one question ("carry
 * paint jobs"), and a second key that only covered half the recolour mods installed
 * here would be a setting the player cannot reason about. Switched off, this still scans
 * and reports but writes nothing — the PartAliases contract.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class ReforgedTransfer
    {
        /// <summary>The one PartModule Reforged patches onto parts.</summary>
        private const string RDX_MODULE = "ModuleReforged";

        /// <summary>Reforged's GameData folder, and TU's — which it requires and cannot
        /// render without. Only ever used as the fallback name for a recipient who has
        /// neither installed (so nothing local can be asked); a sender's real folder is
        /// read off the loaded assembly.</summary>
        private const string RDX_FOLDER = "ReforgedRedux";
        private const string TU_FOLDER = "TexturesUnlimited";

        /// <summary>Persistent fields that are only ever off their default because the
        /// player painted the part. `rdxColored`/`rdxFinish` are the two arming switches
        /// (colour and PBR finish); `rdxZones`/`rdxStack` are the per-zone and layer
        /// stacks, empty until something is placed. Everything else on the module
        /// (rdxR/G/B, rdxMetal, rdxSmooth, rdxScale, rdxRegion…) is a *parameter* of one
        /// of those, meaningless while its switch is off, so reading them would tag a
        /// craft as painted for a value that changes nothing on screen.</summary>
        private static readonly string[] PaintSwitches = { "rdxColored", "rdxFinish" };
        private static readonly string[] PaintStacks = { "rdxZones", "rdxStack" };

        /// <summary>Off switch — shared with TextureTransfer (settings.cfg
        /// `enableTextureTransfer`). Off, nothing is written: the craft keeps whatever its
        /// MODULE nodes hold, exactly as it did before this file existed.</summary>
        private static bool Enabled
        {
            get
            {
                var api = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.Api : null;
                return api == null || api.TextureTransferEnabled;
            }
        }

        // ── Local install ────────────────────────────────────────────────────

        /// <summary>Version of the locally installed Reforged Redux, or null when it isn't
        /// installed. The presence answer, not just a version string: every decision in
        /// this file about whether to keep or drop the modules asks it.</summary>
        public static string InstalledVersion()
        {
            var la = FindCore();
            if (la == null || la.assembly == null) return null;
            var ver = la.assembly.GetName().Version;
            return ver != null ? ver.ToString() : "unknown";
        }

        /// <summary>Reforged's loaded assembly, or null when it isn't installed. Exact
        /// assembly name first (it ships ReforgedRedux.dll), then the type it defines, so
        /// a repack under another file name still resolves. Never a substring match: the
        /// author's own older mod is "Reforged Materials" and is a different thing.</summary>
        private static AssemblyLoader.LoadedAssembly FindCore()
        {
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    if (string.Equals(asm.GetName().Name, "ReforgedRedux",
                                      StringComparison.OrdinalIgnoreCase))
                        return la;
                }

                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    try
                    {
                        if (asm.GetType(RDX_MODULE) != null ||
                            asm.GetType("ReforgedRedux." + RDX_MODULE) != null)
                            return la;
                    }
                    catch { /* GetType can throw on a half-loaded assembly — ignore */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ReforgedTransfer: lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>The GameData folder Reforged lives in, or the canonical name when it
        /// isn't installed here (a recipient naming it in a modpack has no local copy to
        /// read it off).</summary>
        private static string CoreFolder()
        {
            var la = FindCore();
            if (la == null) return RDX_FOLDER;
            // LoadedAssembly.url is the GameData-relative directory of the dll
            // ("ReforgedRedux/Plugins"); its first segment is the mod folder.
            string url = la.url;
            if (string.IsNullOrEmpty(url)) return RDX_FOLDER;
            string[] seg = url.Split('/');
            return seg.Length > 0 && seg[0].Length > 0 ? seg[0] : RDX_FOLDER;
        }

        /// <summary>Whether the local prefab for a part can accept a Reforged module.
        /// Reforged's patch adds one to every loaded part except EVA kerbals, so on an
        /// install that has it this is true for everything the craft could load anyway;
        /// it is the *absence* of the mod this is really asking about. An unknown part is
        /// left alone — never strip on a guess (PartAliases may have swapped it, or it
        /// may simply be missing, which is a different report's business).</summary>
        private static bool PrefabAccepts(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return false;
            try
            {
                var ap = PartLoader.getPartInfoByName(partName);
                if (ap == null || ap.partPrefab == null || ap.partPrefab.Modules == null)
                    return true;
                foreach (PartModule pm in ap.partPrefab.Modules)
                    if (pm != null && string.Equals(pm.moduleName, RDX_MODULE,
                                                    StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        // ── Marketplace / mod tagging ────────────────────────────────────────

        /// <summary>Whether a .craft carries an actual Reforged paint job — the flag behind
        /// the marketplace's "Modded Textures Available" tag, unioned with TU's answer.
        ///
        /// Strict by necessity: Reforged's module rides on every part of every craft saved
        /// on a Reforged install, so "has the module" would tag the entire install's output
        /// as painted. Run on the ORIGINAL craft bytes, before any block is appended.</summary>
        public static bool CraftHasPaint(byte[] craftBytes)
        {
            if (craftBytes == null || craftBytes.Length == 0) return false;
            try
            {
                foreach (var r in ScanCraftText(Encoding.UTF8.GetString(craftBytes)))
                    if (r.Painted) return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ReforgedTransfer.CraftHasPaint failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>The mod folders a .craft's Reforged paint job needs, for tagging a
        /// marketplace listing alongside CkanGenerator.ModFoldersForCraft — which cannot
        /// see either mod, both adding zero parts. Empty for an unpainted craft, so a
        /// Reforged install doesn't tag every craft it exports.</summary>
        public static List<string> PaintFoldersForCraft(byte[] craftBytes)
        {
            var folders = new List<string>();
            if (!CraftHasPaint(craftBytes)) return folders;
            folders.Add(CoreFolder());
            string tu = TextureTransfer.CoreFolder() ?? TU_FOLDER;
            if (!folders.Contains(tu)) folders.Add(tu);
            return folders;
        }

        // ── Import: raw .craft ───────────────────────────────────────────────

        /// <summary>Make the craft's Reforged state fit THIS install: keep every module the
        /// local prefab can accept (the paint arrives exactly as the sender left it) and
        /// drop the ones it can't, so a craft painted on a Reforged install still loads —
        /// in stock colours, with no orphan module nodes — for someone without it.
        ///
        /// Runs on the craft BODY, after every side-channel strip and after PartAliases,
        /// because a substituted part is a different prefab with different modules. There
        /// is no manifest argument: unlike TU and RealFuels, nothing had to be carried.</summary>
        public static byte[] ReconcileCraftBody(byte[] craftBytes, string context)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                var refs = ScanCraftText(text);
                if (refs.Count == 0) return craftBytes; // no Reforged modules at all

                string local = InstalledVersion();
                var report = new Report(context, local);

                // Collect the line ranges to drop, then rebuild once — removing as we go
                // would invalidate every later range.
                var drop = new HashSet<int>();
                foreach (var r in refs)
                {
                    bool keep = local != null && PrefabAccepts(r.PartName);
                    report.Note(r, keep);
                    if (keep || !Enabled) continue;
                    for (int i = r.StartLine; i <= r.EndLine; i++) drop.Add(i);
                }

                report.Post();
                if (drop.Count == 0) return craftBytes;

                string[] lines = text.Split('\n');
                var sb = new StringBuilder(text.Length);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (drop.Contains(i)) continue;
                    sb.Append(lines[i]);
                    if (i < lines.Length - 1) sb.Append('\n');
                }
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ReforgedTransfer.ReconcileCraftBody failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Import: VESSEL node ──────────────────────────────────────────────

        /// <summary>The VESSEL-node counterpart: drop the Reforged modules this install
        /// can't accept before the ProtoVessel is built, so a handed-over vessel doesn't
        /// carry modules the prefab has never heard of into a live scene.</summary>
        public static void ReconcileNode(ConfigNode node, string context)
        {
            if (node == null) return;
            try
            {
                var refs = new List<RdxRef>();
                ScanNode(node, refs);
                if (refs.Count == 0) return;

                string local = InstalledVersion();
                var report = new Report(context, local);
                foreach (var r in refs)
                {
                    bool keep = local != null && PrefabAccepts(r.PartName);
                    report.Note(r, keep);
                    if (keep || !Enabled) continue;
                    if (r.Owner != null && r.Node != null) r.Owner.RemoveNode(r.Node);
                }
                report.Post();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] ReforgedTransfer.ReconcileNode failed: {ex.Message}");
            }
        }

        // ── Scanning ─────────────────────────────────────────────────────────

        /// <summary>One Reforged module found on a craft: which part carries it, whether
        /// the player actually painted that part, and where it sits (line range for the
        /// text path, node + parent for the ConfigNode path).</summary>
        private class RdxRef
        {
            public string PartName;
            public bool Painted;
            public int StartLine = -1;
            public int EndLine = -1;
            public ConfigNode Node;
            public ConfigNode Owner;
        }

        /// <summary>Find every Reforged module in raw .craft text. Line-based rather than
        /// parsed, for the same reason TextureTransfer and FlagTransfer never reparse a
        /// craft: ConfigNode round-tripping a .craft wraps it in a spurious root node the
        /// craft loader rejects.</summary>
        private static List<RdxRef> ScanCraftText(string text)
        {
            var found = new List<RdxRef>();
            if (string.IsNullOrEmpty(text)) return found;

            string[] lines = text.Split('\n');
            var stack = new List<string>();     // enclosing node names, innermost last
            string pending = null;              // node name read, waiting for its `{`
            int pendingLine = -1;
            string curPart = null;              // most recent `part = …` seen
            RdxRef open = null;                 // MODULE currently being read
            string openName = null;
            int openDepth = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (line == "{")
                {
                    stack.Add(pending ?? "");
                    if (open == null && string.Equals(pending, "MODULE", StringComparison.Ordinal))
                    {
                        open = new RdxRef { PartName = curPart, StartLine = pendingLine };
                        openName = null;
                        openDepth = stack.Count;
                    }
                    pending = null;
                    pendingLine = -1;
                    continue;
                }
                if (line == "}")
                {
                    if (open != null && stack.Count == openDepth)
                    {
                        if (string.Equals(openName, RDX_MODULE, StringComparison.OrdinalIgnoreCase))
                        {
                            open.EndLine = i;
                            found.Add(open);
                        }
                        open = null;
                        openName = null;
                        openDepth = -1;
                    }
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    pending = null;
                    continue;
                }
                if (IsNodeOpen(line)) { pending = line; pendingLine = i; continue; }
                pending = null;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                string node = stack.Count > 0 ? stack[stack.Count - 1] : "";

                // `part = mk1pod_4294880972` — the trailing instance id is not the name.
                if (key == "part" && node == "PART")
                {
                    curPart = StripInstanceSuffix(value);
                    continue;
                }

                if (open == null || stack.Count != openDepth) continue;
                if (key == "name") openName = value;
                else if (IsPaintField(key, value)) open.Painted = true;
            }
            return found;
        }

        /// <summary>Find every Reforged module in a VESSEL ConfigNode. A PART node here
        /// names its part with `name` (PartAliases works off the same key).</summary>
        private static void ScanNode(ConfigNode node, List<RdxRef> found, string partName = null)
        {
            if (node == null) return;
            if (node.name == "PART")
                partName = StripInstanceSuffix(node.GetValue("name") ?? node.GetValue("part") ?? partName);

            for (int i = 0; i < node.nodes.Count; i++)
            {
                ConfigNode child = node.nodes[i];
                if (child.name == "MODULE")
                {
                    string mn = child.GetValue("name");
                    if (string.Equals(mn, RDX_MODULE, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new RdxRef
                        {
                            PartName = partName,
                            Painted = NodeIsPainted(child),
                            Node = child,
                            Owner = node,
                        });
                    }
                    continue; // a MODULE holds no PARTs
                }
                ScanNode(child, found, partName);
            }
        }

        private static bool NodeIsPainted(ConfigNode module)
        {
            foreach (var f in PaintSwitches)
                if (IsTrue(module.GetValue(f))) return true;
            foreach (var f in PaintStacks)
                if (!string.IsNullOrEmpty((module.GetValue(f) ?? "").Trim())) return true;
            return false;
        }

        /// <summary>Whether a key/value line on a Reforged module means "the player painted
        /// this". A switch counts only when it is on and a stack only when it is non-empty
        /// — Reforged writes all of them, at their defaults, onto every part it patches.</summary>
        private static bool IsPaintField(string key, string value)
        {
            foreach (var f in PaintSwitches)
                if (string.Equals(key, f, StringComparison.OrdinalIgnoreCase)) return IsTrue(value);
            foreach (var f in PaintStacks)
                if (string.Equals(key, f, StringComparison.OrdinalIgnoreCase))
                    return !string.IsNullOrEmpty(value.Trim());
            return false;
        }

        private static bool IsTrue(string v)
            => !string.IsNullOrEmpty(v) && string.Equals(v.Trim(), "True", StringComparison.OrdinalIgnoreCase);

        /// <summary>A bare identifier alone on a line opens a node (the `{` is next).</summary>
        private static bool IsNodeOpen(string line)
        {
            if (line.Length == 0) return false;
            char c0 = line[0];
            if (!char.IsLetter(c0) && c0 != '_') return false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-') return false;
            }
            return true;
        }

        private static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int us = name.LastIndexOf('_');
            if (us <= 0 || us == name.Length - 1) return name;
            for (int i = us + 1; i < name.Length; i++)
                if (!char.IsDigit(name[i])) return name;
            return name.Substring(0, us);
        }

        // ── Reporting ────────────────────────────────────────────────────────

        /// <summary>Accumulates what happened so the player gets one message rather than
        /// one per part, and says nothing at all unless something visible was actually
        /// lost. Dropping the modules off an UNPAINTED part is the common case on any
        /// craft from a Reforged install and is not news — it is litter removal.</summary>
        private class Report
        {
            private readonly string context;
            private readonly string localVersion;
            private readonly HashSet<string> lostParts = new HashSet<string>();
            private int keptPainted;
            private int droppedClean;

            public Report(string context, string localVersion)
            {
                this.context = string.IsNullOrEmpty(context) ? "This craft" : context;
                this.localVersion = localVersion;
            }

            public void Note(RdxRef r, bool keptIt)
            {
                if (keptIt)
                {
                    if (r.Painted) keptPainted++;
                    return;
                }
                if (r.Painted)
                {
                    if (!string.IsNullOrEmpty(r.PartName)) lostParts.Add(r.PartName);
                    else lostParts.Add("(unnamed part)");
                }
                else droppedClean++;
            }

            public void Post()
            {
                if (lostParts.Count == 0)
                {
                    if (keptPainted > 0)
                        Debug.Log($"[GeneKerman] ReforgedTransfer: '{context}' paint job applied " +
                                  $"in full ({keptPainted} part(s)).");
                    else if (droppedClean > 0)
                        Debug.Log($"[GeneKerman] ReforgedTransfer: '{context}' — dropped " +
                                  $"{droppedClean} unpainted Reforged module(s).");
                    return;
                }

                var missing = MissingFolders();
                string packs = missing.Count > 0
                    ? " Missing: " + string.Join(", ", missing.ToArray()) + "."
                    : "";

                string title, body;
                if (string.IsNullOrEmpty(localVersion))
                {
                    title = $"'{context}' loads in stock colours";
                    body = "It was painted with Reforged Materials Redux, which you don't have. "
                         + "The craft itself is fine — every part is there and it will fly exactly "
                         + "as built; only the custom paint is gone." + packs;
                }
                else
                {
                    title = $"'{context}': part of the paint job is missing";
                    body = $"{lostParts.Count} painted part(s) can't take their Reforged paint on "
                         + "this install, so they load in their default look. The craft is "
                         + "otherwise unaffected." + packs;
                }

                Debug.LogWarning($"[GeneKerman] {title} — {body}");
                GeneKermanMod mod = GeneKermanMod.Instance;
                if (mod != null)
                {
                    try { mod.ShowNotification(title, body); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] ReforgedTransfer: notification failed: {ex.Message}");
                    }
                }

                // Hand the folders to the existing modpack writer, which filters to what is
                // actually missing and writes a .ckan the player can open. Reforged needs TU
                // to render at all, so both are named — CKAN would pull TU as a dependency
                // anyway, but only if it knows this mod, and a bare folder name is all we
                // have when it doesn't.
                if (missing.Count > 0)
                {
                    try
                    {
                        CkanGenerator.GenerateCkanForMissing(
                            context + " (paint)", CkanGenerator.ResolveMods(Folders()));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] ReforgedTransfer: modpack write failed: {ex.Message}");
                    }
                }
            }

            private static List<string> Folders()
            {
                var list = new List<string> { CoreFolder() };
                string tu = TextureTransfer.CoreFolder() ?? TU_FOLDER;
                if (!list.Contains(tu)) list.Add(tu);
                return list;
            }

            /// <summary>The folders above that this install doesn't have.</summary>
            private List<string> MissingFolders()
            {
                var list = new List<string>();
                try
                {
                    string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
                    foreach (var f in Folders())
                        if (!Directory.Exists(Path.Combine(gameData, f))) list.Add(f);
                }
                catch { /* unreadable GameData — report the loss without the fix */ }
                return list;
            }
        }
    }
}
