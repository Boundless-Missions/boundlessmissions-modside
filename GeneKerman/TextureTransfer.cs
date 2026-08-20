/*
 * TextureTransfer.cs – Carry a craft's Textures Unlimited paint job between players,
 * and make a craft painted with TU load cleanly for a recipient who doesn't have it.
 *
 * TU (KSPShaderTools) recolours EXISTING parts: it adds no parts of its own, only a
 * `KSPTextureSwitch` / `SSTURecolorGUI` PartModule that a recolour pack (TURD, the
 * ReStock TU patches …) patches onto stock parts with ModuleManager. The player's
 * choices live in that module as persistent fields — the texture-set name and the
 * packed recolour channels — so they are already written into the .craft / VESSEL
 * node and already ride along with every transfer this mod does. The paint job needs
 * no new channel; what it needs is the two things a part name cannot express:
 *
 *   1. WHICH MOD. Every mod-detection path in this project resolves parts → GameData
 *      folder (CkanGenerator.GetModFolder off AvailablePart.partUrl). A mod that adds
 *      zero parts is invisible to all of it, so a TU craft ships with no hint that TU
 *      or the recolour pack is needed — the same blind spot TweakScaleGuard exists for.
 *      We resolve each referenced texture set back to the GameData folder of the config
 *      that DEFINES it (GameDatabase's KSP_TEXTURE_SET entries), which is recoverable
 *      only on the sender's machine, and carry that with the craft.
 *
 *   2. A CLEAN LOAD WITHOUT IT. A recipient without TU has no KSPTextureSwitch on the
 *      prefab, so the carried MODULE nodes match nothing. KSP tolerates that (it logs
 *      and moves on), but the nodes are pure litter and module-index warnings, so on
 *      import we drop exactly the ones the local prefab cannot accept. The craft then
 *      loads as if it had never been painted — stock colours, no missing parts, no
 *      errors — which is the whole point: TU is a look, never a dependency that can
 *      stop a craft opening.
 *
 * The texture FILES are never embedded: a set is DDS art belonging to the pack author,
 * far too big for a craft transfer and not ours to redistribute. So this is a manifest
 * plus a guard, like GKTSVER — with the addition that a missing pack is a real GameData
 * folder, so it can be handed to CkanGenerator and become an installable modpack.
 *
 * CHANNEL: rides the same side-channel as GKFLAG / GKTSVER / GKMODS — a GKTU node in a
 * VESSEL ConfigNode, or an appended GKTU text block in a raw .craft. Appended AFTER
 * GKTSVER and BEFORE GKMODS, so on import it is stripped after GKMODS and before
 * GKTSVER (whose strip cuts to end of file).
 *
 * NOTE ON NAMES: TU's module and field names are matched from a defensive list rather
 * than a single literal, and every read is null-tolerant, so an unrecognised variant
 * degrades to "carried but not understood" instead of to a broken craft.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class TextureTransfer
    {
        private const string TU_NODE = "GKTU";

        // PartModules TU (and TU-based recolour GUIs) attach to a recoloured part. Any
        // MODULE whose `name` is one of these is a paint-job module, not a part feature.
        private static readonly HashSet<string> TuModules =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "KSPTextureSwitch", "SSTURecolorGUI", "KSPModelSwitch" };

        // Persistent fields on those modules that name a texture set. `currentTextureSet`
        // is the live selection; `textureSet` appears on older configs and on the
        // module's own defaults. The recolour channels themselves live in
        // `persistentData`, which needs no interpretation here — it travels verbatim.
        private static readonly string[] SetFields =
            { "currentTextureSet", "textureSet" };

        // The config node type a recolour pack declares its sets with. The pack's
        // GameData folder is the first segment of the config's URL.
        private const string TEXTURE_SET_CONFIG = "KSP_TEXTURE_SET";

        /// <summary>Off switch (settings.cfg `enableTextureTransfer`). Switched off, the
        /// manifest is neither written nor acted on — a craft still carries whatever its
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

        /// <summary>Version of the locally installed Textures Unlimited, or null if it
        /// isn't installed. Matched on the exact assembly name first (TU ships
        /// TexturesUnlimited.dll), then by the type it defines, so a repack under another
        /// file name still resolves. Never matched on substring: TUFX is a different mod
        /// (scene-wide post-processing, nothing per-part) and must not answer for TU.</summary>
        public static string InstalledVersion()
        {
            var la = FindCore();
            if (la == null || la.assembly == null) return null;
            var ver = la.assembly.GetName().Version;
            return ver != null ? ver.ToString() : "unknown";
        }

        /// <summary>TU's loaded assembly, or null when it isn't installed. One probe, used
        /// by both the version and the folder lookup — resolving one but not the other
        /// would report a paint job as carried while silently dropping the dependency that
        /// makes it render.</summary>
        private static AssemblyLoader.LoadedAssembly FindCore()
        {
            try
            {
                // Pass 1: the canonical assembly, by exact name (TexturesUnlimited.dll).
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    if (string.Equals(asm.GetName().Name, "TexturesUnlimited",
                                      StringComparison.OrdinalIgnoreCase))
                        return la;
                }

                // Pass 2: whichever assembly defines TU's types, in case of a repack under
                // another file name.
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    try
                    {
                        if (asm.GetType("KSPShaderTools.KSPTextureSwitch") != null ||
                            asm.GetType("KSPShaderTools.TexturesUnlimitedLoader") != null)
                            return la;
                    }
                    catch { /* GetType can throw on a half-loaded assembly — ignore */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer: TU lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>GameData folder TU's plugin lives in ("TexturesUnlimited" normally),
        /// or null when it isn't installed. Carried so a recipient without TU gets the
        /// framework in their modpack, not just the recolour pack that depends on it.</summary>
        private static string CoreFolder()
        {
            var la = FindCore();
            if (la == null) return null;
            // LoadedAssembly.url is the GameData-relative directory of the dll
            // ("TexturesUnlimited/Plugins"); its first segment is the mod folder.
            string url = la.url;
            if (string.IsNullOrEmpty(url)) return "TexturesUnlimited";
            string[] seg = url.Split('/');
            return seg.Length > 0 && seg[0].Length > 0 ? seg[0] : "TexturesUnlimited";
        }

        private static Dictionary<string, string> _setFolderCache;

        /// <summary>Map of texture-set name → the GameData folder of the config that
        /// declares it. This is the lookup that turns "a colour scheme" into "a mod you
        /// can install", and it only exists on a machine that HAS the pack — which is why
        /// the answer is captured at export and carried, exactly as CkanGenerator carries
        /// the part → mod mapping. Cached: GameDatabase doesn't change mid-session.</summary>
        private static Dictionary<string, string> SetFolders()
        {
            if (_setFolderCache != null) return _setFolderCache;
            _setFolderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GameDatabase.Instance == null) return _setFolderCache;
                var configs = GameDatabase.Instance.GetConfigs(TEXTURE_SET_CONFIG);
                if (configs == null) return _setFolderCache;

                foreach (var uc in configs)
                {
                    if (uc == null || uc.config == null) continue;
                    string setName = uc.config.GetValue("name");
                    if (string.IsNullOrEmpty(setName)) continue;

                    string url = uc.url ?? "";
                    string[] seg = url.Split('/');
                    string folder = seg.Length > 0 ? seg[0] : "";
                    if (folder.Length == 0) continue;

                    if (!_setFolderCache.ContainsKey(setName))
                        _setFolderCache[setName] = folder;
                }
                Debug.Log($"[GeneKerman] TextureTransfer: indexed {_setFolderCache.Count} texture set(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer: texture-set index failed: {ex.Message}");
            }
            return _setFolderCache;
        }

        /// <summary>Whether a texture set of this name is defined on THIS install. A set
        /// the local packs don't declare is one TU will fall back to default for, so the
        /// part arrives unpainted even though TU is present.</summary>
        private static bool SetIsLocal(string setName)
            => !string.IsNullOrEmpty(setName) && SetFolders().ContainsKey(setName);

        /// <summary>Whether the local prefab for a part can actually accept a paint-job
        /// module of this name. False when TU is present but the pack that patches THIS
        /// part isn't — the case a folder check can't see.
        ///
        /// Not consulted at all when TU is absent: the prefab lookup answers "leave it
        /// alone" for a part it can't find, which is right when the question is "does this
        /// prefab want the module" but wrong when nothing on the install can consume one.
        /// A craft that also arrives with a missing part would otherwise keep a recolour
        /// module for a part that isn't there.</summary>
        private static bool PrefabAccepts(string partName, string moduleName)
        {
            if (string.IsNullOrEmpty(partName) || string.IsNullOrEmpty(moduleName)) return false;
            try
            {
                var ap = PartLoader.getPartInfoByName(partName);
                if (ap == null || ap.partPrefab == null || ap.partPrefab.Modules == null)
                    return true; // unknown part — not ours to judge; leave its nodes alone
                foreach (PartModule pm in ap.partPrefab.Modules)
                    if (pm != null && string.Equals(pm.moduleName, moduleName,
                                                    StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            catch
            {
                return true; // couldn't tell — never strip on a guess
            }
        }

        // ── Manifest ─────────────────────────────────────────────────────────

        /// <summary>What the sender's install knows about a craft's paint job: their TU
        /// version, TU's own folder, and every texture set the craft references paired
        /// with the pack folder that defines it.</summary>
        public class TuManifest
        {
            public string SenderVersion;
            public string CoreFolder;
            /// <summary>set name → defining GameData folder (may be empty if unresolved).</summary>
            public Dictionary<string, string> Sets =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool IsEmpty => Sets.Count == 0 && string.IsNullOrEmpty(SenderVersion);

            /// <summary>The distinct pack folders this craft's paint job needs, plus TU
            /// itself. Duplicates removed, blanks dropped.</summary>
            public List<string> Folders()
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<string>();
                if (!string.IsNullOrEmpty(CoreFolder) && seen.Add(CoreFolder)) list.Add(CoreFolder);
                foreach (var kv in Sets)
                    if (!string.IsNullOrEmpty(kv.Value) && seen.Add(kv.Value)) list.Add(kv.Value);
                return list;
            }
        }

        /// <summary>Build the manifest for a set of referenced texture sets, resolving each
        /// against the local pack index.</summary>
        private static TuManifest BuildManifest(IEnumerable<string> setNames)
        {
            var m = new TuManifest
            {
                SenderVersion = InstalledVersion() ?? "unknown",
                CoreFolder = CoreFolder(),
            };
            var folders = SetFolders();
            foreach (var s in setNames)
            {
                if (string.IsNullOrEmpty(s) || m.Sets.ContainsKey(s)) continue;
                string folder;
                m.Sets[s] = folders.TryGetValue(s, out folder) ? folder : "";
            }
            return m;
        }

        // ── Export: raw .craft ───────────────────────────────────────────────

        /// <summary>Append a GKTU block describing the craft's paint job. No-op on a craft
        /// with no TU modules. Must be appended AFTER any GKTSVER block and BEFORE the
        /// GKMODS block, matching the strip order on the other side. Returns the input
        /// unchanged on error — a paint job is never worth failing a transfer over.</summary>
        public static byte[] EmbedInCraft(byte[] craftBytes)
        {
            if (!Enabled || craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                var refs = ScanCraftText(text);
                if (refs.Count == 0) return craftBytes; // unpainted craft

                var setNames = new List<string>();
                foreach (var r in refs) if (!string.IsNullOrEmpty(r.TextureSet)) setNames.Add(r.TextureSet);

                var manifest = BuildManifest(setNames);

                var sb = new StringBuilder(text);
                if (!text.EndsWith("\n")) sb.Append("\n");
                sb.Append(TU_NODE).Append("\n{\n");
                sb.Append("\tver = ").Append(manifest.SenderVersion).Append("\n");
                if (!string.IsNullOrEmpty(manifest.CoreFolder))
                    sb.Append("\tcore = ").Append(manifest.CoreFolder).Append("\n");
                foreach (var kv in manifest.Sets)
                {
                    sb.Append("\tSET\n\t{\n");
                    sb.Append("\t\tname = ").Append(kv.Key).Append("\n");
                    sb.Append("\t\tfolder = ").Append(kv.Value ?? "").Append("\n");
                    sb.Append("\t}\n");
                }
                sb.Append("}\n");

                Debug.Log($"[GeneKerman] TextureTransfer: embedded paint job — " +
                          $"{refs.Count} module(s), {manifest.Sets.Count} texture set(s).");
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer.EmbedInCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Export: VESSEL node ──────────────────────────────────────────────

        /// <summary>Embed a GKTU node describing the paint job of a vessel being handed
        /// over (rescue wreck, quicksend). Safe on any VESSEL-shaped ConfigNode.</summary>
        public static void EmbedInNode(ConfigNode node)
        {
            if (!Enabled || node == null) return;
            try
            {
                node.RemoveNodes(TU_NODE); // avoid duplicates on re-export

                var refs = new List<TuRef>();
                ScanNode(node, refs);
                if (refs.Count == 0) return;

                var setNames = new List<string>();
                foreach (var r in refs) if (!string.IsNullOrEmpty(r.TextureSet)) setNames.Add(r.TextureSet);

                var manifest = BuildManifest(setNames);

                ConfigNode tn = node.AddNode(TU_NODE);
                tn.AddValue("ver", manifest.SenderVersion);
                if (!string.IsNullOrEmpty(manifest.CoreFolder)) tn.AddValue("core", manifest.CoreFolder);
                foreach (var kv in manifest.Sets)
                {
                    ConfigNode sn = tn.AddNode("SET");
                    sn.AddValue("name", kv.Key);
                    sn.AddValue("folder", kv.Value ?? "");
                }
                Debug.Log($"[GeneKerman] TextureTransfer: embedded paint job into vessel node — " +
                          $"{refs.Count} module(s), {manifest.Sets.Count} texture set(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer.EmbedInNode failed: {ex.Message}");
            }
        }

        // ── Import: strip the block ──────────────────────────────────────────

        /// <summary>Strip the appended GKTU block from raw .craft bytes and hand back what
        /// it said. Runs AFTER the GKMODS strip and BEFORE the GKTSVER strip (which cuts to
        /// end of file). Acting on the manifest is a separate step — see
        /// <see cref="ReconcileCraftBody"/> — because it must run after PartAliases has
        /// settled what each part actually is.</summary>
        public static byte[] StripFromCraft(byte[] rawCraftBytes, out TuManifest manifest)
        {
            manifest = new TuManifest();
            if (rawCraftBytes == null || rawCraftBytes.Length == 0) return rawCraftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(rawCraftBytes);
                int idx = FindBlockStart(text);
                if (idx < 0) return rawCraftBytes;

                manifest = ParseManifestText(text.Substring(idx));

                string body = text.Substring(0, idx).TrimEnd('\r', '\n', ' ', '\t');
                if (body.Length > 0) body += "\n";
                return Encoding.UTF8.GetBytes(body);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer.StripFromCraft failed: {ex.Message}");
                return rawCraftBytes;
            }
        }

        // ── Import: reconcile the craft body ─────────────────────────────────

        /// <summary>Make the craft's paint job fit THIS install: keep every module the
        /// local prefab can accept (so the colours apply exactly as the sender set them),
        /// drop the ones it can't (so a craft painted with a pack the recipient doesn't
        /// have still loads, in stock colours, with no orphan module nodes), and report
        /// what was lost with a CKAN modpack for the packs that would restore it.
        ///
        /// Runs on the craft BODY — after every side-channel strip and after PartAliases,
        /// because a substituted part is a different prefab with different modules.</summary>
        public static byte[] ReconcileCraftBody(byte[] craftBytes, TuManifest manifest, string context)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                var refs = ScanCraftText(text);
                if (refs.Count == 0) return craftBytes; // nothing painted

                string local = InstalledVersion();
                var report = new Report(context, manifest, local);

                // Collect the line ranges to drop, then rebuild once — removing as we go
                // would invalidate every later range.
                var drop = new HashSet<int>();
                foreach (var r in refs)
                {
                    bool keep = local != null && PrefabAccepts(r.PartName, r.ModuleName);
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
                Debug.LogWarning($"[GeneKerman] TextureTransfer.ReconcileCraftBody failed: {ex.Message}");
                return craftBytes;
            }
        }

        /// <summary>The VESSEL-node counterpart: read + remove the GKTU node, then drop the
        /// paint-job modules this install can't accept before the ProtoVessel is built —
        /// a module the prefab doesn't have would otherwise be a load-time complaint on a
        /// vessel the player is being handed in flight.</summary>
        public static void ExtractCheckAndStripFromNode(ConfigNode node, string context)
        {
            if (node == null) return;
            try
            {
                var manifest = new TuManifest();
                ConfigNode tn = node.GetNode(TU_NODE);
                if (tn != null)
                {
                    manifest = ParseManifestNode(tn);
                    node.RemoveNodes(TU_NODE);
                }

                var refs = new List<TuRef>();
                ScanNode(node, refs);
                if (refs.Count == 0) return;

                string local = InstalledVersion();
                var report = new Report(context, manifest, local);
                foreach (var r in refs)
                {
                    bool keep = local != null && PrefabAccepts(r.PartName, r.ModuleName);
                    report.Note(r, keep);
                    if (keep || !Enabled) continue;
                    if (r.Owner != null && r.Node != null) r.Owner.RemoveNode(r.Node);
                }
                report.Post();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer.ExtractCheckAndStripFromNode failed: {ex.Message}");
            }
        }

        // ── Marketplace tagging ──────────────────────────────────────────────

        /// <summary>The recolour-pack folders a .craft's paint job needs, for tagging a
        /// marketplace listing alongside CkanGenerator.ModFoldersForCraft. Without this a
        /// TU craft tags as stock, because TU adds no parts for the part walk to find.
        /// Run on the ORIGINAL craft bytes, before any block is appended.</summary>
        public static List<string> TexturePackFoldersForCraft(byte[] craftBytes)
        {
            var folders = new List<string>();
            if (craftBytes == null || craftBytes.Length == 0) return folders;
            try
            {
                var refs = ScanCraftText(Encoding.UTF8.GetString(craftBytes));
                if (refs.Count == 0) return folders;

                var names = new List<string>();
                foreach (var r in refs) if (!string.IsNullOrEmpty(r.TextureSet)) names.Add(r.TextureSet);
                return BuildManifest(names).Folders();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer.TexturePackFoldersForCraft failed: {ex.Message}");
            }
            return folders;
        }

        /// <summary>Whether a .craft carries a Textures Unlimited paint job at all — the
        /// flag behind the marketplace's "Modded Textures Available" tag.
        ///
        /// Separate from TexturePackFoldersForCraft because the two answer different
        /// questions. That one is "which packs must the buyer install", which comes back
        /// empty when every referenced set is unresolvable (a pack the SENDER hasn't got
        /// either, on a craft passed on from someone who had it); this is "does the craft
        /// carry TU texture state", which such a craft still does. So a listing can be
        /// tagged without naming a pack, and the tag never depends on the local index.
        ///
        /// The test is deliberately "carries TU state", not TweakScaleGuard's stricter
        /// "is anything actually changed" — that comparison isn't available here. TU
        /// writes its persistent fields (the selected set plus the packed colour channels)
        /// into every craft saved on a TU install whether or not the player touched the
        /// recolour GUI, and the unpainted baseline is TU's own runtime derivation from
        /// the set, recoverable from neither the craft file nor the part prefab. What the
        /// file does support is the claim the tag makes: this craft was built with TU and
        /// needs it to look like its blueprint. That is also the same evidence that puts
        /// TU's folder in the listing's mod row, so the tag and the row beside it can't
        /// contradict each other.
        ///
        /// Run on the ORIGINAL craft bytes, before any block is appended.</summary>
        public static bool CraftHasCustomTextures(byte[] craftBytes)
        {
            if (craftBytes == null || craftBytes.Length == 0) return false;
            try
            {
                foreach (var r in ScanCraftText(Encoding.UTF8.GetString(craftBytes)))
                    if (r != null && !string.IsNullOrEmpty(r.TextureSet)) return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TextureTransfer.CraftHasCustomTextures failed: {ex.Message}");
            }
            return false;
        }

        // ── Scanning ─────────────────────────────────────────────────────────

        /// <summary>One paint-job module found on a craft: which part carries it, which
        /// texture set it names, and where it sits (line range for the text path, the node
        /// and its parent for the ConfigNode path).</summary>
        private class TuRef
        {
            public string PartName;
            public string ModuleName;
            public string TextureSet;
            public int StartLine = -1;
            public int EndLine = -1;
            public ConfigNode Node;
            public ConfigNode Owner;
        }

        /// <summary>Find every paint-job module in raw .craft text. Line-based rather than
        /// parsed, for the same reason FlagTransfer never reparses a craft: ConfigNode
        /// round-tripping a .craft wraps it in a spurious root node that KSP's craft loader
        /// rejects. Node names sit on their own line with the brace on the next, which is
        /// how KSP writes them; a hand-collapsed `MODULE {` is simply not seen (the same
        /// limitation CkanGenerator.CollectCraftPartNames has).</summary>
        private static List<TuRef> ScanCraftText(string text)
        {
            var found = new List<TuRef>();
            if (string.IsNullOrEmpty(text)) return found;

            string[] lines = text.Split('\n');
            var stack = new List<string>();     // enclosing node names, innermost last
            var openLine = new List<int>();     // line the matching node name sat on
            string pending = null;              // node name read, waiting for its `{`
            int pendingLine = -1;
            string curPart = null;              // most recent `part = …` seen
            TuRef open = null;                  // MODULE currently being read
            int openDepth = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (line == "{")
                {
                    stack.Add(pending ?? "");
                    openLine.Add(pendingLine);
                    if (open == null && string.Equals(pending, "MODULE", StringComparison.Ordinal))
                    {
                        open = new TuRef { PartName = curPart, StartLine = pendingLine };
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
                        if (!string.IsNullOrEmpty(open.ModuleName) && TuModules.Contains(open.ModuleName))
                        {
                            open.EndLine = i;
                            found.Add(open);
                        }
                        open = null;
                        openDepth = -1;
                    }
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        openLine.RemoveAt(openLine.Count - 1);
                    }
                    pending = null;
                    continue;
                }
                if (IsNodeOpen(line)) { pending = line; pendingLine = i; continue; }
                pending = null;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;

                string node = stack.Count > 0 ? stack[stack.Count - 1] : "";

                // `part = mk1pod_4294880972` — the trailing instance id is not the name.
                if (key == "part" && node == "PART")
                {
                    curPart = StripInstanceSuffix(value);
                    continue;
                }

                if (open == null || stack.Count != openDepth) continue;
                if (key == "name") open.ModuleName = value;
                else if (IsSetField(key)) open.TextureSet = value;
            }
            return found;
        }

        /// <summary>Find every paint-job module in a VESSEL ConfigNode. A PART node here
        /// names its part with `name` (PartAliases works off the same key).</summary>
        private static void ScanNode(ConfigNode node, List<TuRef> found, string partName = null)
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
                    if (!string.IsNullOrEmpty(mn) && TuModules.Contains(mn))
                    {
                        found.Add(new TuRef
                        {
                            PartName = partName,
                            ModuleName = mn,
                            TextureSet = ReadSetField(child),
                            Node = child,
                            Owner = node,
                        });
                    }
                    continue; // a MODULE holds no PARTs
                }
                ScanNode(child, found, partName);
            }
        }

        private static string ReadSetField(ConfigNode module)
        {
            foreach (var f in SetFields)
            {
                string v = module.GetValue(f);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        private static bool IsSetField(string key)
        {
            foreach (var f in SetFields)
                if (string.Equals(key, f, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

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

        // ── Manifest parsing ─────────────────────────────────────────────────

        /// <summary>Index of the line-anchored GKTU block (so a stray substring inside a
        /// value can't match), or -1 if absent.</summary>
        private static int FindBlockStart(string text)
        {
            int idx = text.LastIndexOf(TU_NODE, StringComparison.Ordinal);
            if (idx < 0) return -1;
            if (idx > 0 && text[idx - 1] != '\n' && text[idx - 1] != '\r') return -1;
            return idx;
        }

        private static TuManifest ParseManifestText(string block)
        {
            var m = new TuManifest();
            string name = null, folder = null;
            foreach (var rawLine in block.Split('\n'))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (line == "}")
                {
                    if (!string.IsNullOrEmpty(name)) m.Sets[name] = folder ?? "";
                    name = null; folder = null;
                    continue;
                }
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key == "ver") m.SenderVersion = value;
                else if (key == "core") m.CoreFolder = value;
                else if (key == "name") name = value;
                else if (key == "folder") folder = value;
            }
            return m;
        }

        private static TuManifest ParseManifestNode(ConfigNode tn)
        {
            var m = new TuManifest
            {
                SenderVersion = tn.GetValue("ver"),
                CoreFolder = tn.GetValue("core"),
            };
            foreach (ConfigNode sn in tn.GetNodes("SET"))
            {
                string name = sn.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                m.Sets[name] = sn.GetValue("folder") ?? "";
            }
            return m;
        }

        // ── Reporting ────────────────────────────────────────────────────────

        /// <summary>Accumulates what happened to a craft's paint job so the player gets one
        /// message rather than one per part — and, when something was lost, the modpack
        /// that would bring it back. Says nothing at all when the paint job came through
        /// intact, which is the common case and not news.</summary>
        private class Report
        {
            private readonly string context;
            private readonly TuManifest manifest;
            private readonly string localVersion;
            private readonly HashSet<string> lostParts = new HashSet<string>();
            private readonly HashSet<string> lostSets =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private int kept;

            public Report(string context, TuManifest manifest, string localVersion)
            {
                this.context = string.IsNullOrEmpty(context) ? "This craft" : context;
                this.manifest = manifest ?? new TuManifest();
                this.localVersion = localVersion;
            }

            public void Note(TuRef r, bool keptIt)
            {
                if (keptIt)
                {
                    // Kept, but the set it asks for isn't declared here: TU will load the
                    // part with its default look. Worth saying — it's the same visible
                    // outcome as a dropped module, from a different cause.
                    if (!string.IsNullOrEmpty(r.TextureSet) && !SetIsLocal(r.TextureSet))
                        lostSets.Add(r.TextureSet);
                    else kept++;
                    return;
                }
                if (!string.IsNullOrEmpty(r.PartName)) lostParts.Add(r.PartName);
                if (!string.IsNullOrEmpty(r.TextureSet)) lostSets.Add(r.TextureSet);
            }

            public void Post()
            {
                if (lostParts.Count == 0 && lostSets.Count == 0)
                {
                    if (kept > 0)
                        Debug.Log($"[GeneKerman] TextureTransfer: '{context}' paint job applied " +
                                  $"in full ({kept} part(s)).");
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
                    body = "It was painted with Textures Unlimited, which you don't have. "
                         + "The craft itself is fine — every part is there and it will fly "
                         + "exactly as built; only the custom paint is gone." + packs;
                }
                else
                {
                    title = $"'{context}': part of the paint job is missing";
                    body = $"{lostParts.Count + lostSets.Count} recolour(s) reference a texture "
                         + "pack you don't have, so those parts load in their default look. "
                         + "The craft is otherwise unaffected." + packs;
                }

                GeneKermanMod mod = GeneKermanMod.Instance;
                Debug.LogWarning($"[GeneKerman] {title} — {body}");
                if (mod != null)
                {
                    try { mod.ShowNotification(title, body); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] TextureTransfer: notification failed: {ex.Message}");
                    }
                }

                // Hand the pack folders to the existing modpack writer, which filters to
                // what's actually missing and writes a .ckan the player can open. A paint
                // job is cosmetic, so this is the one place it becomes actionable.
                if (missing.Count > 0)
                {
                    try
                    {
                        CkanGenerator.GenerateCkanForMissing(
                            context + " (textures)", CkanGenerator.ResolveMods(manifest.Folders()));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] TextureTransfer: modpack write failed: {ex.Message}");
                    }
                }
            }

            /// <summary>Pack folders named by the manifest that this install doesn't have.
            /// Empty when the sender's client was too old to send a manifest — in which
            /// case we still report the loss, just without naming what would fix it.</summary>
            private List<string> MissingFolders()
            {
                var list = new List<string>();
                try
                {
                    string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
                    foreach (var f in manifest.Folders())
                        if (!Directory.Exists(Path.Combine(gameData, f))) list.Add(f);
                }
                catch { /* unreadable GameData — report the loss without the fix */ }
                return list;
            }
        }
    }
}
