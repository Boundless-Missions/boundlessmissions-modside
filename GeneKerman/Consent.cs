/*
 * Consent.cs – First-run opt-in consent record (KSP add-on rule 8.1).
 *
 * KSP's add-on rules require an unambiguous in-game opt-in before any
 * personally-identifiable information is gathered or sent. We store that consent
 * in its own file (PluginData/consent.cfg) — separate from settings.cfg — so the
 * agreement is an explicit, standalone record. The link/login menu is gated behind
 * it: a fresh install must accept before it can link a Discord account.
 */

using System;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class Consent
    {
        // The policy version a fresh client assumes is current until the server says
        // otherwise. The server (config/policy) is the source of truth and can raise
        // the bar at any time via SetRequiredVersion — see requiredVersion below —
        // so a policy update no longer needs a mod rebuild.
        public const int BaselineVersion = 1;

        // The version the user must have accepted for consent to count. Starts at the
        // baseline and is bumped to whatever the server advertises on /version/check.
        // Never lowered below the baseline (a missing/garbled server value can't strip
        // consent from an already-accepted client).
        private static int requiredVersion = BaselineVersion;

        private static bool loaded;
        private static bool accepted;
        private static int acceptedVersion;

        // consent.cfg is re-read whenever it changes on disk so manual edits / an
        // external revoke take effect live. Tracked by last-write time; stat is
        // throttled because Accepted is polled from OnGUI every frame.
        private static DateTime lastWriteUtc = DateTime.MinValue;
        private static int lastStatTick;

        private static string ConsentPath =>
            Path.Combine(GeneKermanMod.PluginDataPath, "consent.cfg");

        /// <summary>The policy version currently required (server-driven).</summary>
        public static int RequiredVersion => requiredVersion;

        /// <summary>True once the user has accepted the currently required policy
        /// version. Reflects live edits to consent.cfg and server policy bumps.</summary>
        public static bool Accepted
        {
            get
            {
                EnsureLoaded();
                return accepted && acceptedVersion >= requiredVersion;
            }
        }

        /// <summary>Set the policy version the user must have accepted, as reported by
        /// the server. Bumping past the user's accepted version makes Accepted go false,
        /// which raises the re-consent gate and blocks transmission until they re-accept.
        /// Values below the baseline are ignored so a bad server value can't revoke
        /// consent.</summary>
        public static void SetRequiredVersion(int version)
        {
            if (version < BaselineVersion) return;
            requiredVersion = version;
        }

        private static void EnsureLoaded()
        {
            // Throttle the stat: Accepted is hit once per frame, but consent.cfg
            // changes are rare. Re-stat at most ~once a second (wraparound-safe).
            int now = Environment.TickCount;
            if (loaded && unchecked((uint)(now - lastStatTick)) < 1000u) return;
            lastStatTick = now;

            DateTime writeUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(ConsentPath))
                    writeUtc = File.GetLastWriteTimeUtc(ConsentPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not stat consent.cfg: " + e.Message);
            }

            // Nothing changed since the last read — keep the cached state.
            if (loaded && writeUtc == lastWriteUtc) return;
            lastWriteUtc = writeUtc;
            loaded = true;

            // Reset, then reload from disk. A deleted/missing file means no consent.
            accepted = false;
            acceptedVersion = 0;
            if (writeUtc == DateTime.MinValue) return;

            try
            {
                var node = ConfigNode.Load(ConsentPath);
                var gk = node?.GetNode("GeneKermanConsent");
                if (gk != null)
                {
                    bool.TryParse(gk.GetValue("accepted") ?? "false", out accepted);
                    int.TryParse(gk.GetValue("version") ?? "0", out acceptedVersion);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not read consent.cfg: " + e.Message);
            }
        }

        /// <summary>Record that the user agreed to the Privacy Policy + Terms and
        /// expressly consented to data transmission. Persisted to consent.cfg.</summary>
        public static void Accept()
        {
            accepted = true;
            acceptedVersion = requiredVersion;
            loaded = true;
            try
            {
                Directory.CreateDirectory(GeneKermanMod.PluginDataPath);
                var node = new ConfigNode();
                var gk = node.AddNode("GeneKermanConsent");
                gk.AddValue("accepted", true);
                gk.AddValue("privacyPolicy", true);
                gk.AddValue("termsOfService", true);
                gk.AddValue("dataTransmission", true);
                gk.AddValue("version", requiredVersion);
                gk.AddValue("acceptedUtc", DateTime.UtcNow.ToString("o"));
                node.Save(ConsentPath);
                // Record the file's new write time so the next stat doesn't see our
                // own write as an external change and needlessly reload.
                try { lastWriteUtc = File.GetLastWriteTimeUtc(ConsentPath); } catch { }
                Debug.Log("[GeneKerman] Consent recorded (version " + requiredVersion + ").");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not write consent.cfg: " + e.Message);
            }
        }

        /// <summary>Revoke consent (deletes consent.cfg). The next link attempt will
        /// re-prompt the opt-in gate.</summary>
        public static void Revoke()
        {
            accepted = false;
            acceptedVersion = 0;
            loaded = true;
            lastWriteUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(ConsentPath)) File.Delete(ConsentPath);
                Debug.Log("[GeneKerman] Consent revoked.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not delete consent.cfg: " + e.Message);
            }
        }
    }
}
