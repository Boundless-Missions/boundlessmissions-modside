/*
 * UI/CheckpointPrompt.cs – "Capture a photo?" confirmation for flight milestones.
 *
 * A single bottom-centre panel with a heading, a one-line description, and
 * Capture / Dismiss buttons. Only one prompt shows at a time; it auto-dismisses
 * after a timeout so it never blocks gameplay. The host wires the Capture button
 * to CinematicCapture via the onAccept callback.
 */

using System;
using UnityEngine;

namespace GeneKerman.UI
{
    public class CheckpointPrompt
    {
        private const float WIDTH = 420f;
        private const float HEIGHT = 118f;
        private const float TIMEOUT = 18f;   // s before it dismisses itself

        private bool visible;
        private string title;
        private string message;
        private float shownAt;
        private float timeout = TIMEOUT;
        private Action onAccept;
        private Action onClose;

        private GUIStyle boxStyle;
        private GUIStyle titleStyle;
        private GUIStyle messageStyle;
        private GUIStyle acceptStyle;
        private GUIStyle dismissStyle;
        private bool stylesReady;

        public bool IsVisible => visible;

        public void Show(string title, string message, Action onAccept, Action onClose = null,
            float timeoutOverride = 0f)
        {
            this.title = title;
            this.message = message;
            this.onAccept = onAccept;
            this.onClose = onClose;
            this.timeout = timeoutOverride > 0f ? timeoutOverride : TIMEOUT;
            shownAt = Time.realtimeSinceStartup;
            visible = true;
        }

        public void Dismiss(bool accepted)
        {
            if (!visible) return;
            visible = false;
            var accept = onAccept;
            var close = onClose;
            onAccept = null;
            onClose = null;

            if (accepted) accept?.Invoke();
            close?.Invoke();
        }

        public void Draw()
        {
            if (!visible) return;

            if (Time.realtimeSinceStartup - shownAt > timeout)
            {
                Dismiss(false);
                return;
            }

            if (GKSkin.NeedsRebuild()) stylesReady = false;
            InitStyles();

            float x = (Screen.width - WIDTH) * 0.5f;
            float y = Screen.height - HEIGHT - 90f;   // above the flight controls
            var rect = new Rect(x, y, WIDTH, HEIGHT);

            GUI.Box(rect, "", boxStyle);
            GUI.Label(new Rect(rect.x + 16, rect.y + 10, rect.width - 32, 22), title, titleStyle);
            GUI.Label(new Rect(rect.x + 16, rect.y + 36, rect.width - 32, 38), message, messageStyle);

            float bw = 150f, bh = 30f, gap = 16f;
            float by = rect.y + HEIGHT - bh - 12f;
            float bx = rect.x + (WIDTH - (bw * 2 + gap)) * 0.5f;

            if (GUI.Button(new Rect(bx, by, bw, bh), "📷  Capture", acceptStyle))
                Dismiss(true);
            if (GUI.Button(new Rect(bx + bw + gap, by, bw, bh), "Dismiss", dismissStyle))
                Dismiss(false);
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.12f, 0.08f, 0.95f)) },
                border = new RectOffset(1, 1, 1, 1)
            };

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.3f, 0.95f, 0.5f) }
            };

            messageStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, wordWrap = true,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };

            acceptStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white,
                    background = GKSkin.MakeTex(2, 2, new Color(0.16f, 0.5f, 0.26f, 1f)) },
                hover = { textColor = Color.white,
                    background = GKSkin.MakeTex(2, 2, new Color(0.22f, 0.62f, 0.34f, 1f)) }
            };

            dismissStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f) }
            };

            stylesReady = true;
        }
    }
}
