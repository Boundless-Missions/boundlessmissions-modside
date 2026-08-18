/*
 * UI/Gui/ScrollMemory.cs – Keeping a scrolled list where the player left it.
 *
 * The sidebar is retained-mode, but a rebuild is a *destroy and re-create* of the
 * whole subtree (SidebarPanel.Tick), and a freshly built ScrollRect sits at the top.
 * That is invisible on a short panel and unusable on a long one: the contract form
 * is ten screens tall, and picking a recipient, ticking a switch or choosing a date
 * all mark the panel dirty — so every answer threw the player back to the first
 * question.
 *
 * The scroll offset therefore has to outlive the hierarchy that holds it, which is
 * what this is: a static offset per *logical* list, keyed by a caller-chosen string
 * rather than by anything about the GameObject (there is nothing stable about one —
 * it is a different object every rebuild). Callers pass the key to UIF.ScrollView
 * and forget it when the context genuinely changes, which is what makes a fresh
 * contract form open at the top while a rebuilt one does not.
 *
 * Pixels, not ScrollRect.verticalNormalizedPosition: a rebuild is very often a
 * rebuild into a *different height* (a section expanded, a row filtered out), and a
 * normalized position keeps a fraction rather than a place, so the list would land
 * somewhere near where it was and never at it.
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GeneKerman.UI.Gui
{
    internal sealed class ScrollMemory : MonoBehaviour
    {
        private static readonly Dictionary<string, float> offsets = new Dictionary<string, float>();

        /// <summary>
        /// How many frames the restore is allowed to wait for a content height.
        ///
        /// The first attempt forces the layout itself and normally succeeds. The
        /// budget covers the case it cannot: content that is still measuring
        /// something asynchronous — a TMP label whose font atlas is mid-rebuild, an
        /// avatar row sized from a texture that has not decoded — where the height
        /// is briefly zero and restoring against it would clamp to the top, which is
        /// the very bug this file exists to fix.
        /// </summary>
        private const int SettleFrames = 6;

        /// <summary>
        /// The live instance for each key — the one allowed to write.
        ///
        /// A rebuild leaves two of these alive at once: Object.Destroy is deferred to
        /// the end of the frame, so the outgoing ScrollRect still ticks beside its
        /// replacement, and both answer to the same key. Whichever ran last would win
        /// the write, which on a rebuild into a shorter list means the stale
        /// out-of-range offset overwriting the clamped one that was just restored.
        /// Binding is what settles it: the newer instance is bound later and takes
        /// ownership, and the outgoing one goes quiet.
        /// </summary>
        private static readonly Dictionary<string, ScrollMemory> owners =
            new Dictionary<string, ScrollMemory>();

        /// <summary>Which list this is. Set through Bind; never null in practice.</summary>
        private string key;

        private ScrollRect sr;
        private bool restored;
        private int waited;

        /// <summary>Name this list and claim it. Called by UIF.ScrollView.</summary>
        internal void Bind(string listKey)
        {
            key = listKey;
            if (!string.IsNullOrEmpty(listKey)) owners[listKey] = this;
        }

        /// <summary>
        /// Drop a remembered offset, so the next list built under this key opens at
        /// the top. For a context change the player would read as "a different list"
        /// — a form reset, a different contract — where resuming someone else's
        /// scroll position is worse than starting fresh.
        /// </summary>
        internal static void Forget(string key)
        {
            if (!string.IsNullOrEmpty(key)) offsets.Remove(key);
        }

        private void Awake() => sr = GetComponent<ScrollRect>();

        private void OnDestroy()
        {
            ScrollMemory owner;
            if (!string.IsNullOrEmpty(key) && owners.TryGetValue(key, out owner) && ReferenceEquals(owner, this))
                owners.Remove(key);
        }

        private void LateUpdate()
        {
            if (sr == null || sr.content == null || sr.viewport == null) return;
            if (string.IsNullOrEmpty(key)) return;

            ScrollMemory owner;
            if (owners.TryGetValue(key, out owner) && !ReferenceEquals(owner, this)) return;

            if (!restored) { Restore(); return; }

            offsets[key] = sr.content.anchoredPosition.y;
        }

        private void Restore()
        {
            float want;
            if (!offsets.TryGetValue(key, out want) || want <= 0f) { restored = true; return; }

            // Force the measurement rather than wait for it. Layout is serviced on
            // Canvas.willRenderCanvases, which is *after* LateUpdate, so everything
            // built moments ago still measures zero here — and restoring against a
            // zero height clamps to the top, which is the bug. Doing it inline costs
            // one layout pass per rebuild and keeps the restore invisible; waiting a
            // frame would show the top of the list and then jump to the offset.
            //
            // From the layout *root*, not from the content: the content's own height
            // comes from its ContentSizeFitter and would rebuild on its own, but the
            // viewport's comes from the panel's column groups several levels up, and
            // both halves are needed to know how far this list can actually scroll.
            LayoutRebuilder.ForceRebuildLayoutImmediate(LayoutRoot(sr.viewport));

            float view = sr.viewport.rect.height;
            float content = sr.content.rect.height;

            // Nothing measurable yet: something in this subtree is still sizing
            // itself from an asset that has not arrived. Try again next frame rather
            // than restore against numbers that would clamp to the top — but only
            // for a few, since a genuinely short list also measures this way and
            // must not retry forever.
            if ((view <= 0f || content <= 0f) && ++waited < SettleFrames) return;

            var p = sr.content.anchoredPosition;
            // Clamped, because the rebuild may have produced a shorter list than the
            // one the offset was taken from — a section collapsed, a filter applied.
            p.y = Mathf.Clamp(want, 0f, Mathf.Max(0f, content - view));
            sr.content.anchoredPosition = p;
            restored = true;
        }

        /// <summary>
        /// The outermost ancestor still under layout control — as far up as a
        /// rebuild has to start for this list's viewport to end up its real size.
        /// Stops at the sidebar's own root, since nothing above it is a layout
        /// group, so this never reaches into KSP's canvases.
        /// </summary>
        private static RectTransform LayoutRoot(RectTransform from)
        {
            var root = from;
            for (var t = from.parent as RectTransform; t != null; t = t.parent as RectTransform)
                if (t.GetComponent(typeof(ILayoutGroup)) != null) root = t;

            return root;
        }
    }
}
