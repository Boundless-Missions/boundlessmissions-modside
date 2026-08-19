/*
 * UI/Gui/ToastHost.cs – Top-right transient notifications, on the sidebar Canvas.
 *
 * Replaces the IMGUI UI/NotificationPopup.cs. That one drew each toast into a
 * hardcoded 60px box with the title clipped to 20px and the message to 28px, so
 * anything past roughly one short line was cut off mid-word — every "Couldn't
 * read the screenshot. Try again." lost its tail. Height here is content-driven
 * instead: the toast is a Column whose parent is a Column, so the layout asks
 * each label for its preferred height *at the width it has already been given*
 * and the card is however tall the text needs. That is also why neither label
 * gets a ContentSizeFitter — see UIF.Body(), a fitter would measure the
 * unwrapped width and answer one line, which is the bug we are removing.
 *
 * Four things carried over from the IMGUI version, all deliberate:
 *
 *  1. Newest on top. New cards go to SetAsFirstSibling rather than appending,
 *     which is what the old reverse-iterated draw loop did positionally.
 *  2. At most MaxToasts on screen, oldest evicted. A burst (a craft import
 *     raising one per craft) must not paper the screen.
 *  3. Clicking a toast opens what it is about, and dismisses it.
 *  4. It lives on the sidebar's Canvas, so it inherits that canvas's render
 *     gate — which is exactly the cascade in GeneKermanMod.OnGUI that used to
 *     decide whether Draw() ran at all (hidden UI, update gate, data sharing
 *     off, consent not accepted). Nothing about when a toast may appear
 *     changed in the port.
 *
 * It is built as the Canvas's *last* child, after the image viewer, so it
 * paints over everything the sidebar owns. A toast is time-critical — it is the
 * only notice a player in flight gets that a share failed or their session
 * expired — and the lightbox is a full-screen backdrop that would otherwise
 * swallow both the pixels and the click.
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GeneKerman.UI.Gui
{
    internal sealed class ToastHost
    {
        // ── Geometry (px at the 1920x1080 reference resolution) ─────────────

        private const float ToastWidth = 340f;
        private const float TopMargin = 50f;
        private const float RightMargin = 20f;
        private const float Spacing = 8f;

        private const float Duration = 8f;
        private const float FadeSeconds = 0.5f;
        private const int MaxToasts = 5;

        private sealed class Toast
        {
            public El Root;
            public CanvasGroup Group;
            public string ContractId;
            public string LocalAction;
            public float Shown;
            public bool Closing;      // clicked: fade it out rather than popping it
        }

        private El host;
        private readonly List<Toast> toasts = new List<Toast>();

        /// <summary>Set by SidebarController so a toast can open what it is about.</summary>
        private SidebarController owner;

        // ── Construction ────────────────────────────────────────────────────

        public void Build(El root, SidebarController controller)
        {
            owner = controller;

            host = UIF.Root("Toasts", root.Rt);

            // Pinned to the screen's top-right corner with the pivot in that same
            // corner, so the stack grows *downwards* as toasts are added and the
            // first one never moves. Anchoring by the top edge is what keeps the
            // margins honest on every aspect ratio the CanvasScaler produces.
            host.Rt.anchorMin = new Vector2(1f, 1f);
            host.Rt.anchorMax = new Vector2(1f, 1f);
            host.Rt.pivot = new Vector2(1f, 1f);
            host.Rt.anchoredPosition = new Vector2(-RightMargin, -TopMargin);
            host.Rt.sizeDelta = new Vector2(ToastWidth, 0f);

            // The fitter is legitimate here and nowhere below it: this element's
            // parent is the plain stretched canvas root, not a layout group. Height
            // only — the width is fixed above, and letting it fit horizontally would
            // shrink the column to the longest unwrapped line.
            host.Column(Spacing).Fit(horizontal: false, vertical: true);
        }

        // ── Raising ─────────────────────────────────────────────────────────

        public void Show(string title, string message, string contractId = null,
                         string localAction = null)
        {
            Debug.Log("[GeneKerman] Notification: " + title + " — " + message);

            if (host == null) return;     // canvas never built; the feed still records it

            // Evict from the *oldest* end, which is the bottom of the stack.
            while (toasts.Count >= MaxToasts)
                Remove(toasts.Count - 1);

            var card = UIF.Card(host, "Toast").Column(Theme.Space1).Pad(Theme.Space3);

            var toast = new Toast
            {
                Root = card,
                Group = card.Go.AddComponent<CanvasGroup>(),
                ContractId = contractId,
                LocalAction = localAction,
                Shown = Time.unscaledTime,
            };

            // Body(), not Ellipsis(), on both: a toast that cannot show its own
            // sentence has no other copy on screen to fall back to. The title wraps
            // too — several are long enough to need it at 340px ("Craft scheduled
            // for removal", "Crew arrived without their profession").
            // Fmt.Plain: a toast carries server text verbatim, and a title written for
            // Discord opens with an emoji this font draws as a box.
            UIF.Label(card, Fmt.Plain(title) ?? "", Theme.FontSm, Theme.Primary).Bold().Body();
            if (!string.IsNullOrEmpty(message))
                UIF.Muted(card, Fmt.Plain(message)).Body();

            // The Card's own Image is the raycast target, so the whole card is the
            // button. No visual transition is wired: a toast is not a control the
            // player hunts for, and a hover tint on something that is fading looks
            // like a rendering fault.
            var btn = card.Go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => Activate(toast));

            card.Rt.SetAsFirstSibling();
            toasts.Insert(0, toast);

            // Both off until the first Tick agrees: a card that is invisible must not
            // be clickable, and Tick is the only thing that knows how far the fade
            // has got. CanvasGroup defaults blocksRaycasts to true, so this matters.
            toast.Group.alpha = 0f;
            toast.Group.blocksRaycasts = false;
        }

        // ── Frame ───────────────────────────────────────────────────────────

        /// <summary>Driven by SidebarController.Tick, which only runs while the
        /// canvas is actually rendering. A toast raised behind a gate therefore
        /// still ages — its clock is wall time, not frames — and is dropped on the
        /// first tick after the gate lifts if its eight seconds are already gone.
        /// That matches what the IMGUI version did (its Draw simply never ran), and
        /// it is the behaviour we want: a stale notice about a share that failed
        /// two minutes ago is noise, and the durable copy is in the feed.</summary>
        public void Tick()
        {
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                var t = toasts[i];
                if (t.Root == null || t.Root.Go == null) { toasts.RemoveAt(i); continue; }

                // Unscaled: a toast must fade at the same rate in a 100,000x warp as
                // it does on the launchpad.
                float elapsed = Time.unscaledTime - t.Shown;
                if (elapsed >= Duration) { Remove(i); continue; }

                float alpha;
                if (elapsed < FadeSeconds) alpha = elapsed / FadeSeconds;
                else if (elapsed > Duration - FadeSeconds) alpha = (Duration - elapsed) / FadeSeconds;
                else alpha = 1f;

                t.Group.alpha = Mathf.Clamp01(alpha);
                // Stop a fading card from eating a click aimed at whatever is behind
                // it — the alpha says it is on its way out, the raycast should agree.
                t.Group.blocksRaycasts = !t.Closing && alpha > 0.5f;
            }
        }

        /// <summary>
        /// True while the pointer is over a live toast. Folded into
        /// SidebarController.PointerOverSidebar so clicking one does not also swing
        /// the flight camera behind it: a GraphicRaycaster stops other uGUI, never
        /// KSP's raw-Input reads.
        /// </summary>
        public bool PointerOverToast()
        {
            if (toasts.Count == 0) return false;

            Vector2 mouse = Input.mousePosition;
            for (int i = 0; i < toasts.Count; i++)
            {
                var t = toasts[i];
                if (t.Root == null || t.Root.Go == null || !t.Group.blocksRaycasts) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(t.Root.Rt, mouse, null))
                    return true;
            }
            return false;
        }

        /// <summary>Drop every toast — teardown, and any path that stops the canvas
        /// rendering, since the stack would otherwise reappear mid-fade later.</summary>
        public void Clear()
        {
            for (int i = toasts.Count - 1; i >= 0; i--) Remove(i);
        }

        // ── Internals ───────────────────────────────────────────────────────

        private void Activate(Toast t)
        {
            var mod = GeneKermanMod.Instance;
            if (mod != null && mod.Api != null && mod.Api.IsLinked && owner != null)
            {
                // The sidebar, not the classic window: the toolbar button opens the
                // sidebar now, so that is where a player who clicks a notice expects
                // to land. Both routes below are the controller's own, which is what
                // fires the target panel's OnShown — selecting it from out here would
                // leave the panel visible but never told it was.
                owner.SetOpen(true);

                if (!string.IsNullOrEmpty(t.ContractId)) owner.ShowContract(t.ContractId);
                // No contract, but something to press: land on the feed, where the
                // button is. Clicking a toast about a problem and arriving anywhere
                // else means hunting for the fix it just offered.
                else if (!string.IsNullOrEmpty(t.LocalAction)) owner.ShowNotifications();
            }

            // Fade the card out instead of destroying it under the click. Rewinding
            // Shown so only the trailing fade is left keeps one code path — Tick
            // still owns the removal.
            t.Closing = true;
            t.Group.blocksRaycasts = false;
            float remaining = Duration - (Time.unscaledTime - t.Shown);
            if (remaining > FadeSeconds) t.Shown = Time.unscaledTime - (Duration - FadeSeconds);
        }

        private void Remove(int index)
        {
            if (index < 0 || index >= toasts.Count) return;

            var t = toasts[index];
            toasts.RemoveAt(index);
            if (t.Root == null || t.Root.Go == null) return;

            // Unparent before destroying: Object.Destroy is deferred to the end of
            // the frame, and a card still in the column until then leaves a gap the
            // remaining toasts sit below for one frame — a visible jump on every
            // expiry. Same reason as UIF.ClearChildren.
            t.Root.Rt.SetParent(null, false);
            Object.Destroy(t.Root.Go);
        }
    }
}
