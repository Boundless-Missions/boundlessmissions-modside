/*
 * UI/Gui/Fmt.cs – Display formatting shared by the sidebar's panels.
 *
 * Small enough to be tempting to inline, which is exactly why it is here: the
 * first two of these had already been written twice in two panels by the time
 * this file existed.
 */

using System.Text;

namespace GeneKerman.UI.Gui
{
    internal static class Fmt
    {
        /// <summary>
        /// KSP's Vessel.Situations enum made readable: SUB_ORBITAL -> "sub orbital".
        /// The API passes these through verbatim; they are not player-facing text.
        /// Mirrors `situationLabel` in WebUI/src/lib/utils.ts.
        /// </summary>
        public static string Situation(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.ToLowerInvariant().Replace('_', ' ');

        /// <summary>
        /// A status identifier as display text: `mod_review` -> "mod review". Matches
        /// the web UI's `status.replace(/_/g, " ")`.
        /// </summary>
        public static string Status(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace('_', ' ');

        /// <summary>
        /// Server-authored text with emoji removed, for anything drawn with KSP's own
        /// font. Notification titles are written for Discord ("\u26A0 Submission Refused")
        /// and the same string is handed to the in-game client, where the borrowed font
        /// has no glyph for it and TMP/IMGUI draw a tofu box instead — so the emoji is
        /// not decoration that failed, it is a box in front of every title. Stripping it
        /// here rather than at the source keeps the emoji where it does render: Discord,
        /// the website, and the mod's own browser UI.
        ///
        /// Two rules, no table. A base character followed by U+FE0F is by definition
        /// being *presented* as emoji, so both go — that covers the variation-selector
        /// spellings (warning, stopwatch, arrows) without listing them. What is left is
        /// the ranges that are emoji whether or not they say so: the astral planes, and
        /// Misc Symbols + Dingbats + Misc Symbols and Arrows in the BMP. Everything a
        /// borrowed font does have — the dashes, the ellipsis, the bullet, a bare arrow —
        /// is deliberately outside those ranges and survives.
        /// </summary>
        public static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            StringBuilder sb = null;   // allocated only once something is dropped
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int width = 1;
                bool drop;

                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    width = 2;
                    drop = char.ConvertToUtf32(c, s[i + 1]) >= 0x1F000;
                }
                else if (i + 1 < s.Length && s[i + 1] == '\uFE0F')
                {
                    width = 2;      // base + variation selector, dropped as a pair
                    drop = true;
                }
                else
                {
                    drop = IsSymbol(c);
                }

                // A trailing selector or joiner left by an already-dropped base.
                if (!drop && (c == '\uFE0F' || c == '\uFE0E' || c == '\u200D' || c == '\u20E3'))
                    drop = true;

                if (drop)
                {
                    if (sb == null) sb = new StringBuilder(s, 0, i, s.Length);
                    i += width - 1;
                    continue;
                }

                if (sb != null) sb.Append(s, i, width);
                i += width - 1;
            }

            if (sb == null) return s;

            // The dropped glyph leaves its separating space behind; a title that was
            // "<emoji> Fine Paid" must not arrive as " Fine Paid".
            while (sb.Length > 0 && sb[0] == ' ') sb.Remove(0, 1);
            while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            for (int i = sb.Length - 1; i > 0; i--)
                if (sb[i] == ' ' && sb[i - 1] == ' ') sb.Remove(i, 1);

            return sb.ToString();
        }

        private static bool IsSymbol(char c) =>
            (c >= '\u2600' && c <= '\u27BF')      // Misc Symbols + Dingbats
            || (c >= '\u2B00' && c <= '\u2BFF')   // Misc Symbols and Arrows
            || c == '\u203C' || c == '\u2049';    // !! and !?
    }
}
