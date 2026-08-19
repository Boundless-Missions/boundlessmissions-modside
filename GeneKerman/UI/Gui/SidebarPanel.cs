/*
 * UI/Gui/SidebarPanel.cs – Base class for the sidebar's content panels.
 *
 * Panels are retained-mode: Rebuild() constructs the hierarchy once and it
 * stays until something invalidates it. That is the whole reason for moving off
 * IMGUI, so a panel that rebuilds itself every frame would be a bug, not a
 * style choice — hence the explicit dirty flag.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal abstract class SidebarPanel
    {
        /// <summary>Shown on the panel switcher. Also names the host GameObject.</summary>
        public abstract string Title { get; }

        /// <summary>
        /// True when this panel wants the sidebar widened for a master-detail layout
        /// — a list on the left and the selected item's detail on the right, which is
        /// what the web UI does above `xl`. Polled every frame by the controller, so
        /// it must be a cheap property, not a computation.
        /// </summary>
        internal virtual bool WantsWide => false;

        /// <summary>
        /// True when this panel is worth showing with no server behind it. False for
        /// every panel that fetches, because under the version gate each of those
        /// requests comes back 426 — the sidebar hides them rather than offering a
        /// screen that can only fail (see SidebarController's limited mode).
        /// </summary>
        internal virtual bool WorksOffline => false;

        /// <summary>The element this panel builds into. Null before Attach.</summary>
        protected El Host { get; private set; }

        /// <summary>
        /// The sidebar this panel is mounted in. Panels use it for the few things
        /// that are screen-wide rather than panel-local — today, the image viewer,
        /// which has to cover the whole viewport and so cannot live under Host.
        /// </summary>
        private SidebarController owner;

        private bool dirty = true;
        private bool visible;

        internal void Attach(El host, SidebarController controller)
        {
            Host = host;
            owner = controller;
            dirty = true;
        }

        /// <summary>
        /// Open <paramref name="images"/> full-screen, zoomable and pannable.
        ///
        /// The viewer borrows the textures; it does not take ownership. A panel that
        /// destroys them must call <see cref="CloseImages"/> first — see
        /// ContractsPanel.ClearPreview.
        /// </summary>
        protected void ShowImages(IList<Texture2D> images, IList<string> captions, int start, string title)
            => owner?.ShowImages(images, captions, start, title);

        protected void CloseImages() => owner?.CloseImages();

        /// <summary>
        /// Switch to the inbox and open a contract there — for a panel holding a
        /// reference to one it cannot show itself (a notification about a contract).
        /// No-op when the inbox is not mounted.
        /// </summary>
        protected void OpenContract(string contractId) => owner?.ShowContract(contractId);

        internal void Detach()
        {
            Host = null;
            owner = null;
        }

        internal void SetVisible(bool value)
        {
            bool wasVisible = visible;
            visible = value;
            Host?.Active(value);

            if (!value)
            {
                if (wasVisible) OnHidden();
                return;
            }

            dirty = true;
            if (!wasVisible) OnShown();
        }

        /// <summary>
        /// The panel just stopped being visible. Where a panel holds something Unity
        /// will not reclaim on its own — a Texture2D above all — this is where it
        /// goes back.
        /// </summary>
        internal virtual void OnHidden() { }

        /// <summary>
        /// A scene load is starting. Unity destroys textures across one, so anything
        /// a panel downloaded is about to become a pile of null references it will
        /// never re-fetch — the cache key still says it has them. Drop them here.
        /// </summary>
        internal virtual void OnSceneChanged() { }

        /// <summary>
        /// The panel just became visible. Panels that fetch on demand reset their
        /// "already asked" latch here — see the note in Poll implementations about
        /// why a failed fetch must not be retried every frame.
        /// </summary>
        internal virtual void OnShown() { }

        /// <summary>Rebuild on the next Tick. Cheap to call repeatedly.</summary>
        internal void MarkDirty() => dirty = true;

        // ── Action feedback ─────────────────────────────────────────────────
        //
        // The sidebar's equivalent of the classic window's status line. An action's result
        // has to land somewhere the player is actually looking: the classic window
        // may well be closed, and its status line with it.

        /// <summary>Result of the last action, or empty. Cleared on rebuild triggers.</summary>
        protected string Status { get; private set; } = "";

        /// <summary>True while the last action reported failure — colours the line.</summary>
        protected bool StatusIsError { get; private set; }

        /// <summary>Set while an action is in flight, so buttons can disable themselves.</summary>
        protected bool Busy { get; private set; }

        /// <summary>
        /// Begin an action: mark busy, clear the previous result, and hand back a
        /// completion that records the outcome and re-renders. Every shared action
        /// coroutine invokes its onDone exactly once, so this cannot leave Busy set.
        /// </summary>
        protected Action<bool, string> BeginAction()
        {
            Busy = true;
            Status = "";
            StatusIsError = false;
            MarkDirty();

            return (ok, message) =>
            {
                Busy = false;
                Status = message ?? "";
                StatusIsError = !ok;
                MarkDirty();
            };
        }

        protected void ClearStatus()
        {
            Status = "";
            StatusIsError = false;
        }

        /// <summary>Render the status line, if there is one. Panels call this where they want it.</summary>
        protected void DrawStatus(El parent)
        {
            if (string.IsNullOrEmpty(Status)) return;
            UIF.Label(parent, Status, Theme.FontXs,
                      StatusIsError ? Theme.Destructive : Theme.Primary).Body();
        }

        internal void Tick()
        {
            if (!visible || Host == null) return;

            // A rebuild destroys the hierarchy, and with it the caret, the selection
            // and the half-typed contents of any text box in it. Something as
            // ordinary as a notification arriving marks every panel dirty, so the
            // rebuild waits for the player to finish typing rather than the other
            // way round — the flag stays set and fires on the next tick after blur.
            if (dirty && !(owner != null && owner.IsTyping))
            {
                dirty = false;
                Host.ClearChildren();
                Rebuild();
            }

            Poll();
        }

        /// <summary>Construct the panel's contents into <see cref="Host"/>.</summary>
        protected abstract void Rebuild();

        /// <summary>
        /// Runs every frame the panel is visible. For watching data the panel does
        /// not own — a fetch completing has no callback into here, so the panel
        /// notices by comparing. Keep it to a few comparisons.
        /// </summary>
        protected virtual void Poll() { }
    }
}
