/*
 * LsEndurance.cs – Shared life-support endurance helpers.
 *
 * Endurance is reported in in-game days. KSP's day length depends on the player's
 * Kerbin-time vs Earth-time setting, so read it from the date-time formatter rather
 * than hard-coding 21600/86400.
 */

namespace GeneKerman
{
    public static class LsEndurance
    {
        /// <summary>Seconds in one in-game day (Kerbin = 21600, Earth = 86400),
        /// falling back to a Kerbin day if the formatter isn't available.</summary>
        public static double SecondsPerDay()
        {
            try
            {
                if (KSPUtil.dateTimeFormatter != null)
                    return KSPUtil.dateTimeFormatter.Day;
            }
            catch { /* ignore */ }
            return 21600d;
        }
    }
}
