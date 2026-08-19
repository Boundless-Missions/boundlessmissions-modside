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
 * IMPORT (recipient): we read GKMODS, diff its install paths against the recipient's own
 * GameData, and for any mod they don't have we write a CKAN metapackage (.ckan) listing
 * those mods as dependencies — open it in CKAN and it installs everything the craft
 * needs. Installed crafts also get a small "<craft>.gkmods" sidecar so the editor hook
 * can regenerate the .ckan if the player later loads the craft and is still missing mods.
 *
 * A GameData folder is NOT a mod, and treating it as one is what this file gets wrong
 * if you let it. Two separately-distributed mods routinely share a top-level folder:
 * DeepFreeze installs REPOSoftTech/DeepFreeze while its companion library installs
 * REPOSoftTech/BackgroundResources, and the "-Core" split many mods ship (Firespitter /
 * FirespitterCore, NearFutureElectrical / NearFutureElectrical-Core) puts a parts mod and
 * a plugin-only one in the same folder. Keying on the folder therefore both picks an
 * arbitrary winner on export — usually the companion, since it has no parts and so is a
 * useless thing to hand CKAN — and reads as "already installed" on import for a recipient
 * who has only the companion, which silences the warning entirely. So the registry is
 * indexed by INSTALL PATH, a part resolves through the longest path prefix that exactly
 * one module owns, and the recipient-side check tests that same path rather than a folder.
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
        // Note "SquadExpansion" here is the bare folder only: the DLCs *inside* it are
        // real dependencies (see DlcMods) and are keyed by their two-segment path.
        private static readonly HashSet<string> StockFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Squad", "SquadExpansion", "BoundlessMissions" };

        // The stock expansions. They are dependencies like any other — a paid add-on a
        // large share of players don't own — but they are NOT mods: CKAN can detect a
        // DLC and never install one, so they are reported separately from the modpack
        // rather than written into it as a dependency CKAN would fail to resolve.
        private static readonly Dictionary<string, ModEntry> DlcMods =
            new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase)
        {
            { "SquadExpansion/MakingHistory", new ModEntry {
                folder = "SquadExpansion/MakingHistory", path = "SquadExpansion/MakingHistory",
                ckan = "MakingHistory-DLC", name = "Making History" } },
            { "SquadExpansion/Serenity", new ModEntry {
                folder = "SquadExpansion/Serenity", path = "SquadExpansion/Serenity",
                ckan = "BreakingGround-DLC", name = "Breaking Ground" } },
        };

        private static bool IsDlc(ModEntry m)
        {
            return m != null && m.folder != null && DlcMods.ContainsKey(m.folder);
        }

        private static string GameDataRoot =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

        // Where generated modpacks land — a top-level folder so it's easy to find.
        private static string CkanOutputDir =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GeneKerman_MissingMods");

        // .craft part lines look like `part = mk1pod_4294880972`; the trailing `_<id>`
        // is a per-instance suffix, not part of the part name.
        private static readonly Regex InstanceSuffixRx =
            new Regex(@"_\d+$", RegexOptions.Compiled);
        // A bare identifier on its own line opens a node (the `{` follows on the next).
        private static readonly Regex NodeOpenRx =
            new Regex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

        /// <summary>A mod a craft depends on: its GameData folder, its CKAN identifier
        /// (falls back to the folder when unknown), and a human-readable name.
        ///
        /// <c>path</c> is the GameData-relative directory whose presence means this mod is
        /// installed — usually the same as <c>folder</c>, but "REPOSoftTech/DeepFreeze" for
        /// a mod that lives inside an author's shared folder. It is what the recipient-side
        /// check tests, and it is the one field a GKMODS block written by an older client
        /// won't carry, so every read falls back to <c>folder</c> when it is absent.</summary>
        public class ModEntry
        {
            public string folder;
            public string path;
            public string ckan;
            public string name;
        }

        /// <summary>The install path to test for a mod's presence: <c>path</c> when the
        /// sender recorded one, otherwise the folder (which is what pre-path GKMODS blocks
        /// and non-CKAN senders give us).</summary>
        private static string PathOf(ModEntry m)
        {
            if (m == null) return null;
            return string.IsNullOrEmpty(m.path) ? m.folder : m.path;
        }

        /// <summary>What makes two resolved mods the same mod. The CKAN identifier when we
        /// have one — two mods sharing a folder must stay two entries — falling back to the
        /// install path for a sender without CKAN.</summary>
        private static string DedupeKey(ModEntry m)
        {
            if (m == null) return "";
            return !string.IsNullOrEmpty(m.ckan) ? m.ckan : (PathOf(m) ?? "");
        }

        // ── Export: collect ──────────────────────────────────────────────────

        /// <summary>Resolve the distinct non-stock mods a set of loaded parts come from,
        /// keyed by GameData folder and (when CKAN is present) CKAN identifier.</summary>
        private static List<ModEntry> CollectMods(IEnumerable<AvailablePart> parts)
        {
            var found = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            if (parts == null) return new List<ModEntry>();

            foreach (var ap in parts)
            {
                var e = ResolvePartMod(ap);
                if (e == null) continue;
                string key = DedupeKey(e);
                if (!found.ContainsKey(key)) found[key] = e;
            }
            return found.Values.ToList();
        }

        /// <summary>The mod a loaded part comes from, or null for stock / this mod's own
        /// parts. Prefers the CKAN module that owns the part's own install path over any
        /// guess made from its top-level folder — see the file header for why the folder
        /// alone is not an answer.</summary>
        private static ModEntry ResolvePartMod(AvailablePart ap)
        {
            string folder = GetModFolder(ap);
            if (string.IsNullOrEmpty(folder) || StockFolders.Contains(folder)) return null;

            ModEntry e;
            if (DlcMods.TryGetValue(folder, out e)) return e;

            e = RegistryLookupByUrl(ap.partUrl) ?? RegistryLookupByFolder(folder);
            if (e != null) return e;

            // No CKAN on this machine: the folder is all we can say, which is also all
            // the recipient can check. An identifier CKAN has never heard of makes an
            // unresolvable modpack, but naming the folder at least names the problem.
            return new ModEntry { folder = folder, path = folder, ckan = folder, name = folder };
        }

        /// <summary>Resolve bare GameData folder names to ModEntries carrying their real
        /// CKAN identifiers. The part walk above cannot reach a mod that adds no parts —
        /// a shader/recolour pack, say — so a caller that discovered such a dependency by
        /// other means (see <c>TextureTransfer</c>) needs this to hand it to
        /// <see cref="GenerateCkanForMissing"/> as a proper dependency rather than as a
        /// folder name CKAN has never heard of.</summary>
        public static List<ModEntry> ResolveMods(IEnumerable<string> folders)
        {
            var list = new List<ModEntry>();
            if (folders == null) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || StockFolders.Contains(folder)) continue;
                if (!seen.Add(folder)) continue;

                ModEntry e;
                if (!DlcMods.TryGetValue(folder, out e))
                    e = RegistryLookupByFolder(folder)
                        ?? new ModEntry { folder = folder, path = folder, ckan = folder, name = folder };
                list.Add(e);
            }
            return list;
        }

        /// <summary>The GameData folder a part lives in (first path segment of its URL) —
        /// except under SquadExpansion, where the second segment is kept too. The two
        /// DLCs are bought separately, so "SquadExpansion" alone says nothing about
        /// whether the recipient can load the part; this is the same distinction
        /// ModuleManager draws with <c>:NEEDS[SquadExpansion/MakingHistory]</c>.
        ///
        /// This is a folder, not a mod — see <see cref="ResolvePartMod"/>, which uses it
        /// only to spot stock and the DLCs and to fall back on when CKAN can't be read.</summary>
        private static string GetModFolder(AvailablePart ap)
        {
            if (ap == null || string.IsNullOrEmpty(ap.partUrl)) return null;
            string[] seg = ap.partUrl.Split('/');
            if (seg.Length > 1 && seg[0].Equals("SquadExpansion", StringComparison.OrdinalIgnoreCase))
                return seg[0] + "/" + seg[1];
            return seg[0];
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
                    e.AddValue("path", PathOf(m));
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
                CollectCraftPartNames(text, names);
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
                    sb.Append("\t\tpath = ").Append(PathOf(m)).Append("\n");
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

        /// <summary>The distinct mods a .craft's parts come from, for tagging a marketplace
        /// listing so the website can filter by mod. Unlike the CKAN-export path this surfaces
        /// the stock DLCs (<c>SquadExpansion/MakingHistory</c> → "Making History",
        /// <c>SquadExpansion/Serenity</c> → "Breaking Ground") as their own entries, while
        /// still dropping base-game (Squad) and this mod's own parts. Empty for a stock,
        /// no-DLC craft. Run on the ORIGINAL craft bytes (before GKMODS/flag/thumb embedding).</summary>
        public static List<string> ModFoldersForCraft(byte[] craftBytes)
        {
            var folders = new List<string>();
            if (craftBytes == null || craftBytes.Length == 0) return folders;
            try
            {
                var names = new List<string>();
                CollectCraftPartNames(Encoding.UTF8.GetString(craftBytes), names);
                if (names.Count == 0) return folders;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in names)
                {
                    string name = InstanceSuffixRx.Replace(raw.Trim(), "");
                    var ap = PartLoader.getPartInfoByName(name);
                    if (ap == null) continue;
                    string mod = MarketplaceModName(ap);
                    if (!string.IsNullOrEmpty(mod) && seen.Add(mod))
                        folders.Add(mod);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.ModFoldersForCraft failed: {ex.Message}");
            }
            return folders;
        }

        /// <summary>The distinct part names a .craft references, for the server's pre-flight
        /// check against a buyer's uploaded part catalog. Read straight out of the craft text
        /// rather than resolved through PartLoader, so it is exactly the set of names the
        /// recipient's game will go looking for — including any the SENDER is missing.
        /// Run on the ORIGINAL craft bytes (before GKMODS/flag/thumb embedding).</summary>
        public static List<string> PartNamesForCraft(byte[] craftBytes)
        {
            var names = new List<string>();
            if (craftBytes == null || craftBytes.Length == 0) return names;
            try
            {
                var raw = new List<string>();
                CollectCraftPartNames(Encoding.UTF8.GetString(craftBytes), raw);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in raw)
                {
                    string name = InstanceSuffixRx.Replace(r.Trim(), "");
                    if (name.Length > 0 && seen.Add(name)) names.Add(name);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator.PartNamesForCraft failed: {ex.Message}");
            }
            return names;
        }

        /// <summary>Every part name a .craft text references: each PART's own `part = ` line,
        /// plus the `partName = ` of anything stashed in an inventory (stock STOREDPART, KIS
        /// ITEM), so inventory-only mods aren't dropped.
        ///
        /// Which node a line sits in decides what it means, so this walks the node structure
        /// instead of matching lines flat. `partName` is TWO different fields: on a PART node
        /// it is the Unity component class — "Part" on literally every part, "CompoundPart" on
        /// struts and fuel lines, and in pre-1.0 craft files legacy names like "Strut",
        /// "Winglet", "ControlSurface" — while inside a stored-item node it is a real part
        /// name. A flat scan cannot tell them apart, so it swept "Part" out of every PART node
        /// in every craft and handed it to the buyer's compatibility check as a part nobody
        /// could possibly have installed.</summary>
        private static void CollectCraftPartNames(string text, List<string> into)
        {
            var stack = new List<string>();   // enclosing node names, innermost last
            string pending = null;            // node name read, waiting for its `{`

            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line == "{")
                {
                    stack.Add(pending ?? "");
                    pending = null;
                    continue;
                }
                if (line == "}")
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    pending = null;
                    continue;
                }
                if (NodeOpenRx.IsMatch(line)) { pending = line; continue; }
                pending = null;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;

                string node = stack.Count > 0 ? stack[stack.Count - 1] : "";
                if (key == "part")
                    into.Add(value);
                else if (key == "partName" && (node == "STOREDPART" || node == "ITEM"))
                    into.Add(value);
            }
        }

        /// <summary>The marketplace mod label for a part: the stock DLCs as their own names,
        /// null for base-game / this mod's parts, otherwise the mod's own folder — the last
        /// segment of its install root, so a part under REPOSoftTech/DeepFreeze tags as
        /// "DeepFreeze" and not as its author. That deeper name is only used when the
        /// resolved root actually contains this part, which keeps a plugin-only sibling
        /// (whose root is something like "SomeMod/Plugins") from lending its subfolder name
        /// to a part that doesn't live there. Without CKAN there is nothing to resolve
        /// against and the top-level folder stands, as before.</summary>
        private static string MarketplaceModName(AvailablePart ap)
        {
            if (ap == null || string.IsNullOrEmpty(ap.partUrl)) return null;
            string[] seg = ap.partUrl.Split('/');
            string first = seg[0];
            if (first.Equals("SquadExpansion", StringComparison.OrdinalIgnoreCase))
            {
                if (seg.Length > 1)
                {
                    if (seg[1].Equals("Serenity", StringComparison.OrdinalIgnoreCase)) return "Breaking Ground";
                    if (seg[1].Equals("MakingHistory", StringComparison.OrdinalIgnoreCase)) return "Making History";
                }
                return null; // base SquadExpansion folder — treat as stock
            }
            // Squad / BoundlessMissions (this mod) are not "mods" for filtering purposes.
            if (StockFolders.Contains(first)) return null;

            var e = RegistryLookupByUrl(ap.partUrl);
            string root = PathOf(e);
            if (!string.IsNullOrEmpty(root) && IsPathPrefix(root, ap.partUrl))
            {
                string[] rs = root.Split('/');
                string last = rs[rs.Length - 1];
                if (last.Length > 0) return last;
            }
            return first;
        }

        /// <summary>Whether <paramref name="prefix"/> names a directory the path sits in
        /// (or is), matched a whole segment at a time so "Near" never matches
        /// "NearFuture/…".</summary>
        private static bool IsPathPrefix(string prefix, string path)
        {
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(path)) return false;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return path.Length == prefix.Length || path[prefix.Length] == '/';
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
        /// the recipient doesn't have in GameData. No-op when nothing is missing. A missing
        /// stock expansion is reported but never written into the modpack — CKAN cannot
        /// install a DLC, and listing one as a dependency only makes the pack unresolvable.</summary>
        public static void GenerateCkanForMissing(string context, List<ModEntry> mods)
        {
            if (mods == null || mods.Count == 0) return;

            var installed = InstalledPaths(MaxSegments(mods));
            var missing = mods.Where(m => !installed.Contains(PathOf(m) ?? "")).ToList();
            if (missing.Count == 0) return;

            var missingDlc = missing.Where(IsDlc).ToList();
            missing = missing.Where(m => !IsDlc(m)).ToList();

            // Nothing CKAN can help with — say what's needed and stop.
            if (missing.Count == 0)
            {
                string dlcOnly = string.Join(" and ", missingDlc.Select(m => m.name).ToArray());
                Post($"'{context}' needs the {dlcOnly} expansion",
                     $"This craft uses parts from {dlcOnly}, which you don't have. "
                     + "It is a paid expansion, so CKAN can't install it — the craft will not "
                     + "load without it.");
                return;
            }

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

                string dlcNote = missingDlc.Count == 0 ? "" :
                    $" It also needs the {string.Join(" and ", missingDlc.Select(m => m.name).ToArray())} "
                    + "expansion, which is a paid add-on CKAN can't install for you.";

                Post($"Missing {missing.Count + missingDlc.Count} requirement(s) for '{context}'",
                     $"Needs: {list}. A CKAN installer was saved to "
                     + $"GeneKerman_MissingMods/{Path.GetFileName(outPath)}. "
                     + "Open it in CKAN (File ▸ Install from .ckan file)."
                     + dlcNote);
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
                { { "folder", m.folder }, { "path", PathOf(m) }, { "ckan", m.ckan }, { "name", m.name } });
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
                        path = MiniJSON.GetString(d, "path", folder),
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

        // ── CKAN registry (install path → identifier) ────────────────────────

        /// <summary>How deep below GameData the prefix index goes. A part's url runs at most
        /// <c>Mod/Parts/Category/name/name</c>, and in a real 77-mod install no prefix past
        /// two segments was ever contested, so four is generous — and it keeps the index at
        /// a few thousand entries instead of one per installed file.</summary>
        private const int RegistryPrefixDepth = 4;

        /// <summary>CKAN's answer to "who installed this", in the two shapes we ask it in.</summary>
        private class RegistryIndex
        {
            /// <summary>GameData-relative path prefix → the single module that owns it. A
            /// prefix two modules both install into is deliberately absent, so a lookup
            /// walking down from the longest prefix skips past the ambiguity instead of
            /// guessing at it.</summary>
            public readonly Dictionary<string, ModEntry> byPath =
                new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Top-level GameData folder → the module with the most files under it.
            /// The fallback for a caller who only has a folder name (or a part whose every
            /// prefix is contested). Picking by file count rather than by whichever module
            /// the registry happened to list first is what stops a plugin-only companion
            /// from claiming a folder: it is the parts mod that carries the files.</summary>
            public readonly Dictionary<string, ModEntry> byFolder =
                new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);

            public bool Empty { get { return byPath.Count == 0; } }
        }

        private static RegistryIndex _registryCache;

        /// <summary>Index CKAN's registry.json by install path. Empty when CKAN isn't used.
        /// Cached for the session (the registry doesn't change mid-flight).</summary>
        private static RegistryIndex LoadCkanRegistry()
        {
            if (_registryCache != null) return _registryCache;
            var idx = new RegistryIndex();
            _registryCache = idx;
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "CKAN", "registry.json");
                if (!File.Exists(path)) return idx;

                var root = MiniJSON.DeserializeDict(File.ReadAllText(path));
                var installed = MiniJSON.GetDict(root, "installed_modules");
                if (installed == null) return idx;

                // prefix → identifier, plus the prefixes more than one module claims.
                var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var contested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
                // folder → identifier → how many paths that module installs there.
                var weight = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in installed)
                {
                    var mod = kv.Value as Dictionary<string, object>;
                    if (mod == null) continue;

                    var src = MiniJSON.GetDict(mod, "source_module");
                    string identifier = src != null ? MiniJSON.GetString(src, "identifier", kv.Key) : kv.Key;
                    string name = src != null ? MiniJSON.GetString(src, "name", identifier) : identifier;

                    var files = MiniJSON.GetDict(mod, "installed_files");
                    if (files == null) continue;

                    string shortest = null;
                    int shortestLen = int.MaxValue;
                    foreach (var fileKey in files.Keys)
                    {
                        string rel = RelativeToGameData(fileKey);
                        if (string.IsNullOrEmpty(rel)) continue;

                        string[] segs = rel.Split('/');
                        if (StockFolders.Contains(segs[0])) continue;

                        // Ties break on the path itself, so the root a mod reports never
                        // depends on the order its files were listed in.
                        if (segs.Length < shortestLen ||
                            (segs.Length == shortestLen && string.CompareOrdinal(rel, shortest) < 0))
                        { shortest = rel; shortestLen = segs.Length; }

                        int depth = Math.Min(segs.Length, RegistryPrefixDepth);
                        for (int n = 1; n <= depth; n++)
                        {
                            string prefix = string.Join("/", segs, 0, n);
                            string had;
                            if (owner.TryGetValue(prefix, out had))
                            {
                                if (!had.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                                    contested.Add(prefix);
                            }
                            else owner[prefix] = identifier;
                        }

                        Dictionary<string, int> byId;
                        if (!weight.TryGetValue(segs[0], out byId))
                            weight[segs[0]] = byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        int n0;
                        byId.TryGetValue(identifier, out n0);
                        byId[identifier] = n0 + 1;
                    }
                    if (shortest == null) continue;

                    string installRoot = InstallRoot(shortest);
                    if (string.IsNullOrEmpty(installRoot)) continue;
                    entries[identifier] = new ModEntry
                    {
                        folder = installRoot.Split('/')[0],
                        path = installRoot,
                        ckan = identifier,
                        name = name,
                    };
                }

                foreach (var kv in owner)
                {
                    if (contested.Contains(kv.Key)) continue;
                    ModEntry e;
                    if (entries.TryGetValue(kv.Value, out e)) idx.byPath[kv.Key] = e;
                }

                foreach (var kv in weight)
                {
                    string best = null;
                    int bestN = -1;
                    foreach (var c in kv.Value)
                    {
                        // Ties break on the identifier so the winner never depends on the
                        // order the registry happened to be serialised in.
                        if (c.Value > bestN ||
                            (c.Value == bestN && string.CompareOrdinal(c.Key, best) < 0))
                        { best = c.Key; bestN = c.Value; }
                    }
                    ModEntry e;
                    if (best != null && entries.TryGetValue(best, out e)) idx.byFolder[kv.Key] = e;
                }

                Debug.Log($"[GeneKerman] CkanGenerator: indexed {idx.byPath.Count} install path(s) "
                          + $"across {idx.byFolder.Count} folder(s) from CKAN registry "
                          + $"({contested.Count} shared by more than one mod).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CkanGenerator: CKAN registry read failed: {ex.Message}");
            }
            return _registryCache;
        }

        /// <summary>The CKAN module that owns a part's install path — the longest prefix of
        /// the url exactly one module claims. Null when CKAN isn't in use, or when the part
        /// sits somewhere two modules both install into.</summary>
        private static ModEntry RegistryLookupByUrl(string partUrl)
        {
            var idx = LoadCkanRegistry();
            if (idx.Empty || string.IsNullOrEmpty(partUrl)) return null;

            string[] segs = partUrl.Split('/');
            for (int n = Math.Min(segs.Length, RegistryPrefixDepth); n >= 1; n--)
            {
                ModEntry e;
                if (idx.byPath.TryGetValue(string.Join("/", segs, 0, n), out e)) return e;
            }
            return null;
        }

        /// <summary>The CKAN module that best accounts for a top-level GameData folder, for
        /// callers who have no finer path to go on. Null when CKAN isn't in use.</summary>
        private static ModEntry RegistryLookupByFolder(string folder)
        {
            var idx = LoadCkanRegistry();
            if (idx.Empty || string.IsNullOrEmpty(folder)) return null;
            ModEntry e;
            return idx.byFolder.TryGetValue(folder, out e) ? e : null;
        }

        /// <summary>"GameData/REPOSoftTech/DeepFreeze/Parts/…" → "REPOSoftTech/DeepFreeze/Parts/…".
        /// Null for paths that don't sit under a GameData subfolder.</summary>
        private static string RelativeToGameData(string installedPath)
        {
            if (string.IsNullOrEmpty(installedPath)) return null;
            string[] segs = installedPath.Replace('\\', '/').Split('/');
            for (int i = 0; i < segs.Length - 1; i++)
                if (segs[i].Equals("GameData", StringComparison.OrdinalIgnoreCase))
                    return string.Join("/", segs, i + 1, segs.Length - i - 1);
            return null;
        }

        /// <summary>The directory whose presence proves a mod is installed, taken from the
        /// shallowest path it installs. CKAN lists directories as entries in their own right,
        /// so that is usually already a directory; when a mod's shallowest entry is a loose
        /// file (a bare plugin dll) the containing directory stands in for it.</summary>
        private static string InstallRoot(string shortestPath)
        {
            if (string.IsNullOrEmpty(shortestPath)) return null;
            int slash = shortestPath.LastIndexOf('/');
            string last = slash < 0 ? shortestPath : shortestPath.Substring(slash + 1);
            if (last.IndexOf('.') < 0) return shortestPath;
            return slash < 0 ? null : shortestPath.Substring(0, slash);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>The GameData-relative directory paths this install has, to
        /// <paramref name="depth"/> segments — the set the recipient-side check tests a
        /// mod's install path against. Walking to the depth the mods actually name rather
        /// than to the top level is what makes "has REPOSoftTech/BackgroundResources" stop
        /// reading as "has DeepFreeze"; it also subsumes the DLCs, whose two-segment paths
        /// used to need a special case so that owning one didn't read as owning the other.
        /// Read fresh each time: a player can install a mod and reopen the craft.</summary>
        private static HashSet<string> InstalledPaths(int depth)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(GameDataRoot)) return set;
                WalkInstalled(GameDataRoot, "", set, Math.Max(1, depth));
            }
            catch { /* unreadable GameData → treat as nothing installed */ }
            return set;
        }

        private static void WalkInstalled(string dir, string prefix, HashSet<string> into, int depth)
        {
            if (depth <= 0) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                string rel = prefix.Length == 0
                    ? Path.GetFileName(sub)
                    : prefix + "/" + Path.GetFileName(sub);
                into.Add(rel);
                WalkInstalled(sub, rel, into, depth - 1);
            }
        }

        /// <summary>How deep the deepest install path in a mod list goes, so the GameData
        /// walk stops as soon as it can answer for all of them.</summary>
        private static int MaxSegments(List<ModEntry> mods)
        {
            int max = 1;
            foreach (var m in mods)
            {
                string p = PathOf(m);
                if (string.IsNullOrEmpty(p)) continue;
                int n = 1;
                for (int i = 0; i < p.Length; i++) if (p[i] == '/') n++;
                if (n > max) max = n;
            }
            return max;
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
                        if (string.IsNullOrEmpty(cur.path)) cur.path = cur.folder;
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
                else if (key == "path") cur.path = val;
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
                    path = e.GetValue("path") ?? folder,
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
