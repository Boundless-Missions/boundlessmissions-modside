/*
 * CraftThumb.cs – Carry a craft thumbnail with a shared .craft so the recipient's
 * KSP craft browser shows the craft on import instead of the green missing-thumbnail
 * placeholder.
 *
 * A freshly-installed .craft has no entry in KSP's thumbs/ folder, so the stock VAB/SPH
 * load dialog shows a generic placeholder until the craft is opened and saved. The
 * recipient can't render a thumbnail from the file alone (no live part geometry), but
 * the SENDER has it at export time — so we render the NW perspective view there
 * (VesselRenderer.CaptureNWThumbnail) and ride it along the SAME side-channel as
 * GKFLAG / GKTSVER / GKMODS: a GKTHUMB text block holding the PNG as base64.
 *
 * Ordering: GKTHUMB is appended LAST (after GKMODS) and therefore stripped FIRST on
 * import, so each block's strip stays a clean line-anchored cut to EOF — identical to
 * how GKMODS sits after GKFLAG/GKTSVER. On import CraftInstaller strips the block,
 * decodes the PNG, and writes thumbs/<save>_<VAB|SPH>_<craftname>.png.
 */

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class CraftThumb
    {
        private const string MARKER = "GKTHUMB";

        // ── Export ───────────────────────────────────────────────────────────

        /// <summary>Render an NW thumbnail from the current editor craft / active vessel
        /// and append it to the craft bytes. Best-effort: returns the input unchanged if
        /// nothing renderable is loaded.</summary>
        public static byte[] EmbedThumbForCurrentCraft(byte[] craftBytes)
        {
            return EmbedThumbInCraft(craftBytes, VesselRenderer.CaptureNWThumbnail());
        }

        /// <summary>As above, but render a specific vessel (fleet-extra blueprints in a
        /// multi-vessel submission). <paramref name="craftPath"/> is the blueprint this
        /// thumbnail rides in, used for the fallback below — pass it when you have it.
        ///
        /// A vessel only has live geometry while it is loaded: outside physics range KSP
        /// keeps it as a ProtoVessel and <c>vessel.parts</c> is empty, so the render
        /// returns null and the craft used to ship with no thumbnail at all — the
        /// recipient got KSP's placeholder with nothing in the log to say why. So fall
        /// back to the thumbnail KSP itself drew for this craft: it is the picture the
        /// sender's own craft browser is showing, and it costs no render.</summary>
        public static byte[] EmbedThumbForVessel(byte[] craftBytes, Vessel vessel, string craftPath = null)
        {
            byte[] png = VesselRenderer.CaptureNWThumbnail(vessel);
            if (png == null || png.Length == 0)
            {
                png = ReadLocalThumbnail(craftPath);
                if (png != null)
                    Debug.Log($"[GeneKerman] CraftThumb: nothing to render for '{vessel?.vesselName}' "
                              + "(unloaded?) — carrying KSP's own thumbnail instead.");
                else
                    Debug.LogWarning($"[GeneKerman] CraftThumb: no thumbnail for '{vessel?.vesselName}' — "
                                     + "nothing rendered and none on disk; the recipient will see KSP's placeholder.");
            }
            return EmbedThumbInCraft(craftBytes, png);
        }

        /// <summary>The thumbnail KSP already keeps for a craft on THIS machine, or null if
        /// it has never drawn one (KSP writes them in ShipConstruction.SaveShip and on open
        /// via LoadShip — never merely from listing a craft in the browser, which is why a
        /// craft that arrived from elsewhere and was never opened has none).</summary>
        public static byte[] ReadLocalThumbnail(string craftPath)
        {
            if (string.IsNullOrEmpty(craftPath)) return null;
            try
            {
                string craftName = Path.GetFileNameWithoutExtension(craftPath);
                string facility = Path.GetFileName(Path.GetDirectoryName(craftPath)); // VAB | SPH
                string thumbsDir = Path.Combine(KSPUtil.ApplicationRootPath, "thumbs");

                // KSP keys the file on the save that was loaded when it drew the picture, so
                // a blueprint in the shared <root>/Ships folder sits under whichever save
                // that was — "default" being the usual one. Try this save, then that.
                foreach (string save in new[] { HighLogic.SaveFolder, "default" })
                {
                    if (string.IsNullOrEmpty(save)) continue;
                    string path = Path.Combine(thumbsDir, $"{save}_{facility}_{craftName}.png");
                    if (File.Exists(path)) return File.ReadAllBytes(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CraftThumb.ReadLocalThumbnail failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Append a GKTHUMB block carrying <paramref name="pngBytes"/> (base64) to
        /// raw .craft bytes. MUST be the LAST block appended (after GKFLAG / GKTSVER /
        /// GKMODS) so the strip on the other side is a clean cut to EOF. Returns the input
        /// unchanged when there's no thumbnail or on error.</summary>
        public static byte[] EmbedThumbInCraft(byte[] craftBytes, byte[] pngBytes)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            if (pngBytes == null || pngBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                var sb = new StringBuilder(text);
                if (!text.EndsWith("\n")) sb.Append("\n");
                // Convert.ToBase64String emits a single line (no breaks), so the data
                // value can't contain a line-anchored marker — keeps the strip robust.
                sb.Append(MARKER).Append("\n{\n\tdata = ")
                  .Append(Convert.ToBase64String(pngBytes))
                  .Append("\n}\n");
                Debug.Log($"[GeneKerman] CraftThumb: embedded thumbnail ({pngBytes.Length / 1024}KB).");
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CraftThumb.EmbedThumbInCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Import ───────────────────────────────────────────────────────────

        /// <summary>Strip the appended GKTHUMB block from raw .craft bytes (run FIRST,
        /// before the GKMODS/TweakScale/flag strips, since GKTHUMB is appended last).
        /// Returns the cleaned bytes and hands back the decoded PNG via
        /// <paramref name="pngBytes"/> (null when absent).</summary>
        public static byte[] CheckAndStripFromCraft(byte[] rawCraftBytes, out byte[] pngBytes)
        {
            pngBytes = null;
            if (rawCraftBytes == null || rawCraftBytes.Length == 0) return rawCraftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(rawCraftBytes);
                int idx = FindBlockStart(text);
                if (idx < 0) return rawCraftBytes;

                pngBytes = ExtractPng(text.Substring(idx));

                string body = text.Substring(0, idx).TrimEnd('\r', '\n', ' ', '\t');
                if (body.Length > 0) body += "\n";
                return Encoding.UTF8.GetBytes(body);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CraftThumb.CheckAndStripFromCraft failed: {ex.Message}");
                return rawCraftBytes;
            }
        }

        /// <summary>Write a decoded thumbnail PNG to KSP's thumbs/ folder for an installed
        /// craft, so the stock craft browser shows it. The filename KSP looks up is
        /// "<save>_<EditorFacility>_<craftname>.png"; both the facility (VAB/SPH) and name
        /// are taken from the installed craft's path.</summary>
        public static void InstallThumbnail(string installedCraftPath, byte[] pngBytes)
        {
            if (string.IsNullOrEmpty(installedCraftPath) || pngBytes == null || pngBytes.Length == 0)
                return;
            try
            {
                string save = HighLogic.SaveFolder;
                if (string.IsNullOrEmpty(save)) return; // no save → nowhere KSP would look

                string craftName = Path.GetFileNameWithoutExtension(installedCraftPath);
                string facility = Path.GetFileName(Path.GetDirectoryName(installedCraftPath)); // VAB | SPH

                string thumbsDir = Path.Combine(KSPUtil.ApplicationRootPath, "thumbs");
                Directory.CreateDirectory(thumbsDir);

                string path = Path.Combine(thumbsDir, $"{save}_{facility}_{craftName}.png");
                File.WriteAllBytes(path, pngBytes);
                Debug.Log($"[GeneKerman] CraftThumb: wrote craft thumbnail → {path} ({pngBytes.Length / 1024}KB)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] CraftThumb.InstallThumbnail failed: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Index of the line-anchored GKTHUMB block (so a stray substring inside a
        /// base64 value can't match), or -1 if absent. The block is always appended last.</summary>
        private static int FindBlockStart(string text)
        {
            int idx = text.LastIndexOf(MARKER, StringComparison.Ordinal);
            if (idx < 0) return -1;
            if (idx > 0 && text[idx - 1] != '\n' && text[idx - 1] != '\r') return -1;
            return idx;
        }

        private static byte[] ExtractPng(string block)
        {
            foreach (var raw in block.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim() != "data") continue;
                string b64 = line.Substring(eq + 1).Trim();
                if (b64.Length == 0) return null;
                return Convert.FromBase64String(b64);
            }
            return null;
        }
    }
}
