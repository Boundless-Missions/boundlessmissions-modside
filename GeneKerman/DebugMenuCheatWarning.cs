/*
 * DebugMenuCheatWarning.cs – Warns inside the F12 cheat menu's Set Orbit and
 * Set Position screens that teleporting disqualifies the vessel from contract
 * submission, and hooks their buttons as a second, direct detection channel.
 *
 * The button hook exists because the watchdog's continuity rules and the
 * stock cheats share failure modes discovered the hard way: SetShipOrbit
 * clears vessel.Landed and packs the whole scene before the watchdog's next
 * tick, and "Rendezvous Me" changes only the orbit's phase. The watchdog now
 * judges position-via-orbit (see CheatDetection), but the click itself is the
 * one unambiguous signal — so each teleport button gets a listener that
 * compares the ACTIVE vessel's trajectory against a per-frame pre-click
 * snapshot and taints on a real jump. Note that all three buttons act on the
 * active vessel: the Set Orbit screen's vessel selector only picks the
 * rendezvous TARGET (FlightGlobals.SetShipOrbit / SetShipOrbitRendezvous /
 * SetVesselPosition all read ActiveVessel), a fact checked against the
 * decompiled stock code rather than assumed from the UI. Judgement is by
 * EFFECT: runtime listeners fire in add order, ours after the screen's own,
 * so a click the screen's validation rejected moved nothing and taints
 * nothing.
 *
 * The warning is written into each screen's own errorText slot — the label
 * the screen already positions and styles for its validation messages —
 * rather than a cloned or injected element, because the debug screens are
 * hand-laid prefabs with no layout group to flow an inserted sibling into
 * place. A stock validation message takes the slot back whenever one appears
 * (we only write while it is empty, and hand back the original colour), and a
 * ScreenMessage on first sight of a screen covers the moments the slot is
 * occupied.
 *
 * Discovery is a 2 Hz poll, and it is scoped rather than global. The first
 * version called FindObjectOfType on the belief that Unity indexes objects by
 * type and that "no instances until the tab is opened" made it cheap. Both are
 * wrong: FindObjectOfType walks the whole live-object registry and type-filters
 * it, so a null result is the worst case rather than the free one, and on a
 * heavily modded install that registry is enormous. The cost was a constant,
 * rhythmic hitch, worst in the VAB/SPH because an EveryScene addon is
 * instantiated three times there (see `live`), tripling the scan rate.
 *
 * Instead: DebugScreenSpawner instantiates the debug screen in its own Awake
 * and parents it to itself, so the screen is one cached subtree walk away, its
 * public isShown gates the poll down to a static field read plus a bool while
 * the menu is closed, and the cheat screens are found by walking that subtree.
 * The walk is active-only, so a hit still means "this tab is open" — which is
 * what the old global search was really testing, and what keeps it robust to
 * the screens being created and destroyed as tabs switch. All public API, no
 * reflection.
 */

using System;
using TMPro;
using UnityEngine;
using CheatSetOrbit = KSP.UI.Screens.DebugToolbar.Screens.Cheats.SetOrbit;
using CheatSetPosition = KSP.UI.Screens.DebugToolbar.Screens.Cheats.SetPosition;
using DebugScreen = KSP.UI.Screens.DebugToolbar.DebugScreen;
using DebugScreenSpawner = KSP.UI.Screens.DebugToolbar.DebugScreenSpawner;

namespace GeneKerman
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class DebugMenuCheatWarning : MonoBehaviour
    {
        const float  PollInterval     = 0.5f;
        const float  AnnounceCooldown = 30f;
        const string WarningText =
            "Boundless Missions: teleported vessels are disqualified from contract submission.";
        const string AnnounceText =
            "Boundless Missions: Set Orbit / Set Position taints the vessel - a teleported craft will be disqualified from contract submission.";
        static readonly Color WarnColor = new Color(1.0f, 0.76f, 0.29f);

        // A click moved the active vessel more than this → teleport. The
        // snapshot is at most one frame old, so nothing legitimate crosses it.
        const double ClickJumpFloorM = 2000.0;

        // The one live instance. KSP's AddonLoader calls StartAddons once per
        // matching startup value and the editor matches three of them, so an
        // EveryScene addon is instantiated three times on every VAB/SPH entry
        // while flight and the space centre get one. Three copies is three times
        // the polling and three ScreenMessages, so the first wins and the rest
        // take themselves out.
        private static DebugMenuCheatWarning live;

        private float nextPoll;
        private float nextAnnounce;

        // The debug screen, once it exists. DebugScreenSpawner creates it in its
        // own Awake and parents it to itself, so one walk finds it and it stays
        // put for the life of the scene.
        private DebugScreen screen;

        private CheatSetOrbit    hookedOrbit;
        private Color            orbitErrColor;
        private CheatSetPosition hookedPos;
        private Color            posErrColor;

        // Pre-click snapshot of the ACTIVE vessel's trajectory. Written every
        // frame while either screen is open, so whichever of our Update and the
        // EventSystem's click processing ran first this frame, the snapshot the
        // click listener reads predates the teleport.
        private bool          snapValid;
        private uint          snapPid;
        private CelestialBody snapBody;
        private Orbit         snapOrbit;   // reused instance

        void Awake()
        {
            // Unity's == reads a destroyed instance as null, so a stale `live`
            // from a torn-down scene hands the slot over rather than blocking it.
            if (live != null && !ReferenceEquals(live, this))
            {
                enabled = false;   // Destroy only takes effect at end of frame
                Destroy(this);
                return;
            }
            live = this;
        }

        void OnDestroy()
        {
            if (ReferenceEquals(live, this)) live = null;
        }

        void Update()
        {
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + PollInterval;
                Discover();
            }

            bool anyOpen = false;
            if (hookedOrbit != null)
            {
                AssertWarning(hookedOrbit.errorText, orbitErrColor);
                anyOpen = true;
            }
            if (hookedPos != null)
            {
                AssertWarning(hookedPos.errorText, posErrColor);
                anyOpen = true;
            }

            if (anyOpen) SnapshotActive();
            else snapValid = false;
        }

        private void Discover()
        {
            var root = ResolveScreen();
            if (root == null || !root.isShown)
            {
                // No debug screen, or it is closed. The cheat screens live inside
                // it, so there is nothing to hook and nothing to keep hooked.
                hookedOrbit = null;
                hookedPos   = null;
                return;
            }

            var so = root.GetComponentInChildren<CheatSetOrbit>();
            if (!ReferenceEquals(so, hookedOrbit))
            {
                hookedOrbit = so;
                if (so != null) HookOrbit(so);
            }

            var sp = root.GetComponentInChildren<CheatSetPosition>();
            if (!ReferenceEquals(sp, hookedPos))
            {
                hookedPos = sp;
                if (sp != null) HookPosition(sp);
            }
        }

        private DebugScreen ResolveScreen()
        {
            if (screen != null) return screen;
            try
            {
                var spawner = DebugScreenSpawner.Instance;
                if (spawner == null) return null;
                // includeInactive: the screen hangs off the spawner whether or
                // not it is currently open, and we want it cached either way.
                screen = spawner.GetComponentInChildren<DebugScreen>(true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] DebugMenuCheatWarning: debug screen lookup failed: {ex.Message}");
                screen = null;
            }
            return screen;
        }

        private void HookOrbit(CheatSetOrbit so)
        {
            try
            {
                if (so.errorText != null) orbitErrColor = so.errorText.color;
                // Remove-then-add keeps the hooks idempotent across a screen
                // that is deactivated and reopened rather than destroyed.
                if (so.setOrbitButton != null)
                {
                    so.setOrbitButton.onClick.RemoveListener(OnSetOrbitClicked);
                    so.setOrbitButton.onClick.AddListener(OnSetOrbitClicked);
                }
                if (so.rendezvousButton != null)
                {
                    so.rendezvousButton.onClick.RemoveListener(OnRendezvousClicked);
                    so.rendezvousButton.onClick.AddListener(OnRendezvousClicked);
                }
                Debug.Log("[GeneKerman] F12 Set Orbit screen hooked - teleport warning shown.");
                Announce();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] DebugMenuCheatWarning: Set Orbit hook failed: {ex.Message}");
            }
        }

        private void HookPosition(CheatSetPosition sp)
        {
            try
            {
                if (sp.errorText != null) posErrColor = sp.errorText.color;
                if (sp.setSurfaceButton != null)
                {
                    sp.setSurfaceButton.onClick.RemoveListener(OnSetPositionClicked);
                    sp.setSurfaceButton.onClick.AddListener(OnSetPositionClicked);
                }
                Debug.Log("[GeneKerman] F12 Set Position screen hooked - teleport warning shown.");
                Announce();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] DebugMenuCheatWarning: Set Position hook failed: {ex.Message}");
            }
        }

        private void Announce()
        {
            if (Time.unscaledTime < nextAnnounce) return;
            nextAnnounce = Time.unscaledTime + AnnounceCooldown;
            try
            {
                ScreenMessages.PostScreenMessage(AnnounceText, 6f, ScreenMessageStyle.UPPER_CENTER, WarnColor);
            }
            catch { /* no ScreenMessages in this scene — the errorText slot still warns */ }
        }

        private void AssertWarning(TextMeshProUGUI label, Color origColor)
        {
            if (label == null) return;
            string cur = label.text;
            if (string.IsNullOrEmpty(cur) || cur == WarningText)
            {
                if (cur != WarningText) label.text = WarningText;
                if (label.color != WarnColor) label.color = WarnColor;
                if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
            }
            else if (label.color == WarnColor)
            {
                // A stock validation message took the slot back — return its colour.
                label.color = origColor;
            }
        }

        private void SnapshotActive()
        {
            try
            {
                snapValid = false;
                var v = FlightGlobals.ActiveVessel;
                if (v == null) return;
                var snap = CheatDetection.SnapshotOrbit(v.orbit, snapOrbit);
                if (snap == null) return;
                snapOrbit = snap;
                snapPid   = v.persistentId;
                snapBody  = v.mainBody;
                snapValid = true;
            }
            catch { snapValid = false; }
        }

        private void OnSetOrbitClicked()    => JudgeClick("f12:setorbit",
            "Teleported with the F12 cheat menu's Set Orbit");
        private void OnRendezvousClicked()  => JudgeClick("f12:rendezvous",
            "Teleported to a rendezvous with the F12 cheat menu's Set Orbit");
        private void OnSetPositionClicked() => JudgeClick("f12:setposition",
            "Teleported with the F12 cheat menu's Set Position");

        private void JudgeClick(string key, string reason)
        {
            try
            {
                if (!snapValid) return;
                var v = FlightGlobals.ActiveVessel;
                if (v == null || v.persistentId != snapPid) return;

                bool jumped = v.mainBody != snapBody;
                if (!jumped)
                {
                    if (v.orbit == null || v.orbit.referenceBody == null) return;
                    double ut = Planetarium.GetUniversalTime();
                    jumped = CheatDetection.OrbitPositionDelta(snapOrbit, v.orbit, ut) > ClickJumpFloorM;
                }
                if (jumped)
                    CheatDetection.TaintPid(v.persistentId, v.vesselName, key, reason);
            }
            catch { /* judging a cheat must never break the click */ }
        }
    }
}
