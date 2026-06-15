/*
 * CinematicCapture.cs – "Hero shot" of the active vessel at a milestone.
 *
 * Unlike VesselRenderer (which isolates the vessel onto a blank blueprint),
 * this captures the REAL in-game scene — skybox, the lit vessel, and a nearby
 * object of interest (another vessel, an asteroid/comet, or a celestial body).
 *
 * It briefly drives the flight camera to a computed pose, lets one frame render,
 * grabs it with ScreenCapture, then restores the player's view. The camera is
 * placed on the sunlit side of the vessel so the dominant light source (the home
 * star) falls on the faces pointing at the lens, and framed so the object of
 * interest shares the shot.
 *
 * Flight scene only. Driven as a coroutine from GeneKermanMod.
 */

using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class CinematicCapture
    {
        /// <summary>
        /// Capture a hero shot of the active vessel alongside an object of interest.
        /// Exactly one of <paramref name="targetVessel"/> / <paramref name="targetBody"/>
        /// should be supplied; it frames a paired subject for a vessel and a backdrop
        /// for a celestial body. Invokes <paramref name="onDone"/> with the saved PNG
        /// path (or null on failure).
        /// </summary>
        public static IEnumerator Capture(
            string label, Vessel targetVessel, CelestialBody targetBody,
            Action<string> onDone)
        {
            string path = null;
            try
            {
                path = BuildPath(label);
            }
            catch (Exception ex)
            {
                Debug.LogError("[GeneKerman] Cinematic capture setup failed: " + ex);
            }

            var vessel = FlightGlobals.ActiveVessel;
            var fc = FlightCamera.fetch;
            Camera cam = fc != null ? fc.mainCamera : Camera.main;

            // If anything essential is missing, fall back to a plain screenshot so the
            // player still gets *something* for accepting the prompt.
            if (path == null || vessel == null || cam == null)
            {
                onDone?.Invoke(VesselDataCollector.CaptureScreenshot());
                yield break;
            }

            // ── Compute the camera pose ──
            Vector3 camPos, lookAt, up;
            float fov;
            ComputePose(vessel, targetVessel, targetBody,
                out camPos, out lookAt, out up, out fov);

            // ── Take over the camera for the capture frames ──
            // Disabling the FlightCamera controller stops it from overwriting the
            // transform we set; the ScaledSpace camera still syncs off mainCamera, so
            // distant bodies keep rendering in the backdrop.
            bool fcWasEnabled = fc != null && fc.enabled;
            Vector3 origPos = cam.transform.position;
            Quaternion origRot = cam.transform.rotation;
            float origFov = cam.fieldOfView;

            if (fc != null) fc.enabled = false;
            cam.transform.position = camPos;
            cam.transform.LookAt(lookAt, up);
            cam.fieldOfView = fov;

            // KSP draws distant planets with a SEPARATE ScaledSpace camera. If only the
            // near camera moves, the scaled camera keeps rendering the body from the old
            // pose — leaving a second, offset copy of the planet in the shot (the
            // "duplicate Kerbin"). Disable the ScaledCamera controller so it stops
            // re-syncing to the now-frozen FlightCamera, then drive the scaled camera to
            // the scaled-space equivalent of our pose so both renders line up into one
            // planet.
            Camera scaledCam = ScaledCamera.Instance != null ? ScaledCamera.Instance.cam : null;
            bool scaledWasEnabled = ScaledCamera.Instance != null && ScaledCamera.Instance.enabled;
            Vector3 origScaledPos = default(Vector3);
            Quaternion origScaledRot = default(Quaternion);
            float origScaledFov = 0f;
            if (scaledCam != null)
            {
                origScaledPos = scaledCam.transform.position;
                origScaledRot = scaledCam.transform.rotation;
                origScaledFov = scaledCam.fieldOfView;
                if (ScaledCamera.Instance != null) ScaledCamera.Instance.enabled = false;
                scaledCam.transform.position = ScaledSpace.LocalToScaledSpace(camPos);
                scaledCam.transform.LookAt(ScaledSpace.LocalToScaledSpace(lookAt), up);
                scaledCam.fieldOfView = fov;
            }

            // Hide the HUD so the shot is just the vessel and its backdrop — the same
            // mechanism F2 uses. Restored after the grab.
            SetUIVisible(false);

            // Let this frame render with the new pose, then request the grab. A second
            // end-of-frame wait gives ScreenCapture the frame it queued before we move
            // the camera back.
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForEndOfFrame();
            yield return null;

            // ── Restore the player's view ──
            SetUIVisible(true);
            cam.transform.position = origPos;
            cam.transform.rotation = origRot;
            cam.fieldOfView = origFov;
            if (scaledCam != null)
            {
                scaledCam.transform.position = origScaledPos;
                scaledCam.transform.rotation = origScaledRot;
                scaledCam.fieldOfView = origScaledFov;
                if (ScaledCamera.Instance != null) ScaledCamera.Instance.enabled = scaledWasEnabled;
            }
            if (fc != null) fc.enabled = fcWasEnabled;

            Debug.Log($"[GeneKerman] Cinematic checkpoint shot saved: {path}");
            onDone?.Invoke(path);
        }

        // ── Pose Computation ────────────────────────────────────────────────

        private static void ComputePose(
            Vessel vessel, Vessel targetVessel, CelestialBody targetBody,
            out Vector3 camPos, out Vector3 lookAt, out Vector3 up, out float fov)
        {
            Vector3 vPos = vessel.CoM;
            Vector3 toSun = SunDirection(vPos);
            float radius = VesselRadius(vessel);

            // A stable "up": away from the body we orbit, so the horizon sits level.
            Vector3 upRef = Vector3.up;
            if (vessel.mainBody != null)
            {
                Vector3 fromBody = vPos - (Vector3)vessel.mainBody.position;
                if (fromBody.sqrMagnitude > 1f) upRef = fromBody.normalized;
            }

            if (targetVessel != null)
            {
                // Object mode: frame both craft, viewing across the line between them
                // from the sunlit side so the active vessel's lit face points at us.
                Vector3 tPos = targetVessel.CoM;
                Vector3 axis = tPos - vPos;
                float sep = axis.magnitude;
                Vector3 axisN = axis / Mathf.Max(sep, 0.001f);

                Vector3 camDir = toSun - Vector3.Project(toSun, axisN);
                if (camDir.sqrMagnitude < 0.01f)            // sun ~parallel to the pair
                    camDir = Vector3.Cross(axisN, upRef);
                camDir.Normalize();

                fov = 40f;
                Vector3 mid = (vPos + tPos) * 0.5f;
                float halfExtent = sep * 0.5f + radius + VesselRadius(targetVessel);
                float dist = halfExtent / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.25f;

                camPos = mid + camDir * dist;
                lookAt = mid;
            }
            else if (targetBody != null)
            {
                // Backdrop mode: a three-quarter view with the body sitting behind and
                // to the side of the vessel. We deliberately view from *beside* the
                // vessel→body line rather than straight down it — looking down the
                // radial axis collapses the framing (and made the LookAt roll go wild).
                Vector3 bPos = (Vector3)targetBody.position;
                Vector3 toBody = (bPos - vPos).normalized;

                // Sunlit lateral direction (sun component perpendicular to the body line),
                // so the camera sits on the lit side of the vessel.
                Vector3 sideSun = toSun - Vector3.Project(toSun, toBody);
                if (sideSun.sqrMagnitude < 0.01f)            // sun ~along the body line
                    sideSun = Vector3.Cross(toBody, upRef);
                sideSun.Normalize();

                // Sit mostly on the side of the vessel *opposite* the body, with a small
                // lit-side lateral nudge. This points the view ~16° off the body axis, so
                // the body sits behind the vessel and stays in frame yet clears its
                // silhouette. (The old 0.85/0.5 mix aimed the view ~60° off the body,
                // pushing it out of the shot once the scaled camera tracked the pose.)
                Vector3 camDir = (sideSun * 0.27f - toBody * 0.96f).normalized;

                fov = 42f;
                float dist = radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.4f;
                camPos = vPos + camDir * dist;
                lookAt = vPos;
            }
            else
            {
                // No object of interest: a lit three-quarter portrait of the vessel.
                fov = 35f;
                Vector3 camDir = (toSun + upRef * 0.3f).normalized;
                float dist = radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.2f;
                camPos = vPos + camDir * dist;
                lookAt = vPos;
            }

            // Frame the rocket upright: use its nose (control reference "up") as the
            // camera up so it stands vertically regardless of its real attitude. Bodies
            // in space are spheres, so this introduces no horizon tilt. Guard against the
            // degenerate case where the nose points nearly along the view direction.
            Vector3 viewDir = (lookAt - camPos).normalized;
            Vector3 noseUp = vessel.ReferenceTransform != null
                ? vessel.ReferenceTransform.up
                : (vessel.transform != null ? vessel.transform.up : upRef);
            if (Mathf.Abs(Vector3.Dot(noseUp, viewDir)) > 0.95f) noseUp = upRef;
            if (Mathf.Abs(Vector3.Dot(noseUp, viewDir)) > 0.95f) noseUp = Vector3.Cross(viewDir, Vector3.up);

            up = noseUp;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Show or hide the whole game UI (HUD, toolbar, etc.) for a clean capture —
        /// mirrors the stock F2 toggle. Drives the UI master controller and fires the
        /// onHideUI/onShowUI events so other mods' UI hides too.
        /// </summary>
        private static void SetUIVisible(bool show)
        {
            try
            {
                if (show)
                {
                    KSP.UI.UIMasterController.Instance?.ShowUI();
                    GameEvents.onShowUI.Fire();
                }
                else
                {
                    KSP.UI.UIMasterController.Instance?.HideUI();
                    GameEvents.onHideUI.Fire();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] Could not toggle UI for capture: " + ex.Message);
            }
        }

        /// <summary>World-space unit vector from a point toward the dominant star.</summary>
        private static Vector3 SunDirection(Vector3 from)
        {
            CelestialBody star = null;
            if (Sun.Instance != null) star = Sun.Instance.sun;
            if (star == null && FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0)
                star = FlightGlobals.Bodies[0];

            if (star == null) return Vector3.up;
            Vector3 dir = ((Vector3)star.position - from);
            return dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.up;
        }

        /// <summary>Half the vessel's bounding diagonal, clamped to a sane minimum.</summary>
        private static float VesselRadius(Vessel vessel)
        {
            if (vessel == null) return 5f;
            float r = vessel.vesselSize.magnitude * 0.5f;
            return Mathf.Max(2.5f, r);
        }

        private static string BuildPath(string label)
        {
            string dir = Path.Combine(GeneKermanMod.PluginDataPath, "renders");
            Directory.CreateDirectory(dir);
            string safe = string.Join("_",
                (label ?? "checkpoint").Split(Path.GetInvalidFileNameChars()));
            string filename = $"checkpoint_{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            return Path.Combine(dir, filename);
        }
    }
}
