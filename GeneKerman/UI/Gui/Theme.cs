/*
 * UI/Gui/Theme.cs – Design tokens for the uGUI sidebar.
 *
 * These are the website's tokens (Website/src/app/globals.css), converted from
 * HSL to sRGB once, here, so the two surfaces cannot drift by hand-editing.
 * The site is dark-only and has exactly one accent (#6ad26a) and one radius
 * (0.7rem); that is the whole vocabulary and all of it maps to uGUI.
 *
 * This is the mod's FIRST shared palette. The IMGUI windows have 137 inline
 * `new Color(...)` calls between them, 102 of them distinct. Nothing here
 * touches those — IMGUI keeps its own look until (if) it is ported.
 *
 * Colour space: KSP renders in gamma space, so a UnityEngine.Color component
 * is the sRGB value directly — 0x6a/255 = 0.4157f is #6a on screen. Do not
 * "correct" these for linear space; that would wash the whole panel out.
 */

using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace GeneKerman.UI.Gui
{
    internal static class Theme
    {
        // ── Colour ──────────────────────────────────────────────────────────
        // Names match the CSS custom properties one-for-one, on purpose: when
        // someone changes --card on the site, there is exactly one field here
        // to change to match.

        /// <summary>--background · hsl(0 0% 4%)</summary>
        public static readonly Color Background = Gray(0.04f);
        /// <summary>--foreground · hsl(0 0% 96%)</summary>
        public static readonly Color Foreground = Gray(0.96f);
        /// <summary>--card · hsl(0 0% 6%)</summary>
        public static readonly Color Card = Gray(0.06f);
        /// <summary>--secondary · hsl(0 0% 12%)</summary>
        public static readonly Color Secondary = Gray(0.12f);
        /// <summary>--muted · hsl(0 0% 11%)</summary>
        public static readonly Color Muted = Gray(0.11f);
        /// <summary>--muted-foreground · hsl(0 0% 60%)</summary>
        public static readonly Color MutedForeground = Gray(0.60f);
        /// <summary>--border · hsl(0 0% 15%)</summary>
        public static readonly Color Border = Gray(0.15f);
        /// <summary>--input · hsl(0 0% 16%)</summary>
        public static readonly Color Input = Gray(0.16f);

        /// <summary>
        /// The ground under a chosen segmented chip (Discord's
        /// --background-modifier-selected). Deliberately far above --secondary:
        /// 12% over a 4% panel is a step nobody reads as a state, which is what
        /// the contract filters looked like before.
        /// </summary>
        public static readonly Color Selected = Gray(0.20f);
        /// <summary>
        /// Outline of a chosen chip. Lighter than its own fill, not darker — a
        /// --border (15%) edge around a 20% ground draws a groove, and the row's
        /// unchosen siblings are outlined at exactly that value.
        /// </summary>
        public static readonly Color SelectedBorder = Gray(0.30f);

        /// <summary>--primary · hsl(120 54% 62%) · #6ad26a</summary>
        public static readonly Color Primary = new Color(0.4157f, 0.8235f, 0.4157f, 1f);
        /// <summary>--primary-foreground · hsl(120 40% 8%) — text ON primary</summary>
        public static readonly Color PrimaryForeground = new Color(0.048f, 0.112f, 0.048f, 1f);
        /// <summary>--accent · hsl(120 20% 12%) — tinted surface behind accent text</summary>
        public static readonly Color Accent = new Color(0.096f, 0.144f, 0.096f, 1f);
        /// <summary>--accent-foreground · hsl(120 54% 70%)</summary>
        public static readonly Color AccentForeground = new Color(0.538f, 0.862f, 0.538f, 1f);
        /// <summary>--destructive · hsl(0 62% 50%)</summary>
        public static readonly Color Destructive = new Color(0.81f, 0.19f, 0.19f, 1f);
        /// <summary>--destructive-foreground · hsl(0 0% 98%)</summary>
        public static readonly Color DestructiveForeground = Gray(0.98f);

        /// <summary>
        /// The panel's own ground. The site uses an opaque --background, but this
        /// sits over a live game viewport, so it needs alpha to read as an overlay
        /// rather than a hole. Deliberately NOT backdrop-blur: that needs a GrabPass
        /// shader, which cannot be compiled at runtime, and a flat wash reads better
        /// over a moving 3D scene anyway.
        /// </summary>
        public static readonly Color PanelBackground = new Color(0.04f, 0.04f, 0.04f, 0.94f);

        /// <summary>Hover tint for Selectable colour transitions (uGUI multiplies).</summary>
        public static readonly Color HoverTint = new Color(1.18f, 1.18f, 1.18f, 1f);
        /// <summary>Pressed tint for Selectable colour transitions.</summary>
        public static readonly Color PressedTint = new Color(0.85f, 0.85f, 0.85f, 1f);
        /// <summary>Disabled tint for Selectable colour transitions.</summary>
        public static readonly Color DisabledTint = new Color(1f, 1f, 1f, 0.45f);

        // ── Geometry ────────────────────────────────────────────────────────
        // Reference resolution is 1920x1080 (see SidebarController's CanvasScaler),
        // so these are CSS pixels at 1x and need no conversion.

        /// <summary>--radius · 0.7rem at a 16px root = 11px.</summary>
        public const int Radius = 11;
        /// <summary>Tighter radius for chips, badges and inputs (~rounded-md).</summary>
        public const int RadiusSm = 6;
        /// <summary>Pill radius. Large enough that the 9-slice reads as a capsule.</summary>
        public const int RadiusPill = 14;

        public const int Space1 = 4;
        public const int Space2 = 8;
        public const int Space3 = 12;
        public const int Space4 = 16;
        public const int Space6 = 24;

        /// <summary>Border width, in px, of a card/button outline.</summary>
        public const int BorderWidth = 1;
        /// <summary>
        /// Border width of a *selected* row's outline. Thicker than the ordinary
        /// one because it is the whole of the state: with the fill left alone (see
        /// UIF.ClickableRow) a 1px edge is doing a job a full-row tint used to do.
        /// </summary>
        public const int BorderWidthSelected = 2;

        // ── Type scale ──────────────────────────────────────────────────────
        // text-xs / text-sm / text-base / text-lg / text-xl from the site.

        public const int FontXs = 11;
        public const int FontSm = 13;
        public const int FontBase = 15;
        public const int FontLg = 17;
        public const int FontXl = 20;

        // ── Font ────────────────────────────────────────────────────────────

        private static TMP_FontAsset font;
        private static bool fontSearched;

        /// <summary>
        /// The font every label in the sidebar uses.
        ///
        /// This is NOT Inter. `TMP_FontAsset.CreateFontAsset` is absent from KSP's
        /// TMP build — verified by symbol scan — so a .ttf cannot be turned into a
        /// TMP font at runtime. Shipping Inter needs a TMP asset baked in Unity
        /// 2019.4.18f1 and loaded from an AssetBundle, which is deferred.
        ///
        /// Until then we borrow whichever SDF font KSP already has loaded. It is a
        /// single field precisely so that swap is a one-line change later.
        ///
        /// Null is a legitimate answer (no TMP font found, or a TMP build that
        /// surprises us) — UIF falls back to legacy UnityEngine.UI.Text so the
        /// panel is never silently blank.
        /// </summary>
        public static TMP_FontAsset Font
        {
            get
            {
                // Re-search when the cached asset has been destroyed under us: TMP
                // assets are Unity objects and a scene teardown can take them.
                if (fontSearched && Alive(font)) return font;

                fontSearched = true;
                font = FindFont();
                return font;
            }
        }

        /// <summary>
        /// Preference order. KSP ships Noto Sans as its UI face; the others are
        /// here so a modded install that replaced it still gets something sane.
        /// Anything is better than nothing, hence the final "first non-null".
        /// </summary>
        private static readonly string[] FontPreference =
        {
            "NotoSans-Regular SDF",
            "NotoSans",
            "Calibri SDF",
            "LiberationSans SDF",
        };

        private static TMP_FontAsset FindFont()
        {
            TMP_FontAsset[] all;
            try
            {
                // FindObjectsOfTypeAll reaches assets that are loaded but not
                // attached to anything in the scene, which is exactly what a
                // font asset is.
                all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GeneKerman] Sidebar: TMP font lookup failed — " + e.Message);
                return null;
            }

            if (all == null || all.Length == 0)
            {
                Debug.LogWarning("[GeneKerman] Sidebar: no TMP_FontAsset found; falling back to legacy text.");
                return null;
            }

            foreach (string want in FontPreference)
                foreach (var f in all)
                    if (f != null && f.name != null &&
                        f.name.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Debug.Log("[GeneKerman] Sidebar font: " + f.name);
                        return f;
                    }

            foreach (var f in all)
                if (f != null)
                {
                    Debug.Log("[GeneKerman] Sidebar font (fallback): " + f.name);
                    return f;
                }

            return null;
        }

        /// <summary>Drop the cached font so it is re-resolved (scene change / teardown).</summary>
        public static void InvalidateFont()
        {
            font = null;
            fontSearched = false;
            fontMaterial = null;
        }

        private static Material fontMaterial;

        /// <summary>
        /// Our own copy of the font's material, and the reason the sidebar goes
        /// textless the moment a craft launches.
        ///
        /// A TMP_FontAsset ships one default material, and every text object that does
        /// not ask for another one *shares* it — including ours. A material is mutable
        /// global state: colour mask, stencil test, z-test, clip rect. Anything in the
        /// game that writes to that one asset writes to every label we have ever
        /// drawn. The flight scene is where KSP and the mod pile do the most of that,
        /// which is exactly where our text vanished while KSP's own kept rendering:
        /// its labels were on "NotoSans-Regular Dropshadow Outline", a private variant,
        /// and ours were on the shared default.
        ///
        /// So we take a copy, once, and never share again. The copy is made at build
        /// time — in the main menu, before the game has had a chance to touch
        /// anything — and normalised anyway, so a copy taken from an already-poisoned
        /// original still comes out drawable.
        ///
        /// HideAndDontSave: a runtime Material is a Unity object, and without it this
        /// would be collected on the next scene load, which is the failure it exists
        /// to prevent.
        /// </summary>
        public static Material FontMaterial
        {
            get
            {
                var f = Font;
                if (f == null || f.material == null) return null;

                // Re-clone when the atlas moves under us: a copy pointing at a
                // destroyed texture draws nothing, which is the bug in a new hat.
                if (fontMaterial != null && fontMaterial.mainTexture == f.material.mainTexture)
                    return fontMaterial;

                var m = new Material(f.material)
                {
                    name = "GK Sidebar Font Material",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Normalize(m);

                fontMaterial = m;
                Debug.Log("[GeneKerman] Sidebar font material cloned from " + f.material.name + ".");
                return fontMaterial;
            }
        }

        /// <summary>
        /// Put the four properties that can hide text back where they belong.
        ///
        /// Each of these is something another UI legitimately sets on a material it
        /// believes is its own: a colour mask of 0 for a mask's own quad, a stencil
        /// comparison for masked content, a z-test for world-space text. On a shared
        /// material each one is also a way to make everyone else's text disappear
        /// while every diagnostic still says it is being drawn.
        /// </summary>
        private static void Normalize(Material m)
        {
            // 15 = RGBA. Zero means "render, write no colour" — invisible, and the
            // single likeliest way a shared UI material gets left unusable.
            if (m.HasProperty("_ColorMask")) m.SetFloat("_ColorMask", 15f);

            // 8 = CompareFunction.Always, with no reference value: no stencil test.
            if (m.HasProperty("_Stencil")) m.SetFloat("_Stencil", 0f);
            if (m.HasProperty("_StencilComp")) m.SetFloat("_StencilComp", 8f);

            // Also Always. The sidebar is a ScreenSpaceOverlay canvas drawn after
            // everything, so there is no depth for it to lose a comparison against.
            if (m.HasProperty("_ZTestMode")) m.SetFloat("_ZTestMode", 8f);
        }

        /// <summary>
        /// Whether a font asset can still draw anything.
        ///
        /// A plain null check is not enough, and the difference is what left the
        /// whole sidebar textless after a launch: the *asset* can survive a scene
        /// load while its atlas texture is unloaded underneath it, and TMP asked to
        /// render from an atlas that is gone draws nothing at all — no error, no
        /// missing-glyph boxes, just empty rects where every label was. The material
        /// is what actually reaches the GPU, so that is what gets checked.
        ///
        /// ReferenceEquals for the "we never had one" case, deliberately: a
        /// *destroyed* asset compares == null under Unity's overload, so `f == null`
        /// cannot tell "no font on this install" from "the font just died", and the
        /// recovery path needs to tell them apart.
        /// </summary>
        public static bool Alive(TMP_FontAsset f)
            => f != null && f.material != null && f.material.mainTexture != null;

        /// <summary>True when this install never resolved a TMP font at all (legacy text).</summary>
        public static bool NeverHadFont(TMP_FontAsset f) => ReferenceEquals(f, null);

        // ── Helpers ─────────────────────────────────────────────────────────

        private static Color Gray(float v) => new Color(v, v, v, 1f);

        /// <summary>Same colour at a different alpha — the `/60` suffix from Tailwind.</summary>
        public static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        /// <summary>
        /// Toward white. Used for hover states, which are baked as separate sprites
        /// rather than applied as a tint — see Sprites.BindStates for why.
        /// Alpha is preserved, so lightening a transparent ghost button keeps it
        /// transparent instead of turning it into a white slab.
        /// </summary>
        public static Color Lighten(Color c, float t)
            => new Color(Mathf.Lerp(c.r, 1f, t), Mathf.Lerp(c.g, 1f, t), Mathf.Lerp(c.b, 1f, t), c.a);

        /// <summary>Toward black; the pressed state. Alpha preserved, as above.</summary>
        public static Color Darken(Color c, float t)
            => new Color(Mathf.Lerp(c.r, 0f, t), Mathf.Lerp(c.g, 0f, t), Mathf.Lerp(c.b, 0f, t), c.a);

        /// <summary>
        /// Difficulty/status accents, kept as one lookup so the sidebar and any
        /// later panel agree. Unknown keys fall back to muted rather than magenta.
        /// </summary>
        private static readonly Dictionary<string, Color> statusColors =
            new Dictionary<string, Color>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "success",  new Color(0.4157f, 0.8235f, 0.4157f, 1f) },
                { "warning",  new Color(0.90f,   0.70f,   0.25f,   1f) },
                { "danger",   new Color(0.81f,   0.19f,   0.19f,   1f) },
                { "info",     new Color(0.38f,   0.63f,   0.92f,   1f) },
            };

        public static Color Status(string key)
        {
            Color c;
            return statusColors.TryGetValue(key ?? "", out c) ? c : MutedForeground;
        }

        /// <summary>
        /// Contract status → accent, matching `STATUS_STYLE` in
        /// WebUI/src/screens/Contracts.tsx exactly. This is deliberately *not*
        /// the classic window's GetStatusColor: that mapping is older and different (it paints
        /// active green and completed cyan), and the sidebar's whole purpose is to
        /// read as the same product as the web UI. IMGUI keeps its own; the two
        /// palettes are a known, accepted difference rather than an oversight.
        ///
        /// Unknown statuses fall back to muted, which is also what the web UI does
        /// for `cancelled`.
        /// </summary>
        public static Color ContractStatus(string status)
        {
            switch (status)
            {
                case "pending":    return new Color(0.96f, 0.62f, 0.07f, 1f); // amber-500
                case "active":     return new Color(0.05f, 0.65f, 0.91f, 1f); // sky-500
                case "submitted":  return new Color(0.55f, 0.36f, 0.96f, 1f); // violet-500
                case "completed":  return new Color(0.06f, 0.73f, 0.51f, 1f); // emerald-500
                case "disputed":   return new Color(0.94f, 0.27f, 0.27f, 1f); // red-500
                case "mod_review": return new Color(0.98f, 0.45f, 0.09f, 1f); // orange-500
                default:           return MutedForeground;                    // cancelled, unknown
            }
        }
    }
}
