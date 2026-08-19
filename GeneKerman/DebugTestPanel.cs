#if GK_DEBUG_PANEL
/*
 * DebugTestPanel.cs — in-game security self-test panel (F12).
 *
 * COMPILED OUT of production builds. The whole file is wrapped in
 * #if GK_DEBUG_PANEL, which the csproj defines only when built off the
 * 'production' channel (see build.sh: GK_CHANNEL=dev). A shipped DLL contains
 * none of this code.
 *
 * It runs the LIVE half of 1808_security_test_checklist.md that can be exercised
 * from inside KSP:
 *
 *   Category A (client-only, no account): feeds MALICIOUS inputs to the real mod
 *   code and checks the filesystem — proving the CRITICAL (flag installer) and
 *   HIGH (craft filename) fixes actually hold in the running DLL, not just in a
 *   ported spec.
 *
 *   Category B (online, needs the mod linked): calls the dev-server-only
 *   /api/v1/debug/signtest endpoint and proves the signed-URL invariant — a
 *   private object downloads via its signed URL (200) but is forbidden via its
 *   bare public URL (403). Skipped cleanly if not linked or the endpoint is off.
 *
 * Toggle with F12. (In flight F12 also toggles KSP's aero overlay; harmless in a
 * dev build, and this is the only build where this file exists at all.)
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace GeneKerman
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class DebugTestPanel : MonoBehaviour
    {
        private const KeyCode ToggleKey = KeyCode.F12;

        // 1×1 transparent PNG — a real image so the flag installer's texture decode
        // doesn't log noise. Its SHA-256 is what the content-addressed flag file is
        // named after, so the test can assert the stored filename.
        private const string TinyPngB64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        private class Row
        {
            public readonly string Name;
            public string Status = "—";
            public string Detail = "";
            public Row(string name) { Name = name; }
        }

        private readonly Row _flagUrlExt = new Row("Flag: malicious url/ext neutralized (CRITICAL)");
        private readonly Row _flagContent = new Row("Flag: content-addressed storage");
        private readonly Row _craftName = new Row("Craft filename: traversal blocked (HIGH)");
        private readonly Row _signedUrl = new Row("Signed-URL invariant (signed 200 / public 403)");
        private List<Row> _rows;

        private bool _open;
        private bool _running;
        private Rect _win = new Rect(70, 70, 620, 430);
        private Vector2 _scroll;

        private void Awake()
        {
            _rows = new List<Row> { _flagUrlExt, _flagContent, _craftName, _signedUrl };
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey)) _open = !_open;
        }

        private void OnGUI()
        {
            if (!_open) return;
            _win = GUILayout.Window(GetInstanceID(), _win, DrawWindow,
                "GeneKerman — Security Self-Test  [DEBUG BUILD]");
        }

        // ── UI ────────────────────────────────────────────────────────────────

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label("DEBUG-ONLY build. This panel is compiled out of production.");
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUI.enabled = !_running;
            if (GUILayout.Button(_running ? "Running…" : "Run all tests", GUILayout.Height(26)))
                StartCoroutine(RunAll());
            GUI.enabled = true;
            if (GUILayout.Button("Copy results", GUILayout.Height(26), GUILayout.Width(120)))
                GUIUtility.systemCopyBuffer = ResultsText();
            if (GUILayout.Button("Close", GUILayout.Height(26), GUILayout.Width(80)))
                _open = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(300));
            foreach (var r in _rows) DrawRow(r);
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.Label("A green 'Flag' + 'Craft filename' row is live proof the CRITICAL/HIGH\n" +
                            "fixes hold in THIS DLL. Signed-URL row needs the mod linked and a dev\n" +
                            "server with DEBUG_ENDPOINTS_ENABLED=true.");
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void DrawRow(Row r)
        {
            GUILayout.BeginHorizontal("box");
            var prev = GUI.color;
            GUI.color = r.Status == "PASS" ? Color.green
                      : r.Status == "FAIL" ? new Color(1f, 0.45f, 0.45f)
                      : r.Status == "SKIP" ? Color.yellow
                      : Color.white;
            GUILayout.Label(r.Status, GUILayout.Width(52));
            GUI.color = prev;
            GUILayout.BeginVertical();
            GUILayout.Label(r.Name);
            if (!string.IsNullOrEmpty(r.Detail))
                GUILayout.Label("   " + r.Detail);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private string ResultsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("GeneKerman security self-test");
            foreach (var r in _rows)
                sb.AppendLine($"[{r.Status,-4}] {r.Name}  —  {r.Detail}");
            return sb.ToString();
        }

        // ── Runner ──────────────────────────────────────────────────────────────

        private IEnumerator RunAll()
        {
            _running = true;
            foreach (var r in _rows) { r.Status = "…"; r.Detail = ""; }

            Apply(_flagUrlExt, Test_FlagUrlExtNeutralized);
            Apply(_flagContent, Test_FlagContentAddressed);
            Apply(_craftName, Test_CraftFilenameTraversal);
            yield return StartCoroutine(Test_SignedUrlInvariant(_signedUrl));

            _running = false;
        }

        private static void Apply(Row row, Func<(bool ok, string detail)> test)
        {
            try
            {
                var (ok, detail) = test();
                row.Status = ok ? "PASS" : "FAIL";
                row.Detail = detail;
            }
            catch (Exception e)
            {
                row.Status = "FAIL";
                row.Detail = "harness exception: " + e.Message;
            }
        }

        // ── Category A: client-only file-write safety ────────────────────────────

        private static string GameDataDir =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

        private (bool, string) Test_FlagUrlExtNeutralized()
        {
            string evil = Path.Combine(GameDataDir, "GK_EVIL", "pwn.dll");
            // Unique bytes each run: the flag installer content-addresses and skips a
            // texture already registered in GameDatabase (which persists for the KSP
            // session even after the file is deleted), so a fixed image would only
            // install once and every re-run would falsely read as "not installed".
            byte[] data = UniquePayload();
            string hashFile = Path.Combine(GameDataDir, "GeneKerman", "Flags", Sha256Hex(data) + ".png");
            TryDelete(evil);
            TryDelete(hashFile);
            try
            {
                // A crafted GKFLAG block that TRIES to escape GameData and write a .dll.
                string b64 = UrlSafeB64(data);                          // matches DecodeFlagData
                string craft =
                    "ship = GKDebugFlagTest\nversion = 1.12.5\ntype = SPH\n" +
                    "GKFLAG\n{\n" +
                    "\turl = ../../../../GameData/GK_EVIL/pwn\n" +
                    "\text = dll\n" +
                    "\tdata = " + b64 + "\n}\n";

                FlagTransfer.StripAndInstallFlagsFromCraft(Encoding.UTF8.GetBytes(craft));

                if (File.Exists(evil))
                    return (false, "SECURITY FAIL: wrote " + evil);
                if (!File.Exists(hashFile))
                    return (false, "flag not installed (expected content-addressed <sha>.png)");
                return (true, "path + ext ignored; stored as <sha>.png inside Flags/");
            }
            finally
            {
                TryDelete(hashFile);
                TryDeleteDir(Path.Combine(GameDataDir, "GK_EVIL"));
            }
        }

        private (bool, string) Test_FlagContentAddressed()
        {
            byte[] data = UniquePayload();                              // unique — see note above
            string sha = Sha256Hex(data);
            string hashFile = Path.Combine(GameDataDir, "GeneKerman", "Flags", sha + ".png");
            TryDelete(hashFile);
            try
            {
                FlagTransfer.InstallStandaloneFlag("gk-debug-test", data);
                if (!File.Exists(hashFile))
                    return (false, "expected " + sha.Substring(0, 12) + "….png under Flags/");
                return (true, "filename == SHA-256(bytes)");
            }
            finally
            {
                TryDelete(hashFile);
            }
        }

        private (bool, string) Test_CraftFilenameTraversal()
        {
            byte[] craft = Encoding.UTF8.GetBytes(
                "ship = GKDebugFilenameTest\nversion = 1.12.5\ntype = SPH\n");
            string finalPath = null;
            try
            {
                finalPath = CraftInstaller.Install(craft, "..\\..\\..\\GK_EVIL\\evil.craft", null);
                if (string.IsNullOrEmpty(finalPath))
                    return (false, "Install returned null (no save loaded?)");
                string full = Path.GetFullPath(finalPath).Replace('\\', '/');
                if (full.Contains("GK_EVIL"))
                    return (false, "SECURITY FAIL: escaped to " + full);
                if (!full.Contains("/Ships/"))
                    return (false, "not under a Ships/ dir: " + full);
                return (true, "landed as " + Path.GetFileName(finalPath) + " under Ships/");
            }
            finally
            {
                if (!string.IsNullOrEmpty(finalPath))
                {
                    TryDelete(finalPath);
                    TryDelete(finalPath + ".gkmods");
                    TryDelete(finalPath.Replace(".craft", ".loadmeta"));
                }
            }
        }

        // ── Category B: online signed-URL invariant ──────────────────────────────

        private IEnumerator Test_SignedUrlInvariant(Row row)
        {
            var mod = GeneKermanMod.Instance;
            var api = mod != null ? mod.Api : null;
            if (api == null || !api.IsLinked)
            {
                row.Status = "SKIP";
                row.Detail = "link the mod to a running server first";
                yield break;
            }

            string body = null; long code = 0; bool ok = false;
            yield return api.Get("/api/v1/debug/signtest", (o, r, c) => { ok = o; body = r; code = c; });

            if (code == 404)
            {
                row.Status = "SKIP";
                row.Detail = "server debug endpoints off (set DEBUG_ENDPOINTS_ENABLED=true on a dev server)";
                yield break;
            }
            if (!ok || string.IsNullOrEmpty(body))
            {
                row.Status = "FAIL";
                row.Detail = "signtest request failed (" + code + ")";
                yield break;
            }

            string signed = null, publicUrl = null;
            try
            {
                var dict = MiniJSON.DeserializeDict(body);
                signed = MiniJSON.GetString(dict, "signed_url", "");
                publicUrl = MiniJSON.GetString(dict, "public_url", "");
            }
            catch (Exception e)
            {
                row.Status = "FAIL";
                row.Detail = "bad response JSON: " + e.Message;
                yield break;
            }
            if (string.IsNullOrEmpty(signed) || string.IsNullOrEmpty(publicUrl))
            {
                row.Status = "FAIL";
                row.Detail = "response missing signed_url/public_url";
                yield break;
            }

            long signedCode = -1, publicCode = -1;
            yield return RawStatus(signed, c => signedCode = c);
            yield return RawStatus(publicUrl, c => publicCode = c);

            bool pass = signedCode == 200 && publicCode == 403;
            row.Status = pass ? "PASS" : "FAIL";
            row.Detail = $"signed→{signedCode} (want 200), public→{publicCode} (want 403)";
        }

        private static IEnumerator RawStatus(string url, Action<long> onCode)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 20;
                yield return req.SendWebRequest();
                onCode(req.responseCode);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // A unique-per-call PNG: the 1×1 image plus a GUID appended after IEND.
        // Unity's PNG loader stops at IEND and ignores the trailing bytes, so it
        // still decodes as a valid image (no load errors), while the appended GUID
        // makes the SHA-256 — and therefore the content-addressed flag path — fresh
        // on every run, so the installer never dedupes the write away.
        private static byte[] UniquePayload()
        {
            byte[] png = Convert.FromBase64String(TinyPngB64);
            byte[] salt = Guid.NewGuid().ToByteArray();
            byte[] outb = new byte[png.Length + salt.Length];
            Buffer.BlockCopy(png, 0, outb, 0, png.Length);
            Buffer.BlockCopy(salt, 0, outb, png.Length, salt.Length);
            return outb;
        }

        // URL-safe base64, no padding — the exact shape FlagTransfer.DecodeFlagData expects.
        private static string UrlSafeB64(byte[] data)
        {
            return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, true); }
            catch { /* best effort */ }
        }
    }
}
#endif
