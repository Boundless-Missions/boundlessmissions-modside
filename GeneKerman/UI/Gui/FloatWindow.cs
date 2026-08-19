/*
 * UI/Gui/FloatWindow.cs – A draggable window on the sidebar's Canvas.
 *
 * The sidebar is one centred panel with a tab strip: exactly one screen at a
 * time, and it owns the middle of the display while it is open. That is wrong for
 * the flows that are *about* what is behind them — submitting a contract means
 * looking at the craft on the build stage or the ship in flight while you read
 * what the mission still wants. Those get a window instead: same Canvas, same
 * theme, same builder, but positioned by the player and independent of whether
 * the sidebar itself is open.
 *
 * Windows are children of a layer above the sidebar panel and below the image
 * viewer and the toasts, so a lightbox and a notification still paint over them.
 * Within the layer, the last child is on top, which is what BringToFront does.
 *
 * Everything load-bearing about the Canvas is the controller's and is inherited
 * for free by living on it: the render gate (F2 and capture hide the whole
 * canvas, so no window can appear in a screenshot), the font recovery, the sprite
 * refresh, and the InputLockManager lock — SidebarController.PointerOverSidebar
 * asks every open window, so a click or a drag on one never reaches the game.
 *
 * Two things here are the window's own:
 *
 *  1. The drag is applied in canvas units, not screen pixels. The CanvasScaler
 *     maps a 1920x1080 design onto the real window, so a raw pointer delta moves
 *     the window further than the pointer on any display above that height.
 *  2. Position is clamped into the canvas on every drag *and* on every show. A
 *     window remembered from a larger resolution would otherwise open off screen
 *     with no way to reach its header and drag it back.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GeneKerman.UI.Gui
{
    /// <summary>
    /// A panel that lives in a <see cref="FloatWindow"/> rather than in the sidebar's
    /// tab strip. Identical to any other panel — it is the same base class — except
    /// that it can close the window it is shown in, which is what a Cancel button and
    /// a finished flow both need.
    /// </summary>
    internal abstract class WindowPanel : SidebarPanel
    {
        internal FloatWindow Window;

        protected void CloseWindow() => Window?.Hide();

        /// <summary>
        /// The window was closed — by its own X, by Escape, or by teardown, none of
        /// which go through the panel. A flow that set something up outside itself
        /// undoes it here: the submission screen pauses Physics Range Extender while
        /// it is up, and a close that skipped this would leave it paused for the rest
        /// of the session.
        /// </summary>
        internal virtual void OnWindowClosed() { }
    }

    internal sealed class FloatWindow
    {
        /// <summary>
        /// Where each window was last dragged to, by key, for this session. Static
        /// because the window itself is rebuilt on nothing — but a player who moved
        /// the submit window out of the way of the VAB parts list means it to stay
        /// there for the next contract too.
        /// </summary>
        private static readonly Dictionary<string, Vector2> positions = new Dictionary<string, Vector2>();

        private readonly float width;
        private readonly float height;
        private readonly Vector2 defaultPos;
        private readonly WindowPanel panel;

        /// <summary>Names the GameObject and keys the remembered position. The panel's
        /// own title, so a window and the screen inside it cannot drift apart.</summary>
        private string Key => panel.Title;

        private El root;
        private El body;
        private RectTransform layer;
        private bool visible;

        public bool IsOpen => visible;

        /// <summary>Sibling index within the window layer — its z-order. Used to find
        /// the topmost open window, which is the one Escape closes.</summary>
        internal int Depth => root != null ? root.Rt.GetSiblingIndex() : -1;

        /// <param name="defaultOffset">
        /// Where the window first appears, as an offset from the screen centre in
        /// canvas units. The submit window sits right of centre so the craft it is
        /// about is not behind it.
        /// </param>
        internal FloatWindow(WindowPanel content, float width, float height, Vector2 defaultOffset)
        {
            panel = content;
            this.width = width;
            this.height = height;
            defaultPos = defaultOffset;
        }

        // ── Construction ────────────────────────────────────────────────────

        internal void Build(El windowLayer, SidebarController controller)
        {
            layer = windowLayer.Rt;

            root = UIF.Root(Key, layer);
            root.Rt.anchorMin = new Vector2(0.5f, 0.5f);
            root.Rt.anchorMax = new Vector2(0.5f, 0.5f);
            root.Rt.pivot = new Vector2(0.5f, 0.5f);
            root.Rt.sizeDelta = new Vector2(width, height);

            Vector2 start;
            root.Rt.anchoredPosition = positions.TryGetValue(Key, out start) ? start : defaultPos;

            root.Bg(Theme.PanelBackground, Theme.Radius, Theme.Border)
                .Column(Theme.Space2)
                .Pad(Theme.Space3);
            // Same two reasons as the sidebar panel: eat clicks so they never reach
            // the game behind, and hand the wheel down to whatever list is under the
            // pointer instead of swallowing it at the background.
            root.Raycast(true);
            root.Go.AddComponent<ScrollForwarder>();

            BuildHeader();

            body = UIF.Box(root, "Body").Column(0f).Flex(1f, 1f);
            panel.Window = this;
            panel.Attach(body, controller);

            root.Active(false);
        }

        private void BuildHeader()
        {
            // The header is the drag handle, so unlike the sidebar's it is painted:
            // an invisible strip is a grab target the player cannot see, and the
            // Image is also what makes it a raycast target at all.
            var header = UIF.Box(root, "Header")
                            .Row(Theme.Space2)
                            .H(28)
                            .Bg(Theme.Secondary, Theme.RadiusSm)
                            .Pad(Theme.Space2, Theme.Space1, 0, 0);
            header.Raycast(true);

            UIF.Label(header, panel.Title, Theme.FontSm).Bold();
            UIF.Grow(header);
            UIF.Button(header, "X", Hide, BtnStyle.Ghost, 22).E.W(26);

            var drag = header.Add<WindowDrag>();
            drag.Bind(root.Rt, layer, BringToFront, Remember);
        }

        // ── Show / hide ─────────────────────────────────────────────────────

        public void Show()
        {
            if (root == null) return;

            visible = true;
            root.Active(true);
            // The resolution may have changed since this position was recorded.
            WindowDrag.Clamp(root.Rt, layer);
            BringToFront();
            panel.SetVisible(true);
            panel.MarkDirty();
        }

        public void Hide()
        {
            if (root == null || !visible) return;

            visible = false;
            panel.SetVisible(false);
            root.Active(false);
            // After the flag is cleared, so a panel that closes itself in response
            // (SubmitPanel does, through the session) lands on a no-op rather than
            // recursing back into here.
            panel.OnWindowClosed();
        }

        public void BringToFront() => root?.Rt.SetAsLastSibling();

        private void Remember()
        {
            if (root != null) positions[Key] = root.Rt.anchoredPosition;
        }

        // ── Frame ───────────────────────────────────────────────────────────

        internal void Tick()
        {
            if (!visible) return;
            panel.Tick();
        }

        /// <summary>True while the pointer is over this window — the controller folds
        /// this into the same input lock the sidebar takes.</summary>
        internal bool PointerOver()
        {
            if (!visible || root == null) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(root.Rt, Input.mousePosition, null);
        }

        internal void OnSceneChange()
        {
            panel?.OnSceneChanged();
            panel?.MarkDirty();
        }

        internal void Destroy()
        {
            if (visible) Hide();
            panel.SetVisible(false);
            panel.Window = null;
            panel.Detach();
            root = null;
            body = null;
            layer = null;
        }
    }

    /// <summary>
    /// Drags the window its handle belongs to. On a ScreenSpaceOverlay canvas the
    /// pointer delta is in screen pixels while anchoredPosition is in canvas units,
    /// and the CanvasScaler makes those two different numbers on any display that
    /// isn't the 1080p reference — so the delta is divided by the scale factor, or
    /// the window outruns the pointer.
    /// </summary>
    internal sealed class WindowDrag : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        private RectTransform target;
        private RectTransform bounds;
        private Action focused;
        private Action moved;
        private Canvas canvas;

        internal void Bind(RectTransform window, RectTransform area, Action onFocus, Action onMoved)
        {
            target = window;
            bounds = area;
            focused = onFocus;
            moved = onMoved;
        }

        public void OnPointerDown(PointerEventData eventData) => focused?.Invoke();

        public void OnDrag(PointerEventData eventData)
        {
            if (target == null) return;

            if (canvas == null) canvas = target.GetComponentInParent<Canvas>();
            float scale = (canvas != null && canvas.scaleFactor > 0f) ? canvas.scaleFactor : 1f;

            target.anchoredPosition += eventData.delta / scale;
            Clamp(target, bounds);
            moved?.Invoke();
        }

        /// <summary>
        /// Keep the window inside the canvas. Both rects are centred, so the reachable
        /// offset is half the slack in each axis; a window larger than the area it is
        /// in has no slack at all and is pinned to the centre rather than clamped to a
        /// negative range.
        /// </summary>
        internal static void Clamp(RectTransform window, RectTransform area)
        {
            if (window == null || area == null) return;

            float slackX = Mathf.Max(0f, (area.rect.width - window.rect.width) * 0.5f - EdgeMarginStatic);
            float slackY = Mathf.Max(0f, (area.rect.height - window.rect.height) * 0.5f - EdgeMarginStatic);

            var p = window.anchoredPosition;
            window.anchoredPosition = new Vector2(
                Mathf.Clamp(p.x, -slackX, slackX),
                Mathf.Clamp(p.y, -slackY, slackY));
        }

        private const float EdgeMarginStatic = 12f;
    }
}
