/*
 * UI/Gui/SidebarController.cs – The in-game uGUI sidebar.
 *
 * A left-edge tab that slides a panel in over the viewport. Built once at
 * Start() and parented to the mod's DontDestroyOnLoad GameObject, so the
 * hierarchy survives every scene change; only GPU resources die, and
 * Sprites.Refresh() puts those back.
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
 *  5. The slide runs off Time.unscaledDeltaTime in Tick(), not a coroutine, so
 *     a scene load mid-animation cannot strand the panel half-open.
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

        private const float TabWidth = 26f;
        private const float TabHeight = 88f;
        private const float VerticalMargin = 48f;

        /// <summary>
        /// Top margin while the panel is on the right — enough to clear KSP's own
        /// application launcher strip, which lives in that corner.
        /// </summary>
        private const float EditorTopMargin = 96f;

        private const float SlideSeconds = 0.18f;
        private const float PulseSeconds = 4f;
        private const float PulseHz = 1f;

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
        private RectTransform slider;
        private RectTransform panelRect;
        private RectTransform tabRect;
        private Image tabGlow;
        private Lbl tabCaret;
        private El contentHost;
        private readonly ImageViewer viewer = new ImageViewer();

        private bool built;
        private bool open;
        private float openAmount;       // 0 = closed, 1 = open
        private float currentWidth = PanelWidth;
        private float pulseRemaining;
        private bool hiddenByGame;      // F2 / capture in progress
        private bool lockHeld;
        private bool textLockHeld;
        private bool pauseLockHeld;     // Escape is ours while the panel is open
        private bool typing;            // a text box in the sidebar has focus
        private bool dragLatch;         // a drag that started on the panel
        private float lastCanvasScale = -1f;

        /// <summary>Which edge the panel is docked to. Right in the editor. See UpdateEdge.</summary>
        private bool onRight;

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
            ApplySlide();
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

            // One moving parent for both the panel and the tab, so the tab rides
            // the panel's edge and the slide is a single transform write.
            var sliderEl = UIF.Root("Slider", root.Rt);
            slider = sliderEl.Rt;
            slider.anchorMin = new Vector2(0f, 0f);
            slider.anchorMax = new Vector2(0f, 1f);
            slider.pivot = new Vector2(0f, 0.5f);
            slider.sizeDelta = new Vector2(PanelWidth + TabWidth, 0f);
            slider.anchoredPosition = Vector2.zero;

            BuildPanel(sliderEl);
            BuildTab(sliderEl);

            // Last child of the root, so it paints over the panel — a
            // ScreenSpaceOverlay canvas draws in hierarchy order, and there is no
            // second Canvas here to give it a sortingOrder of its own.
            viewer.Build(root);
        }

        private void BuildPanel(El sliderEl)
        {
            var panel = UIF.Root("Panel", sliderEl.Rt);
            panelRect = panel.Rt;
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 0.5f);
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

            // Panel switcher — only meaningful once there is more than one panel,
            // which today means the dev gallery is on.
            // A Column of rows, not one Row: past four panels the captions no longer
            // fit a single strip (see RebuildTabStrip). No fixed height — the group
            // reports whatever its rows come to, one line or two.
            tabStrip = UIF.Box(panel, "Tabs").Column(Theme.Space1).Active(false);

            // Content fills whatever the header and switcher leave.
            contentHost = UIF.Box(panel, "Content").Column(0f).Flex(0f, 1f);
        }

        private void BuildTab(El sliderEl)
        {
            var tab = UIF.Root("Tab", sliderEl.Rt);
            tabRect = tab.Rt;
            tabRect.anchorMin = new Vector2(0f, 0.5f);
            tabRect.anchorMax = new Vector2(0f, 0.5f);
            tabRect.pivot = new Vector2(0f, 0.5f);
            tabRect.sizeDelta = new Vector2(TabWidth, TabHeight);
            tabRect.anchoredPosition = new Vector2(PanelWidth, 0f);

            var img = tab.Go.AddComponent<Image>();
            img.raycastTarget = true;
            var btn = tab.Go.AddComponent<Button>();
            Sprites.BindStates(
                img, btn,
                Sprites.Rounded(Theme.Alpha(Theme.Card, 0.94f), Theme.RadiusSm, Theme.Border),
                Sprites.Rounded(Theme.Alpha(Theme.Secondary, 0.96f), Theme.RadiusSm, Theme.Border),
                Sprites.Rounded(Theme.Alpha(Theme.Muted, 0.96f), Theme.RadiusSm, Theme.Border),
                Sprites.Rounded(Theme.Alpha(Theme.Card, 0.5f), Theme.RadiusSm, Theme.Alpha(Theme.Border, 0.5f)));
            btn.onClick.AddListener(Toggle);

            // The pulse: an overlay carrying only a --primary outline, faded in and
            // out by alpha. Animating the tint of a single Image is the cheap way to
            // do this — the sprite colours are baked, so swapping sprites per frame
            // to change a border colour would allocate a texture per step.
            var glow = UIF.Root("Glow", tabRect).Stretch(-2, -2, -2, -2);
            tabGlow = glow.Go.AddComponent<Image>();
            tabGlow.raycastTarget = false;
            Sprites.Bind(tabGlow, Sprites.Rounded(Theme.Alpha(Theme.Primary, 0f), Theme.RadiusSm, Theme.Primary, 2));
            tabGlow.color = new Color(1f, 1f, 1f, 0f);

            // Chevron. ASCII on purpose: KSP's font is borrowed, not shipped, so a
            // typographic glyph is a bet on whatever face happens to be loaded.
            var caret = UIF.Root("Caret", tabRect).Stretch();
            tabCaret = UIF.Label(caret, ">", Theme.FontLg, Theme.MutedForeground).Align(TextAlign.Center);
            tabCaret.E.Stretch();
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
            // The caret points at what pressing it does, not at where the panel is.
            tabCaret?.Set(CaretGlyph());
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
                }
            }
            if (!render) return;

            UpdatePixelDensity();
            UpdateAssets();
            UpdateEdge();
            AnimateSlide();
            AnimatePulse();
            viewer.Tick();
            UpdateInputLock();
            UpdateEscape();

            if (open && openAmount > 0.99f)
                active?.Tick();
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
        /// Put the sidebar on the right in the VAB and SPH, and take the pull-out tab
        /// away there.
        ///
        /// KSP owns the left edge in the editor — the part list, the category strip
        /// and the sub-assembly flap are all under where the panel and its tab sit.
        /// The right edge is empty apart from the stock toolbar, which the top margin
        /// below clears. The tab can go because it is no longer the only way in: the
        /// toolbar button opens the sidebar in every scene.
        ///
        /// Only the tab is hidden. A sidebar that is already open stays open, and the
        /// header's X still closes it.
        ///
        /// Polled rather than hooked to a scene event, for the same reason as the rest
        /// of Tick: this has to be right after a scene load *and* after a canvas
        /// rebuild, and comparing a bool is cheaper than being sure of the ordering.
        /// </summary>
        private void UpdateEdge()
        {
            bool editor = HighLogic.LoadedSceneIsEditor;

            if (onRight != editor)
            {
                onRight = editor;
                ApplyEdge();
            }

            // The tab comes back whenever the panel is out, editor or not: it is the
            // affordance that says this thing can be put away again, and it is the
            // one control that is always in the same place. openAmount keeps it
            // through the closing slide rather than blinking out at the first frame.
            bool wantTab = !editor || open || openAmount > 0.001f;
            if (tabRect != null && tabRect.gameObject.activeSelf != wantTab)
                tabRect.gameObject.SetActive(wantTab);
        }

        /// <summary>
        /// Mirror the three rects that make up the sidebar onto the other edge.
        ///
        /// Anchors and pivots only — every position is written by ApplySlide, which
        /// reads <see cref="onRight"/> for its sign. Doing it any other way means two
        /// copies of the slide arithmetic that have to agree.
        /// </summary>
        private void ApplyEdge()
        {
            float x = onRight ? 1f : 0f;

            if (slider != null)
            {
                slider.anchorMin = new Vector2(x, 0f);
                slider.anchorMax = new Vector2(x, 1f);
                slider.pivot = new Vector2(x, 0.5f);
            }

            if (panelRect != null)
            {
                panelRect.anchorMin = new Vector2(x, 0f);
                panelRect.anchorMax = new Vector2(x, 1f);
                panelRect.pivot = new Vector2(x, 0.5f);
            }

            if (tabRect != null)
            {
                tabRect.anchorMin = new Vector2(x, 0.5f);
                tabRect.anchorMax = new Vector2(x, 0.5f);
                tabRect.pivot = new Vector2(x, 0.5f);
            }

            // The caret points at what pressing it does, and that reverses with the
            // edge — see CaretGlyph.
            tabCaret?.Set(CaretGlyph());
            ApplySlide();
        }

        /// <summary>
        /// Which way the tab's chevron points: at the motion the click causes, not at
        /// where the panel is. On the left that is "&lt;" to close; on the right the
        /// same action moves the panel the other way, so the glyph flips with it.
        /// </summary>
        private string CaretGlyph() => open == !onRight ? "<" : ">";

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
            if (open != pauseLockHeld)
            {
                if (open) InputLockManager.SetControlLock(ControlTypes.PAUSE, PauseLockId);
                else InputLockManager.RemoveControlLock(PauseLockId);
                pauseLockHeld = open;
            }

            if (!open || typing || !Input.GetKeyDown(KeyCode.Escape)) return;

            if (viewer.IsOpen) viewer.Close();
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

        private void AnimateSlide()
        {
            // Unscaled throughout: the panel must still animate during a time-warp
            // pause or while the game is paused at the space centre.
            float dt = Time.unscaledDeltaTime;

            float target = open ? 1f : 0f;
            bool moved = false;

            if (!Mathf.Approximately(openAmount, target))
            {
                openAmount = Mathf.MoveTowards(openAmount, target, dt / SlideSeconds);
                moved = true;
            }

            // The panel widens to make room for a detail pane and narrows back when
            // the selection is cleared. Same duration as the slide, so a click that
            // does both reads as one motion.
            float targetWidth = (active != null && active.WantsWide) ? PanelWideWidth : PanelWidth;
            if (!Mathf.Approximately(currentWidth, targetWidth))
            {
                float speed = (PanelWideWidth - PanelWidth) / SlideSeconds;
                currentWidth = Mathf.MoveTowards(currentWidth, targetWidth, speed * dt);
                moved = true;
            }

            if (moved) ApplySlide();
        }

        private void ApplySlide()
        {
            if (slider == null) return;

            // Smoothstep so the motion eases at both ends instead of stopping dead.
            float t = openAmount * openAmount * (3f - 2f * openAmount);

            // +1 on the left, -1 on the right: the tab rides the panel's inner edge
            // and "closed" is off the screen's nearer side, and both reverse together.
            float sign = onRight ? -1f : 1f;

            // Asymmetric on the right, because that is where KSP puts the stock
            // toolbar — and the toolbar button is how the sidebar is opened in the
            // editor, so a panel covering it would be a door that locks behind you.
            float top = onRight ? EditorTopMargin : VerticalMargin;
            panelRect.sizeDelta = new Vector2(currentWidth, -(top + VerticalMargin));
            panelRect.anchoredPosition = new Vector2(0f, (VerticalMargin - top) * 0.5f);

            slider.sizeDelta = new Vector2(currentWidth + TabWidth, 0f);
            tabRect.anchoredPosition = new Vector2(sign * currentWidth, 0f);
            slider.anchoredPosition = new Vector2(-sign * currentWidth * (1f - t), 0f);
        }

        private void AnimatePulse()
        {
            if (tabGlow == null) return;

            if (pulseRemaining <= 0f)
            {
                if (tabGlow.color.a != 0f) tabGlow.color = new Color(1f, 1f, 1f, 0f);
                return;
            }

            pulseRemaining -= Time.unscaledDeltaTime;
            float phase = Mathf.Abs(Mathf.Sin(Time.unscaledTime * Mathf.PI * PulseHz));
            // Fade the whole pulse out over its last second so it ends on a stroke
            // rather than snapping off mid-cycle.
            float envelope = Mathf.Clamp01(pulseRemaining);
            tabGlow.color = new Color(1f, 1f, 1f, phase * envelope);
        }

        /// <summary>
        /// A notification arrived. Unconditional by design — this fires regardless
        /// of any setting and regardless of whether the panel is already open, since
        /// a notification arriving while you are reading the feed is still news.
        /// Suppressed only while the game's UI is hidden, where nothing may draw.
        /// </summary>
        public void Pulse()
        {
            if (!built || hiddenByGame) return;
            pulseRemaining = PulseSeconds;
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
            // Deliberately not gated on the panel being open: the tab is on screen
            // in every scene, and in the editor a click that reaches the game behind
            // it places or deselects a part.
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

            Vector2 mouse = Input.mousePosition;
            // Null camera: correct for a ScreenSpaceOverlay canvas.
            //
            // activeSelf on the tab is load-bearing: RectangleContainsScreenPoint
            // answers for a rect whether or not the GameObject is drawn, so without
            // it the hidden editor tab would still take a control lock over a strip
            // of the VAB that shows nothing.
            return (panelRect != null && RectTransformUtility.RectangleContainsScreenPoint(panelRect, mouse, null))
                || (tabRect != null && tabRect.gameObject.activeSelf &&
                    RectTransformUtility.RectangleContainsScreenPoint(tabRect, mouse, null));
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

            Sprites.ClearBindings();

            if (rootGo != null) Object.Destroy(rootGo);
            rootGo = null;
            canvas = null;
            built = false;
        }
    }
}
