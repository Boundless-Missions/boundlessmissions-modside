/*
 * UI/Gui/Fmt.cs – Display formatting shared by the sidebar's panels.
 *
 * Small enough to be tempting to inline, which is exactly why it is here: the
 * first two of these had already been written twice in two panels by the time
 * this file existed.
 */

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
    }
}
