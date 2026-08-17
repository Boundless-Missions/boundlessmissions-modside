/*
 * CraftInstaller.cs – Saves received craft/loadmeta files to KSP directories.
 *
 * When a contract submission includes craft files, this class
 * places them in the correct Ships/ directory based on the craft type
 * header (VAB or SPH) in the current save.
 */

using System.IO;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class CraftInstaller
    {
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
            if (craftData == null || craftData.Length == 0)
            {
                Debug.LogWarning("[GeneKerman] CraftInstaller: No craft data provided.");
                return null;
            }

            // Decompress if gzipped (gzip magic bytes: 0x1F 0x8B)
            byte[] rawData = craftData;
            if (craftData.Length >= 2 && craftData[0] == 0x1F && craftData[1] == 0x8B)
            {
                try
                {
                    using (var ms = new MemoryStream(craftData))
                    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        gz.CopyTo(output);
                        rawData = output.ToArray();
                    }
                    Debug.Log($"[GeneKerman] Decompressed craft file: {craftData.Length} → {rawData.Length} bytes");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] Gzip decompression failed, using raw data: {ex.Message}");
                    rawData = craftData;
                }
            }

            // Pull off the carried thumbnail FIRST of all: the GKTHUMB block is appended
            // after every other side-channel block (GKFLAG / GKTSVER / GKTU / GKMODS), so it must
            // be stripped before any of them. The PNG is written to KSP's thumbs/ folder
            // after the craft lands on disk (we need its final name/facility for the path).
            byte[] thumbPng;
            rawData = CraftThumb.CheckAndStripFromCraft(rawData, out thumbPng);

            // Then the carried mod list: the GKMODS block sits after the GKFLAG and GKTSVER
            // blocks, so it must be stripped before either of those strips runs. The parsed
            // list is used after the craft is written.
            System.Collections.Generic.List<CkanGenerator.ModEntry> requiredMods;
            rawData = CkanGenerator.CheckAndStripFromCraft(rawData, out requiredMods);

            // Then the paint job's manifest: the GKTU block sits between GKMODS and
            // GKTSVER, so it strips after the one and before the other (the TweakScale
            // strip cuts everything from its block to end of file). Only the block comes
            // off here — acting on it has to wait until the craft body has settled.
            TextureTransfer.TuManifest tuManifest;
            rawData = TextureTransfer.StripFromCraft(rawData, out tuManifest);

            // Warn the player if this craft uses TweakScale and theirs is missing or a
            // different version, then strip the GKTSVER block. Must run BEFORE the flag
            // strip: the version block is appended after the GKFLAG blocks, and the flag
            // strip cuts everything from the first GKFLAG block to end of file.
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

            // Sanitize filename
            string safeName = craftFileName;
            if (string.IsNullOrEmpty(safeName))
                safeName = "received_craft.craft";
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
