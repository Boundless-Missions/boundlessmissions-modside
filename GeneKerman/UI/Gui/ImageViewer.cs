/*
 * UI/Gui/ImageViewer.cs – Full-screen lightbox for submission images.
 *
 * Blueprint sheets and telemetry diagrams arrive at a size the 400px sidebar
 * cannot show: in the SUBMISSION card they are thumbnails, and a thumbnail of a
 * multi-view render is unreadable. Clicking one opens it here, over the whole
 * viewport, with wheel-zoom and drag-pan.
 *
 * It lives on the sidebar's own Canvas as the last child of the root, so it
 * draws above the panel without a second Canvas to keep in sync. Three
 * consequences of covering the entire screen, all handled here:
 *
 *  1. The input lock must widen. SidebarController locks only while the pointer
 *     is over the 400px panel; with this open, a pan-drag anywhere on screen
 *     would otherwise spin the camera underneath. See PointerOverSidebar.
 *  2. It must not own its textures. They belong to the SubmissionPreview that
 *     downloaded them, which disposes them when the selection moves. The viewer
 *     holds references and closes itself the moment one goes null — which is
 *     also what happens across a scene load, where Unity destroys them for us.
 *  3. Escape closes it, but not from here — SidebarController.UpdateEscape owns
 *     that key, because closing the viewer must not also pause the game behind
 *     it. It holds a PAUSE lock for as long as the panel is open and closes
 *     whatever is on top: this viewer first, then the panel. The lock is scoped
 *     to the panel being open on purpose; locking a player out of their own
 *     pause menu for a whole session is worse than any click-through. The X and
 *     a click on the backdrop still close it too.
 *
 * Input is read from raw Input rather than through EventSystem handlers. The
 * backdrop is a raycast target, so the EventSystem's hit is this overlay and
 * nothing beneath it sees the wheel; and a drag that leaves the image still
 * needs to keep panning, which pointer-enter/exit callbacks make awkward.
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GeneKerman.UI.Gui
{
    internal sealed class ImageViewer
    {
        /// <summary>Inset of the frame from the screen edge, at the reference resolution.</summary>
        private const float FrameMargin = 40f;

        private const float ZoomMin = 0.5f;
        private const float ZoomMax = 10f;

        /// <summary>Zoom factor per wheel notch.</summary>
        private const float ZoomBase = 1.2f;

        /// <summary>
        /// Upper bound on the fit-to-frame scale, so a small telemetry thumbnail is
        /// not blown up to a wall of interpolated pixels the moment it opens.
        /// </summary>
        private const float MaxFitScale = 3f;

        /// <summary>Pointer travel, in canvas units, past which a click becomes a drag.</summary>
        private const float DragThreshold = 4f;

        private const float DoubleClickSeconds = 0.35f;

        private El overlay;
        private RectTransform frameRect;
        private RectTransform viewportRect;
        private RectTransform imageRect;
        private RawImage image;
        private Lbl caption;
        private Lbl counter;
        private Lbl zoomLbl;
        private float shownZoom = -1f;
        private Btn prevBtn;
        private Btn nextBtn;

        private readonly List<Texture2D> shots = new List<Texture2D>();
        private readonly List<string> labels = new List<string>();
        private int index;
        private string title = "";

        private bool open;
        private float zoom = 1f;
        private Vector2 pan;
        private Vector2 baseSize;       // fit-to-frame size at zoom 1
        private bool needsFit;          // recompute baseSize on the next tick with a valid rect

        private bool pressed;           // a mouse button went down while we were open
        private bool pressedInFrame;
        private bool dragging;
        private Vector2 dragLast;
        private Vector2 pressOrigin;
        private float lastClickTime = -1f;

        public bool IsOpen => open;

        // ── Construction ────────────────────────────────────────────────────

        /// <summary>
        /// Build the overlay hierarchy, hidden. Called once from SidebarController
        /// after the panel, so it is the later sibling and therefore drawn on top —
        /// a ScreenSpaceOverlay canvas paints in hierarchy order.
        /// </summary>
        public void Build(El root)
        {
            overlay = UIF.Root("ImageViewer", root.Rt).Stretch();

            // The backdrop. Raycast target on purpose: it is what stops the wheel and
            // the clicks from reaching the panel and the game behind it.
            overlay.Fill(Theme.Alpha(Color.black, 0.82f)).Raycast(true);

            var frame = UIF.Root("Frame", overlay.Rt)
                           .Stretch(FrameMargin, FrameMargin, FrameMargin, FrameMargin)
                           .Bg(Theme.PanelBackground, Theme.Radius, Theme.Border);
            frameRect = frame.Rt;

            BuildHeader(frame);
            BuildViewport(frame);
            BuildFooter(frame);

            overlay.Active(false);
        }

        private void BuildHeader(El frame)
        {
            var bar = UIF.Root("Header", frame.Rt);
            bar.Rt.anchorMin = new Vector2(0f, 1f);
            bar.Rt.anchorMax = new Vector2(1f, 1f);
            bar.Rt.pivot = new Vector2(0.5f, 1f);
            bar.Rt.offsetMin = new Vector2(Theme.Space4, -44f);
            bar.Rt.offsetMax = new Vector2(-Theme.Space4, -Theme.Space3);
            bar.Row(Theme.Space2).ChildAlign(TextAnchor.MiddleLeft);

            caption = UIF.Label(bar, "", Theme.FontBase).Bold();
            counter = UIF.Muted(bar, "", Theme.FontSm);
            UIF.Grow(bar);
            // Percent of the texture's native size, not of the fit — "100%" then
            // means one screen pixel per source pixel, which is the number you want
            // when deciding whether a blueprint's small print is legible.
            zoomLbl = UIF.Muted(bar, "", Theme.FontSm);
            UIF.Button(bar, "Reset", ResetView, BtnStyle.Ghost, 26).E.PrefW(64);
            UIF.Button(bar, "X", Close, BtnStyle.Ghost, 26).E.W(30);
        }

        private void BuildViewport(El frame)
        {
            var view = UIF.Root("Viewport", frame.Rt).Stretch(Theme.Space3, Theme.Space3, 52f, 44f);
            viewportRect = view.Rt;
            view.Go.AddComponent<RectMask2D>();

            var img = UIF.Root("Image", viewportRect);
            imageRect = img.Rt;
            imageRect.anchorMin = new Vector2(0.5f, 0.5f);
            imageRect.anchorMax = new Vector2(0.5f, 0.5f);
            imageRect.pivot = new Vector2(0.5f, 0.5f);

            image = img.Go.AddComponent<RawImage>();
            // The backdrop already catches everything; a second target here would
            // only change which object the raycast reports, which nothing reads.
            image.raycastTarget = false;

            // Step-through arrows, vertically centred over the image. Hidden for a
            // single-image set — see Show.
            prevBtn = NavButton(view, "<", -1, true);
            nextBtn = NavButton(view, ">", 1, false);
        }

        private Btn NavButton(El view, string glyph, int delta, bool left)
        {
            var host = UIF.Root("Nav", view.Rt);
            host.Rt.anchorMin = new Vector2(left ? 0f : 1f, 0.5f);
            host.Rt.anchorMax = new Vector2(left ? 0f : 1f, 0.5f);
            host.Rt.pivot = new Vector2(left ? 0f : 1f, 0.5f);
            host.Rt.sizeDelta = new Vector2(34f, 56f);
            host.Rt.anchoredPosition = new Vector2(left ? Theme.Space2 : -Theme.Space2, 0f);
            host.Row(0).ChildAlign(TextAnchor.MiddleCenter);

            var b = UIF.Button(host, glyph, () => Step(delta), BtnStyle.Secondary, 56, Theme.Space1);
            b.E.Flex(1f, 0f).PrefW(0f);
            return b;
        }

        private void BuildFooter(El frame)
        {
            var bar = UIF.Root("Footer", frame.Rt);
            bar.Rt.anchorMin = new Vector2(0f, 0f);
            bar.Rt.anchorMax = new Vector2(1f, 0f);
            bar.Rt.pivot = new Vector2(0.5f, 0f);
            bar.Rt.offsetMin = new Vector2(Theme.Space4, Theme.Space2);
            bar.Rt.offsetMax = new Vector2(-Theme.Space4, 30f);
            bar.Row(Theme.Space2).ChildAlign(TextAnchor.MiddleLeft);

            UIF.Muted(bar, "Scroll to zoom  -  drag to pan  -  double-click to reset  -  click outside to close",
                      Theme.FontXs);
        }

        // ── Open / close ────────────────────────────────────────────────────

        /// <summary>
        /// Show <paramref name="images"/> starting at <paramref name="start"/>.
        ///
        /// The textures are borrowed, never owned: the list is whatever the caller
        /// still holds, and the viewer closes itself if one is destroyed underneath
        /// it (Tick). Callers that dispose their textures should still call Close
        /// explicitly — this is the safety net, not the contract.
        /// </summary>
        public void Show(IList<Texture2D> images, IList<string> captions, int start, string setTitle)
        {
            if (overlay == null || images == null || images.Count == 0) return;

            shots.Clear();
            labels.Clear();
            for (int i = 0; i < images.Count; i++)
            {
                if (images[i] == null) continue;
                shots.Add(images[i]);
                labels.Add(captions != null && i < captions.Count ? captions[i] : null);
            }
            if (shots.Count == 0) return;

            title = setTitle ?? "";
            index = Mathf.Clamp(start, 0, shots.Count - 1);
            open = true;
            pressed = false;
            dragging = false;
            overlay.Active(true);

            bool many = shots.Count > 1;
            prevBtn.E.Active(many);
            nextBtn.E.Active(many);

            Apply();
        }

        public void Close()
        {
            if (!open) return;
            open = false;
            pressed = false;
            dragging = false;
            shots.Clear();
            labels.Clear();
            if (image != null) image.texture = null;
            overlay?.Active(false);
        }

        private void Step(int delta)
        {
            if (shots.Count == 0) return;
            index = (index + delta + shots.Count) % shots.Count;
            Apply();
        }

        /// <summary>Point the RawImage at the current shot and refit it.</summary>
        private void Apply()
        {
            var tex = shots[index];
            image.texture = tex;

            string label = labels[index];
            caption.Set(string.IsNullOrEmpty(label) ? title : label);
            counter.Set(shots.Count > 1 ? (index + 1) + " / " + shots.Count : "");

            ResetView();
        }

        /// <summary>Back to fit-to-frame, centred.</summary>
        private void ResetView()
        {
            zoom = 1f;
            pan = Vector2.zero;
            // The viewport's rect is only meaningful after a layout pass, and Show
            // may well be the frame the overlay was activated on. Defer.
            needsFit = true;
        }

        // ── Frame ───────────────────────────────────────────────────────────

        /// <summary>Called from SidebarController.Tick while the sidebar renders.</summary>
        public void Tick()
        {
            if (!open) return;

            // The preview that owns these textures disposed them, or a scene load
            // did. Either way there is nothing left to show.
            if (shots.Count == 0 || shots[index] == null) { Close(); return; }

            if (needsFit && viewportRect.rect.width > 1f && viewportRect.rect.height > 1f)
            {
                needsFit = false;
                Fit();
            }

            HandleInput();
            ApplyTransform();
        }

        private void Fit()
        {
            var tex = shots[index];
            Rect vp = viewportRect.rect;
            float s = Mathf.Min(vp.width / tex.width, vp.height / tex.height);
            s = Mathf.Min(s, MaxFitScale);
            baseSize = new Vector2(tex.width * s, tex.height * s);

            // The readout is a function of baseSize as well as zoom, and this is the
            // one place baseSize moves.
            shownZoom = -1f;
        }

        private void HandleInput()
        {
            Vector2 mouse = Input.mousePosition;
            bool inViewport = RectTransformUtility.RectangleContainsScreenPoint(viewportRect, mouse, null);
            bool inFrame = RectTransformUtility.RectangleContainsScreenPoint(frameRect, mouse, null);

            // ── Wheel: zoom about the cursor ────────────────────────────────
            //
            // mouseScrollDelta rather than the "Mouse ScrollWheel" axis: the axis is
            // whatever KSP's input manager says it is, and it is scaled by that
            // binding's sensitivity. This one is notches, straight from the platform.
            float scroll = Input.mouseScrollDelta.y;
            if (inViewport && !Mathf.Approximately(scroll, 0f) && baseSize.x > 0f)
            {
                float next = Mathf.Clamp(zoom * Mathf.Pow(ZoomBase, scroll), ZoomMin, ZoomMax);
                if (!Mathf.Approximately(next, zoom) && LocalPoint(mouse, out Vector2 local))
                {
                    // Keep whatever pixel is under the cursor under the cursor: the
                    // point's position in image space is invariant across the zoom.
                    Vector2 inImage = (local - pan) / zoom;
                    pan = local - inImage * next;
                    zoom = next;
                }
            }

            // ── Left button: drag to pan, click-outside to close, double to reset ──
            if (Input.GetMouseButtonDown(0))
            {
                pressed = true;
                pressedInFrame = inFrame;
                dragging = false;
                if (LocalPoint(mouse, out Vector2 down))
                {
                    dragLast = down;
                    pressOrigin = down;
                }
            }
            else if (Input.GetMouseButton(0) && pressed && pressedInFrame)
            {
                if (LocalPoint(mouse, out Vector2 now))
                {
                    if (!dragging && (now - pressOrigin).sqrMagnitude > DragThreshold * DragThreshold)
                        dragging = true;

                    if (dragging)
                    {
                        pan += now - dragLast;
                        dragLast = now;
                    }
                }
            }
            else if (Input.GetMouseButtonUp(0) && pressed)
            {
                // A press that began on the backdrop and ended there dismisses. Both
                // ends are checked so a pan that overshoots the frame does not close
                // the viewer the player is using.
                if (!pressedInFrame && !inFrame) { pressed = false; Close(); return; }

                if (!dragging && inViewport)
                {
                    float now = Time.unscaledTime;
                    if (now - lastClickTime < DoubleClickSeconds) { ResetView(); lastClickTime = -1f; }
                    else lastClickTime = now;
                }

                pressed = false;
                dragging = false;
            }
        }

        /// <summary>Cursor position in the viewport's local space, which is what pan is in.</summary>
        private bool LocalPoint(Vector2 screen, out Vector2 local)
            => RectTransformUtility.ScreenPointToLocalPointInRectangle(viewportRect, screen, null, out local);

        private void ApplyTransform()
        {
            if (baseSize.x <= 0f) return;

            Vector2 size = baseSize * zoom;
            Rect vp = viewportRect.rect;

            // Clamp so the image cannot be flung out of view. On an axis where it is
            // smaller than the viewport there is nothing to pan along, so it centres.
            float maxX = Mathf.Max(0f, (size.x - vp.width) * 0.5f);
            float maxY = Mathf.Max(0f, (size.y - vp.height) * 0.5f);
            pan = new Vector2(Mathf.Clamp(pan.x, -maxX, maxX), Mathf.Clamp(pan.y, -maxY, maxY));

            imageRect.sizeDelta = size;
            imageRect.anchoredPosition = pan;

            // Only on change: this runs every frame, and a label assignment is a
            // string allocation plus a TMP mesh rebuild.
            if (Mathf.Abs(zoom - shownZoom) > 0.001f)
            {
                shownZoom = zoom;
                var tex = shots[index];
                float native = tex.width > 0 ? size.x / tex.width * 100f : 100f;
                zoomLbl.Set(Mathf.RoundToInt(native) + "%");
            }
        }
    }
}
