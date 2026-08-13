/*
 * UI/UpdateRequiredWindow.cs – "update required" prompt.
 *
 * Shown when the server reports this client's DLL is no longer the published
 * latest. While it's up, GeneKermanMod suppresses every other window.
 *
 * "Continue anyway" dismisses it for the session (GeneKermanMod.AcknowledgeUpdate)
 * and drops the player into the limited main window: flag import/export and the
 * Settings tab, which is the only way to point the mod at a different server
 * without hand-editing settings.cfg. Everything server-backed stays blocked —
 * the gate is enforced server-side with 426 regardless of what this window does.
 */

using UnityEngine;

namespace GeneKerman.UI
{
    public class UpdateRequiredWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 220, Screen.height / 2 - 190, 440, 380);
        private readonly int windowId = "GKUpdateRequired".GetHashCode();

        public bool Visible { get; private set; }
        private string currentVersion = "";
        private string latestVersion = "";
        private string downloadUrl = "";

        private GUIStyle titleStyle;
        private GUIStyle boxStyle;
        private GUIStyle bodyStyle;
        private GUIStyle urlStyle;
        private GUIStyle noteStyle;
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
                GUIStyle.none, GUILayout.Width(440), GUILayout.Height(380));
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
            noteStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = true,
                normal = { textColor = new Color(0.62f, 0.62f, 0.66f) }
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

            GUILayout.Space(10);
            if (GUILayout.Button("Continue anyway (limited)", GUILayout.Height(26)))
            {
                if (GeneKermanMod.Instance != null)
                    GeneKermanMod.Instance.AcknowledgeUpdate();
            }
            GUILayout.Space(2);
            GUILayout.Label(
                "Keeps flag import/export and the Settings tab — including switching to " +
                "another server. Missions, contracts and the marketplace stay unavailable " +
                "until you update.",
                noteStyle);
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
