/*
 * UI/DeviceVerifyWindow.cs – "New device — approve in Discord" prompt.
 *
 * Shown when the server blocks this device (device binding). The decision happens
 * in the user's Discord DM (✅ Yes, it's me / 🚫 No — report); this window just
 * tells them what to do and reflects the outcome the client polls for.
 */

using UnityEngine;

namespace GeneKerman.UI
{
    public class DeviceVerifyWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 210, Screen.height / 2 - 130, 420, 260);
        private readonly int windowId = "GKDeviceVerify".GetHashCode();

        public bool Visible { get; private set; }
        private string message = "";

        private GUIStyle titleStyle;
        private GUIStyle boxStyle;
        private GUIStyle bodyStyle;
        private bool stylesReady;

        public void Show(string msg)
        {
            message = msg;
            Visible = true;
        }

        public void Hide() => Visible = false;

        public void Draw()
        {
            if (!Visible) return;
            if (GKSkin.NeedsRebuild()) stylesReady = false;
            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawContent, "",
                GUIStyle.none, GUILayout.Width(420), GUILayout.Height(260));
        }

        private void InitStyles()
        {
            if (stylesReady) return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.6f, 0.2f) }
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
            stylesReady = true;
        }

        private void DrawContent(int id)
        {
            InitStyles();
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("🖥️ Device verification", titleStyle);
            GUILayout.Space(12);
            GUILayout.Label(message, bodyStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Space(10);
            if (GUILayout.Button("Close", GUILayout.Height(26)))
                Visible = false;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
