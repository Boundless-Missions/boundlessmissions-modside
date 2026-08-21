/*
 * TextSanitizer.cs – Makes server-authored notification text renderable in game.
 *
 * Notification titles/messages are written for Discord, where "🛟 New Rescue Mission"
 * and "<:KCoin:1510200111253291258>" both draw. Neither survives KSP: the bundled
 * fonts (IMGUI and the borrowed TMP asset alike) carry no emoji glyphs, so every
 * emoji renders as a tofu box, and Discord's custom-emoji markup arrives as literal
 * angle-bracket noise. Sanitized once at the two ingestion funnels — the socket/poll
 * toast path and the feed fetch — so every renderer downstream (toasts, sidebar feed,
 * IMGUI feed, KSP.log) sees clean text and no call site can forget.
 *
 * Deliberately narrow: Discord markup becomes its readable name ("KCoin"), emoji are
 * removed, and everything else — Turkish text, °, Δv, plain arrows — passes through
 * untouched. An emoji this misses still shows as one box, which is cosmetic; a rule
 * that over-strips would eat meaning, which isn't.
 */

using System.Text;
using System.Text.RegularExpressions;

namespace GeneKerman
{
    public static class TextSanitizer
    {
        // <:KCoin:1510…> and animated <a:name:id> → the name alone.
        private static readonly Regex DiscordEmoji =
            new Regex(@"<a?:(\w+):\d+>", RegexOptions.Compiled);

        /// <summary>Strip what KSP's fonts cannot draw from one notification string.</summary>
        public static string CleanNotif(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            s = DiscordEmoji.Replace(s, "$1");

            var sb = new StringBuilder(s.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                // Astral plane (every 🛟🚀📜-style emoji) arrives as a surrogate pair.
                if (char.IsHighSurrogate(c))
                {
                    i++;  // consume the low half too
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;  // orphaned half — drop

                if (IsUnrenderable(c)) continue;

                // Removing an emoji between spaces leaves "a  b" — collapse as we go.
                if (c == ' ' && lastWasSpace) continue;
                lastWasSpace = c == ' ';
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>BMP characters the game fonts have no glyph for: the symbol blocks
        /// Discord messages actually use (⏰ ✅ ❌ ⚠ ↩ live here), plus the invisible
        /// joiners/selectors that ride along with emoji. Plain arrows (→ U+2192) and
        /// maths (Δ, ≈, °) are deliberately outside every range.</summary>
        private static bool IsUnrenderable(char c)
        {
            int cp = c;
            if (cp == 0xFE0F || cp == 0x200D) return true;        // VS-16, ZWJ
            if (cp == 0x21A9 || cp == 0x21AA) return true;        // ↩ ↪
            if (cp >= 0x2300 && cp <= 0x23FF) return true;        // misc technical (⏰ ⌛ ⏳)
            if (cp >= 0x2600 && cp <= 0x27BF) return true;        // symbols + dingbats (✅ ❌ ⚠ ✔ ☀)
            if (cp >= 0x2B00 && cp <= 0x2BFF) return true;        // more symbols/arrows (⬆ ⭐)
            return false;
        }
    }
}
