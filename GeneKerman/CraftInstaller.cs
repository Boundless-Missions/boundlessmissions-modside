/*
 * CraftInstaller.cs – Saves received craft/loadmeta files to KSP directories.
 *
 * When a contract submission includes craft files, this class
 * places them in the correct Ships/ directory based on the craft type
 * header (VAB or SPH) in the current save.
 */

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GeneKerman
{
    public static class CraftInstaller
    {
        // ── Bounded gunzip ────────────────────────────────────────────────────

        /// <summary>The server's per-file upload ceiling (api_server.MAX_UPLOAD_BYTES).
        /// Nothing larger than this can have been stored, so it — not a guess at what a
        /// craft weighs — is the real bound on what may come back down.</summary>
        public const int ServerUploadCeilingBytes = 25 * 1024 * 1024;

        /// <summary>
        /// Ceiling on what one downloaded payload may expand to.
        ///
        /// gzip reaches ~1000:1 on repetitive input, so the server's upload limit permits
        /// a multi-gigabyte expansion from a file that passed every check on the way in —
        /// and MemoryStream doubles its buffer, so the peak allocation is about twice the
        /// target before it throws, by which point KSP is gone.
        ///
        /// The first cut of this cap was 8 MB, justified by "a few hundred KB", which was
        /// measured on the small test saves and was wrong about the game people actually
        /// play. Across the four dev instances the largest single VESSEL node is 3.02 MB
        /// (563 parts), the largest .craft 1.54 MB, and one carried flag PNG 587 KB as
        /// base64 — so a live-vessel quicksend of that station is the node plus a ~2 MB
        /// blueprint plus flag blocks, about 6 MB for *one* vessel, and ImportFleet
        /// handles a GKFLEET of several. Those payloads uploaded happily and then refused
        /// to come down, and the refusal is the worst-shaped failure available: the import
        /// never acks, so it is re-downloaded and re-refused every launch, forever.
        ///
        /// So the ceiling is derived from the only number that actually bounds this — what
        /// the server would store — at 2×, which leaves room for the gzip container's own
        /// worst case without pretending to know how big a ship is. It is a guard against
        /// a bomb, not a size policy; the size policy is the server's.
        /// </summary>
        public const int MaxDecompressedBytes = 2 * ServerUploadCeilingBytes;

        /// <summary>Count non-overlapping occurrences of an ASCII needle in a byte
        /// buffer, without materialising the whole payload as a string.
        ///
        /// Used to bound a craft's declared part count before anything parses or walks
        /// it — so the guard cannot itself be the expensive step it is guarding.</summary>
        internal static int CountOccurrences(byte[] hay, string needle)
        {
            if (hay == null || string.IsNullOrEmpty(needle)) return 0;
            int n = needle.Length, count = 0;
            for (int i = 0; i + n <= hay.Length; i++)
            {
                int j = 0;
                while (j < n && hay[i + j] == (byte)needle[j]) j++;
                if (j == n) { count++; i += n - 1; }
            }
            return count;
        }

        /// <summary>True for the gzip magic bytes.</summary>
        public static bool IsGzip(byte[] data)
        {
            return data != null && data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;
        }

        /// <summary>
        /// Gunzip within <see cref="MaxDecompressedBytes"/>. Over the cap (or on any
        /// stream error) the payload is reported unusable rather than throwing mid-way
        /// through an install: the caller leaves its queue entry unacked and says so,
        /// which is a retry, where a half-applied install cannot be undone.
        /// </summary>
        public static bool TryGunzip(byte[] data, out byte[] raw)
        {
            raw = null;
            if (!IsGzip(data)) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var gz = new System.IO.Compression.GZipStream(
                           ms, System.IO.Compression.CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buf = new byte[64 * 1024];
                    long total = 0;
                    int n;
                    // Read in fixed chunks rather than CopyTo: the cap has to be tested
                    // between reads, and CopyTo only returns once the whole stream (of
                    // whatever size the sender chose) is already in memory.
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxDecompressedBytes)
                        {
                            Debug.LogWarning($"[GeneKerman] Refused a payload that expands past " +
                                             $"{MaxDecompressedBytes / (1024 * 1024)} MB " +
                                             $"(from {data.Length} compressed bytes).");
                            return false;
                        }
                        output.Write(buf, 0, n);
                    }
                    raw = output.ToArray();
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Gzip decompression failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Install a craft file into the correct Ships/ directory.
        /// Parses the craft header to determine type (VAB/SPH).
        /// </summary>
        /// <param name="craftData">Raw craft file bytes (may be gzipped)</param>
        /// <param name="craftFileName">Original filename (e.g. "lil guy.craft")</param>
        /// <param name="loadmetaContent">Optional loadmeta content string</param>
        /// <returns>Path where the craft was installed, or null on failure</returns>
        public static string Install(byte[] craftData, string craftFileName, string loadmetaContent = null)
        {
            string refusal;
            return Install(craftData, craftFileName, loadmetaContent, out refusal);
        }

        /// <summary>
        /// <see cref="Install(byte[],string,string)"/>, additionally saying whether the
        /// failure was the *payload* rather than a passing condition.
        ///
        /// The distinction is the whole point: a caller that leaves its queue entry
        /// unacked is asking for the same bytes again, which is right for a download
        /// hiccup and a permanent loop for a payload this build will never accept. A
        /// non-null <paramref name="refusal"/> is a sentence to show the player and a
        /// signal to ack and move on.
        /// </summary>
        public static string Install(byte[] craftData, string craftFileName,
                                     string loadmetaContent, out string refusal)
        {
            refusal = null;
            if (craftData == null || craftData.Length == 0)
            {
                Debug.LogWarning("[GeneKerman] CraftInstaller: No craft data provided.");
                refusal = "the file was empty";
                return null;
            }

            // Decompress if gzipped (gzip magic bytes: 0x1F 0x8B). A payload that will
            // not fit the cap is unusable, not "install what came out of it": there is
            // no partial craft worth writing, and the old fall-back to the raw bytes
            // would have written the gzip container to disk as a .craft.
            byte[] rawData = craftData;
            if (IsGzip(craftData))
            {
                if (!TryGunzip(craftData, out rawData))
                {
                    Debug.LogWarning("[GeneKerman] CraftInstaller: the craft payload could not be " +
                                     "decompressed within the size cap; nothing installed.");
                    refusal = $"it could not be unpacked within the {MaxDecompressedBytes / (1024 * 1024)} MB limit";
                    return null;
                }
                Debug.Log($"[GeneKerman] Decompressed craft file: {craftData.Length} → {rawData.Length} bytes");
            }

            // Bound the PART count before anything walks it.
            //
            // The byte cap above bounds the payload but not its SHAPE: a `.craft` is
            // plain text and a minimal part stanza is a few dozen bytes, so a file
            // comfortably inside the cap can still declare hundreds of thousands of
            // parts. Everything downstream is O(parts) on the main thread — the alias
            // walk, the paint and fuel reconciles, the ScaleBridge pass, and KSP's own
            // load — so this is the blueprint half of VesselTransfer.MaxPartsPerVessel.
            // Counted on the raw text rather than after a parse, because the parse is
            // itself part of the cost being bounded.
            int partCount = CountOccurrences(rawData, "\npart = ") + CountOccurrences(rawData, "\nPART\n");
            if (partCount > VesselTransfer.MaxPartsPerVessel)
            {
                Debug.LogWarning($"[GeneKerman] CraftInstaller: refusing a craft declaring " +
                                 $"{partCount} parts (cap {VesselTransfer.MaxPartsPerVessel}).");
                refusal = $"it declares {partCount} parts, past the " +
                          $"{VesselTransfer.MaxPartsPerVessel} this can install";
                return null;
            }

            // Pull off the carried thumbnail FIRST of all: the GKTHUMB block is appended
            // after every other side-channel block (GKFLAG / GKTSVER / GKTU / GKRF /
            // GKMODS), so it must be stripped before any of them. The PNG is written to KSP's thumbs/ folder
            // after the craft lands on disk (we need its final name/facility for the path).
            byte[] thumbPng;
            rawData = CraftThumb.CheckAndStripFromCraft(rawData, out thumbPng);

            // Then the carried mod list: the GKMODS block sits after the GKFLAG, GKTSVER,
            // GKTU and GKRF blocks, so it must be stripped before any of those strips
            // runs. The parsed list is used after the craft is written.
            System.Collections.Generic.List<CkanGenerator.ModEntry> requiredMods;
            rawData = CkanGenerator.CheckAndStripFromCraft(rawData, out requiredMods);

            // Then the fuel/engine configuration manifest: the GKRF block sits between
            // GKTU and GKMODS, so it strips after GKMODS and before GKTU. Acting on it
            // waits until the craft body has settled, like the paint job's.
            RealFuelsTransfer.RfManifest rfManifest;
            rawData = RealFuelsTransfer.StripFromCraft(rawData, out rfManifest);

            // Then the paint job's manifest: the GKTU block sits between GKMODS and
            // GKTSVER, so it strips after the one and before the other (the TweakScale
            // strip cuts everything from its block to end of file). Only the block comes
            // off here — acting on it has to wait until the craft body has settled.
            TextureTransfer.TuManifest tuManifest;
            rawData = TextureTransfer.StripFromCraft(rawData, out tuManifest);

            // Warn the player if this craft uses TweakScale and theirs is missing or a
            // different version, then strip the GKTSVER block. Must run BEFORE the flag
            // strip: the version block is appended after the GKFLAG blocks, and the flag
            // strip takes the *trailing run* of them — so anything left sitting after
            // them stops the run being the tail and no flag installs at all.
            rawData = TweakScaleGuard.CheckAndStripFromCraft(rawData);

            // Install any custom mission flags this craft carried and strip the
            // GKFLAG side-channel nodes so the blueprint written to disk is clean.
            rawData = FlagTransfer.StripAndInstallFlagsFromCraft(rawData);

            // Every side-channel block is off by now, so what's left is the craft body:
            // swap any part this install doesn't have for the equivalent it does have
            // (e.g. Making History's InflatableAirlock ↔ ReStock+'s restock-airlock-1),
            // and report whatever is still missing. Must run before the craft is written.
            rawData = PartAliases.ApplyToCraft(rawData, craftFileName);

            // Last, now that every part is the one this install will actually load: keep
            // the recolour modules this install can accept (the paint job arrives intact)
            // and drop the ones it can't. A substituted part is a different prefab with
            // different modules, which is why this waits for PartAliases rather than
            // running with the other strips. Without Textures Unlimited the craft simply
            // comes out in stock colours — nothing about it fails to load.
            rawData = TextureTransfer.ReconcileCraftBody(rawData, tuManifest, craftFileName);

            // And the other recolour system: Reforged Materials Redux keeps its paint in
            // its own ModuleReforged fields, which TU's scan can't see. Same placement and
            // the same outcome — the paint arrives intact on an install that has Reforged,
            // and on one that doesn't the modules come off so the craft loads in stock
            // colours instead of carrying a module row nothing can consume. No manifest
            // to pass: Reforged is one mod in one folder, so nothing had to be carried.
            rawData = ReforgedTransfer.ReconcileCraftBody(rawData, craftFileName);

            // And the fuel/engine configuration: on a RealFuels install, check the
            // craft's tank types / engine configs / RO environment against what is
            // defined here; without RealFuels, drop the RF modules and any propellant
            // this install doesn't define so the parts fill from their local prefabs.
            // Same placement rationale as the paint job — after PartAliases has settled
            // what each part actually is.
            rawData = RealFuelsTransfer.ReconcileCraftBody(rawData, rfManifest, craftFileName);

            // Parse craft type from header
            string craftType = ParseCraftType(rawData);
            if (string.IsNullOrEmpty(craftType))
            {
                Debug.LogWarning("[GeneKerman] Could not determine craft type, defaulting to VAB.");
                craftType = "VAB";
            }

            // Determine save directory
            string saveDir = GetSaveShipsDir(craftType);
            if (saveDir == null)
            {
                // Fallback to root Ships directory
                saveDir = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", craftType);
            }

            Directory.CreateDirectory(saveDir);

            // Sanitize filename. craftFileName is server-supplied (it originates from
            // whatever name another player uploaded), so it must be reduced to a bare
            // basename before it touches Path.Combine: a "../" segment or a rooted path
            // would otherwise escape saveDir and let a shared craft write arbitrary bytes
            // outside the Ships folder.
            string safeName = SanitizeCraftFileName(craftFileName);
            if (!safeName.EndsWith(".craft"))
                safeName += ".craft";

            // Avoid overwriting — append number if exists
            string finalPath = Path.Combine(saveDir, safeName);
            int counter = 1;
            while (File.Exists(finalPath))
            {
                string nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
                finalPath = Path.Combine(saveDir, $"{nameWithoutExt}_{counter}.craft");
                counter++;
            }

            // The loop above renamed the FILE, but KSP's craft browser lists a craft by
            // the `ship` field INSIDE it, and the editor saves under that name too. So two
            // crafts landing as Station.craft / Station_1.craft would both read "Station"
            // in the browser, and opening the second and hitting Save would write straight
            // over the first — the dedup would have bought nothing. Point the ship field at
            // the name the file actually got, but only when the loop above moved us: a
            // craft that installed under its own name keeps whatever its author called it.
            string installedName = Path.GetFileNameWithoutExtension(finalPath);
            if (!string.Equals(installedName, Path.GetFileNameWithoutExtension(safeName),
                               System.StringComparison.Ordinal))
            {
                rawData = RenameShip(rawData, installedName);
                loadmetaContent = RenameLoadmetaShip(loadmetaContent, installedName);
            }

            // Write craft file
            File.WriteAllBytes(finalPath, rawData);
            Debug.Log($"[GeneKerman] Craft installed: {finalPath} ({rawData.Length} bytes)");

            // Drop a .gkmods sidecar and, if the player is missing any of the craft's
            // mods, write a CKAN modpack they can use to install them.
            CkanGenerator.OnCraftInstalled(finalPath, requiredMods);

            // Write the carried NW-view thumbnail so the craft browser shows it on first
            // browse instead of KSP's missing-thumbnail placeholder. No-op if none rode along.
            CraftThumb.InstallThumbnail(finalPath, thumbPng);

            // Write loadmeta if provided
            if (!string.IsNullOrEmpty(loadmetaContent))
            {
                string loadmetaPath = finalPath.Replace(".craft", ".loadmeta");
                File.WriteAllText(loadmetaPath, loadmetaContent, Encoding.UTF8);
                Debug.Log($"[GeneKerman] Loadmeta installed: {loadmetaPath}");
            }

            return finalPath;
        }

        /// <summary>
        /// Rewrite a craft's top-level `ship` field to <paramref name="newName"/>.
        /// Only the header is considered — the match has to sit before the first PART
        /// node, so a description line or a part field that happens to begin "ship ="
        /// can't be mistaken for the real one. Returns the bytes unchanged if there is
        /// no such field or anything throws: a craft that installs under a duplicate
        /// display name is a nuisance, one that fails to install is not.
        /// </summary>
        private static byte[] RenameShip(byte[] data, string newName)
        {
            try
            {
                string text = Encoding.UTF8.GetString(data);
                int bodyStart = text.IndexOf("\nPART", System.StringComparison.Ordinal);
                if (bodyStart < 0) bodyStart = text.Length;

                // [^\r\n]* rather than .* so a CRLF craft — which is what any craft
                // authored on Windows is — keeps its \r: '.' matches it, and swallowing
                // it would leave this one line ending differently from the rest. No '$'
                // either, and that one is load-bearing: .NET's multiline '$' matches only
                // before a '\n', so "[^\r\n]*$" cannot match a CRLF line at all. The
                // class is greedy and can't cross a line break, so the anchor added
                // nothing but a silent no-op on every Windows craft.
                Match m = Regex.Match(text.Substring(0, bodyStart),
                                      @"(?m)^([ \t]*ship[ \t]*=[ \t]*)([^\r\n]*)");
                if (!m.Success)
                {
                    Debug.LogWarning("[GeneKerman] CraftInstaller: no 'ship' field to rename; " +
                                     $"'{newName}' will show under its old name in the browser.");
                    return data;
                }

                string renamed = text.Substring(0, m.Index) + m.Groups[1].Value + newName +
                                 text.Substring(m.Index + m.Length);
                Debug.Log($"[GeneKerman] CraftInstaller: renamed ship '{m.Groups[2].Value}' → " +
                          $"'{newName}' to match the deduplicated filename.");
                return Encoding.UTF8.GetBytes(renamed);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CraftInstaller: ship rename failed: {ex.Message}");
                return data;
            }
        }

        /// <summary>
        /// Point a loadmeta's `shipName` at the craft's installed name. The file is a
        /// flat key = value list (see VesselDataCollector.ParseLoadmeta) and its
        /// shipName is what GetCraftInfo reports, so leaving it on the old name would
        /// undo half of the rename. No-op when the key isn't present.
        /// </summary>
        private static string RenameLoadmetaShip(string loadmeta, string newName)
        {
            if (string.IsNullOrEmpty(loadmeta)) return loadmeta;
            try
            {
                // MatchEvaluator, not a "${1}" + newName replacement string: a '$' in the
                // name would otherwise be read as a group reference.
                return Regex.Replace(loadmeta, @"(?m)^([ \t]*shipName[ \t]*=[ \t]*)[^\r\n]*",
                                     m => m.Groups[1].Value + newName);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CraftInstaller: loadmeta rename failed: {ex.Message}");
                return loadmeta;
            }
        }

        /// <summary>
        /// Reduce a server-supplied craft filename to a safe basename. Strips any
        /// directory components (handling both '/' and '\', since KSP's Mono runtime
        /// treats only one as a separator per platform), drops leading dots so ".."
        /// can't survive, replaces anything outside a conservative charset, and caps
        /// the length. Falls back to a default when nothing usable remains.
        /// </summary>
        private static string SanitizeCraftFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "received_craft.craft";
            name = name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name.Substring(slash + 1);           // basename only
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append((char.IsLetterOrDigit(c) || c == '.' || c == '_' ||
                           c == '-' || c == ' ') ? c : '_');
            name = sb.ToString().TrimStart('.').Trim();      // no leading dots / stray edges
            if (name.Length > 128)
                name = name.Substring(0, 128);
            return string.IsNullOrEmpty(name) ? "received_craft.craft" : name;
        }

        /// <summary>
        /// Parse the craft type (VAB/SPH) from the craft file header.
        /// Looks for "type = VAB" or "type = SPH" in the first few lines.
        /// </summary>
        private static string ParseCraftType(byte[] data)
        {
            try
            {
                string header = Encoding.UTF8.GetString(data, 0, System.Math.Min(data.Length, 1024));
                using (var reader = new StringReader(header))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.StartsWith("type"))
                        {
                            int eq = line.IndexOf('=');
                            if (eq >= 0)
                            {
                                string val = line.Substring(eq + 1).Trim();
                                if (val == "VAB" || val == "SPH")
                                    return val;
                            }
                        }
                        // Stop after reading a few lines
                        if (line.StartsWith("PART") || line.StartsWith("{"))
                            break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Failed to parse craft type: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Get the Ships directory for the current save game.
        /// Returns null if no save is loaded.
        /// </summary>
        private static string GetSaveShipsDir(string craftType)
        {
            if (HighLogic.SaveFolder == null)
                return null;

            string saveRoot = Path.Combine(
                KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder,
                "Ships", craftType);

            return saveRoot;
        }
    }
}
