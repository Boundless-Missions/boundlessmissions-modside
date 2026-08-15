/*
 * UI/Gui/ScrollForwarder.cs – Makes the wheel work anywhere on the panel.
 *
 * Two halves of the same problem, in opposite directions: ScrollForwarder pushes a
 * wheel event that landed *above* the list down into it, and ScrollRelay pushes one
 * that was swallowed by a widget back *up* to the list.
 *
 * uGUI delivers a scroll event to whatever the pointer raycast hits, then bubbles
 * it up the hierarchy to the first IScrollHandler. The panel's ScrollRect is a
 * *descendant* of the panel background, so a wheel over the header, the tab strip
 * or the panel's padding bubbles upward past the panel and out — away from the
 * ScrollRect, never into it.
 *
 * This sits on the panel root and redirects those events down to whichever
 * ScrollRect is currently on screen. Events that start inside the list never get
 * here: ScrollRect handles them first and bubbling stops at the first handler.
 */

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GeneKerman.UI.Gui
{
    internal sealed class ScrollForwarder : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            // Only one panel's subtree is active at a time, and GetComponentInChildren
            // skips inactive objects by default — so this resolves to the visible
            // panel's list without the controller having to wire anything up.
            var target = GetComponentInChildren<ScrollRect>();
            if (target != null) target.OnScroll(eventData);
        }
    }

    /// <summary>
    /// Hand the wheel back to the enclosing list.
    ///
    /// An input field is itself an IScrollHandler — TMP uses it to scroll a multi-line
    /// box — so Unity stops bubbling the moment the pointer is over one, and the wheel
    /// does nothing at all on a single-line field. Since every component implementing
    /// the interface *on the handling GameObject* is executed, adding this beside the
    /// field is enough: TMP still gets its event, and the list gets one too.
    ///
    /// GetComponentInParent finds the nearest enclosing ScrollRect, which is the one
    /// the field is visually inside — a list nested elsewhere in the panel is a
    /// sibling subtree, not an ancestor, so it cannot be picked up by mistake.
    /// </summary>
    internal sealed class ScrollRelay : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            var target = GetComponentInParent<ScrollRect>();
            if (target != null) target.OnScroll(eventData);
        }
    }
}
