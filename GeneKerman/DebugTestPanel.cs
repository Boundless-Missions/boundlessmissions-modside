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
            public string Status = "-";
            public string Detail = "";
            public Row(string name) { Name = name; }
        }

        private readonly Row _flagUrlExt = new Row("Flag: malicious url/ext neutralized (CRITICAL)");
        private readonly Row _flagContent = new Row("Flag: content-addressed storage");
        private readonly Row _craftName = new Row("Craft filename: traversal blocked (HIGH)");
        private readonly Row _crewTag = new Row("Crew ownership tag: forgery refused, honest return strips (CRITICAL)");
        private readonly Row _crewOwnerId = new Row("Crew ownership decided by account id, not display name (CRITICAL)");
        private readonly Row _crewBusy = new Row("Crew adoption refused while the roster entry is crewing something (HIGH)");
        private readonly Row _signedUrl = new Row("Signed-URL invariant (signed 200 / public 403)");
        private List<Row> _rows;

        private bool _open;
        private bool _running;
        private Rect _win = new Rect(70, 70, 620, 430);
        private Vector2 _scroll;

        private void Awake()
        {
            _rows = new List<Row> { _flagUrlExt, _flagContent, _craftName, _crewTag,
                                    _crewOwnerId, _crewBusy, _signedUrl };
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
                "GeneKerman - Security Self-Test  [DEBUG BUILD]");
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
                sb.AppendLine($"[{r.Status,-4}] {r.Name}  -  {r.Detail}");
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
            Apply(_crewTag, Test_CrewOwnershipTag);
            Apply(_crewOwnerId, Test_CrewOwnershipByAccountId);
            Apply(_crewBusy, Test_CrewAdoptionRefusedWhileCrewing);
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

        // ── Category A: crew ownership tagging ───────────────────────────────────
        //
        // The whole table for VesselTransfer.ApplyIncomingOwnershipTag, run against the
        // real function. It is here rather than in a comment because the two rows that
        // matter most are the same *string* — an honest rescue coming home and a forged
        // capture attempt both read "{me}'s Jeb" written by somebody else — so the
        // property under test is that the allow-list, and only the allow-list, tells them
        // apart. Getting it wrong in either direction is destructive: refuse the honest
        // one and the issuer's own kerbals are swept out of their roster by the hand-over
        // that follows; accept the forged one and a peer picks which of the recipient's
        // kerbals to have deleted.
        //
        // Pure string work, so it needs no save, no scene and no network.

        private (bool, string) Test_CrewOwnershipTag()
        {
            // What the server holds for a rescue this player issued: the crew names their
            // own client tagged when it handed the wreck over.
            var issued = VesselTransfer.HomeboundSet(new List<string> { "A's Jeb" });

            var cases = new List<(string label, string input, string owner, string me,
                                  HashSet<string> attested, string expect)>
            {
                ("A→B wreck out",        "Jeb",           "A",       "B", null,   "A's Jeb"),
                ("B→A craft home",       "A's Jeb",       "B",       "A", issued, "Jeb"),
                ("B's own pilot",        "Bill Kerman",   "B",       "A", issued, "B's Bill Kerman"),
                ("issuer vessel restore","Jeb",           "A",       "A", issued, "Jeb"),
                ("submission restore",   "A's Jeb",       "B",       "B", null,   "A's Jeb"),
                ("multi-hop A→B→C",      "A's Jeb",       "B",       "C", null,   "A's Jeb"),
                ("quicksend declined",   "Jeb",           "A",       "A", null,   "Jeb"),
                ("forged tag",           "A's Jeb",       "Mallory", "A", null,   "Mallory's A's Jeb"),
                // Same forgery inside a genuine rescue delivery: the contract attests to
                // Jeb and to nobody else, so a name off the list keeps the refusal.
                ("forged tag, off-list", "A's Bob",       "Mallory", "A", issued, "Mallory's A's Bob"),
                // An unnamed owner must never yield a bare name — that is adoption, and
                // the bot really does send an empty owner_name on an old contract.
                ("no owner, bare name",  "Zaphod Kerman", "",        "A", null,
                                         VesselTransfer.UnknownOwnerTag + "'s Zaphod Kerman"),
                ("no owner, forged tag", "A's Jeb",       "",        "A", null,
                                         VesselTransfer.UnknownOwnerTag + "'s A's Jeb"),
                // …but an unnamed owner on the issuer's own wreck coming back is exactly
                // what the stripped half of the allow-list is for.
                ("no owner, restore",    "Jeb",           "",        "A", issued, "Jeb"),
            };

            var bad = new List<string>();
            foreach (var c in cases)
            {
                string got = VesselTransfer.ApplyIncomingOwnershipTag(
                    c.input, c.owner, c.me, c.attested);
                if (got != c.expect)
                    bad.Add($"{c.label}: got '{got}', want '{c.expect}'");
            }

            if (bad.Count > 0)
                return (false, "SECURITY FAIL: " + string.Join("; ", bad.ToArray()));
            return (true, cases.Count + " rows: forgery re-tagged, attested return stripped, " +
                          "no bare adoption");
        }

        // ── Category A: crew ownership decided by ACCOUNT ID ─────────────────────
        //
        // The table above proves the *tagger* refuses a forged claim inside a payload.
        // This one proves the other half: who the server said the craft belongs to.
        // That decision used to be `ownerName == myName`, and both sides of it are
        // Discord display names — self-chosen, changeable, not unique. Setting yours to
        // a victim's and quicksending a vessel whose crew carried plain names made
        // "coming home" true on their client: no tag claimed anything, so the tagger
        // had nothing to refuse, the names arrived bare, and the victim's own kerbals
        // were adopted onto the attacker's hull, to be deleted by the next hand-over.
        //
        // Pure string work — no save, no scene, no network.

        private (bool, string) Test_CrewOwnershipByAccountId()
        {
            // ownerId, myId, ownerName, myName → is this ours, and was it decided on
            // the name because an id was missing?
            var cases = new List<(string label, string ownerId, string myId,
                                  string ownerName, string myName, bool home, bool byName)>
            {
                ("id match",                 "111", "111", "A",      "A",      true,  false),
                // The whole point of an id: the name may have changed since, on either end.
                ("id match, names differ",   "111", "111", "OldA",   "NewA",   true,  false),
                // THE SPOOF. Mallory renamed themselves to the victim and re-linked.
                // Identical display names, different accounts → not ours, do not strip.
                ("SPOOF: names equal, ids differ", "999", "111", "Victim", "Victim", false, false),
                ("id mismatch",              "222", "111", "B",      "A",      false, false),
                // A web account id is not a snowflake; it is opaque and compared ordinally.
                ("id compare is ordinal",    "ABC", "abc", "A",      "A",      false, false),
                ("id match, web account id", "u_7f3a", "u_7f3a", "A", "A",     true,  false),
                // Whitespace never turns into a mismatch, and never into a value either.
                ("id padded",                " 111", "111 ", "A",    "A",      true,  false),
                ("id all whitespace → name", "   ",  "111", "A",     "A",      true,  true),

                // ── the fallback: no id at either end, decide on the name as before ──
                // Entries queued before the server carried owner_id; a contract fetched
                // from a server older than issuer_id/contractor_id on the contract list
                // (the rescue-wreck spawn and the completed-contract craft download both
                // read the id off that list now, so "" there means an old server, not a
                // path that never had one); the browser UI's craft-download bridge, whose
                // page sends a name alone; and a client whose profile has not landed yet.
                ("no owner id → name match",    "",    "111", "A", "A", true,  true),
                ("no owner id → name mismatch", "",    "111", "B", "A", false, true),
                ("no local id yet → name",      "111", "",    "A", "A", true,  true),
                ("neither id → name",           "",    "",    "A", "A", true,  true),
                // Nothing to compare at all is not "ours". Failing *open* here would be
                // adoption by default, which is the capture the ids exist to stop.
                ("neither id, no names",        "",    "",    "",  "",  false, true),
                ("neither id, no owner name",   "",    "",    "",  "A", false, true),
                // Still case-insensitive on the name leg — that half is unchanged.
                ("name fallback ignores case",  "",    "",    "a", "A", true,  true),
            };

            var bad = new List<string>();
            foreach (var c in cases)
            {
                bool byName;
                bool got = VesselTransfer.DecideComingHome(c.ownerId, c.myId, c.ownerName,
                                                           c.myName, out byName);
                if (got != c.home)
                    bad.Add($"{c.label}: home={got}, want {c.home}");
                if (byName != c.byName)
                    bad.Add($"{c.label}: byName={byName}, want {c.byName}");
            }

            if (bad.Count > 0)
                return (false, "SECURITY FAIL: " + string.Join("; ", bad.ToArray()));
            return (true, cases.Count + " rows: the spoof (matching names, different " +
                          "accounts) is refused; a missing id falls back to the name " +
                          "rather than failing closed");
        }

        // ── Category A: adoption refused while the kerbal is busy ────────────────
        //
        // "Coming home" is permission to REUSE a roster entry, not permission to reuse
        // one that is aboard something. The issuer of a rescue can still be holding the
        // original stranded vessel when the delivery lands (its removal deferred because
        // they were flying it, or rolled back by a quickload), and that leg carries no
        // vessel_pid for the upstream guard to catch — so the arrival used to key onto a
        // ProtoCrewMember a live ProtoVessel already referenced: desynced seats and crew
        // counts, and recovering either vessel took the kerbal out of the other.
        //
        // Driven with a null roster and an explicit "currently crewing" set, so it runs
        // with no save loaded. What it CANNOT check is the set itself — whether
        // CrewedNames actually sees a deferred-removal wreck's crew in a real save is a
        // live test, not this one.

        private (bool, string) Test_CrewAdoptionRefusedWhileCrewing()
        {
            var bad = new List<string>();
            var busy = new HashSet<string>(StringComparer.Ordinal) { "Jeb", "A's Bill" };
            var free = new HashSet<string>(StringComparer.Ordinal);

            Func<string, bool, HashSet<string>, string> run = (name, home, crewed) =>
                VesselTransfer.ResolveIncomingCrewName(
                    name, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    home, crewed);

            // The ordinary honest return: the kerbal left this save when the craft was
            // handed over, so nothing here is crewing anything and the name comes back
            // untouched. Breaking THIS is how the last fix round destroyed rosters.
            string r1 = run("Jeb", true, free);
            if (r1 != "Jeb") bad.Add($"honest return: got '{r1}', want 'Jeb'");

            // Same name, but this save still has that Jeb aboard something. Adoption
            // would put two vessels on one kerbal, so the arrival is moved aside.
            string r2 = run("Jeb", true, busy);
            if (r2 == "Jeb") bad.Add("busy return: adopted a kerbal that is crewing something");
            if (string.IsNullOrEmpty(r2)) bad.Add("busy return: produced no name");

            // …and moved aside the way any other collision is: with an ownership prefix,
            // or a bare arrival would read as one of ours and never be swept.
            if (!VesselTransfer.IsBorrowedCrewName(r2))
                bad.Add($"busy return: '{r2}' carries no ownership prefix");

            // A borrowed arrival that is not coming home is unaffected when free…
            string r3 = run("A's Bob", false, free);
            if (r3 != "A's Bob") bad.Add($"free borrowed: got '{r3}', want 'A's Bob'");

            // …and keeps its owner's prefix when it has to be renamed around a busy one.
            string r4 = run("A's Bill", false, busy);
            if (r4 == "A's Bill") bad.Add("busy borrowed: kept a name that is crewing something");
            if (!r4.StartsWith("A's ", StringComparison.Ordinal))
                bad.Add($"busy borrowed: '{r4}' lost the owner prefix");

            // The spoof from the row above, landing where it hurts: an attacker whose
            // display name matches gets comingHome=true on an old, id-less entry — and
            // still cannot take a kerbal who is flying something.
            string r5 = run("Jeb", true, busy);
            if (r5 == "Jeb") bad.Add("spoof onto a busy kerbal: adopted");

            if (bad.Count > 0)
                return (false, "SECURITY FAIL: " + string.Join("; ", bad.ToArray()));
            return (true, "6 checks: a free name is still adopted (the honest return), a " +
                          "name crewing something is renamed aside with its prefix kept");
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
