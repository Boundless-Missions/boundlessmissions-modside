/*
 * VesselRenderer.cs – Blueprint-style multi-view vessel renderer.
 *
 * Captures 8 views of the vessel (6 ortho + 2 perspective), composites
 * them onto a blueprint grid background with labels.
 *
 * Vessel isolation: temporarily moves all part renderers to layer 30,
 * renders with cullingMask = 1<<30, uses magenta chroma key for clean
 * background removal, then restores original layers.
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
        const int IMG_W = 2048;
        const int IMG_H = 1100;
        const int CELL  = 440;          // Each view cell size
        const int RENDER_SIZE = 440;    // Individual render resolution
        const int ISOLATION_LAYER = 30;
        const float PADDING = 1.35f;

        // Column X positions (4 columns)
        static readonly int[] COL_X = { 64, 544, 1024, 1504 };
        // Row Y positions in screen coords (top-down, cell top edge)
        static readonly int[] CELL_Y = { 90, 580 };
        // Label Y positions (above cells)
        static readonly int[] LABEL_Y = { 70, 560 };

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
                    return CaptureFromFlight();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeneKerman] Blueprint render failed: {ex}");
            }
            return VesselDataCollector.CaptureScreenshot();
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
            float mass = 0, cost = 0;
            foreach (var p in ship.parts)
            {
                mass += p.mass + p.GetResourceMass();
                cost += p.partInfo?.cost ?? 0f;
            }

            // Editor: vessel is aligned to world axes
            return RenderBlueprint(renderers, bounds, Quaternion.identity,
                name, partCount, mass, cost);
        }

        private static string CaptureFromFlight()
        {
            var vessel = FlightGlobals.ActiveVessel;
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
                0f);
        }

        // ── Core Rendering ──────────────────────────────────────────────────

        private static string RenderBlueprint(
            Renderer[] renderers, Bounds bounds, Quaternion vesselRotation,
            string vesselName, int partCount, float mass, float cost)
        {
            int layerMask = 1 << ISOLATION_LAYER;

            // ── Isolate vessel on dedicated layer ──
            // Use part-level isolation to avoid duplicate-gameObject layer bugs
            List<Part> parts = GetCurrentParts();
            var origLayers = IsolatePartLayers(parts, ISOLATION_LAYER);

            // Replace scene lighting with uniform fill lights so all 8 views
            // receive equal illumination regardless of the in-game sun angle.
            var origAmbientMode  = RenderSettings.ambientMode;
            var origAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f);
            var fillLightObjects = CreateBlueprintLights(layerMask);

            // ── Set up shared render resources ──
            var rt = new RenderTexture(RENDER_SIZE, RENDER_SIZE, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            rt.Create();

            // ReadPixels cannot read directly from an MSAA RenderTexture on many
            // GPUs/drivers — it returns only the clear color, so every view comes
            // back blank while the CPU-drawn grid/labels still appear. Resolve the
            // MSAA target into this plain (non-MSAA) texture before reading back.
            var resolveRt = new RenderTexture(RENDER_SIZE, RENDER_SIZE, 0, RenderTextureFormat.ARGB32);
            resolveRt.antiAliasing = 1;
            resolveRt.Create();

            var camObj = new GameObject("GK_BlueprintCam");
            var cam = camObj.AddComponent<Camera>();
            cam.targetTexture = rt;
            cam.cullingMask = layerMask;
            cam.nearClipPlane = 0.01f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            var readTex = new Texture2D(RENDER_SIZE, RENDER_SIZE, TextureFormat.ARGB32, false);

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
                readTex.ReadPixels(new Rect(0, 0, RENDER_SIZE, RENDER_SIZE), 0, 0);
                readTex.Apply();
                blackPass[i] = readTex.GetPixels32();

                // White pass
                cam.backgroundColor = Color.white;
                cam.Render();
                Graphics.Blit(rt, resolveRt);
                RenderTexture.active = resolveRt;
                readTex.ReadPixels(new Rect(0, 0, RENDER_SIZE, RENDER_SIZE), 0, 0);
                readTex.Apply();
                whitePass[i] = readTex.GetPixels32();

                RenderTexture.active = null;
            }

            // ── Cleanup render resources ──
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
                Debug.LogWarning("[GeneKerman] Blueprint capture produced no vessel pixels — falling back to a plain screenshot.");
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
                DrawString(blueprint, VIEWS[i].label, COL_X[col], LABEL_Y[row], 2, C_LABEL);
            }

            // Title and stats
            DrawString(blueprint, vesselName.ToUpper(), 64, 18, 3, C_TITLE);
            string stats = $"PARTS:{partCount}  MASS:{mass:F1}T";
            if (cost > 0) stats += $"  COST:{cost:N0}";
            DrawString(blueprint, stats, 64, IMG_H - 28, 2, C_STATS);

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
            string filename = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
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

            cam.farClipPlane = dist * 3f;
            cam.transform.position = center + view.direction * dist;
            cam.transform.LookAt(center, view.up);

            if (view.perspective)
            {
                cam.orthographic = false;
                cam.fieldOfView = 30f;
            }
            else
            {
                cam.orthographic = true;
                cam.orthographicSize = viewSize * PADDING;
            }
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
                bool majorH = sy % 128 == 0;
                bool minorH = sy % 20 == 0;

                for (int x = 0; x < IMG_W; x++)
                {
                    bool majorV = x % 128 == 0;
                    bool minorV = x % 20 == 0;

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
            // 1px border around each cell
            for (int x = sx - 1; x <= sx + CELL; x++)
            {
                SetPx(px, x, sy - 1, C_BORDER);
                SetPx(px, x, sy + CELL, C_BORDER);
            }
            for (int y = sy - 1; y <= sy + CELL; y++)
            {
                SetPx(px, sx - 1, y, C_BORDER);
                SetPx(px, sx + CELL, y, C_BORDER);
            }
        }

        private static void DrawFrameBorder(Color32[] px)
        {
            // 2px frame around entire image
            for (int t = 0; t < 2; t++)
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
