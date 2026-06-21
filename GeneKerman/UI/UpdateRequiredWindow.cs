/*
 * UI/UpdateRequiredWindow.cs – Blocking "update required" prompt.
 *
 * Shown when the server reports this client's DLL is no longer the published
 * latest. While it's up, GeneKermanMod suppresses every other window, so the mod
 * is unusable until the player updates (or a re-check clears it).
 */

using UnityEngine;

namespace GeneKerman.UI
{
    public class UpdateRequiredWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 220, Screen.height / 2 - 150, 440, 300);
        private readonly int windowId = "GKUpdateRequired".GetHashCode();

        public bool Visible { get; private set; }
        private string currentVersion = "";
        private string latestVersion = "";
        private string downloadUrl = "";

        private GUIStyle titleStyle;
        private GUIStyle boxStyle;
        private GUIStyle bodyStyle;
        private GUIStyle urlStyle;
        private bool stylesReady;

        public void Show(string current, string latest, string url)
        {
            currentVersion = current ?? "";
            latestVersion = latest ?? "";
            downloadUrl = url ?? "";
            Visible = true;
        }

        public void Hide() => Visible = false;

        public void Draw()
        {
            if (!Visible) return;
            if (GKSkin.NeedsRebuild()) stylesReady = false;
            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(440), GUILayout.Height(300));
        }

        private void InitStyles()
        {
            if (stylesReady) return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.4f, 0.35f) }
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
            urlStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = true,
                normal = { textColor = new Color(0.55f, 0.7f, 0.95f) }
            };
            stylesReady = true;
        }

        private void DrawContent(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("⛔ Update required", titleStyle);
            GUILayout.Space(12);
            GUILayout.Label(
                $"Your version {currentVersion} is no longer supported.\n" +
                $"Download {latestVersion} to keep using GeneKerman.",
                bodyStyle);
            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(downloadUrl))
            {
                if (GUILayout.Button("Download latest", GUILayout.Height(32)))
                    Application.OpenURL(downloadUrl);
                GUILayout.Space(4);
                GUILayout.Label(downloadUrl, urlStyle);
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Re-check after updating", GUILayout.Height(26)))
            {
                if (GeneKermanMod.Instance != null)
                    GeneKermanMod.Instance.RecheckVersion();
            }
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
