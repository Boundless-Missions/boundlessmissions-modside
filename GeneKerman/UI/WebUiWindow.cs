/*
 * UI/WebUiWindow.cs – The in-game panel shown while the UI is running in a browser.
 *
 * This window is the recovery path, and it is not optional. Application.OpenURL can
 * fail silently in ways we cannot detect: xdg-open may be absent under Flatpak or
 * Proton, the Steam overlay may swallow the call or hand the URL to Steam's own
 * ancient CEF (which will not run a modern React bundle), and the player may simply
 * close the tab. In every one of those cases the toolbar button would otherwise appear
 * to do nothing at all.
 *
 * So: always show where the UI is, always offer a way back to it, and always offer a
 * way out to the in-game panel.
 */

using UnityEngine;

namespace GeneKerman.UI
{
    public class WebUiWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 230, Screen.height / 2 - 110, 460, 220);
        private readonly int windowId = "GKWebUi".GetHashCode();

        public bool Visible { get; set; }

        private GUIStyle titleStyle;
        private GUIStyle boxStyle;
        private GUIStyle bodyStyle;
        private GUIStyle urlStyle;
        private bool stylesReady;

        private float copiedAt = -10f;

        public void Draw()
        {
            if (!Visible) return;
            if (GKSkin.NeedsRebuild()) stylesReady = false;
            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(460), GUILayout.Height(220));
        }

        private void InitStyles()
        {
            if (stylesReady) return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.45f, 0.8f, 0.45f) }
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
            urlStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.7f, 0.85f, 1f) }
            };
            stylesReady = true;
        }

        private void DrawContent(int id)
        {
            InitStyles();
            var mod = GeneKermanMod.Instance;

            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("Boundless Missions is open in your browser", titleStyle);
            GUILayout.Space(8);
            GUILayout.Label("If no tab opened, copy this address into your browser.", bodyStyle);
            GUILayout.Space(8);

            // Selectable so the player can copy it by hand even if the button below
            // fails — GUIUtility.systemCopyBuffer is unreliable on some Linux setups.
            string url = mod?.WebBridgeUrl ?? "(not running)";
            GUILayout.TextField(url, urlStyle, GUILayout.Height(24));

            GUILayout.Space(4);
            if (Time.realtimeSinceStartup - copiedAt < 2f)
                GUILayout.Label("Copied to clipboard.", bodyStyle);
            else
                GUILayout.Label(" ", bodyStyle);

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reopen in browser", GUILayout.Height(26)))
                mod?.ReopenWebUi();

            if (GUILayout.Button("Copy URL", GUILayout.Height(26)))
            {
                GUIUtility.systemCopyBuffer = url;
                copiedAt = Time.realtimeSinceStartup;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            // Back to the in-game sidebar: SetUiMode(false) is what the toolbar button
            // then opens, so this is the recovery path when the browser never appeared.
            if (GUILayout.Button("Use the in-game panel", GUILayout.Height(24)))
                mod?.SetUiMode(false);

            if (GUILayout.Button("Close", GUILayout.Height(24)))
                Visible = false;
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
