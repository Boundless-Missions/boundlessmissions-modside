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

        // Login approval: set once /auth/link returns "approval_required". The user
        // presses a Log-in button somewhere else; we poll until then.
        private bool awaitingApproval;
        // WHERE that button is: "discord" (the DM the bot just sent) or "panel" (the
        // website account page that minted the code, which is the only route an
        // account with no Discord ever takes). The server says so in `approve_via`,
        // because both routes answer with the same status and the same challenge_id
        // and nothing here can tell them apart. Empty is an older server that never
        // sent the field — then both surfaces are named rather than one guessed at,
        // since guessing Discord is what sent website players hunting for a DM that
        // was never coming.
        private string approveVia = "";
        // Set when the server answers `totp_required`: this account has an
        // authenticator, so there is nothing to wait for — a code is typed here.
        private bool awaitingTotp;
        private string totpCode = "";
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

            if (awaitingTotp)
            {
                GUILayout.Label(
                    "This account uses an authenticator app.\n" +
                    "Enter the 6-digit code it shows\n(or one of your recovery codes).",
                    labelStyle
                );
                GUILayout.Space(15);

                GUI.SetNextControlName("TotpField");
                totpCode = GUILayout.TextField(totpCode, 12, codeFieldStyle);

                GUILayout.Space(10);
                GUI.enabled = !isLinking && totpCode.Trim().Length >= 6;
                if (GUILayout.Button(isLinking ? "Checking..." : "Confirm", buttonStyle))
                    DoLinkTotp();
                GUI.enabled = true;

                if (GUILayout.Button("← Cancel", GUI.skin.label))
                {
                    awaitingTotp = false;
                    approvalChallengeId = "";
                    totpCode = "";
                    statusMessage = "";
                }
            }
            else if (!awaitingApproval)
            {
                // Instructions
                // Both routes, website first: a code from the account page works
                // for everyone, including players who never joined the Discord.
                // /linkcode is still there for those who are already in it.
                GUILayout.Label(
                    "1. Get a code at boundlessmissions.com/account\n" +
                    "   (or type /b linkcode in Discord)\n" +
                    "2. Enter the 6-digit code below\n" +
                    "3. Click Link Account, then approve it",
                    labelStyle
                );
                GUILayout.Space(15);

                // Code input
                GUI.SetNextControlName("LinkCodeField");
                linkCode = GUILayout.TextField(linkCode, 6, codeFieldStyle);

                GUILayout.Space(10);

                // Link button
                GUI.enabled = !isLinking && linkCode.Length == 6;
                if (GUILayout.Button(isLinking ? "Linking..." : "Link Account", buttonStyle))
                {
                    DoLink();
                }
                GUI.enabled = true;
            }
            else
            {
                // Push approval: the user confirms on the surface that did not
                // consume the code. Nothing to type here — we just wait for their
                // button press.
                GUILayout.Label(ApprovalInstructions(), labelStyle);
                GUILayout.Space(15);

                if (GUILayout.Button("← Cancel", GUI.skin.label))
                {
                    awaitingApproval = false;
                    approvalChallengeId = "";
                    approveVia = "";
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

        /// <summary>
        /// Second half of a link the server gated on an authenticator.
        ///
        /// A wrong code deliberately keeps the window on this step rather than
        /// dropping back to the code field: the link code has already been spent,
        /// so starting over would mean fetching a whole new one for a typo.
        /// </summary>
        private void DoLinkTotp()
        {
            isLinking = true;
            statusMessage = "Checking...";

            GeneKermanMod.Instance.RunCoroutine(
                GeneKermanMod.Instance.Api.SubmitLinkTotp(
                    approvalChallengeId, totpCode.Trim(), (ok, data, err) =>
                {
                    isLinking = false;
                    if (ok && data != null)
                    {
                        awaitingTotp = false;
                        totpCode = "";
                        approvalChallengeId = "";
                        statusMessage = "(Ok) Linked as " + MiniJSON.GetString(data, "username") + "!";
                        GeneKermanMod.Instance.OnAccountLinked(data);
                    }
                    else
                    {
                        totpCode = "";
                        statusMessage = "(No) " + (err ?? "That code wasn't right. Try again.");
                    }
                }));
        }

        private void DoLink()
        {
            isLinking = true;
            statusMessage = "Linking...";

            GeneKermanMod.Instance.RunCoroutine(
                GeneKermanMod.Instance.Api.LinkAccount(linkCode, (ok, data, err) =>
                {
                    isLinking = false;
                    if (ok && MiniJSON.GetString(data, "status") == "totp_required")
                    {
                        // The account has an authenticator, which supersedes the DM
                        // and the panel: nothing to wait for, just a code to type.
                        approvalChallengeId = MiniJSON.GetString(data, "challenge_id");
                        awaitingTotp = true;
                        totpCode = "";
                        statusMessage = "(Ok) Enter the code from your authenticator app.";
                    }
                    else if (ok && MiniJSON.GetString(data, "status") == "approval_required")
                    {
                        // A Log-in button is waiting somewhere — in the Discord DM the
                        // server just sent, or on the account page for a panel-minted
                        // code. `approve_via` says which; wait for the press either way.
                        approvalChallengeId = MiniJSON.GetString(data, "challenge_id");
                        approveVia = MiniJSON.GetString(data, "approve_via");
                        awaitingApproval = true;
                        statusMessage = "(Ok) " + ApprovalStatusLine();
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

        /// <summary>
        /// What the waiting screen says while an approval is outstanding. Three
        /// answers, not two: a code minted on the website is approved on the website
        /// (and an account with no Discord has no other route), a code from
        /// /b linkcode is approved in the DM the bot sent, and an older server that
        /// sends no `approve_via` gets both named — which is longer, but never sends
        /// anyone to a surface that has nothing on it.
        /// </summary>
        private string ApprovalInstructions()
        {
            // The button names are the ones actually drawn on each surface — the
            // account page's KSP card says "Yes, that's me", the DM says "Log in" —
            // because an instruction that names a button nobody can find is only
            // slightly better than one that names the wrong surface.
            if (approveVia == "panel")
                return "Open boundlessmissions.com/account and press\n" +
                       "\"Yes, that's me\" to approve this sign-in.\n\nWaiting for approval…";
            if (approveVia == "discord")
                return "Check your Discord DMs and press \"✅ Log in\"\n" +
                       "to approve this sign-in.\n\nWaiting for approval…";
            return "Approve this sign-in where you got the code:\n" +
                   "• boundlessmissions.com/account (\"Yes, that's me\"), or\n" +
                   "• your Discord DMs (\"✅ Log in\").\n\nWaiting for approval…";
        }

        /// The same distinction, one line long, for the status strip.
        private string ApprovalStatusLine()
        {
            if (approveVia == "panel")
                return "Approve the login on your account page.";
            if (approveVia == "discord")
                return "Approve the login in your Discord DMs.";
            return "Approve the login where you got the code.";
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
