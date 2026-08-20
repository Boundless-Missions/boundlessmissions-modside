/*
 * UI/ConsentWindow.cs – First-run privacy / terms opt-in gate (KSP add-on rule 8.1).
 *
 * Shown before the link menu on a fresh install. The user must read the Privacy
 * Policy and Terms (linked to boundlessmissions.com/pp and /tos), tick both
 * agreement boxes, and accept before any data is gathered or a Discord account
 * is linked. Acceptance is recorded by Consent.Accept() (consent.cfg).
 */

using UnityEngine;

namespace GeneKerman.UI
{
    public class ConsentWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 260, Screen.height / 2 - 255, 520, 510);
        private readonly int windowId = "GKConsent".GetHashCode();

        // The three unambiguous opt-in checkboxes (rule 8.1).
        private bool agreePrivacy;        // read & agree to the Privacy Policy
        private bool agreeTerms;          // read & agree to the Terms of Service
        private bool agreeTransmission;   // formal consent to data being collected & sent

        private GUIStyle titleStyle, boxStyle, bodyStyle, buttonStyle, linkStyle, checkStyle;
        private bool stylesReady;

        public void Draw()
        {
            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawWindowContent, "",
                GUIStyle.none, GUILayout.Width(520), GUILayout.Height(510));
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.2f, 0.9f, 0.4f) }
            };

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.12f, 0.16f, 0.97f)) },
                padding = new RectOffset(22, 22, 18, 18)
            };

            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.82f, 0.82f, 0.85f) }
            };

            linkStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.35f, 0.7f, 1f) },
                hover = { textColor = new Color(0.55f, 0.82f, 1f) }
            };

            checkStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = Color.white },
                onNormal = { textColor = Color.white },
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

            GUILayout.Label("Boundless Missions · Privacy & Terms", titleStyle);
            GUILayout.Space(10);

            // What we gather and why (rule 8.1: inform the user of goal and extent).
            GUILayout.Label(
                "Boundless Missions connects your Kerbal Space Program game to the " +
                "Boundless Missions Discord community. To do that, this add-on gathers " +
                "and sends the following to the Boundless Missions servers:\n\n" +
                "• Your Discord account link (so contracts, balance and missions are tied to you)\n" +
                "• A random device identifier (account security only; not your MAC address " +
                "or any other hardware data)\n" +
                "• Gameplay data: vessel telemetry, craft files, part lists and screenshots " +
                "you submit to contracts or share; submissions may be reviewed by Google's " +
                "Gemini AI\n" +
                "• Your MAC address and KSP.log, only inside a report you or the account " +
                "owner files: a bug report attaches your (trimmed) log, and a device report " +
                "collects the reported device's MAC and log\n\n" +
                "No data is sent until you accept below, and you can turn all data sharing " +
                "off again at any time from the toolbar (Settings ▸ Data sharing).",
                bodyStyle);

            GUILayout.Space(8);

            // Clickable policy links.
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Privacy Policy ↗", linkStyle))
                Application.OpenURL(ApiClient.PrivacyPolicyUrl);
            GUILayout.Space(20);
            if (GUILayout.Button("Terms of Service ↗", linkStyle))
                Application.OpenURL(ApiClient.TermsOfServiceUrl);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(12);

            // Unambiguous opt-in checkboxes — all three required.
            agreePrivacy = GUILayout.Toggle(agreePrivacy,
                " I have read and agree to the Privacy Policy.",
                checkStyle);
            GUILayout.Space(6);
            agreeTerms = GUILayout.Toggle(agreeTerms,
                " I have read and agree to the Terms of Service.",
                checkStyle);
            GUILayout.Space(6);
            agreeTransmission = GUILayout.Toggle(agreeTransmission,
                " I expressly consent to my account, device and gameplay information being " +
                "collected and transmitted to the Boundless Missions servers whenever " +
                "requested to provide these features.",
                checkStyle);

            GUILayout.Space(14);

            GUILayout.BeginHorizontal();
            GUI.enabled = agreePrivacy && agreeTerms && agreeTransmission;
            if (GUILayout.Button("Agree & Continue", buttonStyle))
            {
                Consent.Accept();
                GeneKermanMod.Instance.OnConsentGranted();
            }
            GUI.enabled = true;
            if (GUILayout.Button("Decline", buttonStyle, GUILayout.Width(120)))
            {
                GeneKermanMod.Instance.ShowConsentWindow = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUI.DragWindow();
        }
    }
}
