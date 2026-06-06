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
