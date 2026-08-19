/*
 * UI/LinkWindow.cs – First-time account linking window.
 *
 * Shows a text field for the 6-digit code and a "Link Account" button.
 * Appears when the user clicks the toolbar button while not linked.
 */

using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI
{
    public class LinkWindow
    {
        private Rect windowRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 150, 400, 300);
        private string linkCode = "";
        private string statusMessage = "";
        private bool isLinking;

        // Discord login approval: set once /auth/link returns "approval_required".
        // The user presses a Log-in button in their Discord DM; we poll until then.
        private bool awaitingApproval;
        private string approvalChallengeId = "";
        private string serverUrlInput = "";
        private bool showSettings;

        private readonly int windowId = "GKLink".GetHashCode();

        // Styles (lazy init)
        private GUIStyle titleStyle;
        private GUIStyle boxStyle;
        private GUIStyle codeFieldStyle;
        private GUIStyle buttonStyle;
        private GUIStyle statusStyle;
        private GUIStyle labelStyle;
        private bool stylesReady;

        public void Draw()
        {
            if (GKSkin.NeedsRebuild())
                stylesReady = false;

            windowRect = ClickThroughHelper.Window(windowId, windowRect, DrawWindowContent, "",
                GUIStyle.none, GUILayout.Width(400), GUILayout.Height(300));
        }

        private void InitStyles()
        {
            if (stylesReady) return;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.2f, 0.9f, 0.4f) }
            };

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.12f, 0.12f, 0.16f, 0.95f)) },
                padding = new RectOffset(20, 20, 15, 15)
            };

            codeFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 50,
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.08f, 0.08f, 0.12f, 0.9f)), textColor = Color.white },
                focused = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.3f, 0.15f, 0.9f)), textColor = Color.white }
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                fixedHeight = 40,
                normal = { background = GKSkin.MakeTex(2, 2, new Color(0.15f, 0.6f, 0.3f, 0.9f)), textColor = Color.white },
                hover = { background = GKSkin.MakeTex(2, 2, new Color(0.2f, 0.7f, 0.4f, 0.9f)), textColor = Color.white },
                active = { background = GKSkin.MakeTex(2, 2, new Color(0.1f, 0.5f, 0.25f, 0.9f)), textColor = Color.white }
            };

            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            stylesReady = true;
        }

        private void DrawWindowContent(int id)
        {
            InitStyles();

            GUILayout.BeginVertical(boxStyle);

            // Title
            GUILayout.Label("🎮 Boundless Missions · Link KSP", titleStyle);
            GUILayout.Space(10);

            if (!awaitingApproval)
            {
                // Instructions
                GUILayout.Label(
                    "1. In Discord, type /b linkcode\n2. Enter the 6-digit code below\n3. Click Link Account",
                    labelStyle
                );
                GUILayout.Space(15);

                // Code input
                GUI.SetNextControlName("LinkCodeField");
                linkCode = GUILayout.TextField(linkCode, 6, codeFieldStyle);

                GUILayout.Space(10);

                // Link button
                GUI.enabled = !isLinking && linkCode.Length == 6;
                if (GUILayout.Button(isLinking ? "Linking..." : "[Link] Link Account", buttonStyle))
                {
                    DoLink();
                }
                GUI.enabled = true;
            }
            else
            {
                // Push approval: the user confirms in their own Discord DM. Nothing
                // to type here — we just wait for their button press.
                GUILayout.Label(
                    "Check your Discord DMs and press \"✅ Log in\"\nto approve this sign-in.\n\nWaiting for approval…",
                    labelStyle
                );
                GUILayout.Space(15);

                if (GUILayout.Button("← Cancel", GUI.skin.label))
                {
                    awaitingApproval = false;
                    approvalChallengeId = "";
                    statusMessage = "";
                }
            }

            // Status message
            if (!string.IsNullOrEmpty(statusMessage))
            {
                GUILayout.Space(5);
                statusStyle.normal.textColor = statusMessage.StartsWith("(Ok)") ? new Color(0.2f, 0.9f, 0.4f) : new Color(0.9f, 0.3f, 0.3f);
                GUILayout.Label(statusMessage, statusStyle);
            }

            GUILayout.Space(10);

            // Server URL settings toggle
            if (GUILayout.Button(showSettings ? "▼ Settings" : "▶ Settings", GUI.skin.label))
                showSettings = !showSettings;

            if (showSettings)
            {
                var api = GeneKermanMod.Instance.Api;
                bool official = api.UseOfficialServer;

                // Official Server vs Custom IP switch
                GUILayout.BeginHorizontal();
                GUI.backgroundColor = official ? new Color(0.2f, 0.7f, 0.35f) : Color.white;
                if (GUILayout.Button("Official", GUILayout.Height(24)))
                {
                    api.SetOfficialServer();
                    statusMessage = "Using official server.";
                }
                GUI.backgroundColor = !official ? new Color(0.2f, 0.7f, 0.35f) : Color.white;
                if (GUILayout.Button("Custom", GUILayout.Height(24)))
                {
                    if (string.IsNullOrEmpty(serverUrlInput))
                        serverUrlInput = api.CustomServerUrl;
                    api.SetCustomServer(serverUrlInput);
                    serverUrlInput = api.ServerUrl;
                    statusMessage = "Using custom IP.";
                }
                GUI.backgroundColor = Color.white;
                GUILayout.EndHorizontal();

                if (official)
                {
                    GUILayout.Label(ApiClient.OfficialServerUrl, labelStyle);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Server:", GUILayout.Width(55));
                    if (string.IsNullOrEmpty(serverUrlInput))
                        serverUrlInput = api.ServerUrl;

                    serverUrlInput = GUILayout.TextField(serverUrlInput, GUILayout.Height(24));
                    if (GUILayout.Button("Set", GUILayout.Width(40), GUILayout.Height(24)))
                    {
                        api.SetCustomServer(serverUrlInput);
                        serverUrlInput = api.ServerUrl;
                        statusMessage = "Server URL updated.";
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(5);

            // Close button
            if (GUILayout.Button("Close", GUILayout.Height(25)))
            {
                GeneKermanMod.Instance.ShowLinkWindow = false;
            }

            GUILayout.EndVertical();

            GUI.DragWindow();
        }

        private void DoLink()
        {
            isLinking = true;
            statusMessage = "Linking...";

            GeneKermanMod.Instance.RunCoroutine(
                GeneKermanMod.Instance.Api.LinkAccount(linkCode, (ok, data, err) =>
                {
                    isLinking = false;
                    if (ok && MiniJSON.GetString(data, "status") == "approval_required")
                    {
                        // Server DM'd a Log-in button — wait for the user to press it.
                        approvalChallengeId = MiniJSON.GetString(data, "challenge_id");
                        awaitingApproval = true;
                        statusMessage = "(Ok) Approve the login in your Discord DMs.";
                        StartApprovalPoll();
                    }
                    else if (ok)
                    {
                        statusMessage = "(Ok) Linked as " + MiniJSON.GetString(data, "username") + "!";
                        GeneKermanMod.Instance.OnAccountLinked(data);
                    }
                    else
                    {
                        statusMessage = "(No) " + (err ?? "Link failed. Check the code and try again.");
                    }
                })
            );
        }

        private void StartApprovalPoll()
        {
            string challengeId = approvalChallengeId;
            GeneKermanMod.Instance.RunCoroutine(
                GeneKermanMod.Instance.Api.PollLoginApproval(challengeId, (ok, data, err) =>
                {
                    // Ignore a stale poll the user already cancelled / restarted.
                    if (!awaitingApproval || approvalChallengeId != challengeId)
                        return;
                    if (ok)
                    {
                        awaitingApproval = false;
                        statusMessage = "(Ok) Linked as " + MiniJSON.GetString(data, "username") + "!";
                        GeneKermanMod.Instance.OnAccountLinked(data);
                    }
                    else
                    {
                        awaitingApproval = false;
                        approvalChallengeId = "";
                        statusMessage = "(No) " + (err ?? "Login was not approved.");
                    }
                })
            );
        }
    }
}
