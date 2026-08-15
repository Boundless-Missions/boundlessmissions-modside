/*
 * UI/Gui/UIF.cs – A small fluent builder over uGUI.
 *
 * Raw uGUI is ~20 lines of AddComponent per widget, which is how a screen file
 * grows to MainWindow's 3,244 lines. Everything in this file exists so a panel
 * reads as a description of what is on screen:
 *
 *     var card = UIF.Card(col).Pad(Theme.Space4).Column(Theme.Space2);
 *     UIF.Label(card, title).Size(Theme.FontBase).Bold();
 *     UIF.Label(card, body).Color(Theme.MutedForeground).Wrap();
 *
 * Text is TMP where KSP has a TMP font and legacy UnityEngine.UI.Text where it
 * does not (see Theme.Font). The fallback is not theoretical politeness: if the
 * font lookup ever fails, TMP renders nothing at all, and an entirely blank
 * panel would look like the sidebar is broken rather than like one asset is
 * missing.
 */

using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

namespace GeneKerman.UI.Gui
{
    internal enum TextAlign { Left, Center, Right }

    /// <summary>Which of the site's button variants a button is drawn as.</summary>
    internal enum BtnStyle { Primary, Secondary, Ghost, Destructive }

    // ── Element handle ──────────────────────────────────────────────────────

    /// <summary>A RectTransform under construction. Every method returns `this`.</summary>
    internal sealed class El
    {
        public readonly GameObject Go;
        public readonly RectTransform Rt;

        private LayoutElement layoutElement;
        private HorizontalOrVerticalLayoutGroup group;
        private Image image;

        internal El(GameObject go)
        {
            Go = go;
            Rt = go.GetComponent<RectTransform>();
        }

        public Image Image => image;

        // ── Layout ──────────────────────────────────────────────────────────

        /// <summary>Stack children top-to-bottom.</summary>
        public El Column(float spacing = 0f)
        {
            var g = Go.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = true;
            // False, always: a column of cards must keep each card at its own
            // height instead of dividing the panel evenly between them.
            g.childForceExpandHeight = false;
            g.childAlignment = TextAnchor.UpperLeft;
            group = g;
            return this;
        }

        /// <summary>Stack children left-to-right.</summary>
        public El Row(float spacing = 0f)
        {
            var g = Go.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = false;
            g.childForceExpandHeight = false;
            g.childAlignment = TextAnchor.MiddleLeft;
            group = g;
            return this;
        }

        public El Pad(int all) => Pad(all, all, all, all);

        public El Pad(int left, int right, int top, int bottom)
        {
            if (group != null)
                group.padding = new RectOffset(left, right, top, bottom);
            return this;
        }

        public El ChildAlign(TextAnchor anchor)
        {
            if (group != null) group.childAlignment = anchor;
            return this;
        }

        /// <summary>Let children stretch to fill this element's width (rows only).</summary>
        public El ExpandChildrenWidth(bool expand = true)
        {
            if (group != null) group.childForceExpandWidth = expand;
            return this;
        }

        /// <summary>
        /// Size this element to its content.
        ///
        /// Only for elements whose parent is NOT a layout group. A layout group
        /// already asks its children for a preferred size (a child's own layout
        /// group answers that), and a ContentSizeFitter underneath one fights it —
        /// the symptom is a row that oscillates in height or collapses to zero.
        /// The scroll content is the legitimate case: its parent is a plain
        /// viewport.
        /// </summary>
        public El Fit(bool horizontal = false, bool vertical = true)
        {
            var f = Go.AddComponent<ContentSizeFitter>();
            f.horizontalFit = horizontal ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
            f.verticalFit = vertical ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
            return this;
        }

        /// <summary>
        /// Fill the parent RectTransform exactly. For children of a plain
        /// RectTransform only — a layout group drives its children's offsets, and
        /// it interprets them against whatever anchors it finds, so stretching a
        /// layout child produces sizes that look arbitrary.
        /// </summary>
        public El Stretch(float left = 0, float right = 0, float top = 0, float bottom = 0)
        {
            Rt.anchorMin = Vector2.zero;
            Rt.anchorMax = Vector2.one;
            Rt.pivot = new Vector2(0.5f, 0.5f);
            Rt.offsetMin = new Vector2(left, bottom);
            Rt.offsetMax = new Vector2(-right, -top);
            return this;
        }

        private LayoutElement Le => layoutElement ?? (layoutElement = Go.AddComponent<LayoutElement>());

        public El W(float w) { Le.preferredWidth = w; Le.minWidth = w; return this; }
        public El H(float h) { Le.preferredHeight = h; Le.minHeight = h; return this; }
        public El Size(float w, float h) => W(w).H(h);
        public El MinH(float h) { Le.minHeight = h; return this; }
        public El PrefH(float h) { Le.preferredHeight = h; return this; }
        public El PrefW(float w) { Le.preferredWidth = w; return this; }
        /// <summary>Take the leftover space along the parent's axis.</summary>
        public El Flex(float w = 0, float h = 0) { Le.flexibleWidth = w; Le.flexibleHeight = h; return this; }

        // ── Paint ───────────────────────────────────────────────────────────

        /// <summary>Rounded background with an optional outline.</summary>
        public El Bg(Color fill, int radius = Theme.Radius, Color? stroke = null, int borderWidth = Theme.BorderWidth)
        {
            EnsureImage();
            Sprites.Bind(image, Sprites.Rounded(fill, radius, stroke, borderWidth));
            return this;
        }

        /// <summary>
        /// A circle of exactly <paramref name="diameter"/> px — unread dots, status
        /// pips, avatar placeholders. Also sizes the element, since a circle drawn
        /// into a rect of another shape is an ellipse.
        /// </summary>
        public El Dot(Color fill, int diameter, Color? stroke = null)
        {
            EnsureImage();
            Sprites.Bind(image, Sprites.Circle(fill, diameter, stroke));
            return Size(diameter, diameter);
        }

        /// <summary>A flat untextured fill — dividers, the panel ground, the scrim.</summary>
        public El Fill(Color color)
        {
            EnsureImage();
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.color = color;
            return this;
        }

        /// <summary>Whether this element swallows pointer input. Labels should not.</summary>
        public El Raycast(bool on)
        {
            if (image != null) image.raycastTarget = on;
            return this;
        }

        public El Active(bool on) { Go.SetActive(on); return this; }

        public El Name(string n) { Go.name = n; return this; }

        public T Add<T>() where T : Component => Go.AddComponent<T>();

        private void EnsureImage()
        {
            if (image == null) image = Go.GetComponent<Image>() ?? Go.AddComponent<Image>();
        }

        /// <summary>
        /// Destroy every child, keeping this element. Used to rebuild a list.
        ///
        /// The unparent is not redundant: Object.Destroy is deferred to the end of
        /// the frame, so children rebuilt in the same call would render *alongside*
        /// the old ones for one frame — a visible double of the whole feed.
        /// </summary>
        public void ClearChildren()
        {
            for (int i = Rt.childCount - 1; i >= 0; i--)
            {
                var child = Rt.GetChild(i);
                child.SetParent(null, false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }
    }

    // ── Text handle ─────────────────────────────────────────────────────────

    /// <summary>
    /// A label, backed by TMP or by legacy Text. Callers never learn which — the
    /// point of this type is that a panel written against it keeps working if the
    /// font situation changes (see Theme.Font).
    /// </summary>
    internal sealed class Lbl
    {
        public readonly El E;
        private readonly TMP_Text tmp;
        private readonly Text legacy;

        internal Lbl(El e, TMP_Text t, Text l) { E = e; tmp = t; legacy = l; }

        public string Text
        {
            get => tmp != null ? tmp.text : (legacy != null ? legacy.text : "");
            set { if (tmp != null) tmp.text = value ?? ""; else if (legacy != null) legacy.text = value ?? ""; }
        }

        public Lbl Set(string s) { Text = s; return this; }

        public Lbl Color(Color c)
        {
            if (tmp != null) tmp.color = c;
            else if (legacy != null) legacy.color = c;
            return this;
        }

        public Lbl Size(int px)
        {
            if (tmp != null) tmp.fontSize = px;
            else if (legacy != null) legacy.fontSize = px;
            return this;
        }

        public Lbl Bold(bool on = true)
        {
            if (tmp != null) tmp.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
            else if (legacy != null) legacy.fontStyle = on ? FontStyle.Bold : FontStyle.Normal;
            return this;
        }

        public Lbl Align(TextAlign a)
        {
            if (tmp != null)
                tmp.alignment = a == TextAlign.Left ? TextAlignmentOptions.Left
                              : a == TextAlign.Center ? TextAlignmentOptions.Center
                              : TextAlignmentOptions.Right;
            else if (legacy != null)
                legacy.alignment = a == TextAlign.Left ? TextAnchor.MiddleLeft
                                 : a == TextAlign.Center ? TextAnchor.MiddleCenter
                                 : TextAnchor.MiddleRight;
            return this;
        }

        public Lbl Wrap(bool on = true)
        {
            if (tmp != null) tmp.enableWordWrapping = on;
            else if (legacy != null) legacy.horizontalOverflow = on ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            return this;
        }

        /// <summary>
        /// Wrapped body text that grows to as many lines as it needs.
        ///
        /// This is just Wrap() plus an explicit note: no ContentSizeFitter is
        /// involved. Inside a Column the group assigns the width first and then
        /// asks the text for its preferred height at that width, which is the
        /// measurement we want. A fitter here would answer against the text's
        /// *unwrapped* preferred width instead and report one line.
        /// </summary>
        public Lbl Body()
        {
            Wrap();
            return this;
        }

        /// <summary>
        /// Cut a too-long single line off at the element's width instead of letting
        /// it run past. Labels default to Overflow (see UIF.Label) because that is
        /// what makes a line size itself; anything that must fit a *given* width —
        /// a URL in a 170px button — has to opt out of that.
        ///
        /// The catch: TMP's Ellipsis truncates vertically as well, so a label whose
        /// rect ends up shorter than one line renders *nothing at all*. Inside a
        /// fixed-height parent that is easy to cause — a layout group squeezes its
        /// children when their preferred heights do not fit. Give such a parent a
        /// MinH rather than an H.
        /// </summary>
        public Lbl Ellipsis()
        {
            if (tmp != null)
            {
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Ellipsis;
            }
            else if (legacy != null)
            {
                // Legacy Text has no ellipsis mode; clipping to one line is the
                // closest it gets.
                legacy.horizontalOverflow = HorizontalWrapMode.Wrap;
                legacy.verticalOverflow = VerticalWrapMode.Truncate;
            }
            return this;
        }

        /// <summary>The backing components, for the two callers that need the real
        /// thing: an input field wants a TMP_Text/Text to render into.</summary>
        internal TMP_Text Tmp => tmp;
        internal Text Legacy => legacy;
    }

    /// <summary>
    /// A single-line text box, backed by TMP_InputField or by legacy InputField —
    /// same dual path as Lbl, and for the same reason (Theme.Font).
    /// </summary>
    internal sealed class Fld
    {
        public readonly El E;
        private readonly TMP_InputField tmp;
        private readonly InputField legacy;

        internal Fld(El e, TMP_InputField t, InputField l) { E = e; tmp = t; legacy = l; }

        public string Text
        {
            get => tmp != null ? tmp.text : (legacy != null ? legacy.text : "");
            set { if (tmp != null) tmp.text = value ?? ""; else if (legacy != null) legacy.text = value ?? ""; }
        }

        public bool Focused => tmp != null ? tmp.isFocused : (legacy != null && legacy.isFocused);

        public Fld Interactable(bool on)
        {
            if (tmp != null) tmp.interactable = on;
            else if (legacy != null) legacy.interactable = on;
            return this;
        }

        /// <summary>Every keystroke — for keeping a draft the panel can rebuild from.</summary>
        public Fld OnChanged(Action<string> a)
        {
            if (a == null) return this;
            var h = new UnityAction<string>(a);
            if (tmp != null) tmp.onValueChanged.AddListener(h);
            else if (legacy != null) legacy.onValueChanged.AddListener(h);
            return this;
        }

        /// <summary>
        /// Enter, and only Enter.
        ///
        /// onEndEdit also fires when the box merely loses focus, which would connect
        /// to a half-typed address the moment the player clicked elsewhere. The key
        /// check separates the two: the callback runs in the same frame as the
        /// keypress that ended the edit, so Input still reports it.
        /// </summary>
        public Fld OnSubmit(Action<string> a)
        {
            if (a == null) return this;
            var h = new UnityAction<string>(s =>
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) a(s);
            });
            if (tmp != null) tmp.onEndEdit.AddListener(h);
            else if (legacy != null) legacy.onEndEdit.AddListener(h);
            return this;
        }
    }

    /// <summary>A button: the clickable element plus its caption.</summary>
    internal sealed class Btn
    {
        public readonly El E;
        public readonly Button Button;
        public readonly Lbl Label;

        internal Btn(El e, Button b, Lbl l) { E = e; Button = b; Label = l; }

        public Btn Interactable(bool on) { Button.interactable = on; return this; }

        public Btn OnClick(Action a)
        {
            Button.onClick.RemoveAllListeners();
            if (a != null) Button.onClick.AddListener(new UnityAction(a));
            return this;
        }
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    internal static class UIF
    {
        private const int UILayer = 5; // Unity's built-in "UI" layer

        private static Font builtinFont;

        // ── Live label registry ─────────────────────────────────────────────
        //
        // Every TMP label the sidebar has made, so a font that dies under it can be
        // swapped out in place. Rebuilding the panels would not be enough: the
        // header, the tab strip and the image viewer are built once and never again,
        // and they went textless with everything else.
        //
        // Same shape as Sprites.bindings, including the amortised sweep — a panel
        // rebuild makes a couple of hundred of these and the destroyed ones must not
        // accumulate for the rest of the session.

        private static readonly System.Collections.Generic.List<TMP_Text> liveLabels =
            new System.Collections.Generic.List<TMP_Text>();
        private static int labelSweepAt = 256;

        private static void Track(TMP_Text t)
        {
            liveLabels.Add(t);
            if (liveLabels.Count < labelSweepAt) return;

            for (int i = liveLabels.Count - 1; i >= 0; i--)
                if (liveLabels[i] == null) liveLabels.RemoveAt(i);

            labelSweepAt = Mathf.Max(256, liveLabels.Count * 2);
        }

        /// <summary>
        /// Regenerate every live label's mesh, now, on this thread.
        ///
        /// Two words matter: *now*, and *mesh*. TMP normally queues a rebuild and lets
        /// TMP_UpdateManager do it at the end of the frame, and every ordinary "please
        /// redraw" call — SetAllDirty, havePropertiesChanged — only queues. A label
        /// that is queued and never serviced draws nothing at all, and it does so
        /// silently: no error, no missing-glyph boxes, just empty rects.
        /// ForceMeshUpdate is the one call that bypasses the queue and builds the mesh
        /// synchronously, so it is correct whether the queue is dead, the mesh is
        /// stale, or the material was swapped underneath us.
        ///
        /// Called repeatedly for a few seconds after a scene change rather than once:
        /// KSP's loads are long and staged, and whatever clears these does not
        /// announce itself.
        /// </summary>
        public static int RefreshText()
        {
            var mat = Theme.FontMaterial;

            int n = 0;
            for (int i = liveLabels.Count - 1; i >= 0; i--)
            {
                var t = liveLabels[i];
                if (t == null) { liveLabels.RemoveAt(i); continue; }

                // Labels made before the material existed, or re-pointed at the shared
                // material by TMP when a font was reassigned, come back here.
                if (mat != null && !ReferenceEquals(t.fontSharedMaterial, mat))
                    t.fontSharedMaterial = mat;

                t.havePropertiesChanged = true;
                t.SetAllDirty();
                t.ForceMeshUpdate();
                n++;
            }

            return n;
        }

        /// <summary>
        /// Point every live label at the current font. Returns how many were moved,
        /// which is the only visible evidence the recovery happened — see the log
        /// line in SidebarController.UpdateFont.
        ///
        /// In place rather than by rebuilding: a rebuild would throw away scroll
        /// positions, a half-typed contract and the open calendar, for a change that
        /// is one field assignment per label.
        /// </summary>
        public static int RefreshFont()
        {
            var font = Theme.Font;
            if (font == null) return 0;

            int moved = 0;
            for (int i = liveLabels.Count - 1; i >= 0; i--)
            {
                var t = liveLabels[i];
                if (t == null) { liveLabels.RemoveAt(i); continue; }

                // ReferenceEquals: the label's current font may be a destroyed asset,
                // and == would call it null and compare equal to nothing useful.
                if (ReferenceEquals(t.font, font)) continue;

                t.font = font;
                // Assigning a font puts the label back on that font's shared material,
                // which is the thing we are avoiding — see Theme.FontMaterial.
                var mat = Theme.FontMaterial;
                if (mat != null) t.fontSharedMaterial = mat;
                t.SetAllDirty();
                moved++;
            }

            return moved;
        }

        /// <summary>A bare RectTransform GameObject parented to an arbitrary Transform.</summary>
        public static El Root(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = UILayer;
            go.transform.SetParent(parent, false);
            return new El(go);
        }

        /// <summary>A bare RectTransform GameObject parented to another element.</summary>
        public static El Node(El parent, string name = "Node")
        {
            var e = Root(name, parent.Rt);
            return e;
        }

        /// <summary>An empty box with no paint — a layout container.</summary>
        public static El Box(El parent, string name = "Box") => Node(parent, name);

        /// <summary>The site's Card: --card fill, --border outline, --radius corners.</summary>
        public static El Card(El parent, string name = "Card")
        {
            return Node(parent, name).Bg(Theme.Card, Theme.Radius, Theme.Border);
        }

        public static Lbl Label(El parent, string text, int size = Theme.FontSm, Color? color = null)
        {
            var e = Node(parent, "Label");

            Lbl lbl;
            var font = Theme.Font;
            if (font != null)
            {
                var t = e.Go.AddComponent<TextMeshProUGUI>();
                t.font = font;
                t.text = text ?? "";
                t.fontSize = size;
                t.color = color ?? Theme.Foreground;
                t.alignment = TextAlignmentOptions.Left;
                t.enableWordWrapping = false;
                t.raycastTarget = false;
                // TMP's default overflow truncates at the rect; the layout system
                // sizes the rect from the text, so Overflow is what makes a single
                // line actually appear at its natural width.
                t.overflowMode = TextOverflowModes.Overflow;
                // Our own material, never the font's shared one — see Theme.FontMaterial.
                var mat = Theme.FontMaterial;
                if (mat != null) t.fontSharedMaterial = mat;
                Track(t);
                lbl = new Lbl(e, t, null);
            }
            else
            {
                var t = e.Go.AddComponent<Text>();
                t.font = BuiltinFont();
                t.text = text ?? "";
                t.fontSize = size;
                t.color = color ?? Theme.Foreground;
                t.alignment = TextAnchor.MiddleLeft;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.raycastTarget = false;
                lbl = new Lbl(e, null, t);
            }

            return lbl;
        }

        /// <summary>Muted small print — timestamps, hints, secondary lines.</summary>
        public static Lbl Muted(El parent, string text, int size = Theme.FontXs)
            => Label(parent, text, size, Theme.MutedForeground);

        /// <param name="hPad">
        /// Horizontal breathing room around the caption. Lower it for a tab strip,
        /// where the buttons share a fixed width and the padding is what decides
        /// whether the last one's label fits.
        /// </param>
        public static Btn Button(El parent, string text, Action onClick,
                                 BtnStyle style = BtnStyle.Secondary, int height = 30,
                                 int hPad = Theme.Space3)
        {
            // Hover and pressed are given explicitly rather than derived, because a
            // Ghost button's fill is fully transparent and any "lighten by 12%" rule
            // applied to it stays invisible — the one variant where the hover state
            // has to *appear* rather than brighten.
            Color fill, hover, pressed, fg;
            Color? stroke;
            switch (style)
            {
                case BtnStyle.Primary:
                    fill = Theme.Primary; fg = Theme.PrimaryForeground; stroke = null;
                    hover = Theme.Lighten(fill, 0.12f); pressed = Theme.Darken(fill, 0.12f);
                    break;
                case BtnStyle.Destructive:
                    fill = Theme.Destructive; fg = Theme.DestructiveForeground; stroke = null;
                    hover = Theme.Lighten(fill, 0.12f); pressed = Theme.Darken(fill, 0.12f);
                    break;
                case BtnStyle.Ghost:
                    fill = Theme.Alpha(Theme.Secondary, 0f); fg = Theme.Foreground; stroke = Theme.Border;
                    hover = Theme.Alpha(Theme.Secondary, 1f); pressed = Theme.Alpha(Theme.Muted, 1f);
                    break;
                default:
                    fill = Theme.Secondary; fg = Theme.Foreground; stroke = Theme.Border;
                    hover = Theme.Lighten(fill, 0.14f); pressed = Theme.Darken(fill, 0.10f);
                    break;
            }

            var e = Node(parent, "Button").H(height);
            var img = e.Go.AddComponent<Image>();
            img.raycastTarget = true;

            var btn = e.Go.AddComponent<Button>();

            // Hover/pressed are separate baked sprites rather than a colour tint:
            // CanvasRenderer clamps tint colours, so "the same button, 15% lighter"
            // is not expressible as a multiplier the way it is in CSS.
            Sprites.BindStates(
                img, btn,
                Sprites.Rounded(fill, Theme.RadiusSm, stroke),
                Sprites.Rounded(hover, Theme.RadiusSm, stroke),
                Sprites.Rounded(pressed, Theme.RadiusSm, stroke),
                Sprites.Rounded(Theme.Alpha(fill, fill.a * 0.4f), Theme.RadiusSm,
                                stroke.HasValue ? (Color?)Theme.Alpha(stroke.Value, 0.4f) : null));

            // The caption is a child stretched over the button so the Button's own
            // RectTransform stays the hit area regardless of text length.
            var labelHost = Root("Text", e.Rt).Stretch(hPad, hPad, 0, 0);
            var lbl = Label(labelHost, text, Theme.FontSm, fg).Align(TextAlign.Center);
            lbl.E.Stretch();

            var b = new Btn(e, btn, lbl);
            b.OnClick(onClick);
            return b;
        }

        /// <summary>
        /// A square button carrying a drawn shape instead of a caption — the star in
        /// the player picker.
        ///
        /// The icon is a child rather than the button's own Image, because the button
        /// already uses its Image for the four SpriteSwap states and a Selectable has
        /// exactly one targetGraphic. The icon's colours are baked into its sprite,
        /// so it needs no tint and takes no raycast.
        /// </summary>
        public static Btn IconButton(El parent, SpriteKey icon, Action onClick,
                                     BtnStyle style = BtnStyle.Ghost, int size = 24, int iconSize = 14)
        {
            var b = Button(parent, "", onClick, style, size, 0);
            b.E.W(size);

            var host = Root("Icon", b.E.Rt);
            host.Rt.anchorMin = new Vector2(0.5f, 0.5f);
            host.Rt.anchorMax = new Vector2(0.5f, 0.5f);
            host.Rt.pivot = new Vector2(0.5f, 0.5f);
            host.Rt.anchoredPosition = Vector2.zero;
            host.Rt.sizeDelta = new Vector2(iconSize, iconSize);

            var img = host.Go.AddComponent<Image>();
            img.raycastTarget = false;
            Sprites.Bind(img, icon);

            return b;
        }

        /// <summary>
        /// A small chip — status, counts, difficulty bands. Its width comes from its
        /// own Row layout (padding + the label's preferred width), which is what the
        /// parent group reads; no ContentSizeFitter, see El.Fit.
        ///
        /// Softly rounded, not a capsule: at 20px tall a pill radius clamps to a
        /// half-circle on each end, which reads as a token rather than as a label.
        /// Pass Theme.RadiusPill if a capsule is actually wanted.
        /// </summary>
        public static El Badge(El parent, string text, Color accent, Color? fill = null,
                              int radius = Theme.RadiusSm)
        {
            var e = Node(parent, "Badge")
                .Row(0)
                .Pad(Theme.Space2, Theme.Space2, 2, 2)
                .ChildAlign(TextAnchor.MiddleCenter)
                .Bg(fill ?? Theme.Alpha(accent, 0.14f), radius, Theme.Alpha(accent, 0.5f))
                .H(20);

            Label(e, text, Theme.FontXs, accent);
            return e;
        }

        /// <summary>
        /// A whole row that is itself the button — the inbox pattern from
        /// WebUI/src/screens/Contracts.tsx, where each `&lt;li&gt;` is a full-width
        /// button that highlights on hover and stays highlighted while selected.
        ///
        /// Returns the row for the caller to fill; its children need no special
        /// treatment, because a click on a child bubbles up to this Button.
        ///
        /// Square corners by default: list rows sit inside a rounded Card and are
        /// separated by dividers, so rounding each one would read as a stack of
        /// tiles. A standalone row (a settings toggle) passes Theme.RadiusSm.
        /// </summary>
        public static El ClickableRow(El parent, Action onClick, bool selected = false, int radius = 0)
        {
            var e = Node(parent, "Row");
            var img = e.Go.AddComponent<Image>();
            img.raycastTarget = true;

            var btn = e.Go.AddComponent<Button>();
            btn.onClick.AddListener(new UnityAction(onClick ?? (() => { })));

            Color rest = selected ? Theme.Alpha(Theme.Muted, 0.6f) : Theme.Alpha(Theme.Muted, 0f);
            Sprites.BindStates(
                img, btn,
                Sprites.Rounded(rest, radius, null, 0),
                Sprites.Rounded(Theme.Alpha(Theme.Muted, selected ? 0.7f : 0.4f), radius, null, 0),
                Sprites.Rounded(Theme.Alpha(Theme.Muted, 0.8f), radius, null, 0),
                Sprites.Rounded(rest, radius, null, 0));

            return e;
        }

        /// <summary>
        /// A single-line text box.
        ///
        /// KSP-specific consequence, handled in SidebarController: while this has
        /// focus every keystroke is also a KSP keybind. Staging on space and a
        /// quicksave on F5 are both one letter into typing an address away, so the
        /// controller takes a second control lock for as long as anything inside the
        /// sidebar is focused.
        /// </summary>
        public static Fld TextField(El parent, string value, string placeholder = null, int height = 30,
                                    bool multiline = false)
        {
            var e = Node(parent, "Field").H(height);

            // Built inactive, activated at the end. An InputField's OnEnable wires
            // its caret against textViewport and textComponent, and neither exists
            // until the children below do — enabling first throws inside Unity's own
            // code, where there is nothing of ours to catch it.
            e.Go.SetActive(false);

            var img = e.Go.AddComponent<Image>();
            img.raycastTarget = true;

            // An input field consumes the wheel (it is an IScrollHandler in its own
            // right), so without this the page stops scrolling wherever the pointer
            // happens to cross a text box.
            e.Go.AddComponent<ScrollRelay>();

            // The text lives in a masked child rather than in the field's own rect,
            // so a value longer than the box scrolls inside it instead of spilling
            // over the card next to it.
            // Asymmetric on purpose, for a single line. An input field positions its
            // text from the line box — ascender to descender — rather than from the
            // glyphs, and KSP's font has enough ascent that the result sits visibly
            // high in a 30px box. Setting the text component's alignment does not move
            // it, because the field drives that placement itself, so the viewport is
            // what gets nudged. A multi-line box fills from the top and needs none of
            // this.
            var area = multiline
                ? Root("TextArea", e.Rt).Stretch(Theme.Space2, Theme.Space2, 4, 4)
                : Root("TextArea", e.Rt).Stretch(Theme.Space2, Theme.Space2, 6, 0);
            area.Go.AddComponent<RectMask2D>();

            var ph = Label(area, placeholder ?? "", Theme.FontSm, Theme.Alpha(Theme.MutedForeground, 0.7f));
            ph.E.Stretch();
            var txt = Label(area, value ?? "", Theme.FontSm);
            txt.E.Stretch();

            Fld fld;
            Selectable sel;
            if (txt.Tmp != null)
            {
                var align = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
                txt.Tmp.alignment = align;
                ph.Tmp.alignment = align;
                if (multiline)
                {
                    txt.Tmp.enableWordWrapping = true;
                    ph.Tmp.enableWordWrapping = true;
                }

                var input = e.Go.AddComponent<TMP_InputField>();
                input.textViewport = area.Rt;
                input.textComponent = txt.Tmp;
                input.placeholder = ph.Tmp;
                input.lineType = multiline
                    ? TMP_InputField.LineType.MultiLineNewline
                    : TMP_InputField.LineType.SingleLine;
                // Off, deliberately: this is a URL box, and a stray < in it must be
                // a character rather than markup.
                input.richText = false;
                input.customCaretColor = true;
                input.caretColor = Theme.Foreground;
                input.caretWidth = 1;
                input.selectionColor = Theme.Alpha(Theme.Primary, 0.35f);
                fld = new Fld(e, input, null);
                sel = input;
            }
            else
            {
                var input = e.Go.AddComponent<InputField>();
                input.textComponent = txt.Legacy;
                input.placeholder = ph.Legacy;
                input.lineType = multiline
                    ? InputField.LineType.MultiLineNewline
                    : InputField.LineType.SingleLine;
                input.customCaretColor = true;
                input.caretColor = Theme.Foreground;
                input.selectionColor = Theme.Alpha(Theme.Primary, 0.35f);
                fld = new Fld(e, null, input);
                sel = input;
            }

            Sprites.BindStates(
                img, sel,
                Sprites.Rounded(Theme.Input, Theme.RadiusSm, Theme.Border),
                Sprites.Rounded(Theme.Input, Theme.RadiusSm, Theme.Primary),
                Sprites.Rounded(Theme.Input, Theme.RadiusSm, Theme.Primary),
                Sprites.Rounded(Theme.Alpha(Theme.Input, 0.5f), Theme.RadiusSm, Theme.Alpha(Theme.Border, 0.5f)));

            e.Go.SetActive(true);

            // After activation, not before: assigning .text runs UpdateLabel, which
            // walks state an InputField only sets up in Awake/OnEnable. The label
            // already carries the value for the frame in between, so nothing flickers.
            fld.Text = value ?? "";
            return fld;
        }

        /// <summary>
        /// A settings row that toggles: label and hint on the left, a track-and-knob
        /// switch on the right, the whole row clickable — the site's `role="switch"`
        /// button from WebUI/src/screens/Settings.tsx.
        /// </summary>
        public static El Switch(El parent, string label, string hint, bool on, Action<bool> onChange)
        {
            bool next = !on;
            var row = ClickableRow(parent, () => onChange?.Invoke(next), false, Theme.RadiusSm)
                      .Row(Theme.Space2)
                      .Pad(Theme.Space2)
                      .ChildAlign(TextAnchor.MiddleLeft);

            // PrefW(0) with Flex(1): a Row hands out preferred widths first, so a
            // text block that asks for its natural width would push the switch off
            // the end. Asking for nothing and taking the remainder is what keeps the
            // switch pinned to the right edge.
            var text = Box(row, "Text").Column(2).PrefW(0).Flex(1f);
            Label(text, label, Theme.FontSm);
            if (!string.IsNullOrEmpty(hint)) Muted(text, hint).Body();

            // The track's child alignment *is* the state: the knob sits at whichever
            // end is on, and the layout group puts it there. No animation — a
            // retained-mode row would need a per-frame tween driver for 4px of
            // travel, and the colour change already carries the state.
            //
            // Softly rounded rather than a capsule, and a rounded square rather than
            // a circle for the knob — the same call as Badge's: at this size a pill
            // radius is a half-circle on each end, which reads as a different, older
            // control than everything else on the panel.
            var track = Box(row, "Track").Row(0).Pad(2)
                        .ChildAlign(on ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft)
                        .Bg(on ? Theme.Primary : Theme.Muted, Theme.RadiusSm, Theme.Border)
                        .Size(36, 20);
            track.Raycast(false);
            Node(track, "Knob")
                .Bg(on ? Theme.PrimaryForeground : Theme.MutedForeground, 4)
                .Size(14, 14);

            return row;
        }

        /// <summary>
        /// One of a small set of mutually exclusive cards — the site's `Choice`.
        ///
        /// Selected is a --primary outline over --secondary rather than a filled
        /// --primary button: two filled buttons side by side read as two things to
        /// press, when only the unselected one is actually an action.
        /// </summary>
        public static El Choice(El parent, string title, string subtitle, bool selected, Action onClick)
        {
            // MinH, never H: H fixes the preferred height too, and a Column that
            // cannot fit its children squeezes them — which silently blanks any
            // label using Ellipsis (see Lbl.Ellipsis). MiddleLeft so a card with no
            // subtitle centres its title instead of leaving a gap under it.
            var e = Node(parent, "Choice").Column(1).Pad(Theme.Space2)
                    .ChildAlign(TextAnchor.MiddleLeft).MinH(46);

            var img = e.Go.AddComponent<Image>();
            img.raycastTarget = true;
            var btn = e.Go.AddComponent<Button>();
            btn.onClick.AddListener(new UnityAction(onClick ?? (() => { })));

            Color fill = selected ? Theme.Secondary : Theme.Alpha(Theme.Card, 0f);
            Color stroke = selected ? Theme.Primary : Theme.Border;
            Sprites.BindStates(
                img, btn,
                Sprites.Rounded(fill, Theme.RadiusSm, stroke),
                Sprites.Rounded(selected ? Theme.Lighten(Theme.Secondary, 0.12f) : Theme.Alpha(Theme.Muted, 0.5f),
                                Theme.RadiusSm, stroke),
                Sprites.Rounded(Theme.Alpha(Theme.Muted, 0.8f), Theme.RadiusSm, stroke),
                Sprites.Rounded(fill, Theme.RadiusSm, Theme.Alpha(stroke, 0.4f)));

            Label(e, title, Theme.FontSm).Bold();
            // Ellipsis, not overflow: the subtitle is a host name in a ~170px card,
            // and nothing clips a layout child on its own.
            Muted(e, subtitle).Ellipsis();
            return e;
        }

        /// <summary>
        /// A panel's title row: name on the left, Refresh on the right. Every panel
        /// wants this, so it lives here rather than as a fifth private copy.
        /// </summary>
        public static El PanelHeader(El parent, string title, Action onRefresh)
        {
            var head = Box(parent, "Head").Row(Theme.Space2).H(26);
            Label(head, title, Theme.FontBase).Bold();
            Grow(head);
            if (onRefresh != null)
                Button(head, "Refresh", onRefresh, BtnStyle.Ghost, 24).E.PrefW(74);
            return head;
        }

        /// <summary>
        /// The empty/loading/unavailable state: a card with a line and optional
        /// explanation. Shared for the same reason PanelHeader is.
        /// </summary>
        public static El Notice(El parent, string title, string body = null)
        {
            var card = Card(parent, "Notice").Column(Theme.Space1).Pad(Theme.Space3);
            Label(card, title, Theme.FontSm, Theme.MutedForeground);
            if (!string.IsNullOrEmpty(body)) Muted(card, body).Body();
            return card;
        }

        /// <summary>
        /// A texture at a fixed height, width derived from its aspect.
        ///
        /// Deliberately not AspectRatioFitter: it is an ILayoutSelfController, so it
        /// fights any layout group that controls its child's size — which every
        /// Column here does. Driving both dimensions from a known height sidesteps
        /// that entirely and is exact. Put it in a Row followed by a Grow to
        /// left-align it, since a Row does not force children to fill the width.
        /// </summary>
        public static El Picture(El parent, Texture2D tex, float height)
        {
            var e = Node(parent, "Picture");
            if (tex == null) return e.H(height);

            var raw = e.Go.AddComponent<RawImage>();
            raw.texture = tex;
            raw.raycastTarget = false;

            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            return e.Size(Mathf.Max(1f, height * aspect), height);
        }

        /// <summary>
        /// A Picture that opens something when clicked — the thumbnail into the
        /// full-screen viewer.
        ///
        /// The hover cue is a frame around the image rather than a tint on it: a
        /// RawImage's colour multiplies the texture, so every "highlight" available
        /// there darkens or washes out the picture itself, which reads as disabled
        /// rather than as hoverable.
        /// </summary>
        public static El PictureButton(El parent, Texture2D tex, float height, Action onClick)
        {
            const int inset = 2;

            var frame = Node(parent, "PictureButton")
                .Row(0)
                .Pad(inset)
                .ChildAlign(TextAnchor.MiddleLeft)
                .H(height + inset * 2);

            var img = frame.Go.AddComponent<Image>();
            img.raycastTarget = true;

            var btn = frame.Go.AddComponent<Button>();
            btn.onClick.AddListener(new UnityAction(onClick ?? (() => { })));

            Color hover = Theme.Alpha(Theme.Primary, 0.10f);
            Sprites.BindStates(
                img, btn,
                Sprites.Rounded(Theme.Alpha(Theme.Muted, 0f), Theme.RadiusSm, Theme.Alpha(Theme.Border, 0f)),
                Sprites.Rounded(hover, Theme.RadiusSm, Theme.Primary),
                Sprites.Rounded(Theme.Alpha(Theme.Primary, 0.18f), Theme.RadiusSm, Theme.Primary),
                Sprites.Rounded(Theme.Alpha(Theme.Muted, 0f), Theme.RadiusSm, Theme.Alpha(Theme.Border, 0f)));

            Picture(frame, tex, height);
            return frame;
        }

        /// <summary>A 1px --border rule.</summary>
        public static El Divider(El parent)
            => Node(parent, "Divider").Fill(Theme.Border).H(1).Raycast(false);

        /// <summary>Fixed empty space along the parent's axis.</summary>
        public static El Spacer(El parent, float size = Theme.Space2)
            => Node(parent, "Spacer").H(size).PrefW(size);

        /// <summary>Elastic empty space — pushes what follows to the far end of a row.</summary>
        public static El Grow(El parent)
            => Node(parent, "Grow").Flex(1f, 0f);

        /// <summary>
        /// A vertical scroll area. Returns the *view* (size this one — usually
        /// .Flex(1,1)); <paramref name="content"/> is where rows go and already has
        /// a Column layout. The viewport gets a RectMask2D rather than a Mask,
        /// because Mask needs a stencil-capable material and RectMask2D is both
        /// cheaper and immune to whatever material state KSP leaves around.
        /// </summary>
        public static El ScrollView(El parent, out El content)
        {
            var view = Node(parent, "Scroll");
            var sr = view.Go.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;
            sr.inertia = false;

            var viewport = Root("Viewport", view.Rt).Stretch();
            viewport.Go.AddComponent<RectMask2D>();

            // An invisible raycast target covering the whole viewport.
            //
            // Unity delivers a scroll event to whatever the raycast hits and then
            // bubbles it UP looking for an IScrollHandler. The viewport and content
            // have no Graphic of their own, so over a gap between rows — or the empty
            // space below the last one — the ray fell through to the panel background,
            // which is an *ancestor* of this ScrollRect. Bubbling from there goes away
            // from the ScrollRect, never to it, so the wheel did nothing.
            var catcher = viewport.Go.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            catcher.raycastTarget = true;

            var body = Root("Content", viewport.Rt);
            body.Rt.anchorMin = new Vector2(0f, 1f);
            body.Rt.anchorMax = new Vector2(1f, 1f);
            body.Rt.pivot = new Vector2(0.5f, 1f);
            body.Rt.offsetMin = Vector2.zero;
            body.Rt.offsetMax = Vector2.zero;
            body.Column(Theme.Space2).Fit(horizontal: false, vertical: true);

            sr.viewport = viewport.Rt;
            sr.content = body.Rt;

            content = body;
            return view;
        }

        private static Font BuiltinFont()
        {
            if (builtinFont != null) return builtinFont;
            builtinFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return builtinFont;
        }
    }
}
