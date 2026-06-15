/*
 * FlagTransfer.cs – Carry a craft's custom mission flags between players.
 *
 * A part stores the flag the player painted on it as `flag = <GameData-relative
 * path without extension>` (e.g. "MyFlags/eagle"). When a craft/vessel moves to
 * another player who doesn't have that image on disk, KSP shows a missing decal.
 *
 * This rides the SAME channel the rest of a transfer already uses: just as crew
 * roster data travels as GKCREW child nodes inside the vessel ConfigNode, the
 * referenced flag image(s) travel as GKFLAG child nodes (base64). On the
 * receiving side the files are written into GameData and injected into
 * GameDatabase at runtime so they render immediately (no restart), then the
 * GKFLAG nodes are stripped so the craft/vessel loads normally.
 *
 * CONTENT ADDRESSING: KSP names imported flags with short random IDs (e.g.
 * "Squad/Flags/UtB0nwS"), so two players can have the SAME name pointing at
 * DIFFERENT images. To make transfers collision-proof we don't keep the original
 * path: every embedded flag is re-addressed to "GeneKerman/Flags/<sha256-of-bytes>"
 * and the craft/vessel's flag references (missionFlag / flag / currentflagUrl …)
 * are rewritten to that path. Identical images dedupe to the same hash; different
 * images can never clash, and a player's own Squad/Flags is never touched.
 *
 * Stock flags (Squad/SquadExpansion) are never embedded — everyone has them.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GeneKerman
{
    public static class FlagTransfer
    {
        private const string FLAG_NODE = "GKFLAG";

        // GameData-relative folder that holds every flag received via transfer, keyed
        // by content hash so names are globally unique and dedupe automatically.
        private const string TRANSFER_DIR = "GeneKerman/Flags";

        // Extensions KSP recognises for a flag texture, in probe order.
        private static readonly string[] FLAG_EXTS =
            { "png", "dds", "jpg", "jpeg", "truecolor", "mbm", "tga" };

        private static string GameDataRoot =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

        // ── Export: embed ────────────────────────────────────────────────────

        /// <summary>Embed every non-stock flag referenced by the node's PART list as
        /// GKFLAG child nodes. Safe to call on any VESSEL/craft-root ConfigNode.</summary>
        public static void EmbedFlagsInNode(ConfigNode node)
        {
            if (node == null) return;
            try
            {
                node.RemoveNodes(FLAG_NODE); // avoid duplicates on re-export
                var urls = CollectFlagUrls(node);
                Debug.Log($"[GeneKerman] FlagTransfer: scanned node, found {urls.Count} candidate flag URL(s): {string.Join(", ", new List<string>(urls).ToArray())}");
                if (urls.Count == 0) return;

                var manifest = BuildFlagManifest(urls);
                if (manifest.Count == 0) return;

                // Repoint the node's flag references to the content-addressed paths …
                var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in manifest) remap[kv.Key] = kv.Value.NewUrl;
                RewriteFlagValuesInNode(node, remap);

                // … then attach the images so the recipient can resolve them.
                foreach (var kv in manifest)
                {
                    FlagAsset a = kv.Value;
                    ConfigNode fn = node.AddNode(FLAG_NODE);
                    fn.AddValue("url", a.NewUrl);
                    fn.AddValue("ext", a.Ext);
                    fn.AddValue("data", EncodeFlagData(a.Data));
                }
                Debug.Log($"[GeneKerman] FlagTransfer: embedded {manifest.Count} flag(s) into node (content-addressed).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] FlagTransfer.EmbedFlagsInNode failed: {ex.Message}");
            }
        }

        /// <summary>Embed flags into raw .craft file bytes (text ConfigNode). Returns
        /// the (possibly modified) bytes; on any failure returns the input unchanged.</summary>
        public static byte[] EmbedFlagsInCraft(byte[] craftBytes)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                // IMPORTANT: a .craft file has NO enclosing node (bare top-level `ship =`,
                // `PART {}` …). Parsing it and writing it back via ConfigNode.ToString()
                // wraps everything in a spurious `root { }`, which KSP's craft loader
                // rejects ("0 parts / incompatible version"). So we parse a *copy* only to
                // discover the flag URLs, then APPEND the GKFLAG block(s) as raw text to the
                // original bytes — the craft body stays byte-for-byte unchanged.
                ConfigNode probe = LoadConfigFromBytes(craftBytes);
                if (probe == null) return craftBytes;
                var urls = CollectFlagUrls(probe);
                Debug.Log($"[GeneKerman] FlagTransfer: craft scan found {urls.Count} candidate flag URL(s): {string.Join(", ", new List<string>(urls).ToArray())}");
                if (urls.Count == 0) return craftBytes;

                var manifest = BuildFlagManifest(urls);
                if (manifest.Count == 0) return craftBytes;

                // Repoint each flag reference in the craft body to its content-addressed
                // path (plain text edit — never reparse the craft), then append the images.
                string text = Encoding.UTF8.GetString(craftBytes);
                foreach (var kv in manifest)
                    text = RewriteFlagUrlInText(text, kv.Key, kv.Value.NewUrl);

                var sb = new StringBuilder();
                foreach (var kv in manifest)
                {
                    FlagAsset a = kv.Value;
                    sb.Append(FLAG_NODE).Append("\n{\n");
                    sb.Append("\turl = ").Append(a.NewUrl).Append("\n");
                    sb.Append("\text = ").Append(a.Ext).Append("\n");
                    sb.Append("\tdata = ").Append(EncodeFlagData(a.Data)).Append("\n");
                    sb.Append("}\n");
                }
                Debug.Log($"[GeneKerman] FlagTransfer: embedded {manifest.Count} flag(s) into craft (content-addressed).");

                if (!text.EndsWith("\n")) text += "\n";
                return Encoding.UTF8.GetBytes(text + sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] FlagTransfer.EmbedFlagsInCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Import: install + strip ──────────────────────────────────────────

        /// <summary>Install every GKFLAG carried by the node (writing missing files to
        /// GameData and injecting them into GameDatabase for immediate display), then
        /// remove the GKFLAG nodes so the craft/vessel loads cleanly.</summary>
        public static void ExtractAndInstallFlags(ConfigNode node)
        {
            if (node == null) return;
            try
            {
                var flags = new List<ConfigNode>();
                CollectFlagNodesRecursive(node, flags);
                Debug.Log($"[GeneKerman] FlagTransfer: import found {flags.Count} GKFLAG node(s) to install.");
                int installed = 0;
                foreach (ConfigNode fn in flags)
                    if (TryInstallFlagNode(fn)) installed++;
                if (flags.Count > 0) node.RemoveNodes(FLAG_NODE);
                if (installed > 0)
                    Debug.Log($"[GeneKerman] FlagTransfer: installed {installed} flag(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] FlagTransfer.ExtractAndInstallFlags failed: {ex.Message}");
            }
        }

        /// <summary>Install + strip flags from raw .craft bytes before they're written to
        /// disk. Returns cleaned bytes; on failure returns the input unchanged.</summary>
        public static byte[] StripAndInstallFlagsFromCraft(byte[] rawCraftBytes)
        {
            if (rawCraftBytes == null || rawCraftBytes.Length == 0) return rawCraftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(rawCraftBytes);
                // The flag block(s) were appended at the very end of the craft text. Find
                // where they start so we can install them then cut them back off, leaving
                // the original craft body untouched (never reformat it via ConfigNode).
                int idx = FindFlagSectionStart(text);
                if (idx < 0)
                {
                    Debug.Log("[GeneKerman] FlagTransfer: downloaded craft carries no GKFLAG block (sender embedded none) — nothing to install.");
                    return rawCraftBytes;
                }

                InstallFlagsFromText(text.Substring(idx));

                string body = text.Substring(0, idx).TrimEnd('\r', '\n', ' ', '\t');
                if (body.Length > 0) body += "\n";
                return Encoding.UTF8.GetBytes(body);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] FlagTransfer.StripAndInstallFlagsFromCraft failed: {ex.Message}");
                return rawCraftBytes;
            }
        }

        /// <summary>Index of the first appended GKFLAG block in craft text (start of its
        /// line), or -1 if none. Anchored to line start so a stray substring can't match.</summary>
        private static int FindFlagSectionStart(string text)
        {
            int i = text.IndexOf("\n" + FLAG_NODE, StringComparison.Ordinal);
            if (i >= 0) return i + 1; // skip the leading newline
            return text.StartsWith(FLAG_NODE, StringComparison.Ordinal) ? 0 : -1;
        }

        /// <summary>Parse a text fragment containing only GKFLAG nodes (read-only) and
        /// install each carried flag.</summary>
        private static void InstallFlagsFromText(string flagSection)
        {
            ConfigNode parsed = LoadConfigFromBytes(Encoding.UTF8.GetBytes(flagSection));
            if (parsed == null) return;
            var flagNodes = new List<ConfigNode>();
            CollectFlagNodesRecursive(parsed, flagNodes);
            Debug.Log($"[GeneKerman] FlagTransfer: import found {flagNodes.Count} GKFLAG node(s) to install.");
            int installed = 0;
            foreach (ConfigNode fn in flagNodes)
                if (TryInstallFlagNode(fn)) installed++;
            if (installed > 0)
                Debug.Log($"[GeneKerman] FlagTransfer: installed {installed} flag(s).");
        }

        /// <summary>Collect every GKFLAG node anywhere in the tree (handles a parsed
        /// fragment that ConfigNode may have nested under a wrapper node).</summary>
        private static void CollectFlagNodesRecursive(ConfigNode node, List<ConfigNode> outList)
        {
            for (int i = 0; i < node.nodes.Count; i++)
            {
                ConfigNode child = node.nodes[i];
                if (child.name == FLAG_NODE) outList.Add(child);
                CollectFlagNodesRecursive(child, outList);
            }
        }

        /// <summary>Decode one GKFLAG node and install it. Returns true if something new
        /// was written; false if skipped (already present, malformed, or failed).</summary>
        private static bool TryInstallFlagNode(ConfigNode fn)
        {
            string url = fn.GetValue("url");
            string ext = fn.GetValue("ext");
            string b64 = fn.GetValue("data");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(b64)) return false;
            try
            {
                return InstallOneFlag(url, ext, DecodeFlagData(b64));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] FlagTransfer: install of '{url}' failed: {ex.Message}");
                return false;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Distinct non-stock flag URLs referenced anywhere in the node tree.
        /// A flag URL can live at the root (`missionFlag`), on a part (`flag`), or
        /// inside a flag-decal MODULE (`currentflagUrl` / `flagUrl`), so we walk every
        /// node and pick up any value whose key mentions "flag" and whose value looks
        /// like a path. This skips non-URL flag fields like `flagDisplayed`/`flagSize`.</summary>
        private static HashSet<string> CollectFlagUrls(ConfigNode node)
        {
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectFlagUrlsRecursive(node, urls);
            return urls;
        }

        private static void CollectFlagUrlsRecursive(ConfigNode node, HashSet<string> urls)
        {
            for (int i = 0; i < node.values.Count; i++)
            {
                ConfigNode.Value v = node.values[i];
                if (v == null || string.IsNullOrEmpty(v.name) || string.IsNullOrEmpty(v.value))
                    continue;
                if (v.name.IndexOf("flag", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string val = v.value.Trim();
                if (val.IndexOf('/') < 0) continue;   // a real flag URL is a path
                if (IsStockFlag(val)) continue;
                urls.Add(val);
            }
            for (int i = 0; i < node.nodes.Count; i++)
                CollectFlagUrlsRecursive(node.nodes[i], urls);
        }

        /// <summary>Stock flags ship with the game and exist for everyone. KSP's in-game
        /// flag importer drops *custom* flags into Squad/Flags too (random names like
        /// "xrnjNyc"), so we can't skip that whole folder — only the always-present
        /// default and the expansion flags. Anything else the recipient might lack is
        /// embedded; they skip it on install if they already have it.</summary>
        private static bool IsStockFlag(string url)
        {
            return url.Equals("Squad/Flags/default", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("SquadExpansion/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A flag staged for transfer: its content-addressed destination URL,
        /// file extension, and raw image bytes.</summary>
        private class FlagAsset
        {
            public string NewUrl;
            public string Ext;
            public byte[] Data;
        }

        /// <summary>For each referenced flag URL, read its image and map the original URL
        /// to a content-addressed destination ("GeneKerman/Flags/&lt;sha256&gt;"). URLs whose
        /// image can't be read on disk are dropped (logged).</summary>
        private static Dictionary<string, FlagAsset> BuildFlagManifest(ICollection<string> urls)
        {
            var map = new Dictionary<string, FlagAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (string url in urls)
            {
                string ext;
                byte[] data = ReadFlagFile(url, out ext);
                if (data == null)
                {
                    Debug.LogWarning($"[GeneKerman] FlagTransfer: flag '{url}' referenced but no image file found on disk — not embedding.");
                    continue;
                }
                string newUrl = ComputeContentUrl(data);
                map[url] = new FlagAsset { NewUrl = newUrl, Ext = ext, Data = data };
                Debug.Log($"[GeneKerman] FlagTransfer: '{url}' → '{newUrl}' (.{ext}, {data.Length} bytes).");
            }
            return map;
        }

        /// <summary>URL-safe base64 with no padding. Standard base64 contains '/', and a
        /// "//" pair makes KSP's ConfigNode parser treat the rest of the value as a comment
        /// — silently truncating the image. Mapping '+'→'-', '/'→'_' and dropping '=' keeps
        /// the data intact through any ConfigNode round-trip.</summary>
        private static string EncodeFlagData(byte[] data)
        {
            return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static byte[] DecodeFlagData(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            switch (b.Length % 4)
            {
                case 2: b += "=="; break;
                case 3: b += "="; break;
            }
            return Convert.FromBase64String(b);
        }

        /// <summary>Content-addressed flag URL: the SHA-256 of the image bytes under the
        /// shared transfer folder, so identical images share one name and different ones
        /// never collide.</summary>
        private static string ComputeContentUrl(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var hex = new StringBuilder(h.Length * 2);
                foreach (byte b in h) hex.Append(b.ToString("x2"));
                return TRANSFER_DIR + "/" + hex.ToString();
            }
        }

        /// <summary>Replace a flag URL value in raw craft text with its new path. Only an
        /// exact end-of-line value ("= &lt;oldUrl&gt;") is matched, so a URL that is a prefix
        /// of another can't be partially clobbered.</summary>
        private static string RewriteFlagUrlInText(string text, string oldUrl, string newUrl)
        {
            string pattern = "= " + Regex.Escape(oldUrl) + @"(?=[ \t\r]*(\n|$))";
            return Regex.Replace(text, pattern, "= " + newUrl);
        }

        /// <summary>Repoint every flag-keyed value in the node tree (missionFlag / flag /
        /// currentflagUrl …) from an original URL to its content-addressed path.</summary>
        private static void RewriteFlagValuesInNode(ConfigNode node, Dictionary<string, string> remap)
        {
            for (int i = 0; i < node.values.Count; i++)
            {
                ConfigNode.Value v = node.values[i];
                if (v == null || string.IsNullOrEmpty(v.name) || string.IsNullOrEmpty(v.value)) continue;
                if (v.name.IndexOf("flag", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string nv;
                if (remap.TryGetValue(v.value.Trim(), out nv)) v.value = nv;
            }
            for (int i = 0; i < node.nodes.Count; i++)
                RewriteFlagValuesInNode(node.nodes[i], remap);
        }

        /// <summary>Read the on-disk texture bytes for a flag URL, probing known
        /// extensions. Returns null (and ext = null) if no file is found.</summary>
        private static byte[] ReadFlagFile(string url, out string ext)
        {
            ext = null;
            string rel = url.Replace('/', Path.DirectorySeparatorChar);
            foreach (string e in FLAG_EXTS)
            {
                string path = Path.Combine(GameDataRoot, rel + "." + e);
                if (File.Exists(path))
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(path);
                        ext = e;
                        return data;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[GeneKerman] FlagTransfer: could not read '{path}': {ex.Message}");
                        return null;
                    }
                }
            }
            return null;
        }

        /// <summary>Write a flag to GameData (if absent) and register it with
        /// GameDatabase so it renders without a restart. Returns true if anything
        /// new was installed; false if the recipient already had it.</summary>
        private static bool InstallOneFlag(string url, string ext, byte[] data)
        {
            // Already known to KSP? Don't touch the recipient's existing flag.
            if (GameDatabase.Instance != null && GameDatabase.Instance.GetTexture(url, false) != null)
            {
                Debug.Log($"[GeneKerman] FlagTransfer: flag '{url}' already present on this install — transfer OK, nothing to install.");
                return false;
            }

            if (string.IsNullOrEmpty(ext)) ext = "png";
            string rel = url.Replace('/', Path.DirectorySeparatorChar);
            string path = Path.Combine(GameDataRoot, rel + "." + ext);

            // Persist to disk so the flag survives future sessions. Rewrite when the
            // on-disk size differs from the payload — this heals files left truncated by
            // the earlier base64 bug (a content-addressed match is otherwise identical).
            if (!File.Exists(path) || new FileInfo(path).Length != data.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, data);
                Debug.Log($"[GeneKerman] FlagTransfer: wrote flag '{url}' ({data.Length} bytes) → {path}");
            }

            RegisterRuntimeTexture(url, ext, data);
            return true;
        }

        /// <summary>Decode PNG/JPG bytes and add them to GameDatabase under the flag
        /// URL so a freshly delivered flag shows immediately. Compressed formats
        /// (dds/mbm/...) can't be decoded at runtime and fall back to next launch.</summary>
        private static void RegisterRuntimeTexture(string url, string ext, byte[] data)
        {
            if (GameDatabase.Instance == null) return;
            string e = (ext ?? "").ToLowerInvariant();
            if (e != "png" && e != "jpg" && e != "jpeg")
            {
                Debug.Log($"[GeneKerman] FlagTransfer: '{url}' is .{ext} — will appear after next KSP launch.");
                return;
            }
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!tex.LoadImage(data)) return;
                tex.name = url;
                var info = new GameDatabase.TextureInfo(null, tex, false, false, false) { name = url };
                GameDatabase.Instance.databaseTexture.Add(info);
                Debug.Log($"[GeneKerman] FlagTransfer: registered runtime flag texture '{url}'.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] FlagTransfer: runtime texture register for '{url}' failed: {ex.Message}");
            }
        }

        /// <summary>Load arbitrary ConfigNode text via a temp file — ConfigNode.Parse on
        /// a raw string is unreliable, mirroring VesselTransfer's import path.</summary>
        private static ConfigNode LoadConfigFromBytes(byte[] bytes)
        {
            string tempPath = Path.Combine(
                KSPUtil.ApplicationRootPath, "PluginData", "GeneKerman_flag_tmp.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath));
            File.WriteAllBytes(tempPath, bytes);
            ConfigNode node = ConfigNode.Load(tempPath);
            try { File.Delete(tempPath); } catch { }
            return node;
        }
    }
}
