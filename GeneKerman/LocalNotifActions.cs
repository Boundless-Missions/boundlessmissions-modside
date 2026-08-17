/*
 * LocalNotifActions.cs – A button on a notification that the client itself can act on.
 *
 * Server notifications are things that happened elsewhere, and the only thing to do
 * with one is open the contract it names. A *local* notification (see
 * GeneKermanMod.RaiseLocalNotification) can be a problem this install is able to fix
 * on the spot — and "we noticed your roster breaks the Astronaut Complex" is only half
 * a message without the button that repairs it.
 *
 * The action rides in the notification's own `data` dict as a short key, alongside
 * `contract_id`, so it survives the feed's existing plumbing (the local-notification
 * merge, mark-read, dismiss) without any of it learning a new shape. Both front ends —
 * the classic window's feed tab and the sidebar's NotificationsPanel — render a button
 * from the key and dispatch it here, so neither knows what any action does and a second
 * one costs no UI code.
 *
 * A run reports its own result as a fresh notification rather than into a status line:
 * that is the one channel both front ends already share, and it leaves the outcome in
 * the feed instead of in a label the player has already navigated away from.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public static class LocalNotifActions
    {
        /// <summary>Key inside a notification's `data` dict.</summary>
        public const string DataKey = "local_action";

        /// <summary>Repair kerbals whose profession no installed mod defines.</summary>
        public const string RepairTraits = "repair_traits";

        /// <summary>The action a notification carries, or "" for the ordinary kind.
        /// Unknown keys read as "" — a notification raised by a newer version of this
        /// mod must not draw a button that cannot do anything.</summary>
        public static string Of(Dictionary<string, object> n)
        {
            var data = MiniJSON.GetDict(n, "data");
            if (data == null) return "";
            string action = MiniJSON.GetString(data, DataKey);
            return LabelFor(action) == null ? "" : action;
        }

        /// <summary>Button text for an action, or null when there is no such action.</summary>
        public static string LabelFor(string action)
        {
            switch (action)
            {
                case RepairTraits: return "Fix professions";
                default: return null;
            }
        }

        /// <summary>
        /// Run a notification's action and report the outcome. Strips the key on the way
        /// out so the button disappears from a notification whose work is done — a Fix
        /// button that stays pressable after the fix invites a second press that finds
        /// nothing to do and looks like a failure.
        /// </summary>
        public static void Run(Dictionary<string, object> n)
        {
            string action = Of(n);
            if (string.IsNullOrEmpty(action)) return;

            string title, message;
            try
            {
                switch (action)
                {
                    case RepairTraits:
                        message = TraitRepair.Repair();
                        // Neutral on purpose: the same call reports "nothing to repair"
                        // and "could not repair 2 of them", and a title claiming success
                        // over either would be the wrong half of the message.
                        title = "🧑‍🚀 Crew profession fix";
                        break;
                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                // A throw here is ours, not the player's problem to diagnose: say it
                // plainly and leave the button in place so it can be tried again.
                Debug.LogError($"[GeneKerman] Local action '{action}' failed: {ex}");
                Report("⚠ Couldn't finish that", $"The fix failed: {ex.Message}. See KSP.log.");
                return;
            }

            var data = MiniJSON.GetDict(n, "data");
            if (data != null) data.Remove(DataKey);

            Report(title, message);
        }

        private static void Report(string title, string message)
        {
            var mod = GeneKermanMod.Instance;
            if (mod != null)
            {
                try { mod.ShowNotification(title, message); return; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] Action result notification failed: {ex.Message}");
                }
            }
            try { ScreenMessages.PostScreenMessage($"{title}: {message}", 12f, ScreenMessageStyle.UPPER_CENTER); }
            catch { /* no screen — the log line stands in */ }
        }
    }
}
