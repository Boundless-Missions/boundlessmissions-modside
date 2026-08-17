/*
 * TweakScaleGuard.cs – Carry the sender's TweakScale version with a craft and warn
 * the receiver on a mismatch.
 *
 * TweakScale rescales parts via a "TweakScale" PartModule; the scaled geometry and
 * attach-node offsets baked into a .craft only reconstruct correctly if the receiver
 * has the SAME TweakScale. A receiver missing TweakScale loads the part at its
 * unscaled size (parts drift apart / explode), and the different forks/versions
 * (Biotronic's original vs Lisias') define scale steps and defaults differently, so
 * even an installed-but-mismatched TweakScale can rebuild the craft mis-scaled.
 *
 * This rides the SAME channel FlagTransfer uses: on submit we APPEND a GKTSVER block
 * (raw text, appended LAST so it sits after any GKFLAG blocks) to crafts that use
 * TweakScale; on install we read it FIRST, compare against the locally installed
 * TweakScale version, warn on any mismatch, and strip the block so the craft loads
 * clean. Append-last / strip-first keeps it clear of FlagTransfer's GKFLAG blocks.
 *
 * RELATIONSHIP TO GeneKermanScale. ScaleBridge now BAKES a scaled craft — it copies the
 * final computed scale into a dormant GeneKermanScale module and deletes the TweakScale
 * node — so a baked craft needs no TweakScale at all and this guard must stay quiet
 * about it. That is why the trigger below is "is anything actually rescaled", not "does
 * this craft mention TweakScale". TweakScale attaches its module to every compatible
 * part in the editor whether or not you scale it, and ScaleBridge only strips the ones
 * it snapshots (parts within SCALE_EPSILON of 1x are left alone), so a fully baked craft
 * still carries TweakScale modules — the majority of them, on a typical craft. Matching
 * the bare module name warned every recipient of every baked craft about a version
 * mismatch that could not affect them, while the same submission deliberately omits
 * TweakScale from its dependency list (SubmitWindow.CollectUsedModFolders).
 *
 * So the guard is now aimed at what ScaleBridge does NOT cover:
 *   • the export paths that never run it — marketplace listings, quicksend, export to
 *     file, the blueprint that accompanies a vessel transfer — where crafts still carry
 *     live TweakScale data and this is the recipient's only warning;
 *   • a submission where SnapshotIntoCraftBytes bailed or threw, leaving a real scale
 *     behind. This is the backstop for that.
 */

using System;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class TweakScaleGuard
    {
        private const string VER_NODE = "GKTSVER";

        // TweakScale is written to the craft as a `name = TweakScale` PartModule.
        // ConfigNode serialises values with spaces around the '='.
        private const string TWEAKSCALE_MODULE = "TweakScale";

        // The scale TweakScale applied is currentScale / defaultScale; a part within this
        // of 1x is unscaled. Same value and same meaning as ScaleBridge.SCALE_EPSILON —
        // the two must agree, or this guard would warn about exactly the parts ScaleBridge
        // considered unscaled and therefore left behind.
        private const double SCALE_EPSILON = 0.001;

        /// <summary>Version of the locally installed TweakScale core, or null if it
        /// isn't installed. Resolves the CORE plugin only — its assembly is named "Scale"
        /// (Scale.dll), defining TweakScale.TweakScale. Several siblings also contain
        /// "TweakScale"/"Scale" in their name (KSPe.Light.TweakScale, TweakScale.WatchDog,
        /// TweakScaleSafetyNet, Scale_Redist, the TweakScaleCompanion_* suite) and carry
        /// their OWN versions, so we must not match on substring or we'd report a
        /// companion's version instead of the core's.</summary>
        public static string InstalledVersion()
        {
            try
            {
                // Pass 1: the canonical core assembly, by exact name.
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la?.assembly;
                    if (asm == null) continue;
                    if (string.Equals(asm.GetName().Name, "Scale", StringComparison.OrdinalIgnoreCase))
                        return asm.GetName().Version?.ToString() ?? "unknown";
                }

                // Pass 2: fall back to whichever assembly actually defines the
                // TweakScale module type, in case a fork renames Scale.dll.
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la?.assembly;
                    if (asm == null) continue;
                    try
                    {
                        if (asm.GetType("TweakScale.TweakScale") != null)
                            return asm.GetName().Version?.ToString() ?? "unknown";
                    }
                    catch { /* GetType can throw on a half-loaded assembly — ignore */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TweakScaleGuard: version lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Whether the craft carries a part TweakScale has actually rescaled —
        /// not merely a part TweakScale is attached to. Both scales are persistent fields,
        /// so the answer is in the craft text; a module whose currentScale equals its
        /// defaultScale changes nothing and is no reason to warn anyone.
        ///
        /// "Can't tell" counts as rescaled: a module missing either field, or carrying an
        /// unparseable one, is not PROOF of a no-op, and silence has to be earned. This is
        /// the same stance the marketplace compatibility check takes — unknown must never
        /// render as a green light.</summary>
        private static bool CraftTextUsesTweakScale(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            int depth = 0, moduleDepth = -1;
            double cur = double.NaN, def = double.NaN;

            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line == "{") { depth++; continue; }
                if (line == "}")
                {
                    depth--;
                    if (moduleDepth >= 0 && depth < moduleDepth)
                    {
                        if (IsRescaled(cur, def)) return true;
                        moduleDepth = -1;
                        cur = def = double.NaN;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (moduleDepth < 0)
                {
                    if (key == "name" && value == TWEAKSCALE_MODULE) moduleDepth = depth;
                    continue;
                }
                if (key == "currentScale") cur = ParseScale(value);
                else if (key == "defaultScale") def = ParseScale(value);
            }

            // A block left open by a truncated file still counts if it proved a rescale.
            return moduleDepth >= 0 && IsRescaled(cur, def);
        }

        /// <summary>Whether a TweakScale module's scale pair represents a real rescale.
        /// Unreadable or absent values return true — see CraftTextUsesTweakScale.</summary>
        private static bool IsRescaled(double currentScale, double defaultScale)
        {
            if (double.IsNaN(currentScale) || double.IsNaN(defaultScale)) return true;
            if (Math.Abs(defaultScale) < 1e-6) return true; // no ratio to take
            return Math.Abs(currentScale / defaultScale - 1.0) >= SCALE_EPSILON;
        }

        private static double ParseScale(string value)
        {
            double d;
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d)
                ? d : double.NaN;
        }

        // ── Submit: embed ────────────────────────────────────────────────────

        /// <summary>Append a GKTSVER block recording the sender's TweakScale version to
        /// a craft that uses TweakScale. No-op for unscaled crafts. Returns the
        /// (possibly modified) bytes; on any failure returns the input unchanged.</summary>
        public static byte[] EmbedVersionInCraft(byte[] craftBytes)
        {
            if (craftBytes == null || craftBytes.Length == 0) return craftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(craftBytes);
                if (!CraftTextUsesTweakScale(text)) return craftBytes; // nothing scaled

                string ver = InstalledVersion() ?? "unknown";

                var sb = new StringBuilder(text);
                if (!text.EndsWith("\n")) sb.Append("\n");
                sb.Append(VER_NODE).Append("\n{\n");
                sb.Append("\tver = ").Append(ver).Append("\n");
                sb.Append("}\n");

                Debug.Log($"[GeneKerman] TweakScaleGuard: embedded sender TweakScale version '{ver}'.");
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TweakScaleGuard.EmbedVersionInCraft failed: {ex.Message}");
                return craftBytes;
            }
        }

        // ── Install: warn + strip ────────────────────────────────────────────

        /// <summary>Inspect a downloaded craft for TweakScale use, warn the player if
        /// their TweakScale is missing or a different version than the sender's, and
        /// strip the GKTSVER block so the craft loads clean. Must run BEFORE
        /// FlagTransfer's strip (the version block is appended after the flag blocks).
        /// Returns cleaned bytes; on failure returns the input unchanged.</summary>
        public static byte[] CheckAndStripFromCraft(byte[] rawCraftBytes)
        {
            if (rawCraftBytes == null || rawCraftBytes.Length == 0) return rawCraftBytes;
            try
            {
                string text = Encoding.UTF8.GetString(rawCraftBytes);
                int idx = FindVersionBlockStart(text);

                string senderVer = idx >= 0 ? ExtractVersion(text.Substring(idx)) : null;
                string body = idx >= 0 ? text.Substring(0, idx) : text;

                Warn(CraftTextUsesTweakScale(body), senderVer, InstalledVersion());

                if (idx < 0) return rawCraftBytes; // no block to strip

                body = body.TrimEnd('\r', '\n', ' ', '\t');
                if (body.Length > 0) body += "\n";
                return Encoding.UTF8.GetBytes(body);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] TweakScaleGuard.CheckAndStripFromCraft failed: {ex.Message}");
                return rawCraftBytes;
            }
        }

        private static void Warn(bool craftScaled, string senderVer, string localVer)
        {
            if (!craftScaled) return;

            if (string.IsNullOrEmpty(localVer))
            {
                Post("⚠ This craft uses TweakScale, which you don't have installed, "
                     + "so it may load mis-scaled or broken. Install TweakScale to use it correctly.");
            }
            else if (!string.IsNullOrEmpty(senderVer) && senderVer != "unknown" &&
                     !string.Equals(senderVer, localVer, StringComparison.OrdinalIgnoreCase))
            {
                Post($"⚠ This craft was built with TweakScale {senderVer} but you have {localVer}. "
                     + "Different versions/forks can rescale parts differently, so check the craft for misplaced parts.");
            }
        }

        private static void Post(string msg)
        {
            Debug.LogWarning("[GeneKerman] " + msg);
            try
            {
                ScreenMessages.PostScreenMessage(msg, 10f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch { /* no screen available (e.g. headless) — the log line is enough */ }
        }

        /// <summary>Index of the start of the GKTSVER block (anchored to line start so a
        /// stray substring can't match), or -1 if absent.</summary>
        private static int FindVersionBlockStart(string text)
        {
            int idx = text.LastIndexOf(VER_NODE, StringComparison.Ordinal);
            if (idx < 0) return -1;
            if (idx > 0 && text[idx - 1] != '\n' && text[idx - 1] != '\r') return -1;
            return idx;
        }

        private static string ExtractVersion(string block)
        {
            foreach (var rawLine in block.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("ver"))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0) return line.Substring(eq + 1).Trim();
                }
            }
            return null;
        }
    }
}
