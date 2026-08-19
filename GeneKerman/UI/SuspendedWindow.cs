/*
 * UI/SuspendedWindow.cs – "your account is suspended" notice.
 *
 * Raised when any request comes back 403 `suspended` (see ApiClient's gate hook).
 * While it is up, GeneKermanMod draws nothing else and the sidebar stays hidden:
 * every server-backed feature is refused anyway, and a full interface whose every
 * button fails reads as a broken mod rather than as a decision someone made.
 *
 * IMGUI, like the other gates, for the reason the class comment on
 * GeneKermanMod.OnGUI gives: a gate may not depend on the surface it gates.
 *
 * Three things it must say, because the alternative to each one is a support
 * ticket: *why* (the reason the moderator typed — carried in the 403 body), *for
 * how long* (counted down live from the expiry, not quoted as the duration
 * issued), and *that nothing was lost* — a player who thinks their save, balance
 * or listings are gone panics in a way that a temporary block does not warrant.
 *
 * There is no "continue anyway". The update gate has one because a player can act
 * on it — switch to a server that accepts this build, install the new DLL — and
 * needs the Settings tab to do it. Nothing here is fixable from this side, so the
 * button would open a UI with nothing behind it. What still runs is the part of
 * the mod that never asks the server: rescue removals already promised, the
 * roster sweep, life-support immunity (see the gate in GeneKermanMod.Update).
 * A suspension blocks the services; it does not reach into the player's save.
 */

using System;
using UnityEngine;

namespace GeneKerman.UI
{
    public class SuspendedWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 230, Screen.height / 2 - 175, 460, 350);
        private readonly int windowId = "GKSuspended".GetHashCode();

        public bool Visible { get; private set; }

        private string reason = "";
        // Unix seconds, server clock. 0 means the server did not say — the notice
        // then omits the countdown rather than inventing one.
        private double until;
        private bool checking;
        private string checkResult = "";

        public void Show(string reasonText, double untilUnix)
        {
            reason = (reasonText ?? "").Trim();
            until = untilUnix;
            checkResult = "";
            Visible = true;
        }

        public void Hide() => Visible = false;

        private GUIStyle titleStyle;
        private GUIStyle boxStyle;
        private GUIStyle bodyStyle;
        private GUIStyle reasonStyle;
        private GUIStyle noteStyle;
        private bool stylesReady;

        public void Draw()
        {
            if (!Visible) return;
            if (GKSkin.NeedsRebuild()) stylesReady = false;
            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(460), GUILayout.Height(350));
        }

        private void InitStyles()
        {
            if (stylesReady) return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.55f, 0.30f) }
            };
            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.12f, 0.16f, 0.97f)) },
                padding = new RectOffset(20, 20, 18, 18)
            };
            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };
            reasonStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.95f, 0.80f, 0.55f) }
            };
            noteStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = true,
                normal = { textColor = new Color(0.62f, 0.62f, 0.66f) }
            };
            stylesReady = true;
        }

        /// <summary>
        /// Seconds until the suspension lifts; 0 once it has (or if the server never
        /// said when). The expiry is a server-clock timestamp compared against this
        /// machine's UTC clock, which is close enough for a countdown and, where it
        /// isn't, self-corrects: clearing early only earns a fresh 403.
        /// </summary>
        public static double SecondsLeft(double untilUnix)
        {
            if (untilUnix <= 0) return 0;
            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            double left = untilUnix - now;
            return left > 0 ? left : 0;
        }

        /// <summary>
        /// "2 days 4 hours", "35 minutes", "" once it has run out.
        ///
        /// Computed from the expiry every frame rather than stored: the window can
        /// sit open across a long session, and a figure captured when it opened
        /// would be the one thing on screen that is quietly wrong.
        /// </summary>
        public static string Remaining(double untilUnix)
        {
            double secs = SecondsLeft(untilUnix);
            if (secs <= 0) return "";
            var span = TimeSpan.FromSeconds(secs);
            if (span.TotalDays >= 1)
                return string.Format("{0} day{1} {2} hour{3}", span.Days, span.Days == 1 ? "" : "s",
                                     span.Hours, span.Hours == 1 ? "" : "s");
            if (span.TotalHours >= 1)
                return string.Format("{0} hour{1} {2} min", span.Hours, span.Hours == 1 ? "" : "s",
                                     span.Minutes);
            return string.Format("{0} minute{1}", Math.Max(1, span.Minutes), span.Minutes == 1 ? "" : "s");
        }

        private void DrawContent(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("⏸ Access suspended", titleStyle);
            GUILayout.Space(10);

            string left = Remaining(until);
            GUILayout.Label(
                string.IsNullOrEmpty(left)
                    ? "Your Boundless Missions account is temporarily suspended."
                    : "Your Boundless Missions account is suspended for another " + left + ".",
                bodyStyle);

            if (!string.IsNullOrEmpty(reason))
            {
                GUILayout.Space(8);
                GUILayout.Label("“" + reason + "”", reasonStyle);
            }

            GUILayout.Space(10);
            GUILayout.Label(
                "Missions, contracts, the marketplace and the website are unavailable until " +
                "it ends. Nothing has been deleted — your balance, XP, contracts and listings " +
                "are waiting. Your Discord membership is unaffected.",
                noteStyle);

            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(checkResult))
            {
                GUILayout.Label(checkResult, noteStyle);
                GUILayout.Space(4);
            }

            GUI.enabled = !checking;
            if (GUILayout.Button(checking ? "Checking…" : "Check again", GUILayout.Height(30)))
            {
                checking = true;
                checkResult = "";
                if (GeneKermanMod.Instance != null)
                    GeneKermanMod.Instance.RecheckSuspension(msg =>
                    {
                        checking = false;
                        checkResult = msg;
                    });
                else checking = false;
            }
            GUI.enabled = true;

            GUILayout.Space(6);
            GUILayout.Label(
                "Think this is a mistake? Open a ticket in the Discord server.",
                noteStyle);
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
