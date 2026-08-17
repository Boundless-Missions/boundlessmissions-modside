/*
 * DeviceId.cs – Per-install device identity + report diagnostics.
 *
 * The device id is a random GUID written ONCE to PluginData/device.id and never
 * refreshed, so it's stable for the lifetime of the install (immune to MAC
 * rotation) and carries no personal data. It's sent on every request as the
 * X-Device-Id header; the server binds it to the account at link time and blocks
 * any other id until the user approves it from Discord.
 *
 * The real MAC address is read only when the user files a moderation report
 * (GetMacAddress), never for the binding itself. KSP.log is read for that same
 * report (GetKspLog) and for a bug report the player writes themselves
 * (GetKspLogCapped) — both user-initiated, neither collected in the background.
 */

using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
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

        // KSP.log lives in the game root on every platform; Player.log is a Unity
        // fallback. Both readers below try each and use the first that opens.
        private static string[] LogCandidates()
        {
            return new[]
            {
                Path.Combine(KSPUtil.ApplicationRootPath, "KSP.log"),
                Path.Combine(KSPUtil.ApplicationRootPath, "Player.log"),
            };
        }

        /// <summary>Read KSP.log bytes for a moderation report. Returns null if absent.
        /// KSP holds the log open for writing, so we copy via a shared read stream
        /// (a plain File.ReadAllBytes can fail with a sharing violation).</summary>
        public static byte[] GetKspLog()
        {
            foreach (string path in LogCandidates())
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

        /// <summary>
        /// The head and tail of KSP.log, for an upload that has to fit a size budget
        /// — a bug report's attachment. A heavily modded install writes hundreds of
        /// megabytes in a session, and neither the API nor Discord will take that, so
        /// the choice is between trimming here and having the report rejected whole.
        ///
        /// The two ends are the two things worth keeping: the head carries the loaded
        /// assemblies, the mod list and the system specs; the tail carries whatever
        /// just went wrong. The middle is dropped with a marker saying so. The server
        /// trims to the same head/tail as a backstop, so the ticket reads identically
        /// whichever end did the cutting.
        ///
        /// Seeks rather than reading the whole file: the point is not to hold a 300 MB
        /// log in memory on the player's machine either.
        /// </summary>
        public static byte[] GetKspLogCapped(int headBytes, int tailBytes)
        {
            foreach (string path in LogCandidates())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long len = fs.Length;
                        if (len <= (long)headBytes + tailBytes)
                        {
                            using (var whole = new MemoryStream())
                            {
                                fs.CopyTo(whole);
                                Debug.Log($"[GeneKerman] Read {whole.Length} bytes from {path} for a bug report.");
                                return whole.ToArray();
                            }
                        }

                        long dropped = len - headBytes - tailBytes;
                        byte[] marker = Encoding.UTF8.GetBytes(
                            "\n\n... [GeneKerman: " + dropped.ToString("N0") +
                            " bytes of log omitted between the first " + (headBytes / 1000000) +
                            " MB and last " + (tailBytes / 1000000) + " MB] ...\n\n");

                        using (var ms = new MemoryStream(headBytes + marker.Length + tailBytes))
                        {
                            CopyExactly(fs, ms, headBytes);
                            ms.Write(marker, 0, marker.Length);
                            fs.Seek(-tailBytes, SeekOrigin.End);
                            CopyExactly(fs, ms, tailBytes);
                            Debug.Log($"[GeneKerman] Read {ms.Length} of {len} bytes from {path} for a bug report.");
                            return ms.ToArray();
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GeneKerman] Could not read {path}: {e.Message}");
                }
            }
            Debug.LogWarning("[GeneKerman] No KSP.log/Player.log found for the bug report.");
            return null;
        }

        private static void CopyExactly(Stream from, Stream to, int count)
        {
            var buffer = new byte[64 * 1024];
            while (count > 0)
            {
                int read = from.Read(buffer, 0, Math.Min(buffer.Length, count));
                if (read <= 0) return;   // file shrank under us; take what we got
                to.Write(buffer, 0, read);
                count -= read;
            }
        }
    }
}
