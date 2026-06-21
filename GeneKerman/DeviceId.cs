/*
 * DeviceId.cs – Per-install device identity + report diagnostics.
 *
 * The device id is a random GUID written ONCE to PluginData/device.id and never
 * refreshed, so it's stable for the lifetime of the install (immune to MAC
 * rotation) and carries no personal data. It's sent on every request as the
 * X-Device-Id header; the server binds it to the account at link time and blocks
 * any other id until the user approves it from Discord.
 *
 * The real MAC address and KSP.log are read only when the user files a moderation
 * report (GetMacAddress / GetKspLog), never for the binding itself.
 */

using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using UnityEngine;

namespace GeneKerman
{
    public static class DeviceId
    {
        private static string _current;

        /// <summary>The stable per-install device id, generated once and cached.</summary>
        public static string Current
        {
            get
            {
                if (!string.IsNullOrEmpty(_current))
                    return _current;
                try
                {
                    string path = Path.Combine(GeneKermanMod.PluginDataPath, "device.id");
                    if (File.Exists(path))
                    {
                        _current = File.ReadAllText(path).Trim();
                    }
                    if (string.IsNullOrEmpty(_current))
                    {
                        _current = Guid.NewGuid().ToString("N");
                        Directory.CreateDirectory(GeneKermanMod.PluginDataPath);
                        File.WriteAllText(path, _current);   // write once, never refresh
                        Debug.Log("[GeneKerman] Generated new device id.");
                    }
                }
                catch (Exception e)
                {
                    // If the file can't be read/written, fall back to a volatile id so
                    // the client still works (it'll just look like a new device each run).
                    Debug.LogWarning("[GeneKerman] device.id unavailable: " + e.Message);
                    _current = _current ?? Guid.NewGuid().ToString("N");
                }
                return _current;
            }
        }

        /// <summary>Best-effort physical MAC of the primary active interface, for a
        /// moderation report only. Returns "" if none can be determined.</summary>
        public static string GetMacAddress()
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                    .FirstOrDefault();
                if (nic == null) return "";
                byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
                return mac.Length == 0 ? "" : string.Join(":", mac.Select(b => b.ToString("X2")).ToArray());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not read MAC: " + e.Message);
                return "";
            }
        }

        /// <summary>Read KSP.log bytes for a moderation report. Returns null if absent.
        /// KSP holds the log open for writing, so we copy via a shared read stream
        /// (a plain File.ReadAllBytes can fail with a sharing violation).</summary>
        public static byte[] GetKspLog()
        {
            // KSP.log lives in the game root on every platform; Player.log is a
            // Unity fallback. Try each and use the first that reads.
            string[] candidates =
            {
                Path.Combine(KSPUtil.ApplicationRootPath, "KSP.log"),
                Path.Combine(KSPUtil.ApplicationRootPath, "Player.log"),
            };
            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        Debug.Log($"[GeneKerman] Read {ms.Length} bytes from {path} for device report.");
                        return ms.ToArray();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GeneKerman] Could not read {path}: {e.Message}");
                }
            }
            Debug.LogWarning("[GeneKerman] No KSP.log/Player.log found for device report.");
            return null;
        }
    }
}
