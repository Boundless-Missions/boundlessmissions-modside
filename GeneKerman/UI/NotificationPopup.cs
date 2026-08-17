/*
 * UI/NotificationPopup.cs – Toast-style notification overlay.
 *
 * Shows brief messages in the top-right corner of the screen.
 * Auto-dismisses after 8 seconds, stacks multiple notifications.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI
{
    public class NotificationPopup
    {
        private class Toast
        {
            public string title;
            public string message;
            public string contractId;
            // Set when the notification carries a fix the player can press (see
            // LocalNotifActions). The button itself lives in the feed, not here — a
            // toast fades after 8 seconds and is the wrong place to put the only copy
            // of an action — so this only decides where clicking the toast lands.
            public string localAction;
            public float showTime;
            public float alpha;
        }

        private readonly List<Toast> activeToasts = new List<Toast>();
        private const float DURATION = 8f;
        private const float FADE_TIME = 0.5f;
        private const float TOAST_WIDTH = 320f;
        private const float TOAST_SPACING = 5f;

        private GUIStyle toastStyle;
        private GUIStyle titleStyle;
        private GUIStyle messageStyle;
        private bool stylesReady;

        public void Show(string title, string message, string contractId = null,
                         string localAction = null)
        {
            if (activeToasts.Count >= 5)
                activeToasts.RemoveAt(0);

            activeToasts.Add(new Toast
            {
                title = title,
                message = message,
                contractId = contractId,
                localAction = localAction,
                showTime = Time.realtimeSinceStartup,
                alpha = 1f
            });

            Debug.Log($"[GeneKerman] Notification: {title} — {message}");
        }

        public void Draw()
        {
            if (activeToasts.Count == 0) return;

            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            InitStyles();

            float y = 50f;
            float x = Screen.width - TOAST_WIDTH - 20f;

            for (int i = activeToasts.Count - 1; i >= 0; i--)
            {
                var toast = activeToasts[i];
                float elapsed = Time.realtimeSinceStartup - toast.showTime;

                if (elapsed > DURATION)
                {
                    activeToasts.RemoveAt(i);
                    continue;
                }

                float alpha;
                if (elapsed < FADE_TIME)
                    alpha = elapsed / FADE_TIME;
                else if (elapsed > DURATION - FADE_TIME)
                    alpha = (DURATION - elapsed) / FADE_TIME;
                else
                    alpha = 1f;

                toast.alpha = alpha;

                var prevColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);

                float height = 60f;
                Rect rect = new Rect(x, y, TOAST_WIDTH, height);

                GUI.Box(rect, "", toastStyle);
                GUI.Label(new Rect(rect.x + 12, rect.y + 8, rect.width - 24, 20), toast.title, titleStyle);
                GUI.Label(new Rect(rect.x + 12, rect.y + 28, rect.width - 24, 28), toast.message, messageStyle);

                if (GUI.Button(rect, "", GUIStyle.none))
                {
                    var mod = GeneKermanMod.Instance;
                    if (mod != null && mod.Api.IsLinked)
                    {
                        mod.ShowMainWindow = true;
                        // Deep-link straight to the referenced contract when available.
                        if (!string.IsNullOrEmpty(toast.contractId))
                            mod.OpenContractDetail(toast.contractId);
                        // No contract, but something to press: land on the feed, where
                        // the button is. Clicking a toast about a problem and arriving
                        // anywhere else means hunting for the fix it just offered.
                        else if (!string.IsNullOrEmpty(toast.localAction))
                            mod.OpenNotifications();
                    }
                    activeToasts.RemoveAt(i);
                }

                GUI.color = prevColor;
                y += height + TOAST_SPACING;
            }
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            toastStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.12f, 0.08f, 0.92f)) },
                border = new RectOffset(1, 1, 1, 1)
            };

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.3f, 0.95f, 0.5f) },
                clipping = TextClipping.Clip
            };

            messageStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, wordWrap = true,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f) },
                clipping = TextClipping.Clip
            };

            stylesReady = true;
        }
    }
}
