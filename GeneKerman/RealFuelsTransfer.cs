/*
 * RealFuelsTransfer.cs – Carry a craft's RealFuels / Realism Overhaul fuel-and-engine
 * configuration between players, and make an RF-configured craft load cleanly for a
 * recipient who doesn't run RealFuels.
 *
 * RealFuels (the tank/engine system RSS/RO is built on) CONFIGURES existing parts: it
 * adds no parts of its own, only a `ModuleFuelTanks` PartModule (tank type, volume,
 * per-propellant TANK nodes) and a `ModuleEngineConfigs` PartModule (which engine
 * config is selected), plus its `ModuleEnginesRF` replacement for stock ModuleEngines.
 * All of that state is persistent fields and subnodes, so it is already written into
 * the .craft / VESSEL node and already rides along with every transfer this mod does —
 * two RSS/RO players exchange fully-configured crafts today without this file. What is
 * missing is the same two things TextureTransfer supplies for Textures Unlimited:
 *
 *   1. WHICH MOD. Every mod-detection path in this project resolves parts → GameData
 *      folder (CkanGenerator.GetModFolder off AvailablePart.partUrl). A mod that adds
 *      zero parts is invisible to all of it, so an RO craft ships with no hint that
 *      RealFuels — or the whole RO environment — is needed. We record the sender's RF
 *      version and folder, whether the install runs Realism Overhaul, the tank TYPES
 *      the craft uses resolved to the GameData folder that defines them (GameDatabase's
 *      TANK_DEFINITION entries — the same lookup TextureTransfer does for
 *      KSP_TEXTURE_SET), and the engine config names selected. Tank types resolve to a
 *      folder; an engine CONFIG does not (ModuleManager merges it into the part config
 *      and the origin is lost), so configs are carried as names for the recipient-side
 *      check only.
 *
 *   2. A CLEAN LOAD WITHOUT IT. A recipient without RF has no ModuleFuelTanks /
 *      ModuleEngineConfigs / ModuleEnginesRF on any prefab, so the carried MODULE nodes
 *      match nothing — litter and module-index warnings, exactly the TU case. Worse,
 *      the craft's RESOURCE nodes name RF propellants (Kerosene, LqdOxygen, …) that a
 *      non-RF install has no PartResourceDefinition for. On import we drop the RF
 *      module nodes and every part-level RESOURCE whose resource this install doesn't
 *      define; the parts then fill from their local prefabs — the craft arrives in its
 *      local (stock or RO) fuel configuration rather than half-loaded. The DESIGN was
 *      still balanced for the sender's physics, which no reconcile can fix, so the
 *      report says so instead of pretending the swap made the craft equivalent.
 *
 * Unlike a paint job, a config mismatch cuts BOTH ways: a stock-config craft imported
 * onto a RealFuels/RO install gets its tanks and engines rewritten by RO's patches on
 * arrival. That craft carries no GKRF block at all (the sender had nothing to write),
 * so the reverse warning is a local-only check: RF installed here + a craft with
 * propulsion but no RF state → tell the player the craft was built for different
 * physics. Warn-only, RO installed: plain RealFuels without a config suite changes
 * little, and warning every import on it would be noise.
 *
 * The CKAN modpack a missing-RF recipient gets contains RealFuels and any missing
 * tank-type packs — NOT Realism Overhaul, even when the manifest says the craft came
 * from an RO install. RO is an environment (RSS, engine configs, physics), not a
 * dependency; grafting it onto an existing save because one craft arrived is a trap,
 * so like a missing DLC it is named in the warning but never written into the .ckan.
 *
 * CHANNEL: rides the same side-channel as GKFLAG / GKTSVER / GKTU / GKMODS — a GKRF
 * node in a VESSEL ConfigNode, or an appended GKRF text block in a raw .craft.
 * Appended AFTER GKTU and BEFORE GKMODS, so on import it strips after GKMODS and
 * before GKTU. An older client that has never heard of GKRF still ends up clean: its
 * GKTU strip cuts from the GKTU block to end of file, which takes GKRF with it.
 *
 * NOTE ON NAMES: RF's module names are matched from a defensive list and every read is
 * null-tolerant, so an unrecognised fork degrades to "carried but not understood"
 * instead of to a broken craft — the same stance TextureTransfer takes.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class RealFuelsTransfer
    {
        private const string RF_NODE = "GKRF";

        // PartModules that hold RealFuels tank state. `type` on these names a
        // TANK_DEFINITION; the TANK subnodes (per-propellant fill) live inside and are
        // dropped with the module when it has to go.
        private static readonly HashSet<string> TankModules =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ModuleFuelTanks" };

        // PartModules that hold RealFuels engine-config state. `configuration` on these
        // names the selected CONFIG. ModuleHybridEngine(s) are older RF spellings.
        private static readonly HashSet<string> ConfigModules =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ModuleEngineConfigs", "ModuleHybridEngine", "ModuleHybridEngines" };

        // RF's replacements for the stock engine/RCS modules. Their state (ignitions,
        // ullage) means nothing without RF, and a stock prefab has no module of this
        // name to hand the node to.
        private static readonly HashSet<string> RfEngineModules =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ModuleEnginesRF" };

        private static bool IsRfModule(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   (TankModules.Contains(name) || ConfigModules.Contains(name) ||
                    RfEngineModules.Contains(name));
        }

        // The config node type RealFuels declares tank types with. The defining pack's
        // GameData folder is the first segment of the config's URL.
        private const string TANK_DEF_CONFIG = "TANK_DEFINITION";

        /// <summary>Off switch (settings.cfg `enableFuelConfigTransfer`). Switched off,
        /// the manifest is neither written nor acted on — a craft still carries whatever
        /// its MODULE nodes hold, exactly as it did before this file existed. Checks and
        /// warnings still run; only the writes are held back (same contract as
        /// PartAliases with substitution off).</summary>
        private static bool Enabled
        {
            get
            {
                var api = GeneKermanMod.Instance != null ? GeneKermanMod.Instance.Api : null;
                return api == null || api.FuelConfigTransferEnabled;
            }
        }

        // ── Local install ────────────────────────────────────────────────────

        /// <summary>Version of the locally installed RealFuels, or null if it isn't
        /// installed. Matched on the exact assembly name first (RF ships RealFuels.dll),
        /// then by the types it defines, so a repack under another file name still
        /// resolves. Never matched on substring — "RealFuels" must not answer for a
        /// stray assembly that merely mentions it.</summary>
        public static string InstalledVersion()
        {
            var la = FindCore();
            if (la == null || la.assembly == null) return null;
            var ver = la.assembly.GetName().Version;
            return ver != null ? ver.ToString() : "unknown";
        }

        /// <summary>RF's loaded assembly, or null when it isn't installed. One probe for
        /// both the version and the folder lookup, as TextureTransfer.FindCore.</summary>
        private static AssemblyLoader.LoadedAssembly FindCore()
        {
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    if (string.Equals(asm.GetName().Name, "RealFuels",
                                      StringComparison.OrdinalIgnoreCase))
                        return la;
                }
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    try
                    {
                        if (asm.GetType("RealFuels.Tanks.ModuleFuelTanks") != null ||
                            asm.GetType("RealFuels.ModuleFuelTanks") != null ||
                            asm.GetType("RealFuels.ModuleEngineConfigs") != null)
                            return la;
                    }
                    catch { /* GetType can throw on a half-loaded assembly — ignore */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer: RF lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>GameData folder RF's plugin lives in ("RealFuels" normally), or null
        /// when it isn't installed.</summary>
        private static string CoreFolder()
        {
            var la = FindCore();
            if (la == null) return null;
            string url = la.url;
            if (string.IsNullOrEmpty(url)) return "RealFuels";
            string[] seg = url.Split('/');
            return seg.Length > 0 && seg[0].Length > 0 ? seg[0] : "RealFuels";
        }

        /// <summary>Whether this install runs Realism Overhaul. Assembly first (RO ships
        /// a plugin of that name), folder as fallback — RO is mostly configs, and a
        /// configs-only install is still an RO install.</summary>
        public static bool RealismOverhaulInstalled()
        {
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la != null ? la.assembly : null;
                    if (asm == null) continue;
                    if (string.Equals(asm.GetName().Name, "RealismOverhaul",
                                      StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return Directory.Exists(Path.Combine(
                    KSPUtil.ApplicationRootPath, "GameData", "RealismOverhaul"));
            }
            catch { return false; }
        }

        private static Dictionary<string, string> _tankTypeFolderCache;

        /// <summary>Map of tank-type name → the GameData folder of the TANK_DEFINITION
        /// that declares it. Only meaningful on a machine that HAS the pack, which is
        /// why the answer is captured at export and carried — the same reasoning as
        /// TextureTransfer.SetFolders. Cached: GameDatabase doesn't change mid-session.</summary>
        private static Dictionary<string, string> TankTypeFolders()
        {
            if (_tankTypeFolderCache != null) return _tankTypeFolderCache;
            _tankTypeFolderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GameDatabase.Instance == null) return _tankTypeFolderCache;
                var configs = GameDatabase.Instance.GetConfigs(TANK_DEF_CONFIG);
                if (configs == null) return _tankTypeFolderCache;

                foreach (var uc in configs)
                {
                    if (uc == null || uc.config == null) continue;
                    string typeName = uc.config.GetValue("name");
                    if (string.IsNullOrEmpty(typeName)) continue;

                    string url = uc.url ?? "";
                    string[] seg = url.Split('/');
                    string folder = seg.Length > 0 ? seg[0] : "";
                    if (folder.Length == 0) continue;

                    if (!_tankTypeFolderCache.ContainsKey(typeName))
                        _tankTypeFolderCache[typeName] = folder;
                }
                Debug.Log($"[GeneKerman] RealFuelsTransfer: indexed {_tankTypeFolderCache.Count} tank type(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer: tank-type index failed: {ex.Message}");
            }
            return _tankTypeFolderCache;
        }

        /// <summary>Whether a tank type of this name is defined on THIS install. A type
        /// the local TANK_DEFINITIONs don't declare is one RF will refuse or reset, so
        /// the tank arrives misconfigured even though RF is present.</summary>
        private static bool TankTypeIsLocal(string typeName)
            => !string.IsNullOrEmpty(typeName) && TankTypeFolders().ContainsKey(typeName);

        private static Dictionary<string, HashSet<string>> _partConfigCache;

        /// <summary>The engine-config names the LOCAL install offers for a part, read
        /// from its post-ModuleManager PART config in GameDatabase (the CONFIG subnodes
        /// of its ModuleEngineConfigs). Null when the part or its config can't be found —
        /// "can't tell" must not read as "config missing", the same leniency
        /// TextureTransfer.PrefabAccepts shows an unknown part.
        ///
        /// GameDatabase PART names differ from loaded part names by KSP's '_' → '.'
        /// substitution, so the index normalises through the same replacement.</summary>
        private static HashSet<string> LocalEngineConfigs(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            try
            {
                if (_partConfigCache == null)
                {
                    _partConfigCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    if (GameDatabase.Instance != null)
                    {
                        var parts = GameDatabase.Instance.GetConfigs("PART");
                        if (parts != null)
                        {
                            foreach (var uc in parts)
                            {
                                if (uc == null || uc.config == null) continue;
                                string name = uc.config.GetValue("name");
                                if (string.IsNullOrEmpty(name)) continue;
                                name = name.Replace('_', '.');

                                HashSet<string> set = null;
                                foreach (ConfigNode m in uc.config.GetNodes("MODULE"))
                                {
                                    string mn = m.GetValue("name");
                                    if (string.IsNullOrEmpty(mn) || !ConfigModules.Contains(mn)) continue;
                                    if (set == null)
                                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (ConfigNode c in m.GetNodes("CONFIG"))
                                    {
                                        string cn = c.GetValue("name");
                                        if (!string.IsNullOrEmpty(cn)) set.Add(cn);
                                    }
                                }
                                if (set != null && !_partConfigCache.ContainsKey(name))
                                    _partConfigCache[name] = set;
                            }
                        }
                    }
                }
                HashSet<string> found;
                return _partConfigCache.TryGetValue(partName, out found) ? found : null;
            }
            catch
            {
                return null; // couldn't tell — never flag on a guess
            }
        }

        // ── Manifest ─────────────────────────────────────────────────────────

        /// <summary>What the sender's install knows about a craft's fuel/engine
        /// configuration: their RF version and folder, whether they run Realism
        /// Overhaul, every tank type the craft uses paired with the pack folder that
        /// defines it, and the engine config names selected.</summary>
        public class RfManifest
        {
            public string SenderVersion;
            public string CoreFolder;
            /// <summary>The sender's Realism Overhaul folder ("RealismOverhaul"), or
            /// null/empty for a plain-RealFuels sender. Presence means "this craft was
            /// built for RO physics" — reported, never written into a modpack.</summary>
            public string Env;
            /// <summary>tank type → defining GameData folder (may be empty if unresolved).</summary>
            public Dictionary<string, string> TankTypes =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Engine config names in use. Names only — see the file header.</summary>
            public HashSet<string> EngineConfigs =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool IsEmpty =>
                string.IsNullOrEmpty(SenderVersion) && TankTypes.Count == 0 && EngineConfigs.Count == 0;

            /// <summary>The folders a CKAN modpack may list: RF itself plus the distinct
            /// tank-type packs. The RO environment is deliberately NOT here.</summary>
            public List<string> Folders()
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<string>();
                if (!string.IsNullOrEmpty(CoreFolder) && seen.Add(CoreFolder)) list.Add(CoreFolder);
                foreach (var kv in TankTypes)
                    if (!string.IsNullOrEmpty(kv.Value) && seen.Add(kv.Value)) list.Add(kv.Value);
                return list;
            }

            /// <summary>The folders a marketplace listing should be tagged with —
            /// <see cref="Folders"/> plus the RO environment, so a buyer can see (and
            /// filter for) "this is an RO craft" before spending anything.</summary>
            public List<string> TagFolders()
            {
                var list = Folders();
                if (!string.IsNullOrEmpty(Env) && !list.Contains(Env)) list.Add(Env);
                return list;
            }
        }

        /// <summary>Build the manifest for a scanned craft, resolving each tank type
        /// against the local TANK_DEFINITION index.</summary>
        private static RfManifest BuildManifest(List<RfRef> refs)
        {
            var m = new RfManifest
            {
                SenderVersion = InstalledVersion() ?? "unknown",
                CoreFolder = CoreFolder(),
                Env = RealismOverhaulInstalled() ? "RealismOverhaul" : null,
            };
            var folders = TankTypeFolders();
            foreach (var r in refs)
            {
                if (!string.IsNullOrEmpty(r.TankType) && !m.TankTypes.ContainsKey(r.TankType))
                {
                    string folder;
                    m.TankTypes[r.TankType] = folders.TryGetValue(r.TankType, out folder) ? folder : "";
                }
                if (!string.IsNullOrEmpty(r.EngineConfig))
                    m.EngineConfigs.Add(r.EngineConfig);
            }
            return m;
        }

        // ── Scanning ─────────────────────────────────────────────────────────

        /// <summary>One RealFuels module found on a craft: which part carries it, the
        /// tank type / engine config it names, and where it sits (line range for the
        /// text path, the node and its parent for the ConfigNode path).</summary>
        private class RfRef
        {
            public string PartName;
            public string ModuleName;
            public string TankType;
            public string EngineConfig;
            public int StartLine = -1;
            public int EndLine = -1;
            public ConfigNode Node;
            public ConfigNode Owner;
        }

        /// <summary>A part-level RESOURCE node: its resource name and where it sits.
        /// Collected so the missing-RF reconcile can drop resources this install has no
        /// definition for. Module-level RESOURCE nodes are left alone — they belong to
        /// whatever module holds them.</summary>
        private class ResRef
        {
            public string Name;
            public int StartLine = -1;
            public int EndLine = -1;
            public ConfigNode Node;
            public ConfigNode Owner;
        }

        private class ScanResult
        {
            public readonly List<RfRef> Refs = new List<RfRef>();
            public readonly List<ResRef> Resources = new List<ResRef>();
            /// <summary>The craft has stock-module propulsion or stock LF/OX aboard —
            /// the trigger for the reverse (non-RF craft on an RF install) warning.
            /// Deliberately narrow: a craft with neither engines nor stock fuel has
            /// nothing RO would rewrite in a way worth an alarm.</summary>
            public bool HasStockPropulsion;
        }

        /// <summary>Find every RF module and part-level RESOURCE in raw .craft text.
        /// Line-based rather than parsed, for the same reason every scanner here is
        /// (ConfigNode round-tripping a .craft wraps it in a root node KSP's craft
        /// loader rejects). Same structural walk as TextureTransfer.ScanCraftText.</summary>
        private static ScanResult ScanCraftText(string text)
        {
            var result = new ScanResult();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Split('\n');
            var stack = new List<string>();     // enclosing node names, innermost last
            string pending = null;              // node name read, waiting for its `{`
            int pendingLine = -1;
            string curPart = null;              // most recent `part = …` seen
            RfRef openModule = null;            // MODULE currently being read
            int moduleDepth = -1;
            ResRef openRes = null;              // part-level RESOURCE currently being read
            int resDepth = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (line == "{")
                {
                    string parent = stack.Count > 0 ? stack[stack.Count - 1] : "";
                    stack.Add(pending ?? "");
                    if (openModule == null && openRes == null &&
                        string.Equals(pending, "MODULE", StringComparison.Ordinal))
                    {
                        openModule = new RfRef { PartName = curPart, StartLine = pendingLine };
                        moduleDepth = stack.Count;
                    }
                    else if (openModule == null && openRes == null &&
                             string.Equals(pending, "RESOURCE", StringComparison.Ordinal) &&
                             parent == "PART")
                    {
                        openRes = new ResRef { StartLine = pendingLine };
                        resDepth = stack.Count;
                    }
                    pending = null;
                    pendingLine = -1;
                    continue;
                }
                if (line == "}")
                {
                    if (openModule != null && stack.Count == moduleDepth)
                    {
                        CloseModuleRef(openModule, i, result);
                        openModule = null;
                        moduleDepth = -1;
                    }
                    else if (openRes != null && stack.Count == resDepth)
                    {
                        openRes.EndLine = i;
                        CloseResourceRef(openRes, result);
                        openRes = null;
                        resDepth = -1;
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
                if (value.Length == 0) continue;

                string node = stack.Count > 0 ? stack[stack.Count - 1] : "";

                if (key == "part" && node == "PART")
                {
                    curPart = StripInstanceSuffix(value);
                    continue;
                }

                if (openRes != null && stack.Count == resDepth)
                {
                    if (key == "name") openRes.Name = value;
                    continue;
                }
                if (openModule == null || stack.Count != moduleDepth) continue;
                if (key == "name") openModule.ModuleName = value;
                else if (key == "type") openModule.TankType = value;
                else if (key == "configuration") openModule.EngineConfig = value;
            }
            return result;
        }

        /// <summary>Decide what a closed MODULE was: an RF module worth a ref, a stock
        /// engine (sets the reverse-warning flag), or neither. `type` / `configuration`
        /// were collected blind, so they are kept only where they mean what we read.</summary>
        private static void CloseModuleRef(RfRef r, int endLine, ScanResult result)
        {
            if (string.IsNullOrEmpty(r.ModuleName)) return;
            if (IsRfModule(r.ModuleName))
            {
                if (!TankModules.Contains(r.ModuleName)) r.TankType = null;
                if (!ConfigModules.Contains(r.ModuleName)) r.EngineConfig = null;
                r.EndLine = endLine;
                result.Refs.Add(r);
                return;
            }
            // Stock/other engines: ModuleEngines, ModuleEnginesFX, … (ModuleEnginesRF
            // was caught above). RCS is left out: every craft has RCS-ish bits and the
            // reverse warning should mean "this thing flies on different physics here".
            if (r.ModuleName.StartsWith("ModuleEngines", StringComparison.OrdinalIgnoreCase))
                result.HasStockPropulsion = true;
        }

        private static void CloseResourceRef(ResRef r, ScanResult result)
        {
            if (string.IsNullOrEmpty(r.Name)) return;
            result.Resources.Add(r);
            if (string.Equals(r.Name, "LiquidFuel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Name, "Oxidizer", StringComparison.OrdinalIgnoreCase))
                result.HasStockPropulsion = true;
        }

        /// <summary>Find every RF module and part-level RESOURCE in a VESSEL ConfigNode.
        /// A PART node here names its part with `name` (PartAliases works off the same
        /// key).</summary>
        private static void ScanNode(ConfigNode node, ScanResult result, string partName = null)
        {
            if (node == null) return;
            bool isPart = node.name == "PART";
            if (isPart)
                partName = StripInstanceSuffix(node.GetValue("name") ?? node.GetValue("part") ?? partName);

            for (int i = 0; i < node.nodes.Count; i++)
            {
                ConfigNode child = node.nodes[i];
                if (child.name == "MODULE")
                {
                    string mn = child.GetValue("name");
                    if (string.IsNullOrEmpty(mn)) continue;
                    if (IsRfModule(mn))
                    {
                        result.Refs.Add(new RfRef
                        {
                            PartName = partName,
                            ModuleName = mn,
                            TankType = TankModules.Contains(mn) ? child.GetValue("type") : null,
                            EngineConfig = ConfigModules.Contains(mn) ? child.GetValue("configuration") : null,
                            Node = child,
                            Owner = node,
                        });
                    }
                    else if (mn.StartsWith("ModuleEngines", StringComparison.OrdinalIgnoreCase))
                        result.HasStockPropulsion = true;
                    continue; // a MODULE holds no PARTs, and its RESOURCEs are its own
                }
                if (child.name == "RESOURCE" && isPart)
                {
                    string rn = child.GetValue("name");
                    if (!string.IsNullOrEmpty(rn))
                    {
                        var rr = new ResRef { Name = rn, Node = child, Owner = node };
                        CloseResourceRef(rr, result);
                    }
                    continue;
                }
                ScanNode(child, result, partName);
            }
        }

        // ── Export: raw .craft ───────────────────────────────────────────────

        /// <summary>Append a GKRF block describing the craft's fuel/engine configuration.
        /// No-op on a craft with no RF modules. Must be appended AFTER any GKTU block and
        /// BEFORE the GKMODS block, matching the strip order on the other side. Returns
        /// the input unchanged on error — config metadata is never worth failing a
        /// transfer over.</summary>
        public static byte[] EmbedInCraft(byte[] craftBytes)
        {
            if (!Enabled || craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                var scan = ScanCraftText(text);
                if (scan.Refs.Count == 0) return craftBytes; // no RF state aboard

                var manifest = BuildManifest(scan.Refs);

                var sb = new StringBuilder(text);
                if (!text.EndsWith("\n")) sb.Append("\n");
                AppendManifestText(sb, manifest);

                Debug.Log($"[GeneKerman] RealFuelsTransfer: embedded fuel config — " +
                          $"{scan.Refs.Count} module(s), {manifest.TankTypes.Count} tank type(s), " +
                          $"{manifest.EngineConfigs.Count} engine config(s).");
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer.EmbedInCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        private static void AppendManifestText(StringBuilder sb, RfManifest m)
        {
            sb.Append(RF_NODE).Append("\n{\n");
            sb.Append("\tver = ").Append(m.SenderVersion).Append("\n");
            if (!string.IsNullOrEmpty(m.CoreFolder))
                sb.Append("\tcore = ").Append(m.CoreFolder).Append("\n");
            if (!string.IsNullOrEmpty(m.Env))
                sb.Append("\tenv = ").Append(m.Env).Append("\n");
            foreach (var kv in m.TankTypes)
            {
                sb.Append("\tTANKTYPE\n\t{\n");
                sb.Append("\t\tname = ").Append(kv.Key).Append("\n");
                sb.Append("\t\tfolder = ").Append(kv.Value ?? "").Append("\n");
                sb.Append("\t}\n");
            }
            foreach (var c in m.EngineConfigs)
                sb.Append("\tconfig = ").Append(c).Append("\n");
            sb.Append("}\n");
        }

        // ── Export: VESSEL node ──────────────────────────────────────────────

        /// <summary>Embed a GKRF node describing the fuel/engine configuration of a
        /// vessel being handed over (rescue wreck, quicksend). Safe on any VESSEL-shaped
        /// ConfigNode.</summary>
        public static void EmbedInNode(ConfigNode node)
        {
            if (!Enabled || node == null) return;
            try
            {
                node.RemoveNodes(RF_NODE); // avoid duplicates on re-export

                var scan = new ScanResult();
                ScanNode(node, scan);
                if (scan.Refs.Count == 0) return;

                var manifest = BuildManifest(scan.Refs);

                ConfigNode rn = node.AddNode(RF_NODE);
                rn.AddValue("ver", manifest.SenderVersion);
                if (!string.IsNullOrEmpty(manifest.CoreFolder)) rn.AddValue("core", manifest.CoreFolder);
                if (!string.IsNullOrEmpty(manifest.Env)) rn.AddValue("env", manifest.Env);
                foreach (var kv in manifest.TankTypes)
                {
                    ConfigNode tn = rn.AddNode("TANKTYPE");
                    tn.AddValue("name", kv.Key);
                    tn.AddValue("folder", kv.Value ?? "");
                }
                foreach (var c in manifest.EngineConfigs)
                    rn.AddValue("config", c);

                Debug.Log($"[GeneKerman] RealFuelsTransfer: embedded fuel config into vessel node — " +
                          $"{scan.Refs.Count} module(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer.EmbedInNode failed: {ex.Message}");
            }
        }

        // ── Import: strip the block ──────────────────────────────────────────

        /// <summary>Strip the appended GKRF block from raw .craft bytes and hand back
        /// what it said. Runs AFTER the GKMODS strip and BEFORE the GKTU strip. Acting on
        /// the manifest is a separate step — see <see cref="ReconcileCraftBody"/> —
        /// because it must run after PartAliases has settled what each part actually is.</summary>
        public static byte[] StripFromCraft(byte[] rawCraftBytes, out RfManifest manifest)
        {
            manifest = new RfManifest();
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
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer.StripFromCraft failed: {ex.Message}");
                return rawCraftBytes;
            }
        }

        // ── Import: reconcile the craft body ─────────────────────────────────

        /// <summary>Make the craft's fuel/engine configuration fit THIS install.
        ///
        /// RF present: keep everything, and check what the manifest names against what
        /// is defined here — a tank type or engine config the local packs don't declare,
        /// or an RO craft arriving on a non-RO RealFuels install (and vice versa), is a
        /// craft that will load but not perform as designed, which the player should
        /// hear once, with the pack that fixes it named.
        ///
        /// RF absent: drop the RF module nodes and every part-level RESOURCE this
        /// install has no definition for, so the parts fill from their local prefabs and
        /// the craft loads cleanly in local fuels — then say what was lost and write a
        /// CKAN modpack for RealFuels and the tank packs.
        ///
        /// Runs on the craft BODY — after every side-channel strip and after
        /// PartAliases, because a substituted part is a different prefab.</summary>
        public static byte[] ReconcileCraftBody(byte[] craftBytes, RfManifest manifest, string context)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                var scan = ScanCraftText(text);

                string local = InstalledVersion();
                var report = new Report(context, manifest, local);

                if (scan.Refs.Count == 0)
                {
                    report.NoteReverse(scan.HasStockPropulsion);
                    report.Post();
                    return craftBytes;
                }

                var drop = new HashSet<int>();
                if (local == null)
                {
                    foreach (var r in scan.Refs)
                    {
                        report.NoteDroppedModule(r);
                        if (!Enabled) continue;
                        for (int i = r.StartLine; i <= r.EndLine; i++) drop.Add(i);
                    }
                    foreach (var res in scan.Resources)
                    {
                        if (ResourceIsLocal(res.Name)) continue;
                        report.NoteDroppedResource(res.Name);
                        if (!Enabled) continue;
                        for (int i = res.StartLine; i <= res.EndLine; i++) drop.Add(i);
                    }
                }
                else
                {
                    report.CheckAgainstLocal(scan.Refs);
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
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer.ReconcileCraftBody failed: {ex.Message}");
                return craftBytes;
            }
        }

        /// <summary>The VESSEL-node counterpart: read + remove the GKRF node, then
        /// reconcile the vessel's RF state against this install before the ProtoVessel
        /// is built.</summary>
        public static void ExtractCheckAndStripFromNode(ConfigNode node, string context)
        {
            if (node == null) return;
            try
            {
                var manifest = new RfManifest();
                ConfigNode rn = node.GetNode(RF_NODE);
                if (rn != null)
                {
                    manifest = ParseManifestNode(rn);
                    node.RemoveNodes(RF_NODE);
                }

                var scan = new ScanResult();
                ScanNode(node, scan);

                string local = InstalledVersion();
                var report = new Report(context, manifest, local);

                if (scan.Refs.Count == 0)
                {
                    report.NoteReverse(scan.HasStockPropulsion);
                    report.Post();
                    return;
                }

                if (local == null)
                {
                    foreach (var r in scan.Refs)
                    {
                        report.NoteDroppedModule(r);
                        if (!Enabled) continue;
                        if (r.Owner != null && r.Node != null) r.Owner.RemoveNode(r.Node);
                    }
                    foreach (var res in scan.Resources)
                    {
                        if (ResourceIsLocal(res.Name)) continue;
                        report.NoteDroppedResource(res.Name);
                        if (!Enabled) continue;
                        if (res.Owner != null && res.Node != null) res.Owner.RemoveNode(res.Node);
                    }
                }
                else
                {
                    report.CheckAgainstLocal(scan.Refs);
                }
                report.Post();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer.ExtractCheckAndStripFromNode failed: {ex.Message}");
            }
        }

        /// <summary>Whether this install defines a resource of this name. Unreadable
        /// counts as defined — never drop a tank's contents on a guess.</summary>
        private static bool ResourceIsLocal(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return true;
            try
            {
                var lib = PartResourceLibrary.Instance;
                if (lib == null) return true;
                return lib.GetDefinition(resourceName) != null;
            }
            catch { return true; }
        }

        // ── Marketplace tagging ──────────────────────────────────────────────

        /// <summary>The RealFuels-related folders a .craft's configuration implies, for
        /// tagging a marketplace listing alongside CkanGenerator.ModFoldersForCraft.
        /// Without this an RO craft tags as if it were a stock-config craft, because RF
        /// adds no parts for the part walk to find. Includes the RO environment folder
        /// when the sender runs it, so listings are visibly RO builds. Run on the
        /// ORIGINAL craft bytes, before any block is appended.</summary>
        public static List<string> FuelConfigFoldersForCraft(byte[] craftBytes)
        {
            var folders = new List<string>();
            if (craftBytes == null || craftBytes.Length == 0) return folders;
            try
            {
                var scan = ScanCraftText(Encoding.UTF8.GetString(craftBytes));
                if (scan.Refs.Count == 0) return folders;
                return BuildManifest(scan.Refs).TagFolders();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RealFuelsTransfer.FuelConfigFoldersForCraft failed: {ex.Message}");
            }
            return folders;
        }

        // ── Manifest parsing ─────────────────────────────────────────────────

        /// <summary>Index of the line-anchored GKRF block (so a stray substring inside a
        /// value can't match), or -1 if absent.</summary>
        private static int FindBlockStart(string text)
        {
            int idx = text.LastIndexOf(RF_NODE, StringComparison.Ordinal);
            if (idx < 0) return -1;
            if (idx > 0 && text[idx - 1] != '\n' && text[idx - 1] != '\r') return -1;
            return idx;
        }

        private static RfManifest ParseManifestText(string block)
        {
            var m = new RfManifest();
            string name = null, folder = null;
            foreach (var rawLine in block.Split('\n'))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (line == "}")
                {
                    if (!string.IsNullOrEmpty(name)) m.TankTypes[name] = folder ?? "";
                    name = null; folder = null;
                    continue;
                }
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key == "ver") m.SenderVersion = value;
                else if (key == "core") m.CoreFolder = value;
                else if (key == "env") m.Env = value;
                else if (key == "config") m.EngineConfigs.Add(value);
                else if (key == "name") name = value;
                else if (key == "folder") folder = value;
            }
            return m;
        }

        private static RfManifest ParseManifestNode(ConfigNode rn)
        {
            var m = new RfManifest
            {
                SenderVersion = rn.GetValue("ver"),
                CoreFolder = rn.GetValue("core"),
                Env = rn.GetValue("env"),
            };
            foreach (ConfigNode tn in rn.GetNodes("TANKTYPE"))
            {
                string name = tn.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                m.TankTypes[name] = tn.GetValue("folder") ?? "";
            }
            foreach (var c in rn.GetValues("config"))
                if (!string.IsNullOrEmpty(c)) m.EngineConfigs.Add(c);
            return m;
        }

        // ── Small shared helpers (same shapes as TextureTransfer's) ──────────

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

        /// <summary>Accumulates what happened to a craft's fuel/engine configuration so
        /// the player gets one message rather than one per part — and, where a pack
        /// would fix it, the modpack. Silent when the configuration fits this install,
        /// which is the common (same-community) case and not news.</summary>
        private class Report
        {
            private readonly string context;
            private readonly RfManifest manifest;
            private readonly string localVersion;
            private readonly HashSet<string> droppedParts = new HashSet<string>();
            private readonly HashSet<string> droppedResources =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> missingTankTypes =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> missingConfigs =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private bool reverseWarning;

            public Report(string context, RfManifest manifest, string localVersion)
            {
                this.context = string.IsNullOrEmpty(context) ? "This craft" : context;
                this.manifest = manifest ?? new RfManifest();
                this.localVersion = localVersion;
            }

            public void NoteDroppedModule(RfRef r)
            {
                if (!string.IsNullOrEmpty(r.PartName)) droppedParts.Add(r.PartName);
            }

            public void NoteDroppedResource(string name)
            {
                if (!string.IsNullOrEmpty(name)) droppedResources.Add(name);
            }

            /// <summary>RF is installed here: flag every tank type / engine config the
            /// craft selects that this install doesn't define. Nothing is modified —
            /// RealFuels itself resolves the miss (falls back to a default) — but the
            /// player should know the craft is not configured as its builder left it.</summary>
            public void CheckAgainstLocal(List<RfRef> refs)
            {
                foreach (var r in refs)
                {
                    if (!string.IsNullOrEmpty(r.TankType) && !TankTypeIsLocal(r.TankType))
                        missingTankTypes.Add(r.TankType);
                    if (!string.IsNullOrEmpty(r.EngineConfig))
                    {
                        var local = LocalEngineConfigs(r.PartName);
                        if (local != null && !local.Contains(r.EngineConfig))
                            missingConfigs.Add(r.EngineConfig);
                    }
                }
            }

            /// <summary>The craft carries no RF state at all. On an RO install that is
            /// itself the warning: RO's patches will re-plumb its tanks and engines on
            /// load, so it will not fly as its builder tested it. Plain RealFuels
            /// without RO changes far less, so it stays quiet.</summary>
            public void NoteReverse(bool hasStockPropulsion)
            {
                reverseWarning = hasStockPropulsion && localVersion != null &&
                                 RealismOverhaulInstalled();
            }

            public void Post()
            {
                string title = null, body = null;
                bool offerModpack = false;

                if (droppedParts.Count > 0 || droppedResources.Count > 0)
                {
                    // RF absent — the craft was rebuilt in local fuels.
                    string env = !string.IsNullOrEmpty(manifest.Env)
                        ? " It was built for Realism Overhaul, so it was balanced for "
                          + "real-scale physics — expect very different performance here."
                        : "";
                    string res = droppedResources.Count > 0
                        ? $" {droppedResources.Count} propellant type(s) this install doesn't define "
                          + $"({string.Join(", ", ListOf(droppedResources, 4))}) were removed; "
                          + "those tanks arrive filled with their standard contents."
                        : "";
                    title = $"'{context}' uses RealFuels, which you don't have";
                    body = $"Its fuel/engine configuration ({droppedParts.Count} part(s)) was removed "
                         + "so the craft loads with this install's own tanks and engines."
                         + res + env;
                    offerModpack = true;
                }
                else if (missingTankTypes.Count > 0 || missingConfigs.Count > 0 || EnvMismatch())
                {
                    var bits = new List<string>();
                    if (missingTankTypes.Count > 0)
                        bits.Add($"tank type(s) {string.Join(", ", ListOf(missingTankTypes, 4))} "
                               + "aren't defined here, so those tanks reset to a default type");
                    if (missingConfigs.Count > 0)
                        bits.Add($"engine config(s) {string.Join(", ", ListOf(missingConfigs, 4))} "
                               + "aren't available here, so those engines fall back to their default config");
                    if (EnvMismatch())
                        bits.Add(!string.IsNullOrEmpty(manifest.Env)
                            ? "it was built on a Realism Overhaul install and yours isn't RO, "
                              + "so part stats and configs differ from what the builder tested"
                            : "it was built without Realism Overhaul and your install is RO, "
                              + "so RO's patches re-plumb it on load");
                    title = $"'{context}': fuel/engine configuration won't fully match";
                    body = "The craft loads, but " + string.Join("; ", bits.ToArray()) + ".";
                    offerModpack = missingTankTypes.Count > 0;
                }
                else if (reverseWarning)
                {
                    title = $"'{context}' was built for stock fuels";
                    body = "This install runs Realism Overhaul, which will reconfigure its tanks "
                         + "and engines on load — the craft will fly very differently from how "
                         + "its builder tested it.";
                }

                if (title == null) return;

                Debug.LogWarning($"[GeneKerman] {title} — {body}");
                var mod = GeneKermanMod.Instance;
                if (mod != null)
                {
                    try { mod.ShowNotification(title, body); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] RealFuelsTransfer: notification failed: {ex.Message}");
                    }
                }
                else
                {
                    try { ScreenMessages.PostScreenMessage($"{title}: {body}", 12f, ScreenMessageStyle.UPPER_CENTER); }
                    catch { /* headless — the log line is enough */ }
                }

                // RealFuels and the tank packs are real installable mods; hand them to
                // the existing modpack writer, which filters to what's actually missing.
                // The RO environment is deliberately not in Folders() — see file header.
                if (offerModpack && manifest.Folders().Count > 0)
                {
                    try
                    {
                        CkanGenerator.GenerateCkanForMissing(
                            context + " (fuel config)", CkanGenerator.ResolveMods(manifest.Folders()));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] RealFuelsTransfer: modpack write failed: {ex.Message}");
                    }
                }
            }

            private bool EnvMismatch()
            {
                if (manifest.IsEmpty) return false; // no manifest — nothing to compare
                bool senderRo = !string.IsNullOrEmpty(manifest.Env);
                return senderRo != RealismOverhaulInstalled();
            }

            private static string ListOf(HashSet<string> set, int max)
            {
                var list = new List<string>(set);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                if (list.Count <= max) return string.Join(", ", list.ToArray());
                return string.Join(", ", list.GetRange(0, max).ToArray()) + ", …";
            }
        }
    }
}
