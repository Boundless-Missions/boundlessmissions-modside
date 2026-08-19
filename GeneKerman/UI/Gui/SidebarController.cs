/*
 * UI/Gui/SidebarController.cs – The in-game uGUI sidebar.
 *
 * A centred panel that expands sideways out of the middle of the screen. Built
 * once at Start() and parented to the mod's DontDestroyOnLoad GameObject, so the
 * hierarchy survives every scene change; only GPU resources die, and
 * Sprites.Refresh() puts those back.
 *
 * The toolbar button is the only way in — there is no pull-out tab. That removes
 * the edge-docking the tab existed to serve: a centred panel has no near edge, so
 * the VAB/SPH mirroring (KSP owns the left edge there) has nothing left to do.
 *
 * The open animation scales the panel's transform on X rather than animating its
 * width. A width animation would re-run the whole layout every frame of the
 * expand — and a layout squeezed to nearly zero makes every Ellipsis label render
 * nothing at all, so the panel would flash empty on the way out. Scaling is one
 * transform write and touches no layout.
 *
 * Five things here are load-bearing and easy to lose in a later refactor:
 *
 *  1. A Canvas renders independently of OnGUI, so `uiHidden` does nothing for
 *     it. Without SetHidden() on GameEvents.onHideUI the sidebar appears in
 *     every screenshot, cinematic capture and F2 press.
 *  2. KSP's flight input reads raw Input, and a GraphicRaycaster only stops
 *     clicks reaching *other uGUI*. Without an InputLockManager lock, dragging
 *     on the panel rotates the camera underneath it.
 *  3. That lock must be released on close AND in Destroy(). A leaked control
 *     lock soft-bricks the player's game — it is the worst failure available
 *     here, and it outlives the mod's own UI.
 *  4. The gate cascade in GeneKermanMod.OnGUI (update-required, data-sharing
 *     off, consent not accepted) is where *nothing else in the mod draws*. The
 *     sidebar has to observe it too; consent especially, since rule 8.1 means
 *     nothing happens before opt-in.
 *  5. The expand runs off Time.unscaledDeltaTime in Tick(), not a coroutine, so
 *     a scene load mid-animation cannot strand the panel half-open.
 *
 * The canvas also carries the draggable windows (UI/Gui/FloatWindow.cs), for the
 * flows that are read *against* the scene behind them rather than instead of it —
 * today, submitting a contract. They open and close independently of the panel,
 * and living here is what gives them all five of the above for nothing: the same
 * render gate, the same input lock (PointerOverSidebar asks every open window),
 * the same font and sprite recovery, and the same release-everything teardown.
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace GeneKerman.UI.Gui
{
    internal sealed class SidebarController
    {
        // ── Geometry (px at the 1920x1080 reference resolution) ─────────────

        /// <summary>List-only width. The panel's resting size.</summary>
        private const float PanelWidth = 400f;

        /// <summary>
        /// Master-detail width, requested by a panel that has something selected
        /// (see SidebarPanel.WantsWide). The web UI switches to two columns at `xl`
        /// for the same reason: replacing the list with the detail wastes half the
        /// width and loses your place in the inbox.
        /// </summary>
        private const float PanelWideWidth = 880f;

        private const float VerticalMargin = 48f;

        /// <summary>
        /// Long enough that the deceleration is actually visible. The old edge slide ran
        /// in 0.18s, which an exponential ease would spend almost entirely at full
        /// speed — the curve only reads as a curve if there is time to see it flatten.
        /// </summary>
        private const float ExpandSeconds = 0.25f;

        private const string LockId = "GK_Sidebar";
        private const string TextLockId = "GK_SidebarText";
        private const string PauseLockId = "GK_SidebarPause";

        /// <summary>
        /// Everything the panel must stop from reaching the game while the pointer
        /// is on it. Deliberately not a blanket lock: the pause menu and the app
        /// launcher stay live, because locking the player out of their own escape
        /// menu because a sidebar is open is worse than the click-through it fixes.
        /// </summary>
        private const ControlTypes LockMask =
            ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.CAMERACONTROLS | ControlTypes.CAMERAMODES |
            ControlTypes.TIMEWARP | ControlTypes.EDITOR_LOCK | ControlTypes.EDITOR_UI |
            ControlTypes.KSC_ALL | ControlTypes.TRACKINGSTATION_ALL | ControlTypes.ACTIONS_ALL;

        /// <summary>
        /// Held only while a text box inside the sidebar has focus, and everywhere on
        /// screen rather than over the panel — a keystroke has no cursor position.
        ///
        /// KEYBOARDINPUT is what KSP consults before a key means anything, and the
        /// quick-save pair is called out separately because those two are bound to
        /// bare keys: without this, typing `localhost:5022` in flight is also a
        /// quicksave, a map view and a stage.
        /// </summary>
        private const ControlTypes TextLockMask =
            LockMask | ControlTypes.KEYBOARDINPUT | ControlTypes.QUICKSAVE | ControlTypes.QUICKLOAD;

        // ── State ───────────────────────────────────────────────────────────
        private GameObject rootGo;
        private Canvas canvas;
        private RectTransform panelRect;
        private El contentHost;
        private readonly ImageViewer viewer = new ImageViewer();
        private readonly ToastHost toasts = new ToastHost();

        private bool built;
        private bool open;
        private float openAmount;       // 0 = closed, 1 = open
        private float currentWidth = PanelWidth;
        private bool hiddenByGame;      // F2 / capture in progress
        private bool lockHeld;
        private bool textLockHeld;
        private bool pauseLockHeld;     // Escape is ours while the panel is open
        private bool typing;            // a text box in the sidebar has focus
        private bool dragLatch;         // a drag that started on the panel
        private float lastCanvasScale = -1f;

        /// <summary>Scene the labels were last rebuilt for. See RefreshTextAfterSceneChange.</summary>
        private GameScenes lastScene = GameScenes.LOADING;
        private int textRefreshTicks;

        /// <summary>Poll ticks' worth of text rebuilds after a scene change (six = ~3s).</summary>
        private const int TextRefreshTicks = 6;

        /// <summary>The font the labels on screen are drawn with. See UpdateAssets.</summary>
        private TMP_FontAsset fontInUse;
        private float nextAssetCheck;

        /// <summary>
        /// How often the font and the sprite cache are checked for having been
        /// destroyed. Twice a second: a scene load takes seconds, so this is well
        /// inside "before the player looks", and the check costs two comparisons.
        /// </summary>
        private const float AssetCheckSeconds = 0.5f;

        private readonly List<SidebarPanel> panels = new List<SidebarPanel>();
        private SidebarPanel active;
        private El tabStrip;

        /// <summary>
        /// Where the draggable windows live (see FloatWindow). A plain stretched
        /// element with no Image of its own, so it is invisible to raycasts and a
        /// click anywhere it does not have a window still reaches the panel or the
        /// game underneath.
        /// </summary>
        private El windowLayer;
        private readonly List<FloatWindow> windows = new List<FloatWindow>();

        public bool IsOpen => open;

        // ── Construction ────────────────────────────────────────────────────

        /// <summary>
        /// Build the canvas and the whole hierarchy. Called once from
        /// GeneKermanMod.Start(); safe to call again (it no-ops).
        /// </summary>
        public void Build(Transform parent)
        {
            if (built) return;

            EnsureEventSystem();

            rootGo = new GameObject("GK_Sidebar", typeof(RectTransform));
            rootGo.layer = 5; // UI
            rootGo.transform.SetParent(parent, false);

            canvas = rootGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the game and KSP's own HUD, below its modal dialogs (which sit
            // in the high hundreds). Nudge this, don't redesign it, if something
            // covers the panel.
            canvas.sortingOrder = 100;

            // TMP's distance-field shader reads the glyph scale out of vertex channels
            // that a Canvas strips unless they are asked for. TMP only enables them
            // itself when it creates a sub-mesh (multi-material text: fallbacks,
            // sprites), which plain labels never do — so on a canvas built in code
            // they have to be set here, or the shader gets zeros and every glyph comes
            // out fully transparent: mesh present, material present, nothing on screen.
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
                                              AdditionalCanvasShaderChannels.Normal |
                                              AdditionalCanvasShaderChannels.Tangent;

            var scaler = rootGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // Match height: a left-edge panel should keep its proportions on an
            // ultrawide rather than growing with the extra horizontal space.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;

            rootGo.AddComponent<GraphicRaycaster>();

            BuildHierarchy();

            built = true;
            ApplyExpand();
            // Remembered so a later frame can tell "this install has no TMP font" from
            // "the font we were using has just been destroyed" — see UpdateAssets.
            fontInUse = Theme.Font;

            Debug.Log("[GeneKerman] Sidebar canvas built.");
        }

        /// <summary>
        /// KSP always has an EventSystem; creating a second one breaks its own UI
        /// (Unity logs "Multiple EventSystems in scene" and input goes to one of
        /// them at random). Only create one if the game somehow has none.
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            Debug.LogWarning("[GeneKerman] No EventSystem found — creating one for the sidebar.");
            var go = new GameObject("GK_EventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        private void BuildHierarchy()
        {
            var root = new El(rootGo).Stretch();

            BuildPanel(root);

            // Above the panel, below the lightbox and the toasts: a window is a
            // second surface the player arranges themselves, but a full-screen
            // image and a time-critical notice both outrank it.
            windowLayer = UIF.Root("Windows", root.Rt).Stretch();

            // Last child of the root, so it paints over the panel — a
            // ScreenSpaceOverlay canvas draws in hierarchy order, and there is no
            // second Canvas here to give it a sortingOrder of its own.
            viewer.Build(root);

            // Later still: a toast has to clear the viewer's full-screen backdrop,
            // which would otherwise cover both its pixels and its click.
            toasts.Build(root, this);
        }

        private void BuildPanel(El parent)
        {
            var panel = UIF.Root("Panel", parent.Rt);
            panelRect = panel.Rt;
            // Anchored to the screen's horizontal centre and stretched vertically, with
            // the pivot in the middle: that pivot is what makes the X scale below grow
            // the panel out of the centre in both directions rather than off one side.
            panelRect.anchorMin = new Vector2(0.5f, 0f);
            panelRect.anchorMax = new Vector2(0.5f, 1f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(PanelWidth, -VerticalMargin * 2f);
            panelRect.anchoredPosition = Vector2.zero;

            panel.Bg(Theme.PanelBackground, Theme.Radius, Theme.Border)
                 .Column(Theme.Space3)
                 .Pad(Theme.Space4);
            // The panel eats clicks so they never reach the game behind it. The
            // InputLockManager lock below covers what a raycast cannot (camera
            // drag, ship controls); this covers the rest.
            panel.Raycast(true);

            // ...which also means the panel background swallows wheel events that
            // would otherwise have found the list. Hand them down instead.
            panel.Go.AddComponent<ScrollForwarder>();

            // Header
            var header = UIF.Box(panel, "Header").Row(Theme.Space2).H(28);
            UIF.Label(header, "Boundless Missions", Theme.FontLg).Bold();
            UIF.Grow(header);
            UIF.Button(header, "X", () => SetOpen(false), BtnStyle.Ghost, 24).E.W(30);

            UIF.Divider(panel);

            // Panel switcher — only meaningful once there is more than one panel.
            // A Column of rows, not one Row: past four panels the captions no longer
            // fit a single strip (see RebuildTabStrip). No fixed height — the group
            // reports whatever its rows come to, one line or two.
            tabStrip = UIF.Box(panel, "Tabs").Column(Theme.Space1).Active(false);

            // Content fills whatever the header and switcher leave.
            contentHost = UIF.Box(panel, "Content").Column(0f).Flex(0f, 1f);
        }

        // ── Panels ──────────────────────────────────────────────────────────

        public void AddPanel(SidebarPanel panel)
        {
            if (!built || panel == null) return;

            panels.Add(panel);

            // Flex rather than Stretch: contentHost is a Column, so the host's size
            // is the group's to assign — flexibleHeight 1 is how a layout child asks
            // for the leftover space.
            var host = UIF.Box(contentHost, panel.Title).Column(0f).Flex(1f, 1f);
            panel.Attach(host, this);
            host.Active(false);

            RebuildTabStrip();

            if (active == null) Select(panel);
        }

        /// <summary>
        /// Mount a panel as a draggable window instead of as a tab. Built hidden;
        /// the panel opens it (SubmitPanel.Open) when something asks for it.
        /// </summary>
        public FloatWindow AddWindow(WindowPanel panel, float width, float height, Vector2 defaultOffset)
        {
            if (!built || panel == null) return null;

            var window = new FloatWindow(panel, width, height, defaultOffset);
            window.Build(windowLayer, this);
            windows.Add(window);
            return window;
        }

        /// <summary>The open window nearest the front, or null. Its z-order is its
        /// sibling index, which BringToFront maintains.</summary>
        private FloatWindow TopWindow()
        {
            FloatWindow top = null;
            foreach (var w in windows)
                if (w.IsOpen && (top == null || w.Depth > top.Depth)) top = w;
            return top;
        }

        private bool AnyWindowOpen => TopWindow() != null;

        private void RebuildTabStrip()
        {
            // One panel needs no switcher; the header already names the sidebar.
            tabStrip.Active(panels.Count > 1);
            if (panels.Count <= 1) return;

            tabStrip.ClearChildren();

            // Wrap onto two rows past four panels. Six tabs across 368px leaves each
            // caption about 60px, which truncates "Notifications" to noise; split in
            // half they get 120px, which fits every title the mod has.
            int perRow = panels.Count <= 4 ? panels.Count : (panels.Count + 1) / 2;
            El row = null;

            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                var target = p;

                if (i % perRow == 0) row = UIF.Box(tabStrip, "TabRow").Row(Theme.Space2).H(24);

                // Equal shares of the row rather than a fixed width: a fixed width
                // would push the last button off the edge.
                var b = UIF.Button(row, p.Title, () => Select(target),
                                   ReferenceEquals(p, active) ? BtnStyle.Primary : BtnStyle.Ghost,
                                   24, Theme.Space1);
                // Ellipsis rather than overflow: at five or six tabs a caption as long
                // as "Notifications" no longer fits its share of 368px, and a label
                // that overruns bleeds across the buttons either side of it.
                b.Label.Size(Theme.FontXs).Ellipsis();
                b.E.PrefW(0).Flex(1f);
            }
        }

        private void Select(SidebarPanel panel)
        {
            if (panel == null) return;

            active = panel;
            foreach (var p in panels)
                p.SetVisible(ReferenceEquals(p, panel));

            panel.MarkDirty();
            RebuildTabStrip();
        }

        // ── Open / close ────────────────────────────────────────────────────

        public void Toggle() => SetOpen(!open);

        public void SetOpen(bool value)
        {
            if (open == value) return;
            open = value;
            if (open) active?.MarkDirty();
        }

        /// <summary>
        /// The game hid its UI (F2, or one of our own captures firing onHideUI).
        /// A Canvas gets none of OnGUI's `uiHidden` handling for free, so this is
        /// the only thing keeping the sidebar out of screenshots.
        /// </summary>
        public void SetHidden(bool hidden)
        {
            hiddenByGame = hidden;
            if (hidden) ReleaseLock();
        }

        /// <summary>
        /// Open the full-screen image viewer. Panels reach this through
        /// SidebarPanel.ShowImages; the textures stay theirs (see ImageViewer.Show).
        /// </summary>
        internal void ShowImages(System.Collections.Generic.IList<Texture2D> images,
                                 System.Collections.Generic.IList<string> captions,
                                 int start, string title)
            => viewer.Show(images, captions, start, title);

        /// <summary>Close the viewer — for a panel whose textures are about to go.</summary>
        internal void CloseImages() => viewer.Close();

        /// <summary>
        /// Show a contract, switching panels to get there — what a notification about
        /// one is for. The controller does the switching rather than the feed reaching
        /// across into the inbox itself, because selecting a panel is also what fires
        /// its OnShown/OnHidden pair, and a panel opened behind the switcher's back
        /// would never get either.
        /// </summary>
        internal void ShowContract(string contractId)
        {
            if (string.IsNullOrEmpty(contractId)) return;

            foreach (var p in panels)
            {
                var inbox = p as ContractsPanel;
                if (inbox == null) continue;

                Select(inbox);
                inbox.ShowContract(contractId);
                return;
            }
        }

        /// <summary>Select the notification feed. The counterpart of ShowContract,
        /// and routed the same way — through Select, so the panel gets its
        /// OnShown.</summary>
        internal void ShowNotifications()
        {
            foreach (var p in panels)
            {
                var feed = p as NotificationsPanel;
                if (feed == null) continue;

                Select(feed);
                return;
            }
        }

        /// <summary>
        /// Raise a transient top-right toast. The only entry point — GeneKermanMod
        /// routes every notice here, so "what may appear on screen" stays one
        /// decision (this canvas's render gate) rather than two.
        /// </summary>
        public void Toast(string title, string message, string contractId = null,
                          string localAction = null)
            => toasts.Show(title, message, contractId, localAction);

        internal bool ImagesOpen => viewer.IsOpen;

        // ── Frame ───────────────────────────────────────────────────────────

        /// <summary>Called from GeneKermanMod.Update(), every frame, in every scene.</summary>
        public void Tick()
        {
            if (!built || canvas == null) return;

            bool render = ShouldRender();
            if (canvas.enabled != render)
            {
                canvas.enabled = render;
                if (!render)
                {
                    ReleaseLock();
                    // A hidden canvas still ticks. Closing rather than pausing,
                    // because the reasons rendering stops — F2, consent revoked,
                    // an update gate — are all reasons a full-screen overlay
                    // should not be waiting behind them.
                    viewer.Close();
                    // Same for the stack: a toast that spent the gate frozen
                    // mid-fade would flicker back on screen when it lifted.
                    toasts.Clear();
                }
            }
            if (!render) return;

            UpdatePixelDensity();
            UpdateAssets();
            AnimateExpand();
            viewer.Tick();
            toasts.Tick();
            UpdateInputLock();
            UpdateEscape();

            if (open && openAmount > 0.99f)
                active?.Tick();

            // Windows tick whether or not the sidebar itself is open: a submission in
            // progress is not a tab, and closing the panel must not freeze it.
            foreach (var w in windows) w.Tick();
        }

        /// <summary>
        /// Mirrors the three exclusive gates in GeneKermanMod.OnGUI, where nothing
        /// else in the mod draws. Also hides while the update gate is up at all —
        /// this slice shows server data, and the acknowledged-update mode exists
        /// specifically so that nothing which transmits is on screen.
        /// </summary>
        /// <summary>
        /// Keep the generated sprites at the screen's resolution rather than the
        /// layout's. The CanvasScaler maps a 1920x1080 design onto the real window,
        /// so on anything taller than 1080 every sprite is magnified — which is what
        /// makes the whole panel look soft. Sprites regenerates at a higher density
        /// and the GPU downsamples instead.
        ///
        /// Polled rather than hooked: the scale factor changes on a resolution or
        /// window change, and KSP raises no event we can use for either.
        /// </summary>
        private void UpdatePixelDensity()
        {
            float scale = canvas.scaleFactor;
            if (Mathf.Approximately(scale, lastCanvasScale)) return;

            lastCanvasScale = scale;
            if (Sprites.SetPixelDensity(scale)) Sprites.Refresh();
        }

        /// <summary>
        /// Notice that a scene load took our font or our textures, and put them back.
        ///
        /// This is a poll, and it has to be. OnSceneChange runs on
        /// GameEvents.onGameSceneLoadRequested — which fires when the load is
        /// *requested*, while everything is still alive: the sprite check finds
        /// nothing wrong, the font is not yet dead, and the panels helpfully rebuild
        /// themselves against assets that are about to be destroyed. Launching a
        /// craft left every label in the sidebar blank for exactly that reason, and
        /// no rebuild afterwards could fix it, since a rebuilt label asked for the
        /// same cached, dead font.
        ///
        /// The recovery is cheap because it is asked rarely and answered by two null
        /// comparisons; the expensive half (searching every loaded TMP asset) runs
        /// only when something is actually gone.
        /// </summary>
        /// <summary>
        /// Escape closes what is on top: the image viewer if it is up, otherwise the
        /// panel.
        ///
        /// The lock is the whole design here. KSP's own Escape handler asks
        /// InputLockManager about ControlTypes.PAUSE, so holding that lock for exactly
        /// as long as the panel is open makes the key mean one thing at a time —
        /// closing does not also pause the game behind it, and a second press, with
        /// the lock gone, brings up the pause menu as usual. The panel being open is
        /// the entire condition, so there is no state to get out of step with, and
        /// ReleaseLock drops it on every path that takes the sidebar off screen.
        ///
        /// This is a narrower version of the rule ImageViewer's header states: the
        /// mask still leaves PAUSE alone whenever the sidebar is closed, which is the
        /// case that mattered — locking a player out of their own pause menu
        /// indefinitely is worse than any click-through.
        ///
        /// While a text box has focus the key belongs to the box (Escape is how TMP
        /// cancels an edit), so the first press stops typing and the second closes.
        /// </summary>
        private void UpdateEscape()
        {
            // A window counts as "ours on screen" for exactly the same reason the
            // panel does: Escape has to close it without also pausing the game behind
            // it, and the lock is what makes the key mean one thing at a time.
            bool ours = open || AnyWindowOpen;
            if (ours != pauseLockHeld)
            {
                if (ours) InputLockManager.SetControlLock(ControlTypes.PAUSE, PauseLockId);
                else InputLockManager.RemoveControlLock(PauseLockId);
                pauseLockHeld = ours;
            }

            if (!ours || typing || !Input.GetKeyDown(KeyCode.Escape)) return;

            // Topmost first: the lightbox covers everything, then the frontmost
            // window, and the panel last — which is the order they are stacked in.
            var top = TopWindow();
            if (viewer.IsOpen) viewer.Close();
            else if (top != null) top.Hide();
            else SetOpen(false);
        }

        private void UpdateAssets()
        {
            if (Time.unscaledTime < nextAssetCheck) return;
            nextAssetCheck = Time.unscaledTime + AssetCheckSeconds;

            // No-ops unless a texture went missing; it does the re-binding itself.
            if (Sprites.Lost) Sprites.Refresh();

            RefreshTextAfterSceneChange();

            // Nothing to lose on an install that never found a TMP font — those
            // panels are drawn with legacy Text and Unity's built-in Arial.
            if (Theme.NeverHadFont(fontInUse) || Theme.Alive(fontInUse)) return;

            Theme.InvalidateFont();
            var next = Theme.Font;
            // Leave fontInUse pointing at the dead asset when the search comes back
            // empty, so the next check tries again rather than giving up for the
            // session — the font may simply not be loaded yet this early in a scene.
            if (next == null) return;

            fontInUse = next;
            int moved = UIF.RefreshFont();
            Debug.Log("[GeneKerman] Sidebar font was lost across a scene load; " +
                      "re-applied to " + moved + " labels.");
        }

        /// <summary>
        /// Re-assert the labels' material and rebuild their meshes for a few seconds
        /// after the scene changes.
        ///
        /// The textless-after-launch bug was the font asset's *default* material being
        /// written to by something else in the game (Theme.FontMaterial has the story),
        /// and a private clone is the fix. This is the belt to that braces: whatever
        /// else a scene load can do to a label — a queued rebuild nothing serviced, a
        /// mesh cleared mid-load, our material assignment lost to a font swap —
        /// UIF.RefreshText undoes by re-pointing the material and generating the mesh
        /// synchronously (see the note there on why SetAllDirty alone cannot).
        ///
        /// A burst rather than one shot, because a KSP scene load is long and staged
        /// and nothing announces the moment it is safe. Six of them at the poll
        /// interval covers the first three seconds; the cost is a mesh rebuild per
        /// label, which is what TMP does every time the text changes anyway.
        /// </summary>
        private void RefreshTextAfterSceneChange()
        {
            if (HighLogic.LoadedScene != lastScene)
            {
                lastScene = HighLogic.LoadedScene;
                textRefreshTicks = TextRefreshTicks;
            }

            if (textRefreshTicks <= 0) return;
            textRefreshTicks--;

            // Re-assert the channels: this is the one canvas property TMP needs and
            // does not set for plain labels, and it costs nothing to say again.
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
                                              AdditionalCanvasShaderChannels.Normal |
                                              AdditionalCanvasShaderChannels.Tangent;

            UIF.RefreshText();

            // Flush uGUI's rebuild queue by hand. Generating a mesh (what RefreshText
            // does) and getting it *submitted in the right batch* are two different
            // steps, and the second one is the canvas's, not the label's.
            Canvas.ForceUpdateCanvases();
        }

        private bool ShouldRender()
        {
            if (hiddenByGame) return false;

            var mod = GeneKermanMod.Instance;
            if (mod == null || mod.Api == null) return false;

            if (mod.UpdateRequired) return false;
            if (!mod.Api.DataGatheringEnabled) return false;
            if (!Consent.Accepted) return false;

            return true;
        }

        private void AnimateExpand()
        {
            // Unscaled throughout: the panel must still animate during a time-warp
            // pause or while the game is paused at the space centre.
            float dt = Time.unscaledDeltaTime;

            float target = open ? 1f : 0f;
            bool moved = false;

            if (!Mathf.Approximately(openAmount, target))
            {
                openAmount = Mathf.MoveTowards(openAmount, target, dt / ExpandSeconds);
                moved = true;
            }

            // The panel widens to make room for a detail pane and narrows back when
            // the selection is cleared. Same duration as the expand, so a click that
            // does both reads as one motion. This one *is* a width change rather than
            // a scale: it is a change of resting size, so the layout has to re-run.
            float targetWidth = (active != null && active.WantsWide) ? PanelWideWidth : PanelWidth;
            if (!Mathf.Approximately(currentWidth, targetWidth))
            {
                float speed = (PanelWideWidth - PanelWidth) / ExpandSeconds;
                currentWidth = Mathf.MoveTowards(currentWidth, targetWidth, speed * dt);
                moved = true;
            }

            if (moved) ApplyExpand();
        }

        /// <summary>
        /// Exponential ease-out: full speed on the first frame, decaying by half every
        /// tenth of the way through. 2^-10 leaves 0.1% at t=1, which is close enough to
        /// arrived that the clamp at the end is invisible — and the clamp has to be
        /// there, because the curve never actually reaches 1.
        /// </summary>
        private static float EaseOutExpo(float t)
            => t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);

        private void ApplyExpand()
        {
            if (panelRect == null) return;

            panelRect.sizeDelta = new Vector2(currentWidth, -VerticalMargin * 2f);
            panelRect.anchoredPosition = Vector2.zero;

            // X only, from a pivot at the panel's centre, so it grows out of the middle
            // of the screen in both directions at once. Y is left at 1: scaling both
            // axes would be a zoom, which is a different gesture entirely.
            //
            // Closing replays the same curve backwards, which reads as an ease-*in* —
            // it holds near full width and then collapses. That asymmetry is wanted:
            // opening announces itself, closing gets out of the way.
            float scale = EaseOutExpo(openAmount);
            panelRect.localScale = new Vector3(scale, 1f, 1f);
        }

        /// <summary>
        /// A notification arrived. Unconditional by design — this fires regardless
        /// of any setting and regardless of whether the panel is already open, since
        /// a notification arriving while you are reading the feed is still news.
        /// Suppressed only while the game's UI is hidden, where nothing may draw.
        ///
        /// This used to also pulse a glow on the pull-out tab. With the tab gone there
        /// is nothing on screen to pulse while the panel is closed — the toolbar button
        /// is KSP's, not ours — so what survives is the part that was always the point:
        /// the feed is up to date the moment it is opened. The in-game toast remains
        /// the thing that actually tells a closed-panel player something happened.
        /// </summary>
        public void Pulse()
        {
            if (!built || hiddenByGame) return;
            foreach (var p in panels) p.MarkDirty();
        }

        // ── Input lock ──────────────────────────────────────────────────────

        private void UpdateInputLock()
        {
            UpdateTextLock();

            bool over = PointerOverSidebar();

            // Latch a drag that began on the panel, so dragging a scrollbar off the
            // panel's edge does not hand the rest of the drag to the camera.
            if (over && Input.GetMouseButtonDown(0)) dragLatch = true;
            if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
                dragLatch = false;

            // Locked only while the pointer is actually on the sidebar. Locking for
            // as long as the panel is open would take the whole game away from the
            // player over a 400px strip.
            //
            // With the tab gone, "on the sidebar" and "the panel is open" are the same
            // question — PointerOverSidebar answers false outright while closed, since
            // a scaled-to-nothing panel still has a rect that would otherwise take a
            // lock over a sliver of screen showing nothing.
            bool want = over || dragLatch;
            if (want == lockHeld) return;

            if (want) InputLockManager.SetControlLock(LockMask, LockId);
            else InputLockManager.RemoveControlLock(LockId);
            lockHeld = want;
        }

        /// <summary>
        /// True while a text box under our canvas has focus. Read from the
        /// EventSystem rather than reported by the field, because the field can be
        /// destroyed mid-edit — a rebuild, a scene change — and a lock that depended
        /// on a destroyed object calling back would be a leaked lock. The
        /// EventSystem's selection clears itself when its GameObject dies.
        /// </summary>
        internal bool IsTyping => typing;

        private void UpdateTextLock()
        {
            typing = TextFieldFocused();
            if (typing == textLockHeld) return;

            if (typing) InputLockManager.SetControlLock(TextLockMask, TextLockId);
            else InputLockManager.RemoveControlLock(TextLockId);
            textLockHeld = typing;
        }

        private bool TextFieldFocused()
        {
            var es = EventSystem.current;
            var sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || rootGo == null) return false;
            // activeInHierarchy, not just non-null: switching tabs deactivates the
            // host without clearing the EventSystem's selection, and a lock that
            // waited for a callback from a deactivated field would never come back.
            if (!sel.activeInHierarchy) return false;
            if (!sel.transform.IsChildOf(rootGo.transform)) return false;

            var tmp = sel.GetComponent<TMPro.TMP_InputField>();
            if (tmp != null) return tmp.isFocused;

            var legacy = sel.GetComponent<InputField>();
            return legacy != null && legacy.isFocused;
        }

        private bool PointerOverSidebar()
        {
            // The image viewer covers the entire screen, so while it is up there is
            // nowhere the pointer can be that belongs to the game. Without this, a
            // pan-drag would rotate the camera behind the overlay.
            if (viewer.IsOpen) return true;

            // A window is placed by the player and is open independently of the
            // panel, so it is asked before the closed check below for the same reason
            // the toasts are: dragging one must not also swing the flight camera.
            foreach (var w in windows)
                if (w.PointerOver()) return true;

            // Toasts live in the top-right corner whether or not the panel is open,
            // so this has to be asked before the closed check below — otherwise a
            // click meant for a toast also swings the flight camera.
            if (toasts.PointerOverToast()) return true;

            // Closed is closed. RectangleContainsScreenPoint answers for a rect whether
            // or not anything is drawn, and a scaled-to-nothing panel still has one —
            // so without this the mod would take a control lock over a sliver at the
            // screen's centre with nothing visible there.
            if (!open && openAmount <= 0.001f) return false;

            // Null camera: correct for a ScreenSpaceOverlay canvas. The X scale is
            // accounted for — the rect is transformed to screen space, so mid-expand
            // the hit area matches what is actually on screen.
            Vector2 mouse = Input.mousePosition;
            return panelRect != null
                && RectTransformUtility.RectangleContainsScreenPoint(panelRect, mouse, null);
        }

        /// <summary>
        /// Drop both locks. Called from every path that stops the sidebar being on
        /// screen — hidden, canvas disabled, scene change, teardown — because a
        /// control lock outlives whatever took it.
        /// </summary>
        private void ReleaseLock()
        {
            // First, and unconditionally: of the three, this is the one whose leak the
            // player cannot work around. A stranded PAUSE lock means Escape does
            // nothing for the rest of the session — no pause menu, no way out to the
            // space centre, no quit.
            if (pauseLockHeld)
            {
                InputLockManager.RemoveControlLock(PauseLockId);
                pauseLockHeld = false;
            }

            if (textLockHeld)
            {
                InputLockManager.RemoveControlLock(TextLockId);
                textLockHeld = false;
                typing = false;
            }

            if (!lockHeld) return;
            InputLockManager.RemoveControlLock(LockId);
            lockHeld = false;
            dragLatch = false;
        }

        // ── Scene / teardown ────────────────────────────────────────────────

        /// <summary>
        /// A scene load is starting. The hierarchy survives (it hangs off the
        /// DontDestroyOnLoad object) but its textures do not, so regenerate and
        /// re-bind sprites rather than rebuilding the tree.
        ///
        /// This runs on onGameSceneLoadRequested, i.e. *before* anything has actually
        /// been destroyed, so it cannot be the whole recovery — it is the tidy-up for
        /// what we are leaving (the lightbox, the control lock). Putting back what the
        /// load takes is UpdateAssets, which polls afterwards.
        /// </summary>
        public void OnSceneChange()
        {
            // Before Refresh: the viewer is showing a texture the scene load is about
            // to destroy, and a lightbox left open over a loading screen is the last
            // thing the player wants to find on the other side.
            viewer.Close();

            Sprites.Refresh();
            // The lock belongs to the scene we are leaving. KSP clears locks across
            // some transitions and not others; dropping ours makes that irrelevant.
            ReleaseLock();
            foreach (var w in windows) w.OnSceneChange();
            foreach (var p in panels)
            {
                p.OnSceneChanged();
                p.MarkDirty();
            }
        }

        public void Destroy()
        {
            // Before anything else: a control lock outlives the GameObject that
            // took it, and a leaked one leaves the player unable to fly.
            ReleaseLock();
            viewer.Close();
            toasts.Clear();

            // SetVisible(false) first, so a panel holding textures gets its OnHidden
            // and hands them back — Detach alone would strand them until KSP next
            // changed scene.
            foreach (var p in panels)
            {
                p.SetVisible(false);
                p.Detach();
            }
            panels.Clear();
            active = null;

            foreach (var w in windows) w.Destroy();
            windows.Clear();

            Sprites.ClearBindings();

            if (rootGo != null) Object.Destroy(rootGo);
            rootGo = null;
            canvas = null;
            built = false;
        }
    }
}
