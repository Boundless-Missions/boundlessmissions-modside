/*
 * VesselRenderer.cs – Blueprint-style multi-view vessel renderer.
 *
 * Captures 8 views of the vessel (6 ortho + 2 perspective), composites
 * them onto a blueprint grid background with labels.
 *
 * Vessel isolation: temporarily moves all part renderers to layer 30,
 * renders with cullingMask = 1<<30, uses magenta chroma key for clean
 * background removal, then restores original layers. Anything drawn without a
 * Renderer can't be moved that way and needs redrawing on the isolation layer —
 * see DecalCapture for ConformalDecals.
 *
 * Works in Editor (VAB/SPH) and Flight scenes.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public static class VesselRenderer
    {
        // ── Image Layout ────────────────────────────────────────────────────
        // SCALE multiplies every pixel dimension so the blueprint renders at higher
        // resolution while keeping the layout proportions identical. SCALE = 2 → 2x quality.
        const int SCALE = 2;
        const int IMG_W = 2048 * SCALE;
        const int IMG_H = 1100 * SCALE;
        const int CELL  = 440 * SCALE;          // Each view cell size
        const int RENDER_SIZE = 440 * SCALE;    // Individual render resolution
        const int THUMB_SIZE = 256 * SCALE;     // Craft-thumbnail render resolution (KSP browser tile)
        const int ISOLATION_LAYER = 30;
        const float PADDING = 1.35f;

        // ── Deferred (blackrack) compatibility ──────────────────────────────
        // Deferred rewrites every stock part/suit shader for its own pipeline and
        // prepares only the cameras it knows about (flight/near/scaled/editor/
        // internal). A third-party camera rendering those shaders in *forward*
        // draws nothing in the flight scene — clears run, zero fragments
        // rasterize — which is exactly the blank-blueprint failure the screenshot
        // fallback catches. The fix is to render the capture camera on the
        // deferred path: Deferred installs its deferred shading shader
        // project-wide, so any camera on that path shades the replaced shaders
        // correctly. Two knock-on effects are handled where they occur: the
        // deferred path ignores MSAA (render at SUPERSAMPLE× and box-filter down
        // instead), and it silently reverts an orthographic camera to forward
        // (the six ortho views become narrow-FOV perspective, FAKE_ORTHO_FOV).
        // Without Deferred the capture keeps its stock forward + MSAA path,
        // untouched.
        const float FAKE_ORTHO_FOV = 2f;  // degrees; near-telecentric, so the
                                          // foreshortening error hides in PADDING
        const int SUPERSAMPLE = 2;        // resolution multiplier standing in for MSAA

        static bool? _deferredPresent;
        static bool DeferredPresent
        {
            get
            {
                if (_deferredPresent == null)
                {
                    try
                    {
                        _deferredPresent = AssemblyLoader.loadedAssemblies
                            .Any(a => a.assembly != null && a.assembly.GetName().Name == "Deferred");
                    }
                    catch { _deferredPresent = false; }
                    if (_deferredPresent == true)
                        Debug.Log("[GeneKerman] Deferred detected — capture cameras will use the deferred rendering path.");
                }
                return _deferredPresent.Value;
            }
        }

        // Monotonic counter to keep render filenames unique within a process.
        static int _renderSeq;

        // Column X positions (4 columns)
        static readonly int[] COL_X = { 64 * SCALE, 544 * SCALE, 1024 * SCALE, 1504 * SCALE };
        // Row Y positions in screen coords (top-down, cell top edge)
        static readonly int[] CELL_Y = { 90 * SCALE, 580 * SCALE };
        // Label Y positions (above cells)
        static readonly int[] LABEL_Y = { 70 * SCALE, 560 * SCALE };

        // ── Colors ──────────────────────────────────────────────────────────
        static readonly Color32 C_BG         = new Color32(13, 27, 42, 255);
        static readonly Color32 C_GRID_MINOR = new Color32(21, 40, 58, 255);
        static readonly Color32 C_GRID_MAJOR = new Color32(30, 55, 82, 255);
        static readonly Color32 C_BORDER     = new Color32(46, 100, 148, 255);
        static readonly Color32 C_LABEL      = new Color32(100, 200, 255, 255);
        static readonly Color32 C_TITLE      = new Color32(140, 215, 255, 255);
        static readonly Color32 C_STATS      = new Color32(70, 150, 200, 255);
        static readonly Color32 C_CELL_BG    = new Color32(8, 18, 30, 255);
        // No chroma key needed — dual-pass black/white recovers true alpha

        // ── View Definition ─────────────────────────────────────────────────
        struct ViewDef
        {
            public string label;
            public Vector3 direction;  // Camera offset direction from center
            public Vector3 up;
            public bool perspective;

            public ViewDef(string l, Vector3 d, Vector3 u, bool p)
            { label = l; direction = d.normalized; up = u; perspective = p; }
        }

        static readonly ViewDef[] VIEWS = {
            // Row 1: Front, Right, Top, NW Perspective
            new ViewDef("FRONT",  new Vector3(0, 0, -1),            Vector3.up,      false),
            new ViewDef("RIGHT",  new Vector3(1, 0, 0),             Vector3.up,      false),
            new ViewDef("TOP",    new Vector3(0, 1, 0),             Vector3.forward, false),
            new ViewDef("NW",     new Vector3(-0.7f, 0.45f, -0.7f), Vector3.up,      true),
            // Row 2: Back, Left, Bottom, SE Perspective
            new ViewDef("BACK",   new Vector3(0, 0, 1),             Vector3.up,      false),
            new ViewDef("LEFT",   new Vector3(-1, 0, 0),            Vector3.up,      false),
            new ViewDef("BOTTOM", new Vector3(0, -1, 0),            Vector3.forward, false),
            new ViewDef("SE",     new Vector3(0.7f, -0.45f, 0.7f),  Vector3.up,      true),
        };

        // ── Public API ──────────────────────────────────────────────────────

        public static string CaptureVessel()
        {
            try
            {
                if (HighLogic.LoadedSceneIsEditor)
                    return CaptureFromEditor();
                else if (HighLogic.LoadedSceneIsFlight)
                    return CaptureFromFlightVessel(FlightGlobals.ActiveVessel);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] Blueprint render failed: {ex}");
            }
            return VesselDataCollector.CaptureScreenshot();
        }

        /// <summary>
        /// Render a specific loaded vessel (not necessarily the active one). Used to
        /// capture blueprints for every craft selected in a multi-vessel submission.
        /// Any vessel in physics range is renderable — its parts are isolated on a
        /// dedicated layer and shot with an off-screen camera, so it need not be active.
        /// </summary>
        public static string CaptureVessel(Vessel vessel)
        {
            try
            {
                if (vessel == null || !HighLogic.LoadedSceneIsFlight)
                    return CaptureVessel();
                return CaptureFromFlightVessel(vessel);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] Blueprint render failed for '{vessel?.vesselName}': {ex}");
                return VesselDataCollector.CaptureScreenshot();
            }
        }

        // ── NW-view craft thumbnail ─────────────────────────────────────────

        /// <summary>
        /// Render just the NW perspective view of the current craft as a square,
        /// transparent-background PNG sized for KSP's craft-browser tile. Used to embed
        /// a thumbnail in a shared .craft so the recipient sees the craft on import
        /// instead of KSP's missing-thumbnail placeholder. Returns null if there's no
        /// renderable craft or the off-screen capture produced nothing.
        /// </summary>
        public static byte[] CaptureNWThumbnail()
        {
            try
            {
                if (HighLogic.LoadedSceneIsEditor)
                {
                    var ship = EditorLogic.fetch?.ship;
                    if (ship == null || ship.parts == null || ship.parts.Count == 0) return null;
                    var renderers = CollectRenderers(ship.parts);
                    if (renderers.Length == 0) return null;
                    return RenderNWThumbnail(renderers, ComputeBounds(renderers),
                        Quaternion.identity, ship.parts);
                }
                if (HighLogic.LoadedSceneIsFlight)
                    return CaptureNWThumbnail(FlightGlobals.ActiveVessel);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] NW thumbnail failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>NW thumbnail for a specific loaded vessel (used for fleet-extra
        /// blueprints in a multi-vessel submission). Returns null on failure.</summary>
        public static byte[] CaptureNWThumbnail(Vessel vessel)
        {
            try
            {
                if (vessel == null || vessel.parts == null || vessel.parts.Count == 0) return null;
                var renderers = CollectRenderers(vessel.parts);
                if (renderers.Length == 0) return null;
                Quaternion rot = vessel.ReferenceTransform != null
                    ? vessel.ReferenceTransform.rotation : vessel.transform.rotation;
                return RenderNWThumbnail(renderers, ComputeBounds(renderers), rot, vessel.parts);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] NW thumbnail failed for '{vessel?.vesselName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Render the single NW view (dual-pass alpha, same as the blueprint)
        /// to a transparent-background PNG. Mirrors RenderBlueprint's isolation/lighting/
        /// camera setup but for one view at thumbnail resolution.</summary>
        private static byte[] RenderNWThumbnail(
            Renderer[] renderers, Bounds bounds, Quaternion vesselRotation, List<Part> isolationParts)
        {
            int layerMask = 1 << ISOLATION_LAYER;
            var origLayers = IsolatePartLayers(isolationParts, ISOLATION_LAYER);

            var origAmbientMode  = RenderSettings.ambientMode;
            var origAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f);
            var fillLightObjects = CreateBlueprintLights(layerMask);

            // Same Deferred handling as RenderBlueprint: deferred path instead of
            // forward (which draws nothing under Deferred's replaced shaders), and
            // supersampling standing in for the MSAA the deferred path ignores.
            int ss = DeferredPresent ? SUPERSAMPLE : 1;
            int renderSize = THUMB_SIZE * ss;
            var rt = new RenderTexture(renderSize, renderSize, 24,
                DeferredPresent ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);
            rt.antiAliasing = DeferredPresent ? 1 : 4;
            rt.Create();
            var resolveRt = new RenderTexture(renderSize, renderSize, 0, RenderTextureFormat.ARGB32);
            resolveRt.antiAliasing = 1;
            resolveRt.Create();

            var camObj = new GameObject("GK_ThumbCam");
            var cam = camObj.AddComponent<Camera>();
            cam.targetTexture = rt;
            cam.cullingMask = layerMask;
            cam.nearClipPlane = 0.01f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowHDR = DeferredPresent;
            cam.allowMSAA = !DeferredPresent;
            if (DeferredPresent)
                cam.renderingPath = RenderingPath.DeferredShading;

            // ConformalDecals decals have no Renderer to isolate — the module draws
            // them per-camera on layer 0, which this camera doesn't see. Redraw them
            // on the isolation layer for as long as the capture runs.
            DecalCapture.BeginCapture(cam, ISOLATION_LAYER, isolationParts);

            var readTex = new Texture2D(renderSize, renderSize, TextureFormat.ARGB32, false);

            // NW view (index 3), rotated into the craft's own frame.
            ViewDef v = VIEWS[3];
            ViewDef nw = new ViewDef(v.label,
                vesselRotation * v.direction, vesselRotation * v.up, v.perspective);
            ConfigureCamera(cam, nw, bounds);

            cam.backgroundColor = Color.black;
            cam.Render();
            Graphics.Blit(rt, resolveRt);
            RenderTexture.active = resolveRt;
            readTex.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0);
            readTex.Apply();
            Color32[] black = Downsample(readTex.GetPixels32(), renderSize, ss);

            cam.backgroundColor = Color.white;
            cam.Render();
            Graphics.Blit(rt, resolveRt);
            RenderTexture.active = resolveRt;
            readTex.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0);
            readTex.Apply();
            Color32[] white = Downsample(readTex.GetPixels32(), renderSize, ss);
            RenderTexture.active = null;

            DecalCapture.EndCapture();  // unhook before the camera it draws for goes away
            UnityEngine.Object.DestroyImmediate(readTex);
            UnityEngine.Object.DestroyImmediate(camObj);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            resolveRt.Release();
            UnityEngine.Object.DestroyImmediate(resolveRt);

            RestorePartLayers(origLayers);
            foreach (var go in fillLightObjects)
                UnityEngine.Object.DestroyImmediate(go);
            RenderSettings.ambientMode  = origAmbientMode;
            RenderSettings.ambientLight = origAmbientLight;

            // Recover straight-alpha RGBA from the dual passes so the craft sits on a
            // transparent tile (the black pass is the foreground premultiplied by alpha).
            var outPix = new Color32[THUMB_SIZE * THUMB_SIZE];
            bool any = false;
            for (int i = 0; i < outPix.Length; i++)
            {
                Color32 bl = black[i], wh = white[i];
                float oma = ((wh.r - bl.r) + (wh.g - bl.g) + (wh.b - bl.b)) / (3f * 255f);
                float a = 1f - oma;
                if (a <= 0.004f) { outPix[i] = new Color32(0, 0, 0, 0); continue; }
                any = true;
                outPix[i] = new Color32(
                    (byte)Math.Min(255, (int)(bl.r / a + 0.5f)),
                    (byte)Math.Min(255, (int)(bl.g / a + 0.5f)),
                    (byte)Math.Min(255, (int)(bl.b / a + 0.5f)),
                    (byte)Math.Min(255, (int)(a * 255f + 0.5f)));
            }
            if (!any)
            {
                Debug.LogWarning("[GeneKerman] NW thumbnail capture produced no craft pixels.");
                return null;
            }

            // Final alignment, in pixel space. The camera fit centres the *geometry*,
            // but perspective foreshortening can still leave the rendered silhouette a
            // few pixels off. Recentre on the actual opaque pixels — find their bounding
            // box and shift it so its centre sits at the canvas centre — which is exact
            // regardless of projection. The PADDING margin guarantees room to shift
            // without clipping.
            outPix = CenterOpaquePixels(outPix, THUMB_SIZE, THUMB_SIZE);

            var tex = new Texture2D(THUMB_SIZE, THUMB_SIZE, TextureFormat.ARGB32, false);
            tex.SetPixels32(outPix);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log($"[GeneKerman] NW thumbnail rendered ({png.Length / 1024}KB).");
            return png;
        }

        /// <summary>Shift a square RGBA buffer so the bounding box of its opaque pixels
        /// is centred on the canvas. Translation only (no scaling); exposed edges become
        /// transparent. Returns the input unchanged if it's empty or already centred.</summary>
        private static Color32[] CenterOpaquePixels(Color32[] pix, int w, int h, byte alphaThreshold = 3)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (pix[row + x].a > alphaThreshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return pix; // fully transparent — nothing to centre

            // Shift the content's bounding-box centre onto the canvas centre.
            int dx = Mathf.RoundToInt((w - 1 - maxX - minX) * 0.5f);
            int dy = Mathf.RoundToInt((h - 1 - maxY - minY) * 0.5f);
            if (dx == 0 && dy == 0) return pix;

            var outPix = new Color32[pix.Length]; // zero-init → transparent
            for (int y = 0; y < h; y++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= h) continue;
                int srcRow = y * w;
                int dstRow = ny * w;
                for (int x = 0; x < w; x++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= w) continue;
                    outPix[dstRow + nx] = pix[srcRow + x];
                }
            }
            return outPix;
        }

        // ── Scene Capture Entry Points ──────────────────────────────────────

        private static string CaptureFromEditor()
        {
            var ship = EditorLogic.fetch?.ship;
            if (ship == null || ship.parts == null || ship.parts.Count == 0)
                return VesselDataCollector.CaptureScreenshot();

            var renderers = CollectRenderers(ship.parts);
            if (renderers.Length == 0) return VesselDataCollector.CaptureScreenshot();

            Bounds bounds = ComputeBounds(renderers);
            string name = ship.shipName ?? "Vessel";
            int partCount = ship.parts.Count;
            float mass = 0;
            foreach (var p in ship.parts)
                mass += p.mass + p.GetResourceMass();
            // Full funds cost (dry + module modifiers incl. TweakScale + fuel) — not the
            // bare prefab cost. See VesselDataCollector.GetPartCost.
            float cost = VesselDataCollector.GetVesselCost(ship.parts);

            // Editor: vessel is aligned to world axes
            return RenderBlueprint(renderers, bounds, Quaternion.identity,
                name, partCount, mass, cost, ship.parts);
        }

        private static string CaptureFromFlightVessel(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count == 0)
                return VesselDataCollector.CaptureScreenshot();

            var renderers = CollectRenderers(vessel.parts);
            if (renderers.Length == 0) return VesselDataCollector.CaptureScreenshot();

            Bounds bounds = ComputeBounds(renderers);

            // Use the vessel's root transform rotation so views are
            // relative to the vessel's own orientation, not world axes
            Quaternion vesselRot = vessel.ReferenceTransform != null
                ? vessel.ReferenceTransform.rotation
                : vessel.transform.rotation;

            return RenderBlueprint(renderers, bounds, vesselRot,
                vessel.vesselName ?? "Vessel",
                vessel.parts.Count,
                (float)vessel.totalMass,
                0f, vessel.parts);
        }

        // ── Core Rendering ──────────────────────────────────────────────────

        private static string RenderBlueprint(
            Renderer[] renderers, Bounds bounds, Quaternion vesselRotation,
            string vesselName, int partCount, float mass, float cost,
            List<Part> isolationParts)
        {
            int layerMask = 1 << ISOLATION_LAYER;

            // ── Isolate vessel on dedicated layer ──
            // Use part-level isolation to avoid duplicate-gameObject layer bugs.
            // Isolate exactly the parts we're rendering (the target vessel), which
            // may be a nearby craft rather than the active one.
            List<Part> parts = isolationParts;
            var origLayers = IsolatePartLayers(parts, ISOLATION_LAYER);

            // Replace scene lighting with uniform fill lights so all 8 views
            // receive equal illumination regardless of the in-game sun angle.
            var origAmbientMode  = RenderSettings.ambientMode;
            var origAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f);
            var fillLightObjects = CreateBlueprintLights(layerMask);

            // ── Set up shared render resources ──
            // Under Deferred the deferred path ignores MSAA, so render at
            // SUPERSAMPLE× and box-filter down after readback — that restores the
            // fractional edge coverage the dual-pass alpha math turns into soft
            // edges, same as the MSAA resolve does on the forward path.
            int ss = DeferredPresent ? SUPERSAMPLE : 1;
            int renderSize = RENDER_SIZE * ss;
            // The deferred path must run HDR: Deferred forces HDR on the cameras it
            // manages and its replacement lighting shaders assume it, so an LDR
            // camera takes the logLuv-encoded lighting branch and resolves every
            // lit fragment to black (geometry and alpha survive — the blueprint
            // comes out as unlit silhouettes). Render to an HDR target and let the
            // resolve blit below clamp back down to ARGB32 for readback.
            var rt = new RenderTexture(renderSize, renderSize, 24,
                DeferredPresent ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);
            rt.antiAliasing = DeferredPresent ? 1 : 4;
            rt.Create();

            // ReadPixels cannot read directly from an MSAA RenderTexture on many
            // GPUs/drivers — it returns only the clear color, so every view comes
            // back blank while the CPU-drawn grid/labels still appear. Resolve the
            // MSAA target into this plain (non-MSAA) texture before reading back.
            // (For the non-MSAA deferred path the blit is a plain copy — harmless,
            // and it keeps the two paths' readback chains identical.)
            var resolveRt = new RenderTexture(renderSize, renderSize, 0, RenderTextureFormat.ARGB32);
            resolveRt.antiAliasing = 1;
            resolveRt.Create();

            var camObj = new GameObject("GK_BlueprintCam");
            var cam = camObj.AddComponent<Camera>();
            cam.targetTexture = rt;
            cam.cullingMask = layerMask;
            cam.nearClipPlane = 0.01f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowHDR = DeferredPresent;
            cam.allowMSAA = !DeferredPresent;
            if (DeferredPresent)
                cam.renderingPath = RenderingPath.DeferredShading;

            // ConformalDecals decals have no Renderer to isolate — the module draws
            // them per-camera on layer 0, which this camera doesn't see. Redraw them
            // on the isolation layer for as long as the capture runs.
            DecalCapture.BeginCapture(cam, ISOLATION_LAYER, parts);

            var readTex = new Texture2D(renderSize, renderSize, TextureFormat.ARGB32, false);

            // ── Capture all 8 views (dual-pass: black + white background) ──
            // Rendering against two known backgrounds lets us recover exact alpha:
            //   alpha = 1 - avg(white - black) / 255
            Color32[][] blackPass = new Color32[8][];
            Color32[][] whitePass = new Color32[8][];

            for (int i = 0; i < 8; i++)
            {
                // Rotate view directions by vessel orientation so FRONT=vessel front
                ViewDef v = VIEWS[i];
                ViewDef rotated = new ViewDef(
                    v.label,
                    vesselRotation * v.direction,
                    vesselRotation * v.up,
                    v.perspective
                );
                ConfigureCamera(cam, rotated, bounds);

                // Black pass
                cam.backgroundColor = Color.black;
                cam.Render();
                Graphics.Blit(rt, resolveRt);  // resolve MSAA → plain so ReadPixels works
                RenderTexture.active = resolveRt;
                readTex.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0);
                readTex.Apply();
                blackPass[i] = Downsample(readTex.GetPixels32(), renderSize, ss);

                // White pass
                cam.backgroundColor = Color.white;
                cam.Render();
                Graphics.Blit(rt, resolveRt);
                RenderTexture.active = resolveRt;
                readTex.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0);
                readTex.Apply();
                whitePass[i] = Downsample(readTex.GetPixels32(), renderSize, ss);

                RenderTexture.active = null;

                // Pixel-space recentre for the perspective cells (NW/SE) — the same
                // exact alignment used for the standalone NW thumbnail. Ortho cells are
                // already centred by their projection, so they're left untouched (and a
                // shift could clip their tighter framing).
                if (VIEWS[i].perspective)
                    CenterViewPasses(blackPass[i], whitePass[i], RENDER_SIZE);
            }

            // ── Cleanup render resources ──
            DecalCapture.EndCapture();  // unhook before the camera it draws for goes away
            UnityEngine.Object.DestroyImmediate(readTex);
            UnityEngine.Object.DestroyImmediate(camObj);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            resolveRt.Release();
            UnityEngine.Object.DestroyImmediate(resolveRt);

            // ── Restore layers and lighting ──
            RestorePartLayers(origLayers);
            foreach (var go in fillLightObjects)
                UnityEngine.Object.DestroyImmediate(go);
            RenderSettings.ambientMode  = origAmbientMode;
            RenderSettings.ambientLight = origAmbientLight;

            // ── Safety net: if the off-screen camera captured nothing (e.g. an
            // unsupported readback path on some GPU/driver), every view is pure
            // background. Rather than submit a blank blueprint, fall back to a
            // normal in-game screenshot so a submission is never empty.
            if (!HasVesselContent(blackPass, whitePass))
            {
                Debug.LogWarning("[GeneKerman] Blueprint capture produced no vessel pixels — falling back to a plain screenshot."
                    + (DeferredPresent ? " Deferred is installed and its compatibility path still drew nothing." : ""));
                return VesselDataCollector.CaptureScreenshot();
            }

            // ── Composite blueprint image ──
            Color32[] blueprint = new Color32[IMG_W * IMG_H];
            DrawBlueprintBackground(blueprint);
            DrawCellBackgrounds(blueprint);

            // Blit each view with mathematically correct alpha
            for (int i = 0; i < 8; i++)
            {
                int col = i % 4;
                int row = i / 4;
                BlitView(blueprint, blackPass[i], whitePass[i], COL_X[col], CELL_Y[row]);
                DrawCellBorder(blueprint, COL_X[col], CELL_Y[row]);
                DrawString(blueprint, VIEWS[i].label, COL_X[col], LABEL_Y[row], 2 * SCALE, C_LABEL);
            }

            // Title and stats
            DrawString(blueprint, vesselName.ToUpper(), 64 * SCALE, 18 * SCALE, 3 * SCALE, C_TITLE);
            string stats = $"PARTS:{partCount}  MASS:{mass:F1}T";
            if (cost > 0) stats += $"  COST:{cost:N0}";
            DrawString(blueprint, stats, 64 * SCALE, IMG_H - 28 * SCALE, 2 * SCALE, C_STATS);

            // Outer frame
            DrawFrameBorder(blueprint);

            // ── Save ──
            var finalTex = new Texture2D(IMG_W, IMG_H, TextureFormat.ARGB32, false);
            finalTex.SetPixels32(blueprint);
            finalTex.Apply();
            byte[] png = finalTex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(finalTex);

            string dir = Path.Combine(GeneKermanMod.PluginDataPath, "renders");
            Directory.CreateDirectory(dir);
            string safeName = string.Join("_", vesselName.Split(Path.GetInvalidFileNameChars()));
            // Include a per-process sequence so two vessels captured in the same second
            // (e.g. two same-named probes in a multi-craft submission) don't collide on
            // the timestamp and overwrite each other's render.
            string filename = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}_{++_renderSeq}.png";
            string path = Path.Combine(dir, filename);
            File.WriteAllBytes(path, png);

            Debug.Log($"[GeneKerman] Blueprint saved: {path} ({png.Length / 1024}KB)");
            return path;
        }

        // ── Camera Configuration ────────────────────────────────────────────

        private static void ConfigureCamera(Camera cam, ViewDef view, Bounds bounds)
        {
            Vector3 center = bounds.center;
            Vector3 ext = bounds.extents;

            // Compute the projected half-size on the view's image plane.
            // The camera looks along -forward, so the visible area is
            // determined by the bounds' extents projected onto the camera's
            // right and up axes.
            Vector3 forward = -view.direction;
            Vector3 right = Vector3.Cross(view.up, forward).normalized;
            Vector3 up = Vector3.Cross(forward, right).normalized;

            // Project the AABB extents onto camera right/up to get half-widths
            float halfW = Mathf.Abs(ext.x * right.x) + Mathf.Abs(ext.y * right.y) + Mathf.Abs(ext.z * right.z);
            float halfH = Mathf.Abs(ext.x * up.x) + Mathf.Abs(ext.y * up.y) + Mathf.Abs(ext.z * up.z);
            float viewSize = Mathf.Max(halfW, halfH);
            if (viewSize < 0.01f) viewSize = 1f;

            // Depth along view direction for far clip
            float depth = Mathf.Abs(ext.x * forward.x) + Mathf.Abs(ext.y * forward.y) + Mathf.Abs(ext.z * forward.z);
            float dist = Mathf.Max(viewSize, depth) * 4f;

            // The fake-ortho branch below sets a per-view near plane on the shared
            // camera; reset it here so the views configured after it don't inherit
            // one large enough to clip them out entirely.
            cam.nearClipPlane = 0.01f;

            if (!view.perspective)
            {
                if (DeferredPresent)
                {
                    // Unity's deferred path silently reverts an orthographic camera
                    // to forward — under Deferred's replaced shaders, exactly the
                    // path that draws nothing. Stand a narrow-FOV perspective camera
                    // far back instead: at FAKE_ORTHO_FOV the foreshortening across
                    // the craft's depth stays inside the PADDING margin, so the
                    // framing matches the true ortho cell it replaces. The tight
                    // near/far pair keeps depth precision despite the distance.
                    cam.orthographic = false;
                    cam.fieldOfView = FAKE_ORTHO_FOV;
                    float fakeDist = viewSize * PADDING
                                     / Mathf.Tan(FAKE_ORTHO_FOV * 0.5f * Mathf.Deg2Rad);
                    cam.transform.position = center + view.direction * fakeDist;
                    cam.transform.LookAt(center, view.up);
                    cam.nearClipPlane = Mathf.Max(0.05f, fakeDist - depth * 2f);
                    cam.farClipPlane = fakeDist + depth * 2f + 1f;
                    return;
                }

                // Orthographic: the AABB centre projects exactly to the image centre
                // and viewSize is the exact projected half-extent, so a single LookAt
                // + orthographicSize frames it tight and centred.
                cam.orthographic = true;
                cam.farClipPlane = dist * 3f;
                cam.transform.position = center + view.direction * dist;
                cam.transform.LookAt(center, view.up);
                cam.orthographicSize = viewSize * PADDING;
                return;
            }

            // Perspective (NW/SE): aiming at the AABB centre is *not* enough here —
            // foreshortening shifts the silhouette's visual mass toward the parts
            // nearest the camera, and a fixed-distance heuristic frames each craft
            // differently, so long craft seen diagonally end up visibly off-centre
            // with uneven margins. Instead, fit + centre in screen space: project the
            // 8 AABB corners, then iteratively pan the look target so the projected
            // box centres on (0.5, 0.5) and scale the distance so it fills the frame
            // with the same PADDING margin as the ortho cells. A few passes converge.
            cam.orthographic = false;
            cam.fieldOfView = 30f;

            Vector3[] corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = center + new Vector3(
                    (i & 1) == 0 ? -ext.x : ext.x,
                    (i & 2) == 0 ? -ext.y : ext.y,
                    (i & 4) == 0 ? -ext.z : ext.z);

            Vector3 pivot = center;
            float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

            // Pass A — fit the distance (this also rough-centres as it goes). Angular
            // size scales ~1/dist, so pushing the camera out by (span * PADDING) makes
            // the projected box fill 1/PADDING of the frame. Converges in 2–3 passes.
            for (int pass = 0; pass < 4; pass++)
            {
                cam.transform.position = pivot + view.direction * dist;
                cam.transform.LookAt(pivot, view.up);

                Vector4 box = ProjectedBox(cam, corners);
                float span = Mathf.Max(box.z - box.x, box.w - box.y);
                if (span < 0.0001f) break;

                RecentrePivot(ref pivot, box, dist, tanHalfFov, right, up, cam.aspect);
                dist *= span * PADDING;
            }

            // Pass B — centre at the *final* distance. The last resize in pass A nudges
            // the perspective projection's centre back off (0.5, 0.5), so the centring
            // has to come last, with the distance fixed. A single pan isn't exact
            // (corners at different depths shift by different viewport amounts), so
            // iterate a couple of times to converge.
            for (int pass = 0; pass < 3; pass++)
            {
                cam.transform.position = pivot + view.direction * dist;
                cam.transform.LookAt(pivot, view.up);
                RecentrePivot(ref pivot, ProjectedBox(cam, corners), dist, tanHalfFov, right, up, cam.aspect);
            }

            cam.transform.position = pivot + view.direction * dist;
            cam.transform.LookAt(pivot, view.up);
            cam.farClipPlane = (dist + depth) * 3f;
        }

        /// <summary>Viewport-space bounding box (minX, minY, maxX, maxY) of the 8 world
        /// corners as seen by the camera. ±Infinity sentinels so a silhouette that
        /// temporarily spills outside [0,1] mid-iteration is still measured correctly.</summary>
        private static Vector4 ProjectedBox(Camera cam, Vector3[] corners)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 vp = cam.WorldToViewportPoint(corners[i]);
                if (vp.x < minX) minX = vp.x;
                if (vp.x > maxX) maxX = vp.x;
                if (vp.y < minY) minY = vp.y;
                if (vp.y > maxY) maxY = vp.y;
            }
            return new Vector4(minX, minY, maxX, maxY);
        }

        /// <summary>Pan the look target so the projected box's centre moves toward the
        /// image centre. The viewport offset is converted to a world shift using the
        /// frustum size at the pivot's depth; panning +right moves the silhouette left,
        /// hence the subtraction.</summary>
        private static void RecentrePivot(ref Vector3 pivot, Vector4 box, float dist,
            float tanHalfFov, Vector3 right, Vector3 up, float aspect)
        {
            float frustumH = 2f * dist * tanHalfFov;
            float frustumW = frustumH * aspect;
            float cx = (box.x + box.z) * 0.5f;
            float cy = (box.y + box.w) * 0.5f;
            pivot -= right * ((0.5f - cx) * frustumW) + up * ((0.5f - cy) * frustumH);
        }

        // ── Layer Isolation ─────────────────────────────────────────────────

        private static Renderer[] CollectRenderers(List<Part> parts)
        {
            return parts
                .SelectMany(p => p.GetComponentsInChildren<Renderer>(false))
                .Where(r => r.enabled && r.gameObject.activeInHierarchy
                    && (r is MeshRenderer || r is SkinnedMeshRenderer)
                    && r.bounds.size.sqrMagnitude > 0.0001f)
                .ToArray();
        }

        private static Bounds ComputeBounds(Renderer[] renderers)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        private static List<Part> GetCurrentParts()
        {
            if (HighLogic.LoadedSceneIsEditor)
                return EditorLogic.fetch?.ship?.parts;
            if (HighLogic.LoadedSceneIsFlight)
                return FlightGlobals.ActiveVessel?.parts;
            return null;
        }

        /// <summary>
        /// Move ALL child GameObjects of every part to the isolation layer.
        /// Uses a Dictionary keyed by GameObject instance so each object
        /// is stored exactly once — even if multiple renderers share it.
        /// </summary>
        private static Dictionary<GameObject, int> IsolatePartLayers(List<Part> parts, int layer)
        {
            var orig = new Dictionary<GameObject, int>();
            if (parts == null) return orig;

            foreach (var part in parts)
            {
                if (part == null || part.gameObject == null) continue;
                foreach (var t in part.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if (!orig.ContainsKey(go))
                    {
                        orig[go] = go.layer;
                        go.layer = layer;
                    }
                }
            }
            return orig;
        }

        private static void RestorePartLayers(Dictionary<GameObject, int> orig)
        {
            foreach (var kvp in orig)
            {
                if (kvp.Key != null)  // Guard against destroyed objects
                    kvp.Key.layer = kvp.Value;
            }
        }

        // Six lights: three from above (+35°) and three from below (-35°) at 120° azimuth
        // intervals so every surface normal receives direct illumination regardless of view.
        private static GameObject[] CreateBlueprintLights(int layerMask)
        {
            float[] elevations = {  35f,  35f,  35f, -35f, -35f, -35f };
            float[] azimuths   = {   0f, 120f, 240f,   0f, 120f, 240f };
            var objects = new GameObject[6];
            for (int i = 0; i < 6; i++)
            {
                var go = new GameObject("GK_FillLight");
                var l  = go.AddComponent<Light>();
                l.type        = LightType.Directional;
                l.intensity   = 0.6f;
                l.color       = Color.white;
                l.cullingMask = layerMask;
                l.shadows     = LightShadows.None;
                go.transform.rotation = Quaternion.Euler(elevations[i], azimuths[i], 0f);
                objects[i] = go;
            }
            return objects;
        }

        // ── Blueprint Compositing ───────────────────────────────────────────

        private static void DrawBlueprintBackground(Color32[] px)
        {
            // Fill base color
            for (int i = 0; i < px.Length; i++)
                px[i] = C_BG;

            // Grid lines (in texture coords: y=0 is bottom)
            for (int ty = 0; ty < IMG_H; ty++)
            {
                int sy = IMG_H - 1 - ty;  // screen Y
                bool majorH = sy % (128 * SCALE) == 0;
                bool minorH = sy % (20 * SCALE) == 0;

                for (int x = 0; x < IMG_W; x++)
                {
                    bool majorV = x % (128 * SCALE) == 0;
                    bool minorV = x % (20 * SCALE) == 0;

                    if (majorH || majorV)
                        px[ty * IMG_W + x] = C_GRID_MAJOR;
                    else if (minorH || minorV)
                        px[ty * IMG_W + x] = C_GRID_MINOR;
                }
            }
        }

        private static void DrawCellBackgrounds(Color32[] px)
        {
            for (int i = 0; i < 8; i++)
            {
                int col = i % 4;
                int row = i / 4;
                FillRect(px, COL_X[col], CELL_Y[row], CELL, CELL, C_CELL_BG);
            }
        }

        /// <summary>
        /// Composite a view onto the blueprint using dual-pass alpha recovery.
        /// Math: on black BG, pixel = vessel*a. On white BG, pixel = vessel*a + 255*(1-a).
        /// So (1-a) = avg(white - black) / 255, and final = black + blueprint*(1-a).
        /// </summary>
        /// <summary>Recentre a perspective view's dual-pass renders in pixel space so the
        /// silhouette sits centred in its cell. The camera fit centres the geometry, but
        /// perspective foreshortening can still leave it a few pixels off. Finds the opaque
        /// bounding box (from the two background passes) and shifts both passes by the same
        /// amount, filling exposed edges with each pass's own background so alpha recovery
        /// still reads them as empty. Translation only; no scaling.</summary>
        private static void CenterViewPasses(Color32[] black, Color32[] white, int size)
        {
            int minX = size, minY = size, maxX = -1, maxY = -1;
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    int i = row + x;
                    Color32 bl = black[i], wh = white[i];
                    // (1 - alpha): pure background ≈ 1, vessel pixel < 1. Same recovery
                    // BlitView uses, so the bbox matches exactly what gets composited.
                    float oma = ((wh.r - bl.r) + (wh.g - bl.g) + (wh.b - bl.b)) / (3f * 255f);
                    if (oma < 0.99f)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return; // empty cell — nothing to centre

            int dx = Mathf.RoundToInt((size - 1 - maxX - minX) * 0.5f);
            int dy = Mathf.RoundToInt((size - 1 - maxY - minY) * 0.5f);
            if (dx == 0 && dy == 0) return;

            ShiftPass(black, size, dx, dy, new Color32(0, 0, 0, 255));
            ShiftPass(white, size, dx, dy, new Color32(255, 255, 255, 255));
        }

        /// <summary>Shift a square pixel buffer by (dx, dy), filling exposed edges with
        /// <paramref name="fill"/> (each pass's background colour).</summary>
        private static void ShiftPass(Color32[] pass, int size, int dx, int dy, Color32 fill)
        {
            var src = (Color32[])pass.Clone();
            for (int y = 0; y < size; y++)
            {
                int dstRow = y * size;
                int sy = y - dy;
                for (int x = 0; x < size; x++)
                {
                    int sx = x - dx;
                    pass[dstRow + x] = (sx >= 0 && sx < size && sy >= 0 && sy < size)
                        ? src[sy * size + sx]
                        : fill;
                }
            }
        }

        private static void BlitView(Color32[] dst, Color32[] black, Color32[] white,
            int cellX, int cellScreenY)
        {
            int baseTexY = IMG_H - cellScreenY - RENDER_SIZE;

            for (int vy = 0; vy < RENDER_SIZE; vy++)
            {
                int dstY = baseTexY + vy;
                if (dstY < 0 || dstY >= IMG_H) continue;

                for (int vx = 0; vx < RENDER_SIZE; vx++)
                {
                    int dstX = cellX + vx;
                    if (dstX < 0 || dstX >= IMG_W) continue;

                    int si = vy * RENDER_SIZE + vx;
                    Color32 bl = black[si];
                    Color32 wh = white[si];

                    // Recover (1 - alpha) from the two passes
                    float oma = ((wh.r - bl.r) + (wh.g - bl.g) + (wh.b - bl.b)) / (3f * 255f);

                    if (oma > 0.99f) continue;  // Pure background

                    int idx = dstY * IMG_W + dstX;

                    if (oma < 0.01f)
                    {
                        // Fully opaque vessel pixel
                        dst[idx] = bl;
                    }
                    else
                    {
                        // Semi-transparent edge: final = black_render + bg * (1-alpha)
                        Color32 bg = dst[idx];
                        dst[idx] = new Color32(
                            (byte)Math.Min(255, (int)(bl.r + bg.r * oma + 0.5f)),
                            (byte)Math.Min(255, (int)(bl.g + bg.g * oma + 0.5f)),
                            (byte)Math.Min(255, (int)(bl.b + bg.b * oma + 0.5f)),
                            255
                        );
                    }
                }
            }
        }

        /// <summary>Box-filter a square Color32 buffer down by an integer factor —
        /// the CPU stand-in for the MSAA resolve the deferred path can't provide.
        /// A factor of 1 returns the buffer unchanged (the forward/MSAA path).</summary>
        private static Color32[] Downsample(Color32[] src, int srcSize, int factor)
        {
            if (factor <= 1) return src;
            int dstSize = srcSize / factor;
            var dst = new Color32[dstSize * dstSize];
            int samples = factor * factor;
            for (int y = 0; y < dstSize; y++)
            {
                for (int x = 0; x < dstSize; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < factor; sy++)
                    {
                        int srcRow = (y * factor + sy) * srcSize + x * factor;
                        for (int sx = 0; sx < factor; sx++)
                        {
                            Color32 c = src[srcRow + sx];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                        }
                    }
                    dst[y * dstSize + x] = new Color32(
                        (byte)(r / samples), (byte)(g / samples),
                        (byte)(b / samples), (byte)(a / samples));
                }
            }
            return dst;
        }

        /// <summary>
        /// True if any captured view contains vessel pixels (not pure background).
        /// Uses the same dual-pass test as BlitView: a pixel is background when
        /// (white - black) averages ~255 across channels.
        /// </summary>
        private static bool HasVesselContent(Color32[][] black, Color32[][] white)
        {
            for (int i = 0; i < black.Length; i++)
            {
                Color32[] bl = black[i];
                Color32[] wh = white[i];
                if (bl == null || wh == null) continue;
                for (int p = 0; p < bl.Length; p++)
                {
                    float oma = ((wh[p].r - bl[p].r) + (wh[p].g - bl[p].g) + (wh[p].b - bl[p].b)) / (3f * 255f);
                    if (oma <= 0.99f) return true;  // found a vessel pixel
                }
            }
            return false;
        }

        private static void DrawCellBorder(Color32[] px, int sx, int sy)
        {
            // SCALE-px border around each cell (1px at base resolution)
            for (int t = 0; t < SCALE; t++)
            {
                for (int x = sx - 1 - t; x <= sx + CELL + t; x++)
                {
                    SetPx(px, x, sy - 1 - t, C_BORDER);
                    SetPx(px, x, sy + CELL + t, C_BORDER);
                }
                for (int y = sy - 1 - t; y <= sy + CELL + t; y++)
                {
                    SetPx(px, sx - 1 - t, y, C_BORDER);
                    SetPx(px, sx + CELL + t, y, C_BORDER);
                }
            }
        }

        private static void DrawFrameBorder(Color32[] px)
        {
            // 2px frame around entire image, scaled with resolution
            for (int t = 0; t < 2 * SCALE; t++)
            {
                for (int x = 0; x < IMG_W; x++)
                {
                    SetPx(px, x, t, C_BORDER);
                    SetPx(px, x, IMG_H - 1 - t, C_BORDER);
                }
                for (int y = 0; y < IMG_H; y++)
                {
                    SetPx(px, t, y, C_BORDER);
                    SetPx(px, IMG_W - 1 - t, y, C_BORDER);
                }
            }
        }

        // ── Drawing Primitives ──────────────────────────────────────────────

        private static void SetPx(Color32[] px, int sx, int sy, Color32 c)
        {
            int ty = IMG_H - 1 - sy;
            if (ty >= 0 && ty < IMG_H && sx >= 0 && sx < IMG_W)
                px[ty * IMG_W + sx] = c;
        }

        private static void FillRect(Color32[] px, int sx, int sy, int w, int h, Color32 c)
        {
            for (int dy = 0; dy < h; dy++)
            {
                int ty = IMG_H - 1 - (sy + dy);
                if (ty < 0 || ty >= IMG_H) continue;
                for (int dx = 0; dx < w; dx++)
                {
                    int tx = sx + dx;
                    if (tx >= 0 && tx < IMG_W)
                        px[ty * IMG_W + tx] = c;
                }
            }
        }

        // ── Bitmap Font ─────────────────────────────────────────────────────
        // 5×7 pixel font. Each byte = 5 columns (MSB = left), 7 rows per char.

        private static void DrawString(Color32[] px, string text, int sx, int sy,
            int scale, Color32 color)
        {
            int cx = sx;
            foreach (char ch in text)
            {
                char upper = char.ToUpper(ch);
                if (upper == ' ')
                {
                    cx += 4 * scale;
                    continue;
                }

                byte[] glyph;
                if (_font.TryGetValue(upper, out glyph))
                {
                    for (int row = 0; row < 7; row++)
                    {
                        byte bits = glyph[row];
                        for (int col = 0; col < 5; col++)
                        {
                            if ((bits & (0x10 >> col)) != 0)
                            {
                                // Draw scaled pixel
                                for (int py = 0; py < scale; py++)
                                    for (int px2 = 0; px2 < scale; px2++)
                                        SetPx(px, cx + col * scale + px2,
                                              sy + row * scale + py, color);
                            }
                        }
                    }
                }
                cx += 6 * scale;  // 5px char + 1px spacing, scaled
            }
        }

        /// <summary>
        /// Render a short text label centred on a square, opaque Texture2D using the
        /// built-in 5×7 bitmap font. Shared by the editor View Cube (UI/ViewCube.cs) so it
        /// can label cube faces without duplicating the font. Letters are scaled to fit the
        /// requested size. Texture coords have y=0 at the bottom, so rows are flipped.
        /// </summary>
        public static Texture2D RenderLabelTexture(string text, int size, Color32 fg, Color32 bg)
        {
            text = (text ?? "").ToUpper();

            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            // Choose a scale so the glyph string fits inside the texture with margin.
            int glyphCount = Math.Max(1, text.Length);
            int unscaledW = glyphCount * 6 - 1;   // 5px glyph + 1px spacing, minus trailing
            const int unscaledH = 7;
            int margin = Math.Max(1, size / 8);
            int avail = size - 2 * margin;
            int scale = Math.Max(1, Math.Min(avail / Math.Max(1, unscaledW), avail / unscaledH));

            int textW = unscaledW * scale;
            int textH = unscaledH * scale;
            int ox = (size - textW) / 2;          // left edge of text block (screen coords)
            int oyTop = (size - textH) / 2;        // top edge (screen coords, y down)

            int cx = ox;
            foreach (char ch in text)
            {
                if (ch == ' ') { cx += 4 * scale; continue; }
                byte[] glyph;
                if (_font.TryGetValue(ch, out glyph))
                {
                    for (int row = 0; row < 7; row++)
                    {
                        byte bits = glyph[row];
                        for (int col = 0; col < 5; col++)
                        {
                            if ((bits & (0x10 >> col)) == 0) continue;
                            for (int py = 0; py < scale; py++)
                                for (int pxx = 0; pxx < scale; pxx++)
                                {
                                    int sx = cx + col * scale + pxx;
                                    int sy = oyTop + row * scale + py;  // screen y (down)
                                    int ty = size - 1 - sy;             // texture y (up)
                                    if (sx >= 0 && sx < size && ty >= 0 && ty < size)
                                        px[ty * size + sx] = fg;
                                }
                        }
                    }
                }
                cx += 6 * scale;
            }

            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.SetPixels32(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply();
            return tex;
        }

        // ── Font Data ───────────────────────────────────────────────────────

        private static readonly Dictionary<char, byte[]> _font = new Dictionary<char, byte[]>
        {
            {'A', new byte[]{0x0E,0x11,0x11,0x1F,0x11,0x11,0x11}},
            {'B', new byte[]{0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E}},
            {'C', new byte[]{0x0E,0x11,0x10,0x10,0x10,0x11,0x0E}},
            {'D', new byte[]{0x1C,0x12,0x11,0x11,0x11,0x12,0x1C}},
            {'E', new byte[]{0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F}},
            {'F', new byte[]{0x1F,0x10,0x10,0x1E,0x10,0x10,0x10}},
            {'G', new byte[]{0x0E,0x11,0x10,0x17,0x11,0x11,0x0E}},
            {'H', new byte[]{0x11,0x11,0x11,0x1F,0x11,0x11,0x11}},
            {'I', new byte[]{0x0E,0x04,0x04,0x04,0x04,0x04,0x0E}},
            {'K', new byte[]{0x11,0x12,0x14,0x18,0x14,0x12,0x11}},
            {'L', new byte[]{0x10,0x10,0x10,0x10,0x10,0x10,0x1F}},
            {'M', new byte[]{0x11,0x1B,0x15,0x15,0x11,0x11,0x11}},
            {'N', new byte[]{0x11,0x19,0x19,0x15,0x13,0x13,0x11}},
            {'O', new byte[]{0x0E,0x11,0x11,0x11,0x11,0x11,0x0E}},
            {'P', new byte[]{0x1E,0x11,0x11,0x1E,0x10,0x10,0x10}},
            {'R', new byte[]{0x1E,0x11,0x11,0x1E,0x14,0x12,0x11}},
            {'S', new byte[]{0x0E,0x11,0x10,0x0E,0x01,0x11,0x0E}},
            {'T', new byte[]{0x1F,0x04,0x04,0x04,0x04,0x04,0x04}},
            {'U', new byte[]{0x11,0x11,0x11,0x11,0x11,0x11,0x0E}},
            {'V', new byte[]{0x11,0x11,0x11,0x11,0x0A,0x0A,0x04}},
            {'W', new byte[]{0x11,0x11,0x11,0x15,0x15,0x15,0x0A}},
            {'X', new byte[]{0x11,0x0A,0x04,0x04,0x04,0x0A,0x11}},
            {'Y', new byte[]{0x11,0x11,0x0A,0x04,0x04,0x04,0x04}},
            {'Z', new byte[]{0x1F,0x01,0x02,0x04,0x08,0x10,0x1F}},
            {'J', new byte[]{0x07,0x02,0x02,0x02,0x02,0x12,0x0C}},
            {'Q', new byte[]{0x0E,0x11,0x11,0x11,0x15,0x12,0x0D}},
            {'0', new byte[]{0x0E,0x11,0x13,0x15,0x19,0x11,0x0E}},
            {'1', new byte[]{0x04,0x0C,0x04,0x04,0x04,0x04,0x0E}},
            {'2', new byte[]{0x0E,0x11,0x01,0x06,0x08,0x10,0x1F}},
            {'3', new byte[]{0x1F,0x01,0x02,0x06,0x01,0x11,0x0E}},
            {'4', new byte[]{0x02,0x06,0x0A,0x12,0x1F,0x02,0x02}},
            {'5', new byte[]{0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E}},
            {'6', new byte[]{0x0E,0x10,0x10,0x1E,0x11,0x11,0x0E}},
            {'7', new byte[]{0x1F,0x01,0x02,0x04,0x08,0x08,0x08}},
            {'8', new byte[]{0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E}},
            {'9', new byte[]{0x0E,0x11,0x11,0x0F,0x01,0x01,0x0E}},
            {'.', new byte[]{0x00,0x00,0x00,0x00,0x00,0x04,0x04}},
            {':', new byte[]{0x00,0x04,0x04,0x00,0x04,0x04,0x00}},
            {'-', new byte[]{0x00,0x00,0x00,0x0E,0x00,0x00,0x00}},
            {',', new byte[]{0x00,0x00,0x00,0x00,0x00,0x04,0x08}},
        };
    }
}
