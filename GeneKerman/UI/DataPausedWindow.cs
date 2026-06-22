/*
 * UI/DataPausedWindow.cs – Shown when the user has turned data sharing OFF.
 *
 * With the master data-gathering opt-out active (KSP add-on rule 8.2) the mod runs
 * inert: it collects and sends nothing. Opening the toolbar button in that state
 * shows this panel instead of the normal windows, explaining the mod is paused and
 * offering a single button to re-enable data sharing and resume.
 */

using UnityEngine;

namespace GeneKerman.UI
{
    public class DataPausedWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 220, Screen.height / 2 - 150, 440, 300);
        private readonly int windowId = "GKDataPaused".GetHashCode();

        private GUIStyle titleStyle, boxStyle, bodyStyle, buttonStyle;
        private bool stylesReady;

        public void Draw()
        {
            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawWindowContent, "",
                GUIStyle.none, GUILayout.Width(440), GUILayout.Height(300));
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.75f, 0.25f) }
            };

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.12f, 0.16f, 0.97f)) },
                padding = new RectOffset(22, 22, 18, 18)
            };

            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.82f, 0.82f, 0.85f) }
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                fixedHeight = 38,
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.6f, 0.3f, 0.9f)), textColor = Color.white },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.7f, 0.4f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.5f, 0.25f, 0.9f)), textColor = Color.white }
            };

            stylesReady = true;
        }

        private void DrawWindowContent(int id)
        {
            InitStyles();

            GUILayout.BeginVertical(boxStyle);

            GUILayout.Label("⏸ Boundless Missions is paused", titleStyle);
            GUILayout.Space(14);

            GUILayout.Label(
                "Data sharing is turned OFF, so this add-on is currently inactive — " +
                "it is not collecting or sending any information.\n\n" +
                "Re-enable data sharing to use contracts, missions, notifications and " +
                "everything else again.",
                bodyStyle);

            GUILayout.Space(20);

            if (GUILayout.Button("Enable data sharing & resume", buttonStyle))
            {
                GeneKermanMod.Instance.SetDataGatheringEnabled(true);
                GeneKermanMod.Instance.ShowDataPausedWindow = false;
            }

            GUILayout.Space(6);

            if (GUILayout.Button("Close", buttonStyle, GUILayout.Height(28)))
            {
                GeneKermanMod.Instance.ShowDataPausedWindow = false;
            }

            GUILayout.EndVertical();

            GUI.DragWindow();
        }
    }
}
