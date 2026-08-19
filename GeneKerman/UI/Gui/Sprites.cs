/*
 * UI/Gui/Sprites.cs – Procedural 9-slice sprite factory for the uGUI sidebar.
 *
 * There is no Unity Editor on this machine, so there are no prefabs, no
 * AssetBundles and no custom shaders. Every rounded corner in the sidebar is a
 * small Texture2D drawn here in C# from a signed-distance field, wrapped in a
 * Sprite with a 9-slice border so it scales to any card size without smearing
 * the corners.
 *
 * Two things this must get right, both learned from GKSkin:
 *
 *  1. Scene changes destroy textures. Every texture is HideAndDontSave (as
 *     GKSkin.MakeTex does) AND we keep a registry of the Images we handed
 *     sprites to, so Refresh() can regenerate and re-bind after a scene load.
 *     The hierarchy itself survives — it hangs off the DontDestroyOnLoad
 *     object — so we rebuild GPU resources, not the tree.
 *
 *  2. Do NOT call GKSkin.NeedsRebuild(). It is a destructive one-shot latch
 *     (GKSkin.cs:103-111): the first caller after Invalidate() gets true and
 *     resets the sentinel, everyone after gets false. Calling it here would
 *     silently steal the rebuild from whichever IMGUI window needed it. This
 *     file keeps its own sentinel and never touches GKSkin's.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GeneKerman.UI.Gui
{
    /// <summary>
    /// The pictograms this file can draw. There is no icon font to borrow from and no
    /// Unity Editor to import an SVG with, so an icon here is one more distance
    /// function — which is also what gets it the same antialiasing and the same
    /// fill-or-outline behaviour as everything else (see Star5Distance).
    /// </summary>
    internal enum IconShape { None = 0, Star5, Trash, Restore }

    /// <summary>Identity of a generated sprite. Value type so it keys a Dictionary cheaply.</summary>
    internal struct SpriteKey : IEquatable<SpriteKey>
    {
        public int Radius;
        public int BorderWidth;
        public int Spread;      // >0 = soft shadow falloff, in px, outside the shape
        public int Diameter;    // >0 = a fixed-size circle, drawn unsliced
        public int Icon;        // >0 = a fixed-size pictogram of Shape, drawn unsliced
        public IconShape Shape;
        public uint Fill;
        public uint Stroke;

        /// <summary>True for a circle: no 9-slice, rendered at its natural size.</summary>
        public bool IsCircle => Diameter > 0;

        /// <summary>
        /// Shapes that are not rounded rectangles carry no slice margin and must be
        /// drawn Simple. Stretching a 9-slice star would tear its points off.
        /// </summary>
        public bool IsUnsliced => Diameter > 0 || Icon > 0;

        public bool Equals(SpriteKey o) =>
            Radius == o.Radius && BorderWidth == o.BorderWidth && Spread == o.Spread &&
            Diameter == o.Diameter && Icon == o.Icon && Shape == o.Shape &&
            Fill == o.Fill && Stroke == o.Stroke;

        public override bool Equals(object o) => o is SpriteKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Radius;
                h = (h * 397) ^ BorderWidth;
                h = (h * 397) ^ Spread;
                h = (h * 397) ^ Diameter;
                h = (h * 397) ^ Icon;
                h = (h * 397) ^ (int)Shape;
                h = (h * 397) ^ (int)Fill;
                h = (h * 397) ^ (int)Stroke;
                return h;
            }
        }
    }

    internal static class Sprites
    {
        private sealed class Entry
        {
            public Texture2D Texture;
            public Sprite Sprite;
        }

        private sealed class Binding
        {
            public Image Image;
            public SpriteKey Key;

            // Set for Selectables using SpriteSwap. Their hover/pressed/disabled
            // sprites die on a scene change exactly like the base one, and a
            // Button left holding a destroyed sprite flickers to nothing the
            // first time the pointer touches it.
            public Selectable Selectable;
            public SpriteKey Hover, Pressed, Disabled;
        }

        private static readonly Dictionary<SpriteKey, Entry> cache = new Dictionary<SpriteKey, Entry>();
        private static readonly List<Binding> bindings = new List<Binding>();

        /// <summary>
        /// Our own destruction sentinel — deliberately not GKSkin's, see the header.
        /// A null sentinel after a scene load means Unity took our textures with it.
        /// </summary>
        private static Texture2D sentinel;

        /// <summary>
        /// Texels drawn per UI pixel. 1 until the controller reports the canvas is
        /// scaling up — see SetPixelDensity.
        /// </summary>
        private static int superSample = 1;

        /// <summary>
        /// Match the sprite resolution to the screen.
        ///
        /// The sidebar is laid out against a 1920x1080 reference and the CanvasScaler
        /// stretches that to fit, so on a 1440p screen every UI pixel is 1.33 screen
        /// pixels. A texture generated at 1:1 is then *magnified*, and a 1px card
        /// border resampled up by a third is precisely the "why is everything
        /// blurry" that antialiasing cannot fix — the edge was already soft before
        /// it was stretched.
        ///
        /// So: generate at the next whole multiple above the canvas scale and let
        /// the GPU downsample, which is the direction bilinear filtering is good at.
        /// Returns true when the density changed and the caller must Refresh().
        /// </summary>
        public static bool SetPixelDensity(float canvasScale)
        {
            // The epsilon keeps a scale of exactly 1 at 1x rather than rounding a
            // floating-point 1.0000001 up to a pointless 2x.
            int next = Mathf.Clamp(Mathf.CeilToInt(canvasScale - 0.01f), 1, 4);
            if (next == superSample) return false;

            superSample = next;
            Discard();
            return true;
        }

        /// <summary>
        /// Throw away every generated sprite. Only for a density change: the Images
        /// holding them are left pointing at destroyed objects, so the caller must
        /// Refresh() in the same frame.
        /// </summary>
        private static void Discard()
        {
            foreach (var e in cache.Values)
            {
                if (e.Sprite != null) UnityEngine.Object.Destroy(e.Sprite);
                if (e.Texture != null) UnityEngine.Object.Destroy(e.Texture);
            }
            cache.Clear();
        }

        // ── Public factory ──────────────────────────────────────────────────

        /// <summary>A filled rounded rectangle with an optional 1px outline.</summary>
        public static SpriteKey Rounded(Color fill, int radius = Theme.Radius,
                                        Color? stroke = null, int borderWidth = Theme.BorderWidth)
        {
            return new SpriteKey
            {
                Radius = Mathf.Max(0, radius),
                BorderWidth = stroke.HasValue ? Mathf.Max(0, borderWidth) : 0,
                Spread = 0,
                Fill = Pack(fill),
                Stroke = Pack(stroke ?? fill),
            };
        }

        /// <summary>
        /// A circle of exactly <paramref name="diameter"/> px, drawn unsliced.
        ///
        /// Not expressible as Rounded(): a 9-slice sprite carries slice margins of
        /// radius+1 on each side, so an 8px dot would ask uGUI to fit 2x5px of
        /// non-stretchable margin into an 8px rect, and the corners overlap into a
        /// smear. Small round things get their own primitive.
        /// </summary>
        public static SpriteKey Circle(Color fill, int diameter, Color? stroke = null, int borderWidth = Theme.BorderWidth)
        {
            return new SpriteKey
            {
                Radius = 0,
                BorderWidth = stroke.HasValue ? Mathf.Max(0, borderWidth) : 0,
                Spread = 0,
                Diameter = Mathf.Max(2, diameter),
                Fill = Pack(fill),
                Stroke = Pack(stroke ?? fill),
            };
        }

        /// <summary>
        /// A five-pointed star of exactly <paramref name="size"/> px, tip up.
        ///
        /// This exists because a favourite marker has to be a star and there is no
        /// icon font to borrow one from: KSP's TMP font is whatever the game loaded,
        /// so ★ is a bet that a glyph is present, and an ASCII asterisk is a
        /// placeholder rather than a star. Everything else here is drawn from a
        /// distance field already, so a star is one more distance function — which
        /// also gets it the same free antialiasing and the same outline behaviour
        /// (pass a stroke and a transparent fill for a hollow star).
        /// </summary>
        public static SpriteKey Star(Color fill, int size, Color? stroke = null, int borderWidth = Theme.BorderWidth)
            => Icon(IconShape.Star5, fill, size, stroke, borderWidth);

        /// <summary>
        /// A waste bin — lid, handle and a slotted body — of exactly
        /// <paramref name="size"/> px, drawn unsliced.
        ///
        /// The same argument as Star: an icon has to be a picture, and the two ways of
        /// getting one here are both closed (KSP's TMP font is whatever the game
        /// loaded, so a glyph is a bet; and with no Unity Editor on this machine there
        /// is no import path for an SVG or a PNG). So the bin is built from the
        /// primitive this file already has — four rounded boxes unioned, with two
        /// subtracted for the slots — which keeps it resolution-independent, gets the
        /// supersampling and the antialiasing for free, and takes an outline exactly
        /// as the star does.
        /// </summary>
        public static SpriteKey Trash(Color fill, int size, Color? stroke = null, int borderWidth = Theme.BorderWidth)
            => Icon(IconShape.Trash, fill, size, stroke, borderWidth);

        /// <summary>
        /// An arrow rising out of an open tray — put this back — of exactly
        /// <paramref name="size"/> px, drawn unsliced.
        ///
        /// The counterpart to Trash and deliberately built out of the same parts: the
        /// tray is the bin's body with its lid and slots taken away, so the pair reads
        /// as two directions of one motion rather than as two unrelated pictures. The
        /// direction is the whole message, which is why the arrow is the largest thing
        /// in it and the tray is left as a container to come out of.
        /// </summary>
        public static SpriteKey Restore(Color fill, int size, Color? stroke = null, int borderWidth = Theme.BorderWidth)
            => Icon(IconShape.Restore, fill, size, stroke, borderWidth);

        private static SpriteKey Icon(IconShape shape, Color fill, int size,
                                      Color? stroke, int borderWidth)
        {
            return new SpriteKey
            {
                Radius = 0,
                BorderWidth = stroke.HasValue ? Mathf.Max(0, borderWidth) : 0,
                Spread = 0,
                Icon = Mathf.Max(6, size),
                Shape = shape,
                Fill = Pack(fill),
                Stroke = Pack(stroke ?? fill),
            };
        }

        /// <summary>
        /// A soft drop shadow: the same rounded silhouette, faded out over
        /// <paramref name="spread"/> px. Cheaper and more predictable than a blur —
        /// the falloff is evaluated straight from the distance field.
        /// </summary>
        public static SpriteKey Shadow(Color color, int radius = Theme.Radius, int spread = 8)
        {
            return new SpriteKey
            {
                Radius = Mathf.Max(0, radius),
                BorderWidth = 0,
                Spread = Mathf.Max(1, spread),
                Fill = Pack(color),
                Stroke = Pack(color),
            };
        }

        /// <summary>
        /// Assign the sprite for <paramref name="key"/> to <paramref name="img"/> and
        /// remember the pairing, so a scene change can re-assign a fresh sprite to
        /// the same Image without rebuilding the hierarchy.
        ///
        /// Always sets Image.type = Sliced: these sprites carry a 9-slice border and
        /// rendering them as Simple would stretch the corners into ovals.
        /// </summary>
        public static void Bind(Image img, SpriteKey key)
        {
            if (img == null) return;

            img.sprite = Get(key);
            img.type = key.IsUnsliced ? Image.Type.Simple : Image.Type.Sliced;
            // The colours are baked into the texture; tinting is left to callers
            // that actually want it (the pulse, the read/unread dimming).
            img.color = Color.white;

            Track(new Binding { Image = img, Key = key });
        }

        /// <summary>
        /// Bind a Selectable's four SpriteSwap states at once. uGUI has no colour
        /// arithmetic we can trust for a hover tint (CanvasRenderer clamps, and a
        /// >1 tint is not portable), so hover/pressed are separate baked sprites —
        /// which is free here, because they are cached per style, not per widget.
        /// </summary>
        public static void BindStates(Image img, Selectable sel,
                                      SpriteKey normal, SpriteKey hover, SpriteKey pressed, SpriteKey disabled)
        {
            if (img == null || sel == null) return;

            img.sprite = Get(normal);
            img.type = normal.IsUnsliced ? Image.Type.Simple : Image.Type.Sliced;
            img.color = Color.white;

            sel.transition = Selectable.Transition.SpriteSwap;
            sel.targetGraphic = img;
            sel.spriteState = new SpriteState
            {
                highlightedSprite = Get(hover),
                pressedSprite = Get(pressed),
                disabledSprite = Get(disabled),
            };

            Track(new Binding
            {
                Image = img,
                Key = normal,
                Selectable = sel,
                Hover = hover,
                Pressed = pressed,
                Disabled = disabled,
            });
        }

        /// <summary>
        /// Record a binding, sweeping destroyed ones occasionally.
        ///
        /// The sweep is not housekeeping for its own sake: every list rebuild
        /// destroys its rows and binds new ones, so without it the list grows for
        /// the whole session and Refresh() — which walks all of it — gets slower
        /// with every notification that ever arrived.
        /// </summary>
        private static void Track(Binding b)
        {
            bindings.Add(b);
            if (bindings.Count < sweepAt) return;

            for (int i = bindings.Count - 1; i >= 0; i--)
                if (bindings[i].Image == null)
                    bindings.RemoveAt(i);

            // Next sweep at twice the surviving population, so a genuinely large
            // panel does not trigger a sweep on every single Bind.
            sweepAt = Mathf.Max(128, bindings.Count * 2);
        }

        private static int sweepAt = 128;

        /// <summary>The sprite for a key, generating it on first use.</summary>
        public static Sprite Get(SpriteKey key)
        {
            Entry e;
            if (cache.TryGetValue(key, out e) && e.Texture != null && e.Sprite != null)
                return e.Sprite;

            e = Generate(key);
            cache[key] = e;
            return e.Sprite;
        }

        // ── Scene-change recovery ───────────────────────────────────────────

        /// <summary>
        /// Called on GameEvents.onGameSceneLoadRequested. Regenerates any texture
        /// Unity destroyed and re-assigns sprites to every live bound Image.
        /// Safe to call when nothing was lost — it does nothing in that case.
        /// </summary>
        /// <summary>
        /// True when Unity has destroyed our textures since the last Refresh.
        ///
        /// Worth polling rather than only acting on the scene-change event, because
        /// that event fires when the load is *requested* — everything is still alive
        /// at that point, and the teardown happens afterwards, with nothing to tell
        /// us it did.
        /// </summary>
        public static bool Lost => sentinel == null;

        public static void Refresh()
        {
            bool lost = sentinel == null;

            // Drop dead cache entries so Get() rebuilds them.
            List<SpriteKey> dead = null;
            foreach (var kv in cache)
                if (kv.Value.Texture == null || kv.Value.Sprite == null)
                    (dead ?? (dead = new List<SpriteKey>())).Add(kv.Key);

            if (dead != null)
            {
                lost = true;
                foreach (var k in dead) cache.Remove(k);
            }

            if (lost)
            {
                // A destroyed font asset would render every label invisible, and it
                // dies from the same cause, so re-resolve it on the same signal.
                Theme.InvalidateFont();
                sentinel = MakeSentinel();
            }

            // Re-bind regardless of `lost`: an Image whose sprite reference was
            // nulled is invisible, and the check is a handful of comparisons.
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                var b = bindings[i];
                if (b.Image == null) { bindings.RemoveAt(i); continue; }

                var s = Get(b.Key);
                if (!ReferenceEquals(b.Image.sprite, s))
                {
                    b.Image.sprite = s;
                    b.Image.type = b.Key.IsUnsliced ? Image.Type.Simple : Image.Type.Sliced;
                }

                if (b.Selectable != null)
                    b.Selectable.spriteState = new SpriteState
                    {
                        highlightedSprite = Get(b.Hover),
                        pressedSprite = Get(b.Pressed),
                        disabledSprite = Get(b.Disabled),
                    };
            }
        }

        /// <summary>Forget every binding (sidebar torn down). Textures are left cached.</summary>
        public static void ClearBindings() => bindings.Clear();

        private static Texture2D MakeSentinel()
        {
            var t = new Texture2D(1, 1, TextureFormat.ARGB32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            t.SetPixel(0, 0, Color.clear);
            t.Apply();
            return t;
        }

        // ── Generation ──────────────────────────────────────────────────────

        private static Entry Generate(SpriteKey key)
        {
            if (sentinel == null) sentinel = MakeSentinel();

            Color fill = Unpack(key.Fill);
            Color stroke = Unpack(key.Stroke);

            int r = key.Radius;
            int spread = key.Spread;

            int margin, size;
            if (key.Icon > 0)
            {
                margin = 0;
                size = key.Icon;
            }
            else if (key.IsCircle)
            {
                // No slice at all — the sprite is drawn at its natural size.
                margin = 0;
                size = key.Diameter;
                // A radius at or above the half-extent is clamped to it by the
                // distance function, which is exactly a circle.
                r = size;
            }
            else
            {
                // Slice margin: everything that must not stretch. That is the corner
                // radius plus the shadow falloff, plus 1px of antialiasing headroom
                // — and at least the border *plus one px of fill*, which is the other
                // thing that must not stretch.
                //
                // The +1 on the border is not spare room, it is the whole difference
                // between an outline and a wash. uGUI stretches the 1px centre across
                // the rect and samples it bilinearly, so the sample taken at the very
                // start of the stretched region blends the centre texel with its
                // neighbour — and with margin == BorderWidth that neighbour is the
                // stroke. A 2px outline on a square (radius 0) shape therefore bled
                // its colour a quarter of the way into the rect from every side,
                // which on a selected list row read as a green-tinted fill rather
                // than as the bare edge ClickableRow asks for. One px of fill between
                // the stroke and the stretched centre is what the filter needs to
                // find, so the bleed has nothing to carry inwards.
                margin = r + spread + 1;
                margin = Mathf.Max(margin, key.BorderWidth + 1);
                // 1px of stretchable centre is all a 9-slice needs.
                size = margin * 2 + 1;
            }

            // Everything above is in UI pixels. Everything below is in texels, of
            // which there are `ss` per UI pixel — see SetPixelDensity.
            int ss = superSample;
            int dim = size * ss;
            float rr = r * ss;
            float spreadPx = spread * ss;
            float borderPx = key.BorderWidth * ss;

            var tex = new Texture2D(dim, dim, TextureFormat.ARGB32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,   // survive scene loads
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var px = new Color[dim * dim];
            float half = dim * 0.5f;
            // Half-extent of the *solid* shape: the shadow spread lives outside it.
            float ext = half - spreadPx;

            for (int y = 0; y < dim; y++)
            {
                for (int x = 0; x < dim; x++)
                {
                    // Signed distance to the boundary; negative inside.
                    float d;
                    if (key.Icon > 0)
                    {
                        // One texel of headroom, so the outermost edge has somewhere
                        // for its antialiasing ramp to land.
                        float ir = half - ss;
                        float ix = x + 0.5f - half;
                        float iy = y + 0.5f - half;
                        switch (key.Shape)
                        {
                            case IconShape.Trash:   d = TrashDistance(ix, iy, ir); break;
                            case IconShape.Restore: d = RestoreDistance(ix, iy, ir); break;
                            default:                d = Star5Distance(ix, iy, ir); break;
                        }
                    }
                    else
                    {
                        d = RoundedBoxDistance(x + 0.5f - half, y + 0.5f - half, ext, ext, rr);
                    }

                    Color c;
                    if (spread > 0)
                    {
                        // Shadow: opaque over the silhouette, quadratic falloff out
                        // to `spread`. Quadratic rather than linear because a linear
                        // ramp reads as a visible hard edge at its outer end.
                        float t = Mathf.Clamp01(1f - d / spreadPx);
                        c = fill;
                        c.a *= t * t;
                    }
                    else
                    {
                        // One *texel* of analytic antialias on the outer edge, so the
                        // ramp narrows as the density rises instead of staying a
                        // fixed fraction of a UI pixel.
                        float shape = Mathf.Clamp01(0.5f - d);
                        // ...and on the inner edge, which is where fill meets stroke.
                        float inner = borderPx > 0f
                            ? Mathf.Clamp01(0.5f - (d + borderPx))
                            : 1f;

                        c = Color.Lerp(stroke, fill, inner);
                        c.a = Mathf.Lerp(stroke.a, fill.a, inner) * shape;
                    }

                    px[y * dim + x] = c;
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);

            // pixelsPerUnit is 100 (Canvas.referencePixelsPerUnit's default) times
            // the density, so one UI unit stays one UI pixel however many texels are
            // behind it and Theme's px constants keep meaning px. The slice border
            // is given in texels for the same reason.
            // Image.pixelsPerUnitMultiplier is deliberately never used — its
            // availability across Unity 2019.4 point releases is not worth betting on.
            var sprite = Sprite.Create(
                tex,
                new Rect(0, 0, dim, dim),
                new Vector2(0.5f, 0.5f),
                100f * ss,
                0,
                SpriteMeshType.FullRect,
                new Vector4(margin * ss, margin * ss, margin * ss, margin * ss));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            sprite.name = "GK_" + key.Radius + "_" + key.BorderWidth + "_" + key.Spread + "_" +
                          key.Diameter + "_" + key.Shape;

            return new Entry { Texture = tex, Sprite = sprite };
        }

        /// <summary>
        /// Signed distance from a point to a rounded box centred on the origin.
        /// Standard SDF: negative inside, 0 on the boundary, positive outside.
        /// </summary>
        private static float RoundedBoxDistance(float px, float py, float hx, float hy, float r)
        {
            r = Mathf.Min(r, Mathf.Min(hx, hy));
            float qx = Mathf.Abs(px) - (hx - r);
            float qy = Mathf.Abs(py) - (hy - r);
            float ox = Mathf.Max(qx, 0f);
            float oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        /// <summary>
        /// Signed distance to a regular five-pointed star centred on the origin,
        /// tip up, with outer radius <paramref name="r"/>. Inigo Quilez's sdStar5,
        /// transcribed: fold the plane about the star's two mirror lines until any
        /// point lands beside one edge, then measure to that single segment.
        ///
        /// <c>Inner</c> is the ratio of the inner vertices to the outer ones. 0.55
        /// rather than the pentagram's 0.382: rasterized at 14 px (checked against
        /// this same function outside Unity), the sharp star's legs come out one
        /// pixel wide and vanish into the antialiasing, and the hollow variant's
        /// outline closes over its own hole.
        /// </summary>
        private static float Star5Distance(float px, float py, float r)
        {
            const float Inner = 0.55f;
            const float K1x = 0.809016994375f;   // cos(36°)
            const float K1y = -0.587785252292f;  // -sin(36°)

            px = Mathf.Abs(px);

            float d1 = K1x * px + K1y * py;
            if (d1 > 0f) { px -= 2f * d1 * K1x; py -= 2f * d1 * K1y; }

            // Second mirror: k2 = (-k1.x, k1.y).
            float d2 = -K1x * px + K1y * py;
            if (d2 > 0f) { px -= 2f * d2 * -K1x; py -= 2f * d2 * K1y; }

            px = Mathf.Abs(px);
            py -= r;

            float bax = Inner * -K1y;
            float bay = Inner * K1x - 1f;

            float baLenSq = bax * bax + bay * bay;
            float h = Mathf.Clamp((px * bax + py * bay) / baLenSq, 0f, r);

            float ex = px - bax * h;
            float ey = py - bay * h;
            float dist = Mathf.Sqrt(ex * ex + ey * ey);

            return dist * Mathf.Sign(py * bax - px * bay);
        }

        /// <summary>
        /// Signed distance to a waste bin centred on the origin, fitting a box of
        /// half-extent <paramref name="r"/>. Lid, handle and body are rounded boxes
        /// unioned (min of the distances, which is exact for a union), and the two
        /// slots are subtracted from the body alone (max against the negated slot) so
        /// they cut the body without also biting into the lid above it.
        ///
        /// The proportions are in units of r, which is what keeps the icon identical
        /// at 14 px in a row and at 15 px in the toolbar — and, since the body is the
        /// widest thing only after the lid, the lid is what sets the horizontal
        /// extent. The gap between the lid and the body is deliberate: without it the
        /// silhouette is a single blob at small sizes, and the gap is the one feature
        /// that survives being two pixels tall.
        /// </summary>
        private static float TrashDistance(float px, float py, float r)
        {
            if (r <= 0f) return 1f;

            float x = px / r;
            float y = py / r;

            // Handle, then lid: the handle overlaps the lid, so the union closes the
            // seam rather than leaving a hairline where two edges meet.
            float d = RoundedBoxDistance(x, y - 0.84f, 0.30f, 0.14f, 0.06f);
            d = Mathf.Min(d, RoundedBoxDistance(x, y - 0.60f, 0.88f, 0.12f, 0.06f));

            float body = RoundedBoxDistance(x, y + 0.26f, 0.66f, 0.62f, 0.16f);
            float slots = Mathf.Min(RoundedBoxDistance(x - 0.27f, y + 0.24f, 0.085f, 0.40f, 0.08f),
                                    RoundedBoxDistance(x + 0.27f, y + 0.24f, 0.085f, 0.40f, 0.08f));
            body = Mathf.Max(body, -slots);

            return Mathf.Min(d, body) * r;
        }

        /// <summary>
        /// Signed distance to an arrow rising out of an open tray, centred on the
        /// origin and fitting a box of half-extent <paramref name="r"/>.
        ///
        /// Same construction as TrashDistance: the tray is one rounded box with a
        /// second subtracted from it — offset upwards, so what it takes away is the
        /// inside *and* the top, which is what leaves a container open at the end the
        /// arrow comes out of. The head is a triangle rather than two strokes, because
        /// at 15 px a chevron drawn as two 1 px lines is two grey smudges.
        /// </summary>
        private static float RestoreDistance(float px, float py, float r)
        {
            if (r <= 0f) return 1f;

            float x = px / r;
            float y = py / r;

            // Arrow: head over shaft, unioned so the seam between them closes.
            float d = TriangleUpDistance(x, y, 0.98f, 0.36f, 0.58f);
            d = Mathf.Min(d, RoundedBoxDistance(x, y - 0.02f, 0.19f, 0.44f, 0.06f));

            float outer = RoundedBoxDistance(x, y + 0.64f, 0.84f, 0.34f, 0.16f);
            float inner = RoundedBoxDistance(x, y + 0.46f, 0.58f, 0.32f, 0.10f);

            return Mathf.Min(d, Mathf.Max(outer, -inner)) * r;
        }

        /// <summary>
        /// Signed distance to an isosceles triangle pointing up: apex on the y axis at
        /// <paramref name="apexY"/>, a horizontal base at <paramref name="baseY"/> of
        /// half-width <paramref name="halfWidth"/>.
        ///
        /// The largest of the three edges' half-plane distances, mirrored about x so
        /// only one side has to be measured. Exact inside the triangle and along each
        /// edge; outside a corner it reads slightly short, which spends itself inside
        /// the one texel of antialiasing and never past it.
        /// </summary>
        private static float TriangleUpDistance(float px, float py, float apexY, float baseY, float halfWidth)
        {
            float h = apexY - baseY;
            if (h <= 0f || halfWidth <= 0f) return 1f;

            float x = Mathf.Abs(px);
            float side = (h * x + halfWidth * (py - apexY)) / Mathf.Sqrt(h * h + halfWidth * halfWidth);
            return Mathf.Max(baseY - py, side);
        }

        // ── Colour packing (so SpriteKey stays a cheap value type) ──────────

        private static uint Pack(Color c)
        {
            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            uint a = (uint)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        private static Color Unpack(uint v) => new Color(
            ((v >> 24) & 0xFF) / 255f,
            ((v >> 16) & 0xFF) / 255f,
            ((v >> 8) & 0xFF) / 255f,
            (v & 0xFF) / 255f);
    }
}
