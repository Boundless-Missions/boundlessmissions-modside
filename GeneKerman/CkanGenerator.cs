/*
 * CkanGenerator.cs – Help a player install the mods a shared craft needs.
 *
 * A .craft / vessel only stores part *names*, never which mod each part came from,
 * so a recipient who's missing a mod just sees "this craft has missing parts" with
 * no way to know what to install. This module fixes that by carrying the answer with
 * the transfer and turning it into a CKAN modpack on the other side.
 *
 * EXPORT (sender has the mods): we resolve every non-stock part the craft/vessel uses
 * to its GameData folder (AvailablePart.partUrl) and, if the sender uses CKAN, to the
 * exact CKAN identifier (read from CKAN's registry.json). That list rides the SAME
 * side-channel as GKFLAG / GKTSVER — a GKMODS node embedded in the vessel ConfigNode,
 * or an appended GKMODS text block in a raw .craft. It is appended LAST and stripped
 * FIRST so it never interferes with the flag/TweakScale blocks.
 *
 * IMPORT (recipient): we read GKMODS, diff its folder list against the recipient's own
 * GameData, and for any mod they don't have we write a CKAN metapackage (.ckan) listing
 * those mods as dependencies — open it in CKAN and it installs everything the craft
 * needs. Installed crafts also get a small "<craft>.gkmods" sidecar so the editor hook
 * can regenerate the .ckan if the player later loads the craft and is still missing mods.
 *
 * Mapping a *missing* part back to a mod is impossible on the recipient's side (the part
 * isn't loaded), which is exactly why the mapping is captured at export time instead.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KSP.UI.Screens;
using UnityEngine;

namespace GeneKerman
{
    public static class CkanGenerator
    {
        private const string MODS_NODE = "GKMODS";

        // Folders shipped with the game (or with this mod) — never a "missing" dependency.
        private static readonly HashSet<string> StockFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Squad", "SquadExpansion", "BoundlessMissions" };

        private static string GameDataRoot =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

        // Where generated modpacks land — a top-level folder so it's easy to find.
        private static string CkanOutputDir =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GeneKerman_MissingMods");

        // .craft part lines look like `part = mk1pod_4294880972`; the trailing `_<id>`
        // is a per-instance suffix, not part of the part name.
        private static readonly Regex PartLineRx =
            new Regex(@"(?m)^\s*part\s*=\s*(.+?)\s*$", RegexOptions.Compiled);
        // KIS records each stored item's part as `partName = <name>` (stock items in a
        // .craft already ride the `part = ` lines above). Caught so inventory-only mods
        // aren't dropped from the modpack.
        private static readonly Regex PartNameLineRx =
            new Regex(@"(?m)^\s*partName\s*=\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex InstanceSuffixRx =
            new Regex(@"_\d+$", RegexOptions.Compiled);

        /// <summary>A mod a craft depends on: its GameData folder, its CKAN identifier
        /// (falls back to the folder when unknown), and a human-readable name.</summary>
        public class ModEntry
        {
            public string folder;
            public string ckan;
            public string name;
        }

        // ── Export: collect ──────────────────────────────────────────────────

        /// <summary>Resolve the distinct non-stock mods a set of loaded parts come from,
        /// keyed by GameData folder and (when CKAN is present) CKAN identifier.</summary>
        private static List<ModEntry> CollectMods(IEnumerable<AvailablePart> parts)
        {
            var byFolder = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            if (parts == null) return new List<ModEntry>();

            var registry = LoadCkanRegistry();
            foreach (var ap in parts)
            {
                string folder = GetModFolder(ap);
                if (string.IsNullOrEmpty(folder) || StockFolders.Contains(folder)) continue;
                if (byFolder.ContainsKey(folder)) continue;

                ModEntry e;
                if (!registry.TryGetValue(folder, out e))
                    e = new ModEntry { folder = folder, ckan = folder, name = folder };
                byFolder[folder] = e;
            }
            return byFolder.Values.ToList();
        }

        /// <summary>The GameData folder a part lives in (first path segment of its URL).</summary>
        private static string GetModFolder(AvailablePart ap)
        {
            if (ap == null || string.IsNullOrEmpty(ap.partUrl)) return null;
            int slash = ap.partUrl.IndexOf('/');
            return slash > 0 ? ap.partUrl.Substring(0, slash) : ap.partUrl;
        }

        /// <summary>Resolve part names (from a .craft) to their mods via PartLoader.</summary>
        private static List<ModEntry> CollectModsFromPartNames(IEnumerable<string> names)
        {
            var parts = new List<AvailablePart>();
            foreach (var raw in names)
            {
                string name = InstanceSuffixRx.Replace(raw.Trim(), "");
                var ap = PartLoader.getPartInfoByName(name);
                if (ap != null) parts.Add(ap);
            }
            return CollectMods(parts);
        }

        /// <summary>Recursively gather the part names of items stashed inside inventory
        /// modules — stock <c>ModuleInventoryPart</c> (STOREDPART nodes) and KIS
        /// <c>ModuleKISInventory</c> (ITEM nodes). These items are never vessel parts, so
        /// the normal part walk misses the mods they come from; without this a craft that
        /// only carries (say) a deployed-science part inside a container would ship without
        /// listing that mod. Recurses, so a container nested inside another is covered.</summary>
        private static void CollectInventoryPartNames(ConfigNode node, List<string> names)
        {
            if (node == null) return;
            for (int i = 0; i < node.nodes.Count; i++)
            {
                ConfigNode child = node.nodes[i];
                if (child.name == "STOREDPART" || child.name == "ITEM")
                {
                    // KIS stores the part name directly on the ITEM; stock keeps it in the
                    // nested PART snapshot's `name`. Take whichever is present.
                    string pn = child.GetValue("partName");
                    if (string.IsNullOrEmpty(pn))
                    {
                        ConfigNode p = child.GetNode("PART");
                        if (p != null) pn = p.GetValue("name") ?? p.GetValue("part");
                    }
                    if (!string.IsNullOrEmpty(pn))
                        names.Add(InstanceSuffixRx.Replace(pn.Trim(), ""));
                }
                CollectInventoryPartNames(child, names); // nested containers
            }
        }

        /// <summary>Resolve the stored inventory items in a VESSEL node (see
        /// <see cref="CollectInventoryPartNames"/>) to their loaded AvailableParts.</summary>
        private static List<AvailablePart> ResolveInventoryParts(ConfigNode node)
        {
            var names = new List<string>();
            CollectInventoryPartNames(node, names);

            var parts = new List<AvailablePart>();
            foreach (var n in names)
            {
                var ap = PartLoader.getPartInfoByName(n);
                if (ap != null) parts.Add(ap);
            }
            return parts;
        }

        // ── Export: embed ────────────────────────────────────────────────────

        /// <summary>Embed a GKMODS node listing the mods a live vessel's parts come from
        /// into its VESSEL ConfigNode. No-op when the vessel uses only stock parts.</summary>
        public static void EmbedModsInNode(ConfigNode node, Vessel vessel)
        {
            if (node == null || vessel == null || vessel.parts == null) return;
            try
            {
                node.RemoveNodes(MODS_NODE); // avoid duplicates on re-export

                var parts = vessel.parts.Select(p => p.partInfo).ToList();
                // Items inside inventory modules aren't vessel parts — pull their mods too.
                parts.AddRange(ResolveInventoryParts(node));

                var mods = CollectMods(parts);
                if (mods.Count == 0) return;

                ConfigNode mn = node.AddNode(MODS_NODE);
                foreach (var m in mods)
                {
                    ConfigNode e = mn.AddNode("MOD");
                    e.AddValue("folder", m.folder);
                    e.AddValue("ckan", m.ckan);
                    e.AddValue("name", m.name);
                }
                Debug.Log($"[GeneKerman] CkanGenerator: embedded {mods.Count} mod(s) into vessel node.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.EmbedModsInNode failed: {ex.Message}");
            }
        }

        /// <summary>Append a GKMODS text block to raw .craft bytes. Must be the LAST
        /// block appended (after any GKFLAG / GKTSVER) so the strip on the other side is
        /// a clean cut to EOF. Returns the input unchanged on stock-only crafts or error.</summary>
        public static byte[] EmbedModsInCraft(byte[] craftBytes)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);

                var names = new List<string>();
                foreach (Match m in PartLineRx.Matches(text))
                    names.Add(m.Groups[1].Value);
                // KIS inventory items list their part as `partName = ` rather than a
                // `part = ` line, so grab those too (non-parts resolve to null and drop).
                foreach (Match m in PartNameLineRx.Matches(text))
                    names.Add(m.Groups[1].Value);
                if (names.Count == 0) return craftBytes;

                var mods = CollectModsFromPartNames(names);
                if (mods.Count == 0) return craftBytes;

                var sb = new StringBuilder(text);
                if (!text.EndsWith("\n")) sb.Append("\n");
                sb.Append(MODS_NODE).Append("\n{\n");
                foreach (var m in mods)
                {
                    sb.Append("\tMOD\n\t{\n");
                    sb.Append("\t\tfolder = ").Append(m.folder).Append("\n");
                    sb.Append("\t\tckan = ").Append(m.ckan).Append("\n");
                    sb.Append("\t\tname = ").Append(m.name).Append("\n");
                    sb.Append("\t}\n");
                }
                sb.Append("}\n");

                Debug.Log($"[GeneKerman] CkanGenerator: embedded {mods.Count} mod(s) into craft.");
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.EmbedModsInCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Import: strip + act ──────────────────────────────────────────────

        /// <summary>Read + remove the GKMODS node carried by a VESSEL node and, for any
        /// listed mod the recipient lacks, write a CKAN modpack. Safe on nodes without it.</summary>
        public static void ExtractCheckAndStripMods(ConfigNode node)
        {
            if (node == null) return;
            try
            {
                ConfigNode mn = node.GetNode(MODS_NODE);
                if (mn == null) return;

                var mods = ReadModNodes(mn);
                node.RemoveNodes(MODS_NODE);

                string context = node.GetValue("name") ?? "Imported vessel";
                GenerateCkanForMissing(context, mods);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.ExtractCheckAndStripMods failed: {ex.Message}");
            }
        }

        /// <summary>Strip the appended GKMODS text block from raw .craft bytes (run FIRST,
        /// before the TweakScale/flag strips, since GKMODS is appended last). Returns the
        /// cleaned bytes and hands back the parsed mod list via <paramref name="mods"/>.</summary>
        public static byte[] CheckAndStripFromCraft(byte[] rawCraftBytes, out List<ModEntry> mods)
        {
            mods = new List<ModEntry>();
            if (rawCraftBytes == null || rawCraftBytes.Length == 0) return rawCraftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(rawCraftBytes);
                int idx = FindModsBlockStart(text);
                if (idx < 0) return rawCraftBytes;

                mods = ParseModsFromText(text.Substring(idx));

                string body = text.Substring(0, idx).TrimEnd('\r', '\n', ' ', '\t');
                if (body.Length > 0) body += "\n";
                return Encoding.UTF8.GetBytes(body);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.CheckAndStripFromCraft failed: {ex.Message}");
                return rawCraftBytes;
            }
        }

        /// <summary>After a craft is written to disk: drop a "&lt;craft&gt;.gkmods" sidecar (so
        /// the editor hook can re-check later) and write a CKAN modpack for any missing mod.</summary>
        public static void OnCraftInstalled(string craftPath, List<ModEntry> mods)
        {
            if (string.IsNullOrEmpty(craftPath) || mods == null || mods.Count == 0) return;
            try
            {
                WriteSidecar(craftPath, mods);
                GenerateCkanForMissing(Path.GetFileNameWithoutExtension(craftPath), mods);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.OnCraftInstalled failed: {ex.Message}");
            }
        }

        // ── CKAN modpack output ──────────────────────────────────────────────

        /// <summary>Write a CKAN metapackage (.ckan) for the subset of <paramref name="mods"/>
        /// the recipient doesn't have in GameData. No-op when nothing is missing.</summary>
        public static void GenerateCkanForMissing(string context, List<ModEntry> mods)
        {
            if (mods == null || mods.Count == 0) return;

            var installed = InstalledFolders();
            var missing = mods.Where(m => !installed.Contains(m.folder)).ToList();
            if (missing.Count == 0) return;

            try
            {
                Directory.CreateDirectory(CkanOutputDir);

                string id = "GeneKerman-" + SanitizeIdentifier(context);
                var depends = new List<object>();
                foreach (var m in missing)
                    depends.Add(new Dictionary<string, object> { { "name", m.ckan } });

                string list = string.Join(", ", missing.Select(m => m.name).ToArray());
                var doc = new Dictionary<string, object>
                {
                    { "spec_version", "v1.6" },
                    { "identifier", id },
                    { "name", "GeneKerman: mods for '" + context + "'" },
                    { "abstract", "Mods needed to load '" + context + "', shared via GeneKerman: " + list },
                    { "author", "GeneKerman" },
                    { "version", "1.0" },
                    { "kind", "metapackage" },
                    { "license", "unknown" },
                    { "depends", depends },
                };

                string outPath = Path.Combine(CkanOutputDir, SanitizeIdentifier(context) + ".ckan");
                File.WriteAllText(outPath, MiniJSON.Serialize(doc), Encoding.UTF8);
                Debug.Log($"[GeneKerman] CkanGenerator: wrote modpack for {missing.Count} missing mod(s) → {outPath}");

                Post($"⚠ Missing {missing.Count} mod(s) for '{context}'",
                     $"Needs: {list}. A CKAN installer was saved to "
                     + $"GeneKerman_MissingMods/{Path.GetFileName(outPath)}. "
                     + "Open it in CKAN (File ▸ Install from .ckan file).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.GenerateCkanForMissing failed: {ex.Message}");
            }
        }

        // ── Sidecar (editor re-check) ────────────────────────────────────────

        private static void WriteSidecar(string craftPath, List<ModEntry> mods)
        {
            var arr = new List<object>();
            foreach (var m in mods)
                arr.Add(new Dictionary<string, object>
                { { "folder", m.folder }, { "ckan", m.ckan }, { "name", m.name } });
            File.WriteAllText(craftPath + ".gkmods", MiniJSON.Serialize(arr), Encoding.UTF8);
        }

        /// <summary>Read a "&lt;craft&gt;.gkmods" sidecar, or null if absent/unreadable.</summary>
        public static List<ModEntry> ReadSidecar(string craftPath)
        {
            try
            {
                string path = craftPath + ".gkmods";
                if (!File.Exists(path)) return null;
                var list = MiniJSON.DeserializeList(File.ReadAllText(path));
                if (list == null) return null;

                var mods = new List<ModEntry>();
                foreach (var o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string folder = MiniJSON.GetString(d, "folder", "");
                    if (string.IsNullOrEmpty(folder)) continue;
                    mods.Add(new ModEntry
                    {
                        folder = folder,
                        ckan = MiniJSON.GetString(d, "ckan", folder),
                        name = MiniJSON.GetString(d, "name", folder),
                    });
                }
                return mods;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.ReadSidecar failed: {ex.Message}");
                return null;
            }
        }

        // ── CKAN registry (folder → identifier) ──────────────────────────────

        private static Dictionary<string, ModEntry> _registryCache;

        /// <summary>Map of GameData folder → ModEntry, built from CKAN's registry.json so
        /// embedded mods carry their real CKAN identifiers. Empty when CKAN isn't used.
        /// Cached for the session (the registry doesn't change mid-flight).</summary>
        private static Dictionary<string, ModEntry> LoadCkanRegistry()
        {
            if (_registryCache != null) return _registryCache;
            _registryCache = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "CKAN", "registry.json");
                if (!File.Exists(path)) return _registryCache;

                var root = MiniJSON.DeserializeDict(File.ReadAllText(path));
                var installed = MiniJSON.GetDict(root, "installed_modules");
                if (installed == null) return _registryCache;

                foreach (var kv in installed)
                {
                    var mod = kv.Value as Dictionary<string, object>;
                    if (mod == null) continue;

                    var src = MiniJSON.GetDict(mod, "source_module");
                    string identifier = src != null ? MiniJSON.GetString(src, "identifier", kv.Key) : kv.Key;
                    string name = src != null ? MiniJSON.GetString(src, "name", identifier) : identifier;

                    var files = MiniJSON.GetDict(mod, "installed_files");
                    if (files == null) continue;

                    foreach (var fileKey in files.Keys)
                    {
                        string folder = FolderFromInstalledPath(fileKey);
                        if (string.IsNullOrEmpty(folder) || StockFolders.Contains(folder)) continue;
                        if (!_registryCache.ContainsKey(folder))
                            _registryCache[folder] = new ModEntry { folder = folder, ckan = identifier, name = name };
                    }
                }
                Debug.Log($"[GeneKerman] CkanGenerator: mapped {_registryCache.Count} GameData folder(s) from CKAN registry.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator: CKAN registry read failed: {ex.Message}");
            }
            return _registryCache;
        }

        /// <summary>"GameData/RestockPlus/Parts/..." → "RestockPlus". Null for paths that
        /// don't sit under a GameData subfolder.</summary>
        private static string FolderFromInstalledPath(string installedPath)
        {
            if (string.IsNullOrEmpty(installedPath)) return null;
            string[] segs = installedPath.Replace('\\', '/').Split('/');
            for (int i = 0; i < segs.Length - 1; i++)
                if (segs[i].Equals("GameData", StringComparison.OrdinalIgnoreCase))
                    return segs[i + 1];
            return null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static HashSet<string> InstalledFolders()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(GameDataRoot))
                    foreach (var dir in Directory.GetDirectories(GameDataRoot))
                        set.Add(Path.GetFileName(dir));
            }
            catch { /* unreadable GameData → treat as nothing installed */ }
            return set;
        }

        /// <summary>Index of the line-anchored GKMODS block (so a stray substring inside a
        /// value can't match), or -1 if absent. The block is always appended last.</summary>
        private static int FindModsBlockStart(string text)
        {
            int idx = text.LastIndexOf(MODS_NODE, StringComparison.Ordinal);
            if (idx < 0) return -1;
            if (idx > 0 && text[idx - 1] != '\n' && text[idx - 1] != '\r') return -1;
            return idx;
        }

        private static List<ModEntry> ParseModsFromText(string block)
        {
            var mods = new List<ModEntry>();
            ModEntry cur = null;
            foreach (var raw in block.Split('\n'))
            {
                string line = raw.Trim();
                if (line == "MOD") { cur = new ModEntry(); continue; }
                if (cur == null) continue;

                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    // close of a MOD subblock
                    if (line == "}" && !string.IsNullOrEmpty(cur.folder))
                    {
                        if (string.IsNullOrEmpty(cur.ckan)) cur.ckan = cur.folder;
                        if (string.IsNullOrEmpty(cur.name)) cur.name = cur.folder;
                        mods.Add(cur);
                        cur = null;
                    }
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key == "folder") cur.folder = val;
                else if (key == "ckan") cur.ckan = val;
                else if (key == "name") cur.name = val;
            }
            return mods;
        }

        private static List<ModEntry> ReadModNodes(ConfigNode mn)
        {
            var mods = new List<ModEntry>();
            foreach (ConfigNode e in mn.GetNodes("MOD"))
            {
                string folder = e.GetValue("folder");
                if (string.IsNullOrEmpty(folder)) continue;
                mods.Add(new ModEntry
                {
                    folder = folder,
                    ckan = e.GetValue("ckan") ?? folder,
                    name = e.GetValue("name") ?? folder,
                });
            }
            return mods;
        }

        /// <summary>Turn arbitrary text into a CKAN-legal identifier
        /// (^[A-Za-z][A-Za-z0-9-]*$).</summary>
        private static string SanitizeIdentifier(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '-' || c == ' ' || c == '_') sb.Append('-');
            }
            string r = sb.ToString().Trim('-');
            if (r.Length == 0) r = "craft";
            if (!char.IsLetter(r[0])) r = "GK-" + r;
            return r;
        }

        /// <summary>Surface a missing-mod alert. Prefers the mod's toast notification
        /// (persistent, styled, dismiss-on-click) over the ephemeral green ScreenMessage,
        /// which vanished after a few seconds with no way to get it back. Falls back to a
        /// ScreenMessage only when the mod instance isn't available (e.g. headless).</summary>
        private static void Post(string title, string body)
        {
            Debug.LogWarning($"[GeneKerman] {title} — {body}");

            var mod = GeneKermanMod.Instance;
            if (mod != null)
            {
                try { mod.ShowNotification(title, body); return; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] CkanGenerator: notification failed, " +
                                     $"falling back to screen message: {ex.Message}");
                }
            }

            try { ScreenMessages.PostScreenMessage($"{title}: {body}", 12f, ScreenMessageStyle.UPPER_CENTER); }
            catch { /* no screen (headless) — the log line is enough */ }
        }
    }

    /// <summary>
    /// Editor-side trigger: when a craft is loaded into the VAB/SPH, look for its
    /// "&lt;craft&gt;.gkmods" sidecar (written when GeneKerman installed it) and, if the
    /// player is still missing any of its mods, (re)generate a CKAN modpack. Crafts that
    /// didn't arrive through GeneKerman have no sidecar and are silently ignored.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorCkanWatcher : MonoBehaviour
    {
        void Start()
        {
            GameEvents.onEditorLoad.Add(OnEditorLoad);
        }

        void OnDestroy()
        {
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
        }

        private void OnEditorLoad(ShipConstruct ship, CraftBrowserDialog.LoadType type)
        {
            if (ship == null || string.IsNullOrEmpty(ship.shipName)) return;
            try
            {
                string craftPath = VesselDataCollector.FindCraftFile(ship.shipName);
                if (string.IsNullOrEmpty(craftPath)) return;

                var mods = CkanGenerator.ReadSidecar(craftPath);
                if (mods == null || mods.Count == 0) return;

                CkanGenerator.GenerateCkanForMissing(ship.shipName, mods);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] EditorCkanWatcher.OnEditorLoad failed: {ex.Message}");
            }
        }
    }
}
