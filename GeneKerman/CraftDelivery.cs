/*
 * CraftDelivery.cs – Fetching and installing the craft a completed contract earned you.
 *
 * This logic used to live inside the classic window's DoDownloadCraft, where it reported progress
 * by writing to the IMGUI status line and started its sub-coroutines with fire-and-forget
 * RunCoroutine calls. That shape works for a status line, but it has no completion point,
 * so nothing else could ever wait on it or know whether it worked.
 *
 * Extracted here as a single coroutine that yields its sub-steps and calls onDone exactly
 * once. ClientState wraps it for the sidebar; the web bridge wraps it as a
 * tracked job. One implementation, two front ends — a second copy would drift.
 *
 * Two delivery shapes, in priority order:
 *   vessel_node → a full vessel state (possibly a fleet), spawned into the save
 *   craft_files → a blueprint, written into the save's Ships folder
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class CraftDelivery
    {
        /// <summary>
        /// Downloads and installs whatever a contract delivered. Calls
        /// <paramref name="onDone"/> exactly once, with a player-facing message.
        /// </summary>
        public static IEnumerator Deliver(string contractId, string ownerName, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            if (mod?.Api == null) { onDone(false, "Mod not ready."); yield break; }

            string resp = null;
            bool ok = false;
            yield return mod.Api.Get($"/api/v1/craft/download/{contractId}", (o, r, _) => { ok = o; resp = r; });

            if (!ok || string.IsNullOrEmpty(resp))
            {
                onDone(false, "Could not fetch craft data.");
                yield break;
            }

            var data = MiniJSON.DeserializeDict(resp);
            var craftFiles = MiniJSON.GetList(data, "craft_files");
            string vesselNodeUrl = MiniJSON.GetString(data, "vessel_node_url", null);
            string loadmeta = MiniJSON.GetString(data, "loadmeta", null);

            // A full vessel state beats a blueprint: it carries the actual flown craft,
            // not just its design.
            if (!string.IsNullOrEmpty(vesselNodeUrl))
            {
                yield return ImportVessel(contractId, vesselNodeUrl, ownerName, onDone);
                yield break;
            }

            if (craftFiles == null || craftFiles.Count == 0)
            {
                onDone(false, "No craft file or vessel data in this contract.");
                yield break;
            }

            var first = craftFiles[0] as Dictionary<string, object>;
            if (first == null) { onDone(false, "Invalid craft file data."); yield break; }

            string url = MiniJSON.GetString(first, "url", "");
            string filename = MiniJSON.GetString(first, "filename", "craft.craft");
            if (string.IsNullOrEmpty(url)) { onDone(false, "No download URL."); yield break; }

            byte[] fileData = null;
            bool dlOk = false;
            yield return mod.Api.DownloadFile(url, (o, bytes) => { dlOk = o; fileData = bytes; });

            if (!dlOk || fileData == null) { onDone(false, "Download failed."); yield break; }

            string path = CraftInstaller.Install(fileData, filename, loadmeta);
            if (path == null) { onDone(false, "Failed to install craft file."); yield break; }

            string msg = $"Craft installed: {Path.GetFileName(path)}";
            if (!string.IsNullOrEmpty(loadmeta)) msg += " (+ loadmeta)";
            onDone(true, msg);
        }

        private static IEnumerator ImportVessel(string contractId, string vesselNodeUrl,
                                                string ownerName, Action<bool, string> onDone)
        {
            var mod = GeneKermanMod.Instance;
            string myName = mod.LinkedUsername;

            byte[] fileData = null;
            bool ok = false;
            yield return mod.Api.DownloadFile(vesselNodeUrl, (o, bytes) => { ok = o; fileData = bytes; });

            if (!ok || fileData == null)
            {
                Debug.LogWarning("[GeneKerman] CraftDelivery: vessel node download failed.");
                onDone(false, "Failed to download vessel data.");
                yield break;
            }

            string vesselNodeStr = DecompressToString(fileData);

            // A submission may carry several crafts (GKFLEET) or one (legacy VESSEL);
            // ImportFleet spawns each and installs any embedded blueprints.
            int imported = VesselTransfer.ImportFleet(vesselNodeStr, ownerName, myName);

            if (imported <= 0) { onDone(false, "Failed to import vessel."); yield break; }

            if (imported > 1)
                mod.ShowNotification("Crafts Received", $"{imported} crafts arrived in your save.");

            if (GKContractScenario.Instance != null)
                GKContractScenario.Instance.MarkVesselImported(contractId);

            onDone(true, imported == 1 ? "Vessel imported." : $"{imported} crafts imported.");
        }

        /// <summary>Gunzip (if needed) a downloaded vessel-node blob into its UTF-8 text.</summary>
        public static string DecompressToString(byte[] fileData)
        {
            byte[] rawData = fileData;
            if (fileData != null && fileData.Length >= 2 && fileData[0] == 0x1F && fileData[1] == 0x8B)
            {
                try
                {
                    using (var ms = new MemoryStream(fileData))
                    using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        gz.CopyTo(output);
                        rawData = output.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] Vessel node decompression failed: {ex.Message}");
                    rawData = fileData;
                }
            }
            return Encoding.UTF8.GetString(rawData);
        }
    }
}
