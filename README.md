# Boundless Missions — KSP Mod Side

> A KSP 1.12.x plugin that connects a player's game to the Boundless Missions
> backend: player-issued contracts, reverse auctions, rescue missions, craft and
> vessel transfers between players, real-time notifications, cinematic milestone
> captures, and automated mod-dependency management — all from inside the stock
> game.
>
> The hard part is not the networking. It is that two players never have the same
> install: different mods, different part sets, different life-support rules,
> different rendering stacks. Most of this codebase exists so a craft built on one
> machine arrives intact — or degrades honestly — on another.

---

## Table of Contents

1. [High-Level Architecture](#high-level-architecture)
2. [Directory Layout](#directory-layout)
3. [Source File Map](#source-file-map)
4. [Core Systems](#core-systems)
   - [Lifecycle & State Machine — `GeneKermanMod`](#lifecycle--state-machine--genekermanmod)
   - [Client State — `ClientState`](#client-state--clientstate)
   - [Networking — `ApiClient`](#networking--apiclient)
   - [Real-Time Push — `NotificationSocket`](#real-time-push--notificationsocket)
5. [Vessel & Craft Transfer Pipeline](#vessel--craft-transfer-pipeline)
   - [Serialization — `VesselTransfer`](#serialization--vesseltransfer)
   - [Data Collection — `VesselDataCollector`](#data-collection--vesseldatacollector)
   - [Craft Installation — `CraftInstaller`](#craft-installation--craftinstaller)
6. [Side-Channel Data Blocks](#side-channel-data-blocks)
   - [Custom Flags — `FlagTransfer`](#custom-flags--flagtransfer)
   - [TweakScale Bridge — `ScaleBridge` / `GeneKermanScale` / `TweakScaleGuard`](#tweakscale-bridge--scalebridge--genekermanscale--tweakscaleguard)
   - [Textures Unlimited — `TextureTransfer`](#textures-unlimited--texturetransfer)
   - [RealFuels / Realism Overhaul — `RealFuelsTransfer`](#realfuels--realism-overhaul--realfuelstransfer)
   - [CKAN Mod Dependency — `CkanGenerator`](#ckan-mod-dependency--ckangenerator)
   - [Part Substitution — `PartAliases`](#part-substitution--partaliases)
   - [Craft Thumbnails — `CraftThumb`](#craft-thumbnails--craftthumb)
7. [Visual Rendering](#visual-rendering)
   - [Blueprint Renderer — `VesselRenderer`](#blueprint-renderer--vesselrenderer)
   - [Deferred Rendering Support](#deferred-rendering-support)
   - [ConformalDecals Capture — `DecalCapture`](#conformaldecals-capture--decalcapture)
   - [Cinematic Capture — `CinematicCapture`](#cinematic-capture--cinematiccapture)
8. [Mission Contract System](#mission-contract-system)
   - [Contract Integration — `ContractIntegration`](#contract-integration--contractintegration)
   - [Contract Constraints — `ContractConstraints` / `PartClassifier`](#contract-constraints--contractconstraints--partclassifier)
   - [Editor Enforcement — `EditorPartEnforcer`](#editor-enforcement--editorpartenforcer)
   - [Delta-V Validation — `CraftDeltaV`](#delta-v-validation--craftdeltav)
   - [Orbit Constraints — `OrbitConstraint`](#orbit-constraints--orbitconstraint)
   - [Submission — `SubmissionSession`](#submission--submissionsession)
9. [Life Support, Rescue & Save Repair](#life-support-rescue--save-repair)
   - [Life Support Adapters — `LifeSupport/`](#life-support-adapters--lifesupport)
   - [Emergency Freeze — `RescueImmunityGuardian`](#emergency-freeze--rescueimmunityguardian)
   - [Trait Repair — `TraitRepair`](#trait-repair--traitrepair)
10. [Checkpoint & Milestone Detection](#checkpoint--milestone-detection)
    - [Checkpoint Detector — `CheckpointDetector`](#checkpoint-detector--checkpointdetector)
11. [Identity & Security](#identity--security)
    - [Consent Gate — `Consent`](#consent-gate--consent)
    - [Device Identity — `DeviceId`](#device-identity--deviceid)
    - [Version Integrity — `ModVersion`](#version-integrity--modversion)
    - [Service Suspensions](#service-suspensions)
    - [Part Catalog Upload — `PartCatalogUploader`](#part-catalog-upload--partcataloguploader)
12. [Third-Party Mod Compatibility](#third-party-mod-compatibility)
13. [UI System](#ui-system)
14. [Browser UI Bridge — `Web/`](#browser-ui-bridge--web)
15. [Build & Deployment](#build--deployment)
16. [Dependencies](#dependencies)
17. [Configuration](#configuration)

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  KSP Game Process (Unity 2019.4 / Mono / .NET 4.7.2)            │
│                                                                 │
│  ┌─────────────────────┐    ┌────────────────────┐              │
│  │  GeneKermanMod      │◄──►│  ApiClient         │──► HTTP/S    │
│  │  (MonoBehaviour     │    │  (UnityWebRequest) │    REST API  │
│  │   Singleton)        │    └────────────────────┘              │
│  │                     │    ┌────────────────────┐              │
│  │  • Lifecycle mgmt   │◄──►│  NotificationSocket│──► WebSocket │
│  │  • Coroutine host   │    │  (websocket-sharp) │              │
│  │  • ClientState      │    └────────────────────┘              │
│  └──────────┬──────────┘                                        │
│             │                                                   │
│  ┌──────────▼──────────────┐  ┌─────────────────────────────┐   │
│  │  UI                     │  │  Web/ (loopback bridge)     │   │
│  │  uGUI sidebar (primary) │  │  127.0.0.1:<ephemeral>      │──►│─► browser
│  │  IMGUI gates (consent,  │  │  static WebUI + /gk + proxy │   │
│  │  update, suspension, …) │  │  (opt-in, off by default)   │   │
│  └──────────┬──────────────┘  └─────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Transfer Pipeline (export order = strip order reversed) │   │
│  │  ScaleBridge(bake) ─► FlagTransfer ─► TweakScaleGuard    │   │
│  │    ─► TextureTransfer ─► RealFuelsTransfer               │   │
│  │    ─► CkanGenerator ─► CraftThumb                        │   │
│  │  CraftInstaller ─► PartAliases ─► reconcile ─► Ships/    │   │
│  └──────────────────────────────────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Visual                                                  │   │
│  │  VesselRenderer (blueprints, layer 30, dual-pass alpha)  │   │
│  │  DecalCapture (ConformalDecals re-issue)                 │   │
│  │  CinematicCapture (in-game hero shots, flight camera)    │   │
│  └──────────────────────────────────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Contract System                                         │   │
│  │  ContractIntegration ◄─► ContractConstraints             │   │
│  │  EditorPartEnforcer ◄─► PartClassifier ◄─► CraftDeltaV   │   │
│  │  OrbitConstraint ◄─► SubmissionSession                   │   │
│  └──────────────────────────────────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Rescue & Life Support                                   │   │
│  │  RescueImmunityGuardian ◄─► LifeSupport/ adapters        │   │
│  │  TraitRepair (roster repair)                             │   │
│  │  CheckpointDetector (proximity/SOI/event scanning)       │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

The mod is a single Unity `MonoBehaviour` (`GeneKermanMod`) that attaches to a
persistent `GameObject`. It communicates with a remote server over HTTP REST
(via `ApiClient`) and WebSocket (via `NotificationSocket`). All networking uses
Unity's `UnityWebRequest` for HTTP and the external `websocket-sharp` library
for WebSocket.

**Nothing is transmitted before consent.** `Consent` (KSP add-on rule 8.1) gates
the whole surface: until the player accepts in `ConsentWindow`,
`ApiClient.TransmissionBlocked` is true and no request leaves the process.

---

## Directory Layout

```
KSP Mod Side/
├── build.sh                          # Build + deploy script (3 dev instances)
├── BoundlessMissions.netkan          # CKAN metadata
├── PACKAGING.md                      # Release/zip procedure
├── logo.png / logo_38.png            # Toolbar icons
├── Iconpack-1/                       # UI icon assets
├── dist/                             # Packaged release zips
├── LICENSE                           # GPL-3.0
├── GeneKerman/                       # C# source (the plugin project)
│   ├── GeneKerman.csproj             # .NET 4.7.2 project file
│   ├── lib/                          # Pre-built dependency (websocket-sharp.dll)
│   ├── LifeSupport/                  # Reflection-only adapters, one per LS mod
│   ├── Web/                          # Loopback HTTP bridge for the browser UI
│   ├── UI/                           # IMGUI windows (gates only — see UI System)
│   │   └── Gui/                      # uGUI sidebar: theme, builder, panels, windows
│   │       └── Panels/               # The screens (missions, contracts, tools, …)
│   ├── *.cs                          # Core systems (see Source File Map)
│   ├── bin/                          # Build output (GeneKerman.dll)
│   └── obj/                          # Intermediate build files
│
└── GameData/BoundlessMissions/       # Deployable mod folder (copied into KSP)
    ├── GeneKerman.version            # AVC version file (KSP 1.12.x)
    ├── Patches/
    │   └── GeneKermanScale.cfg       # ModuleManager patch: @PART[*]:FINAL
    ├── Plugins/                      # GeneKerman.dll + websocket-sharp.dll
    ├── PluginData/                   # Runtime data (see Configuration)
    ├── Textures/                     # Toolbar icons, UI icons
    └── WebUI/                        # Built browser-UI bundle served by Web/
```

A second GameData folder, `GameData/GeneKerman/Flags/`, is created at runtime by
`FlagTransfer` to hold content-addressed flag images that arrive with crafts.

> The source project is named `GeneKerman`; the deployed GameData folder is
> `BoundlessMissions`. This is intentional — do not "fix" one to match the other.

---

## Source File Map

Line counts are approximate and drift with every commit; they are here to show
relative weight, not as a contract.

### Core

| File | Lines | Role |
|------|------:|------|
| `GeneKermanMod.cs` | 2,047 | **Entry point.** MonoBehaviour singleton, lifecycle, state machine, toolbar, coroutine host, notification dispatch, local notifications |
| `ClientState.cs` | 936 | **The account, headless.** Profile, missions, contracts and the notification feed — fetch, cache, local-notification merge, de-dup, unread count, every action coroutine. Exposed *by reference* via `GeneKermanMod.State` |
| `ApiClient.cs` | 1,831 | **HTTP networking.** All REST calls, settings/session persistence, version gating (426), device binding (403), suspension (403 `suspended`) |
| `NotificationSocket.cs` | 451 | **WebSocket push.** Real-time notifications, ticket auth, keepalive, exponential backoff reconnect |
| `Consent.cs` | 163 | **Privacy gate.** First-run opt-in record, server-driven policy version, live re-read on file change |
| `MiniJSON.cs` | 356 | **JSON library.** Lightweight serializer/deserializer, no external dependencies |

### Transfer pipeline

| File | Lines | Role |
|------|------:|------|
| `VesselTransfer.cs` | 1,541 | **Vessel serialization.** Export/import live vessels via `ConfigNode`, crew embedding (`GKCREW`), trait application, fleet transfers, ownership tags, save removal + `CrewFate` |
| `CraftInstaller.cs` | 336 | **Craft file writer.** Strips side-channel blocks in reverse order, reconciles, places craft in `Ships/VAB|SPH` |
| `CraftDelivery.cs` | 148 | **Delivery coroutine.** Fetch + install the craft a completed contract earned, with one completion callback |
| `GiftInbox.cs` | 201 | **Quicksend receiver.** Poll/accept/decline craft offers from other players |
| `FlagTransfer.cs` | 674 | **`GKFLAG`.** Embed/extract custom flag images, content-addressed SHA-256 naming, runtime `GameDatabase` injection, dangling-reference reset |
| `ScaleBridge.cs` | 466 | **TweakScale bake.** Copies computed scale/mass/module stats off live parts, strips TweakScale, re-anchors `pos`, writes the layout pin |
| `GeneKermanScale.cs` | 379 | **Scale applicator.** Dormant PartModule on every prefab; re-applies absolute values without TweakScale's exponent math |
| `TweakScaleGuard.cs` | 283 | **`GKTSVER`.** Version warning — fires only when a part is *actually rescaled*, never on the mere presence of a TweakScale module |
| `ScaleEditorReapply.cs` | 68 | **Editor undo/redo fix.** Re-asserts scaled geometry after the editor rebuilds the part tree |
| `TextureTransfer.cs` | 884 | **`GKTU`.** Carries a Textures Unlimited paint job: resolves texture sets → defining GameData folder, reconciles recolour modules per prefab on import |
| `RealFuelsTransfer.cs` | 1,135 | **`GKRF`.** Carries RealFuels/RO tank types and engine configs; checks on an RF install, reconciles to local fuels on one without |
| `CkanGenerator.cs` | 1,078 | **`GKMODS`.** Maps parts → mods via CKAN's install paths, reports missing mods, writes a `.ckan` metapackage |
| `PartAliases.cs` | 410 | **Part substitution.** Swaps a missing part for the equivalent this install has (source of truth for `data/part_aliases.py`) |
| `CraftThumb.cs` | 209 | **`GKTHUMB`.** Embeds/extracts the NW-view thumbnail PNG for KSP's craft browser |
| `VesselDataCollector.cs` | 394 | **Telemetry.** Vessel snapshots (orbit, mass, cost, crew), craft file lookup, screenshots |

### Contracts & submission

| File | Lines | Role |
|------|------:|------|
| `SubmissionSession.cs` | 1,285 | **Submission rules + upload.** Mission-type classification rules, scene/vessel validation, render capture and freshness, the packing coroutine. No UI |
| `SubmissionPreview.cs` | 168 | **Review images.** Fetches the blueprints/telemetry a contractor submitted, one completion callback |
| `ContractCreation.cs` | 606 | **Issuing work.** Direct contract, reverse auction, or rescue — shared by the sidebar form and the web bridge |
| `ContractInbox.cs` | 115 | **Local inbox state.** The client-side trash bin and week grouping (never sent to the server) |
| `ContractIntegration.cs` | 508 | **Stock contract bridge.** Injects API missions as stock contracts in Mission Control |
| `ContractConstraints.cs` | 454 | **Mission limits.** Forbidden/required parts, propellants, categories, Δv, crew count, crew professions (`TraitMods`) |
| `OrbitConstraint.cs` | 202 | **Orbital regime.** Parses the `orbit` sub-object (polar, equatorial, keostationary, Molniya, …) and gates the submit button |
| `PartClassifier.cs` | 189 | **Part analysis.** Derives propellants, engine categories, part categories from live modules |
| `EditorPartEnforcer.cs` | 136 | **Editor filter.** Hides forbidden parts in the VAB/SPH palette during an active contract |
| `CraftDeltaV.cs` | 55 | **Δv reader.** Reads stock `VesselDeltaV` for mission limit validation |

### Rendering

| File | Lines | Role |
|------|------:|------|
| `VesselRenderer.cs` | 1,258 | **Blueprint rendering.** 8-view capture, dual-pass alpha, layer 30 isolation, NW thumbnails, Deferred-path support |
| `DecalCapture.cs` | 277 | **ConformalDecals.** Re-issues the decal draw on the isolation layer so decals appear in blueprints; renders never-drawn text decals |
| `CinematicCapture.cs` | 283 | **Hero shots.** Sunlit camera pose computation, HUD toggle, ScaledSpace sync |
| `CheckpointDetector.cs` | 347 | **Milestone detection.** Rendezvous, flyby, asteroid, EVA, staging, orbit, landing, splashdown |

### Life support, rescue & repair

| File | Lines | Role |
|------|------:|------|
| `RescueImmunityGuardian.cs` | 672 | **Emergency freeze.** Lifts stranded rescue crew out of the simulation, parks them, thaws at 10 km or on demand |
| `LifeSupport/LifeSupportRegistry.cs` | 87 | Which LS mods are installed here, and which one this install actually runs |
| `LifeSupport/LifeSupportScan.cs` | 118 | What a craft is provisioned for, and for how long (the marketplace/contract LS flag) |
| `LifeSupport/LsFreeze.cs` | 95 | Tells each installed LS mod to let go of frozen crew, and hands them back with a clean slate |
| `LifeSupport/LsRations.cs` | 188 | Stows a few days of the *rescuer's* resources aboard the wreck |
| `LifeSupport/LsReflect.cs` | 244 | Defensive reflection helpers shared by every adapter |
| `LifeSupport/LsAdapterBase.cs`<br>`ILifeSupportAdapter.cs`<br>`LsEndurance.cs` | 85 / 65 / 26 | Adapter contract, shared base, endurance result type |
| `LifeSupport/UsiLsAdapter.cs`<br>`TacLsAdapter.cs`<br>`SnacksAdapter.cs`<br>`KerbalismAdapter.cs`<br>`DeepFreezeAdapter.cs` | 75 / 86 / 54 / 150 / 45 | One adapter per optional LS mod, reflection-only |
| `TraitRepair.cs` | 480 | **Roster repair.** Reversibly replaces a profession no installed mod defines; restores it when the mod returns |
| `LocalNotifActions.cs` | 114 | Action keys carried in a local notification's `data`, rendered as a button by both front ends |

### Identity, security & integrations

| File | Lines | Role |
|------|------:|------|
| `DeviceId.cs` | 196 | Stable per-install GUID, MAC address for reports, capped `KSP.log` reader for bug reports |
| `ModVersion.cs` | 93 | DLL SHA-256 hash, challenge-response attestation |
| `PartCatalogUploader.cs` | 92 | Uploads the installed part list, FNV-1a hash gate |
| `PhysicsRangeManager.cs` | 216 | Temporarily disables Physics Range Extender during submissions |
| `ClickThroughHelper.cs` | 114 | Routes IMGUI windows through Click Through Blocker when available |
| `ToolActions.cs` | 690 | Tools-tab operations: flag import, craft export, quicksend, bug report — shared by the sidebar and the web bridge |
| `Favorites.cs` | 108 | Starred players for the quicksend picker (PluginData, not localStorage — the bridge origin changes every launch) |
| `DebugTestPanel.cs` | 378 | In-game security self-test panel. **Compiled out** of production builds (`#if GK_DEBUG_PANEL`) |

### Browser UI bridge (`Web/`)

| File | Lines | Role |
|------|------:|------|
| `Web/LocalServer.cs` | 363 | Loopback HTTP listener: static bundle, `/gk/*` game bridge, `/api/*` proxy, `/gk/events` SSE |
| `Web/BridgeAuth.cs` | 158 | Launch nonce, session cookie + CSRF token — layers 3 and 4 of the five-layer gate |
| `Web/ApiProxy.cs` | 315 | Forwards `/api/*` upstream with the token attached in C#. **The allow-list is the security boundary** |
| `Web/GkRoutes.cs` | 828 | Everything only this process can do: game state, craft actions, contract creation |
| `Web/EventStream.cs` | 152 | SSE push, tee'd from the notification socket |
| `Web/StaticFiles.cs` | 200 | Serves `GameData/BoundlessMissions/WebUI/` |
| `Web/MainThreadQueue.cs` | 180 | Marshals anything touching KSP back onto the Unity main thread |
| `Web/JobRegistry.cs` | 96 | Long-running job results, readable across a page reload |

### UI

| File | Lines | Role |
|------|------:|------|
| `UI/Gui/SidebarController.cs` | 1,025 | Owns the Canvas, the expand animation, input locks, font recovery |
| `UI/Gui/UIF.cs` | 1,207 | Fluent widget builder; font/material re-assertion (`RefreshFont`, `RefreshText`) |
| `UI/Gui/Theme.cs` | 380 | Design tokens ported from the website's `globals.css`; the normalized font material |
| `UI/Gui/Sprites.cs` | 749 | Procedural 9-slice rounded-rect sprites (there is no Unity Editor on this machine) |
| `UI/Gui/SidebarPanel.cs` | 201 | Panel base — `WorksOffline`, show/hide, refresh contract |
| `UI/Gui/FloatWindow.cs` | 284 | Draggable, clamped window shell (used by the submission screen) |
| `UI/Gui/Panels/ContractsPanel.cs` | 1,637 | Contract inbox + active contract actions (incl. rescue wreck spawn) |
| `UI/Gui/Panels/SubmitPanel.cs` | 397 | The submission screen — a pure read of `SubmissionSession` |
| `UI/Gui/Panels/ToolsPanel.cs` | 441 | Export, flag import, quicksend, bug report, roster repair card |
| `UI/Gui/Panels/NotificationsPanel.cs` | 451 | The feed, with local-notification action buttons |
| `UI/Gui/Panels/SettingsPanel.cs` | 323 | Server choice, feature toggles, interface switch |
| `UI/Gui/Panels/MissionsPanel.cs`<br>`ProfilePanel.cs`<br>`MarketPanel.cs` | 172 / 209 / 194 | Weekly missions, profile, marketplace *selling* half |
| `UI/Gui/ContractForm.cs` | 610 | Contract creation — rewards, constraints, mod-list mode, auctions, rescues |
| `UI/Gui/PlayerPicker.cs`<br>`BodyPicker.cs`<br>`DatePicker.cs` | 469 / 175 / 285 | Shared search/favourites, celestial body, and inline month-grid pickers |
| `UI/Gui/ImageViewer.cs` | 409 | Full-screen zoom/pan lightbox for submission images |
| `UI/Gui/ToastHost.cs` | 253 | Toast notifications on the canvas |
| `UI/Gui/ScrollForwarder.cs`<br>`ScrollMemory.cs`<br>`Fmt.cs` | 58 / 158 / 105 | Nested-scroll forwarding, scroll position memory, formatting helpers |
| `UI/ConsentWindow.cs` | 169 | First-run privacy/terms opt-in (IMGUI — it gates the canvas) |
| `UI/LinkWindow.cs` | 283 | Discord account linking flow |
| `UI/SuspendedWindow.cs` | 197 | Service-suspension notice: reason, live countdown, "check again" |
| `UI/UpdateRequiredWindow.cs` | 128 | Mandatory update gate |
| `UI/DeviceVerifyWindow.cs` | 79 | Device binding approval |
| `UI/DataPausedWindow.cs` | 106 | Data-sharing paused notice |
| `UI/CheckpointPrompt.cs` | 147 | "Capture this moment?" prompt over the flight scene |
| `UI/WebUiWindow.cs` | 119 | Recovery path for a browser that never opened |
| `UI/GKSkin.cs` | 144 | Custom IMGUI `GUISkin` for the windows above |

---

## Core Systems

### Lifecycle & State Machine — `GeneKermanMod`

`GeneKermanMod` is the mod's entry point. It is a Unity `MonoBehaviour` marked
with `[KSPAddon(KSPAddon.Startup.MainMenu, true)]` and `DontDestroyOnLoad`,
making it a persistent singleton that survives scene changes.

State machine: `Unlinked → Linking → Linked`.

**Responsibilities:**

- **Initialization**: Creates the `ApiClient`, `NotificationSocket`,
  `CheckpointDetector`, `ClientState` and the uGUI `SidebarController`. Loads
  `PluginData/settings.cfg` and `PluginData/consent.cfg`. Registers the KSP
  Application Launcher toolbar button.
- **Update loop** (`Update()`): Ticks the notification socket, drains queued
  WebSocket notifications, drains `Web.MainThreadQueue` (anything the loopback
  bridge needs done on the main thread), polls the API on a timer fallback when
  the socket is down, ticks the checkpoint detector during flight, and re-checks
  an active suspension against the clock.
- **Scene awareness**: Hooks `GameEvents.onGameSceneLoadRequested` to reset
  state, re-register event hooks, and adjust UI visibility across Space Center,
  Flight, Editor, and Tracking Station scenes.
- **Coroutine host**: Provides `RunCoroutine()` so non-MonoBehaviour classes
  (like `ApiClient`) can start Unity coroutines.
- **Notification handling**: Processes notification dicts from the socket/poll,
  dispatches them to the appropriate handler (contract update, vessel delivery,
  flag delivery, version poke, etc.), and surfaces them via the canvas toasts.
- **Local notifications**: `RaiseLocalNotification()` raises a client-side
  notice about something *this install* found (a broken roster trait, a missing
  mod, a substituted part). A local notification may carry an action key in its
  `data` dict — see `LocalNotifActions`.
- **Gate rendering**: `OnGUI` draws whichever IMGUI gate applies (consent,
  update required, suspension, data paused, device verification, link) and
  suppresses the canvas beneath it.

**Key state:**

```
Instance       : static singleton reference
Api            : the networking client
State          : ClientState — the account, by reference
Socket         : the WebSocket handler
Detector       : the checkpoint detector
PluginDataPath : GameData/BoundlessMissions/PluginData (runtime storage root)
```

### Client State — `ClientState`

`ClientState` is the account as the client knows it: profile, weekly missions,
contracts (incoming and active), and the notification feed — plus the fetch
coroutines, the merge of server notifications with local ones, the de-dup, the
unread count, and every action (`RequestMarkRead`, `RequestDismiss`, accept,
cancel, dispute, …).

It is deliberately headless and deliberately shared **by reference**: the uGUI
sidebar, the browser bridge (`Web/GkRoutes`) and the notification socket all
read the same object. A second copy drifts the moment either side gains a
mutation, and the unread badge is the first thing to show it.

### Networking — `ApiClient`

`ApiClient` encapsulates all HTTP communication with the server. Every API call
is a Unity coroutine using `UnityWebRequest` — the only HTTP client available in
KSP's Mono runtime.

**Request flow:**

1. Every request includes headers: `Authorization: Bearer <token>`,
   `X-Device-Id: <deviceId>`, `X-Mod-Version: <version>`,
   `X-Mod-Hash: <sha256>`.
2. **Consent gate**: `TransmissionBlocked` short-circuits every call until the
   player has accepted the current policy version. Nothing leaves the process
   before that.
3. **Version gate (HTTP 426)**: If the server returns 426 Upgrade Required, the
   client shows `UpdateRequiredWindow` and blocks further API calls until the
   player updates. The `NotificationSocket` also triggers a re-check when it
   receives a `"version"` frame. Acknowledging the gate drops the client into
   **limited mode** — only panels declaring `WorksOffline` (Tools, Settings)
   render, behind a banner offering Re-check / Download latest.
4. **Device binding (HTTP 403 `device_verify`)**: The player's device hasn't
   been approved yet — `DeviceVerifyWindow` is shown with instructions to
   approve from Discord.
5. **Suspension (HTTP 403 `suspended`)**: routed to
   `GeneKermanMod.OnSuspended` → `SuspendedWindow`. The session token is
   deliberately *not* revoked, so every subsequent request keeps returning the
   explanation instead of dropping the player to a link screen.

**Persistence** (all under `PluginData/`):

- `session.token` — the current 30-day signed session token
- `sessions.cfg` — known sessions
- `settings.cfg` — user configuration (see [Configuration](#configuration))

Note that `settings.cfg` stores the server as separate `serverProtocol` /
`serverHost` / `serverPort` values, never as a URL: `//` is a comment delimiter
in `ConfigNode` and would truncate the value. The marketplace URL is split the
same way.

**Key methods:**

- `Get(path, callback)` / `Post(path, json, callback)` — generic REST calls
- `UploadVessel(...)` — multipart form upload (vessel node + blueprint PNG +
  craft file + screenshot + metadata JSON)
- `DownloadCraft(url, callback)` — downloads a `.craft` file
- `GetWsTicket(callback)` — obtains a single-use WebSocket auth ticket

**Challenge-response attestation:**

The server can send a challenge (nonce + offset + length). `ModVersion.AttestDigest`
hashes `SHA256(nonce || dll_bytes[offset..offset+length])` and returns the
digest, proving the player is running an unmodified official DLL.

### Real-Time Push — `NotificationSocket`

`NotificationSocket` maintains a persistent WebSocket connection for
server-pushed events (contract updates, vessel deliveries, version pokes).

**Design constraints:**

- KSP's Mono runtime lacks a usable `ClientWebSocket`, so the mod bundles
  `websocket-sharp.dll`.
- WebSocket event callbacks fire on background threads → all received
  notifications are enqueued into a `ConcurrentQueue<Dictionary<string,object>>`
  and drained on the Unity main thread by `GeneKermanMod.Update()`.

**Connection lifecycle (all driven by `Tick()` on the main thread):**

1. `Connect()` enables the socket; `Tick()` calls `OpenSocket()` on the next
   frame.
2. A short-lived ticket is requested via `TicketProvider` (an
   `ApiClient.GetWsTicket` coroutine) so the long-lived auth token never
   appears in the WebSocket URL.
3. Connection uses `wss://` with cert validation disabled (KSP ships an outdated
   cert store).
4. On close, exponential backoff (2s → 30s) schedules the next reconnect via
   `pendingRetry` / `ScheduleRetry()`.
5. A 25-second keepalive ping (`{"type":"ping"}`) is sent via `SendAsync` to
   keep NAT mappings open and detect silently-dropped connections.
6. When `ConsumeJustConnected()` returns true after a reconnect, the host runs
   a catch-up notification poll to recover messages lost while disconnected.

**Frame types:**

- `{"type":"version"}` → sets `versionPoke = true` → host re-checks mod version
- `{"notification":{...}}` → queued for main-thread processing

---

## Vessel & Craft Transfer Pipeline

Vessel transfer is the mod's most complex subsystem. There are two transfer
paths — **live vessel** (in-flight `ProtoVessel` serialization) and **craft
file** (`.craft` blueprint from the editor or on disk) — each with its own
export/import chain.

### Serialization — `VesselTransfer`

`VesselTransfer` handles the core serialization of vessels and crafts using
KSP's `ConfigNode` system.

**Export (sending a live vessel):**

1. `Vessel.BackupVessel()` → `ProtoVessel` → `protoVessel.Save(node)` →
   `ConfigNode`
2. Crew roster data is embedded as `GKCREW` child nodes (name, trait, level,
   experience, gender) so the receiver can reconstruct the exact crew.
3. `FlagTransfer.EmbedFlagsInNode()` embeds custom flag images.
4. `CkanGenerator.EmbedModsInNode()` embeds the mod dependency manifest.
5. `TextureTransfer.EmbedInNode()` embeds the Textures Unlimited manifest.
6. `RealFuelsTransfer.EmbedInNode()` embeds the RealFuels manifest.
7. `ScaleBridge.SnapshotIntoVesselNode()` bakes the TweakScale-computed values.
8. The final `ConfigNode` is serialized to text for upload.

**Import (receiving a vessel):**

1. Text is parsed via `ConfigNode.Load()` using a **temporary file** on disk
   (because `ConfigNode.Parse()` is unreliable in KSP's Mono runtime).
2. `FlagTransfer.ExtractAndInstallFlags()` installs flag images and strips
   `GKFLAG` nodes.
3. `CkanGenerator.ExtractCheckAndStripMods()` strips and processes `GKMODS`.
4. `PartAliases.ApplyToVesselNode()` substitutes missing parts for the local
   equivalents — **before** the two reconcile passes, because a substituted part
   is a different prefab with different modules.
5. `TextureTransfer.ExtractCheckAndStripFromNode()` checks or reconciles the
   paint job.
6. `RealFuelsTransfer.ExtractCheckAndStripFromNode()` checks or reconciles the
   fuel/engine configuration.
7. `ScaleBridge.NeutralizeTweakScaleForImport()` removes TweakScale MODULE
   nodes from baked parts (so `GeneKermanScale` is the sole authority).
8. Crew data from `GKCREW` nodes is applied to the `ProtoCrewMember` roster via
   `ApplyTrait`, which refuses to create a profession this install cannot
   define; downgrades are reported once and recorded for `TraitRepair`.
9. The vessel is injected into the current game via
   `FlightGlobals.Vessels.Add()` / `ProtoVessel.Load()`.

**Export (sending a `.craft` file):** every export path — contract submission
(×2), quicksend (×2), marketplace listing, export-to-file (×2), and the
blueprint attached to a vessel transfer — runs the **same** chain, in this
order:

```
bake (ScaleBridge) → GKFLAG → GKTSVER → GKTU → GKRF → GKMODS → GKTHUMB
```

Baking is not optional on any of them. A blueprint has no import-side scale
step, so a craft that leaves unbaked cannot be repaired on arrival. Baking
fixes three things at once: the scale itself, the `pos` re-anchor (KSP
serialises a surface-attached part on a scaled parent with a KSP-Recall-dependent
encoding), and the root-local layout pin that `GeneKermanScale` re-asserts at
runtime.

`FlagTransfer.EmbedFlagsInCraft()` and every block after it append **raw text**
and never re-serialize the craft body via `ConfigNode.ToString()`, which would
wrap it in a spurious `root {}` node that KSP's craft loader rejects.

**Import (receiving a `.craft` file):** handled by `CraftInstaller.Install()` in
strict reverse-append order — see below.

**Ownership and removal:** transferred kerbals are tagged `"{owner}'s {name}"`
while they live in someone else's save (`ApplyOwnershipTag`, reversible), which
makes an *untagged* roster name the test for "this one is mine".
`RemoveVesselFromSave` leans on that: giving a craft up calls `Vessel.Die()`,
which kills whoever is aboard, so every removal also decides a `CrewFate` —
`LeavesWithCraft` for the issuer of a rescue, `BorrowedOnly` for its rescuer.
The fate is chosen where the removal is *queued* (only that caller knows which
side this is) and rides the persisted queue, because the removal itself can run
sessions later. `PurgeBorrowedGhostCrew` sweeps borrowed kerbals left dead or
missing by a craft that vanished before its removal ran — not cosmetic, since
KSP counts them against the astronaut-complex hire limit and refuses any new
applicant name that is a substring of an existing roster name.

### Data Collection — `VesselDataCollector`

`VesselDataCollector` captures vessel telemetry for submission metadata:

- **`VesselSnapshot`**: vessel name, type, situation, body, lat/lon/alt, orbital
  elements (SMA, ecc, inc, Ap, Pe, period), part count, mass, cost, crew count
- **`CaptureLoadedVessels()`**: snapshots all vessels in physics range
  (excluding debris, space objects, flags)
- **`GetNearbyVessels()`**: returns live `Vessel` references for multi-vessel
  submission
- **`FindCraftFile()`**: searches `saves/<save>/Ships/` and root `Ships/` for a
  craft by name
- **`CaptureScreenshot()`**: saves a PNG via `ScreenCapture.CaptureScreenshot()`

### Craft Installation — `CraftInstaller`

`CraftInstaller.Install()` is the single entry point for writing a received
craft to disk:

1. Decompresses gzip-compressed craft data (magic bytes `0x1F 0x8B`)
2. Strips side-channel blocks in **exact reverse** of the export order:
   `GKTHUMB → GKMODS → GKRF → GKTU → GKTSVER → GKFLAG`
3. `PartAliases.ApplyToCraft()` — substitute missing parts
4. `TextureTransfer.ReconcileCraftBody()` — keep the recolour modules the local
   prefabs accept, drop the ones they can't
5. `RealFuelsTransfer.ReconcileCraftBody()` — check on an RF install, reconcile
   to local fuels on one without
6. Parses the craft type header (`type = VAB|SPH`)
7. Writes to `saves/<save>/Ships/<type>/` with collision-avoidance numbering
8. Writes the `.loadmeta` sidecar if provided
9. Calls `CkanGenerator.OnCraftInstalled()` for missing-mod detection
10. Calls `CraftThumb.InstallThumbnail()` for the craft browser

> **Critical invariant**: The `.craft` body is never round-tripped through
> `ConfigNode`. Side-channel blocks are appended as raw text at the end and
> stripped as raw text from the end. This preserves the byte-for-byte integrity
> of the craft file, which KSP's craft loader is very strict about.

---

## Side-Channel Data Blocks

The mod carries auxiliary data alongside vessel/craft transfers using a system
of **side-channel blocks** — structured text nodes appended to or embedded
within KSP's `ConfigNode` serialization format.

For `.craft` files (raw text), blocks are **appended at the end** and
**stripped in reverse order** on import:

```
<original craft body>      ← already baked by ScaleBridge
GKFLAG   { ... }           ← appended first,  stripped last
GKTSVER  { ... }
GKTU     { ... }
GKRF     { ... }
GKMODS   { ... }
GKTHUMB  { ... }           ← appended last,   stripped first
```

Order is load-bearing in both directions. Each stripper cuts from its own marker
to end of file, so stripping out of order takes later blocks with it. One useful
consequence: an **older client** receiving a craft that carries `GKRF` loses
nothing — its `GKTU` strip cuts to end of file and removes `GKRF` along the way.

For vessel `ConfigNode`s, blocks are embedded as child nodes (`GKFLAG`,
`GKCREW`, `GKMODS`, `GKTU`, `GKRF`) and removed after extraction.

Three of these blocks exist for the same reason: **the mod adds no parts.**
TweakScale, Textures Unlimited and RealFuels all configure *existing* parts, so
every mod-detection path in this codebase — all of which resolve parts →
GameData folder via `AvailablePart.partUrl` — is blind to them. The block is
what carries the fact a part name cannot express.

### Custom Flags — `FlagTransfer`

**Problem:** KSP stores flags as GameData-relative paths (e.g.
`MyFlags/eagle`). When a craft moves to another player who doesn't have that
image, KSP shows a missing decal.

**Solution:** Content-addressed flag embedding.

**Export:**

1. Walk the ConfigNode tree, collecting every value whose key contains "flag"
   and whose value is a path (contains `/`).
2. Skip stock flags (`Squad/Flags/default`, `SquadExpansion/*`).
3. For each custom flag, read the image bytes from disk (probing `.png`,
   `.dds`, `.jpg`, `.jpeg`, `.truecolor`, `.mbm`, `.tga`).
4. Compute `SHA-256(imageBytes)` → new URL = `GeneKerman/Flags/<hex>`.
5. Rewrite all flag references in the node/craft to the content-addressed path.
6. Encode image as URL-safe base64 (`+`→`-`, `/`→`_`, no padding) — standard
   base64's `/` would be parsed as a comment delimiter by `ConfigNode`.
7. Append as `GKFLAG` nodes with `url`, `ext`, and `data` values.

**Import:**

1. Decode base64, write to `GameData/GeneKerman/Flags/<hash>.<ext>`.
2. Register with `GameDatabase` at runtime (creates a `Texture2D`, loads image
   via `LoadImage()`, adds a `TextureInfo`) so the flag renders immediately
   without a game restart.
3. Content addressing provides automatic deduplication (same image → same file)
   and collision immunity (different images → different files).

**Why content addressing, and why it is also the evidence.** KSP names
player-imported flags with short random ids (`Squad/Flags/UtB0nwS`), so two
players' identically-named flags are different pictures. A hash can only be
computed from the bytes, and a URL is only rewritten for a flag whose bytes were
read — so a `GeneKerman/Flags/<hash>` URL in a craft is **proof the sender held
the image**. A reference that doesn't resolve on arrival was lost in transit,
never missing at export.

**Dangling references are reset at both ends** — `Unresolvable` on export,
`ResetDanglingFlagsInText` / `InNode` on import (always *after* the carried flags
are installed) — back to `Squad/Flags/default`. Left alone the problem is
self-perpetuating rather than cosmetic: a re-export finds no file, embeds
nothing, and ships the same dead URL onward, while every module resolving it
errors. And not always in its own name: `ModuleConformalFlag` with
`useCustomFlag = false` renders the **mission** flag, so a broken mission flag
surfaces as a ConformalDecals exception mid-`OnLoad` (the stock flag decals only
warn).

A conformal decal carrying its own `flagUrl` transfers like any other reference —
`CollectFlagUrls` matches any flag-keyed value that looks like a path.

> The URL-safe base64 is not a style choice. A raw `//` pair makes `ConfigNode`
> treat the rest of the value as a comment and silently truncate the image, which
> is the bug that minted the dangling references still sitting in the test saves.

### TweakScale Bridge — `ScaleBridge` / `GeneKermanScale` / `TweakScaleGuard`

**The problem:** TweakScale rescales parts using a factor + exponent table.
Different TweakScale versions/forks produce different final values from the same
factor. A player without TweakScale gets stock-sized parts. This makes
transferred crafts unreliable.

**The solution:** Snapshot absolute, already-computed values on the sender and
replay them on the receiver without any TweakScale dependency.

**Three classes work together:**

#### `ScaleBridge` (sender-side snapshot + receiver-side neutralization)

**On send (vessel):** `SnapshotIntoVesselNode(vessel, vesselNode)`
- For each part where `model.localScale / prefab.localScale ≠ 1`:
  - Read the linear scale factor from the model transform (or fall back to
    TweakScale's `currentScale / defaultScale` via reflection)
  - Read the final dry mass from `part.mass`
  - Read curated module stats (thrust, torque, RCS power) from live fields
  - Write `gkActive=True`, `gkLinear`, `gkMass`, `gkFields` into the part's
    `GeneKermanScale` MODULE node

**On send (craft):** `SnapshotIntoCraftBytes(craftBytes, liveParts)`
- Matches craft PART nodes to live `Part` objects by `craftID`
- Snapshots + removes TweakScale MODULE node in one pass
- Rewrites `pos` values from live editor transforms to fix KSP-Recall-dependent
  surface-attach encoding
- Writes a root-local layout pin (`gkPin`, `gkPinPos`) into each part so the
  receiver can re-assert the correct position

**On receive:** `NeutralizeTweakScaleForImport(vesselNode)`
- For every PART with `gkActive=True`, removes the TweakScale MODULE node

#### `GeneKermanScale` (receiver-side PartModule)

A `PartModule` added to **every part prefab** via the ModuleManager patch
`@PART[*]:FINAL` in `Patches/GeneKermanScale.cfg`. Dormant by default
(`gkActive=false`, `gkLinear=1`).

When a transferred craft arrives with snapshot data:

- **`OnLoad`**: Applies model scale and attach-node offsets early (before KSP
  builds physics joints)
- **`OnStart`**: Re-applies everything (model, nodes, mass, module stats, drag
  cubes) and starts a 12-frame `ReapplyGeometry()` coroutine to win over KSP's
  own surface-attach re-projection
- **`ApplyPin()`**: In editor scenes, forces the part's transform position to
  `root.position + root.rotation * gkPinPos`, countering KSP's re-seat

**Curated module stats** (the `StatFields` dictionary):
```
ModuleEngines / ModuleEnginesFX : maxThrust, minThrust
ModuleRCSFX / ModuleRCS        : thrusterPower
ModuleReactionWheel            : PitchTorque, YawTorque, RollTorque
```

Resources are NOT handled — `maxAmount` is already persistent in KSP's
serialization and reconstructs correctly without intervention.

#### `ScaleEditorReapply` (undo/redo fix)

A `KSPAddon(EditorAny)` that listens for `onEditorUndo` / `onEditorRedo` and
calls `RequestGeometryReassert()` on every `GeneKermanScale` instance. This is
needed because undo/redo rebuilds the part tree (triggering surface-attach
re-projection) without re-running `OnStart`.

#### `TweakScaleGuard` (version warning)

The backstop for a bake that bailed or threw, and the version-mismatch warning.

- **Export**: Appends a `GKTSVER { ver = <version> }` block — but only to crafts
  where something is **actually rescaled**.
- **Import**: Compares the sender's version against the local install. Posts a
  screen warning if TweakScale is missing or the version differs.

> The trigger is "is anything actually rescaled", never "does this craft mention
> TweakScale". TweakScale attaches its module to every compatible part whether or
> not you scale it, and `ScaleBridge` only strips the ones it snapshots — so a
> fully baked craft still carries TweakScale modules (roughly two thirds of them,
> measured across real crafts). Matching the bare module name warned every
> recipient of every baked craft about a mismatch that could not affect them.
>
> The check compares each module's `currentScale` against its `defaultScale`
> using **the same epsilon as `ScaleBridge`** — the two must agree, or the guard
> would fire on exactly the parts `ScaleBridge` judged unscaled and left behind.
> A module whose fields are absent or unparseable is treated as scaled: silence
> has to be earned.
- **Version detection**: Probes `AssemblyLoader.loadedAssemblies` for the
  `Scale` assembly (exact name match — avoids companions like
  `TweakScaleCompanion_*` or `Scale_Redist`). Falls back to whichever assembly
  defines `TweakScale.TweakScale`.

### Textures Unlimited — `TextureTransfer`

Carries a craft's **Textures Unlimited** paint job across a transfer, and
guarantees a TU-painted craft still loads for someone who hasn't got TU.

The recolour data itself needs no channel: TU keeps it in a `KSPTextureSwitch`
PartModule whose persistent fields (texture set name, packed colour channels)
are already written into the `.craft`/`VESSEL` node, so it has always ridden
along. What it lacks is two things a part name cannot express.

**Which mod.** TU adds zero parts, so the part walk cannot see it. The fix is to
resolve each referenced texture set back to the GameData folder of the config
that *defines* it (`GameDatabase`'s `KSP_TEXTURE_SET` entries) — knowable only
on the sender's machine — and carry that in the `GKTU` block. That block also
feeds `CkanGenerator.ResolveMods`, which turns a missing recolour pack into an
installable CKAN modpack, and `ToolActions`, which unions
`TexturePackFoldersForCraft` into the marketplace mod tags and sends
`CraftHasCustomTextures` as a separate `custom_textures` flag (the website's
**"Modded Textures Available"** tag). The flag is separate on purpose: a texture
set the *sender* cannot resolve contributes no folder while the paint job is
still on the craft.

**A clean load without it.** On import, `ReconcileCraftBody` keeps every
recolour module the local prefab accepts and drops the ones it can't, so the
craft arrives either fully painted or in stock colours — never with orphan
module nodes. The per-part prefab check is what catches the case a folder check
cannot: TU installed, but not the pack that patches *this* part. It is
deliberately **not** consulted when TU is absent entirely, since it answers
"leave it alone" for a part it can't find and a craft can arrive with missing
parts.

The texture *files* are never embedded — a set is DDS art belonging to the pack
author, far too big for a craft transfer and not ours to redistribute. This is a
manifest plus a guard, not a copy.

Gated by `enableTextureTransfer` (default on). TU's module and field names are
matched from a defensive list rather than a single literal, so an unrecognised
variant degrades to "carried but not understood" instead of to a broken craft.

> Not to be confused with **TUFX**, which is scene-wide post-processing and
> carries nothing per-craft.

### RealFuels / Realism Overhaul — `RealFuelsTransfer`

Carries a craft's **RealFuels / Realism Overhaul** fuel-and-engine configuration,
and guarantees an RF-configured craft still loads for someone without RealFuels.

Like TU, RF adds zero parts — it configures existing ones via `ModuleFuelTanks`
(tank type + `TANK` nodes), `ModuleEngineConfigs` (selected config) and its
`ModuleEnginesRF` engine replacement — and the config state already rides in the
craft's MODULE/RESOURCE nodes, so two RSS-RO players exchange working crafts
with no help at all.

What the `GKRF` block adds:

- the sender's RF version and GameData folder
- whether the install runs RO (`env`)
- each tank **type** resolved to the GameData folder whose `TANK_DEFINITION`
  declares it (the same `GameDatabase` lookup `TextureTransfer` does for
  `KSP_TEXTURE_SET`)
- the selected engine-config **names** — names only, because ModuleManager
  merges a `CONFIG` into the part config and its origin folder is unrecoverable.
  Those are checked recipient-side against the local post-patch `PART` config.

**On import with RF**: checks, not changes. Undefined tank types, unavailable
engine configs, and an RO ↔ non-RO environment mismatch are each reported once.

**On import without RF**: a reconcile. The RF module nodes and every part-level
`RESOURCE` naming a locally-undefined propellant are dropped, so parts refill
from their local prefabs and the craft arrives in local fuels instead of
half-loaded — with the caveat stated that the design was balanced for other
physics.

The reverse hazard needs no manifest: a craft with propulsion but no RF state
arriving on an **RO** install is warned about locally, since RO's patches
re-plumb it on load. Plain-RF installs stay quiet.

The generated CKAN modpack lists RealFuels and any missing tank packs but
**never RO** — RO is an environment, not a dependency, so like a DLC it is named
in the warning and kept out of the `.ckan`. Marketplace listings get the RF
folders (including `RealismOverhaul` as a visible tag) unioned into their mod
tags, exactly as TU packs are.

Gated by `enableFuelConfigTransfer` (default on); switched off it still scans
and warns but writes nothing — the `PartAliases` contract.

### CKAN Mod Dependency — `CkanGenerator`

**Problem:** A `.craft` stores only part *names*, never which mod each part
came from. A recipient who's missing a mod just sees "this craft has missing
parts" with no way to know what to install.

**Solution:** Map parts to mods at export time and generate a CKAN metapackage
on import.

**A GameData folder is not a mod.** The whole file is built around not confusing
the two — see [A note on mod detection](#a-note-on-mod-detection). CKAN's
`registry.json` is indexed by **install path**, and a part resolves through the
longest path prefix exactly one module owns; a prefix two modules share is left
out of the index so the walk drops past it rather than guessing.

Getting this wrong broke both directions at once. On export the arbitrary winner
was usually the *companion* module — which, having no parts, is the one useless
thing to hand CKAN. On import, a recipient holding only the companion read as
already having the mod, so no `.ckan` and no warning were produced at all.

**Export:**

1. For each non-stock part, resolve its owning module through the path-prefix
   index above, falling back to the GameData folder from `AvailablePart.partUrl`.
2. If CKAN is installed, read `CKAN/registry.json` (cached for the session). A
   sender **without** CKAN has nothing to resolve against and still reports the
   bare folder.
3. Include inventory items (stock `ModuleInventoryPart` STOREDPART nodes, KIS
   `ModuleKISInventory` ITEM nodes) — parts inside containers would otherwise
   be missed.
4. Embed as a `GKMODS` block with `MOD { folder, path, ckan, name }` entries.
   `path` is what carries the answer and what the recipient-side check tests; it
   is absent from blocks written by older clients, so every read falls back to
   `folder`.

**Import:**

1. Strip GKMODS block and parse mod list.
2. Diff against `GameData/` directories to find missing mods.
3. For missing mods, generate a `.ckan` metapackage file in
   `<KSP>/GeneKerman_MissingMods/<context>.ckan`.
4. Show a persistent notification: "⚠ Missing N mod(s) — open the .ckan file
   in CKAN."
5. Write a `<craft>.gkmods` sidecar alongside the installed craft so the editor
   can re-check later.

**Editor re-check:** `EditorCkanWatcher` is a `KSPAddon(EditorAny)` that
listens for `onEditorLoad`. When a craft with a `.gkmods` sidecar is loaded, it
re-runs missing-mod detection and regenerates the CKAN metapackage if needed.

**Also used for the marketplace.** The same resolution names a part's mod on a
listing (`MarketplaceModName` → "DeepFreeze", not "REPOSoftTech") — but only when
the resolved root actually contains the part, so a plugin-only sibling rooted at
`SomeMod/Plugins` cannot lend its subfolder name to a part living under
`SomeMod/Parts`. `ResolveMods` is also the hook `TextureTransfer` and
`RealFuelsTransfer` use to add dependencies the part walk can never reach.

### Part Substitution — `PartAliases`

`CkanGenerator` answers "which **mod** is missing?". `PartAliases` answers the
narrower and more common question: "this exact **part** isn't here, but the same
thing is, under another name." It runs on both the `.craft` and VESSEL-node
import paths, and stays quiet when the recipient has the mod but not that part —
the gap `CkanGenerator` cannot see.

The motivating case is Making History's `InflatableAirlock` versus ReStock+'s
`restock-airlock-1` — the same object under two names, since ReStock retextures
the DLC part with the very asset ReStock+ builds its DLC-free stand-in from.

That is also how the table was derived, **mechanically rather than by eye**: two
parts are listed as the same thing only when ReStock's DLC patch and the ReStock+
stand-in resolve to the same `ReStock/Assets/...` model, which proves identical
geometry and attach nodes.

Shared art does *not* prove shared balance — ReStock+ reuses the Wolfhound's and
Skiff's bells for much smaller engines — so those live in a separate
**`LookAlikes`** list that is only ever reported, never substituted.

Substitution runs in **both directions**, because ReStock+ *hides* its stand-ins
(`TechHidden` + `category = none`) when the DLC is present, and a career save
treats a hidden part as unpurchased and blocks launch. So "usable" here means
loaded **and** not hidden.

Gated by `enablePartSubstitution` (default on, since it only engages on a craft
that would otherwise refuse to load). Switched off it still scans and reports,
just as advice rather than as changes made. Every swap plus anything still
missing is reported in one notification.

> This file is the **source of truth** for the pairs. The bot's copy at
> `data/part_aliases.py` is generated from it by
> `python tools/gen_part_aliases.py` and must be regenerated when the table
> changes.

### Craft Thumbnails — `CraftThumb`

**Problem:** A freshly-installed `.craft` has no entry in KSP's `thumbs/`
folder, so the craft browser shows a green placeholder.

**Solution:** Render a thumbnail on the sender's side and carry it with the
craft.

**Export:** `EmbedThumbForCurrentCraft()` calls
`VesselRenderer.CaptureNWThumbnail()` to render a northwest-perspective view,
then appends it as a `GKTHUMB { data = <base64> }` block.

**Import:** `CraftInstaller` strips the block, decodes the PNG, and writes it
to `thumbs/<save>_<VAB|SPH>_<craftname>.png` — the exact path KSP's craft
browser looks up.

---

## Visual Rendering

### Blueprint Renderer — `VesselRenderer`

`VesselRenderer` produces clean blueprint images of vessels — the primary visual
submitted with missions. It shoots **8 views** of the craft (6 orthographic + 2
perspective) and composites them onto a blueprint sheet; the same machinery
renders the NW craft-browser thumbnail embedded in shared `.craft` files. A
capture that yields no vessel pixels falls back to a plain screenshot rather than
submit a blank image.

**How it works:**

1. **Layer isolation (layer 30)**: All vessel parts are temporarily moved to
   Unity layer 30 (an unused layer). This isolates them from the rest of the
   scene (terrain, skybox, other vessels).

2. **Temporary camera**: A new `Camera` is created, set to orthographic
   projection, culling everything except layer 30, aimed at the vessel's center
   of mass.

3. **Dual-pass rendering** for clean alpha:
   - **Pass 1 (black background)**: `camera.backgroundColor = Color.black` →
     render → `ReadPixels()` → `blackTex`
   - **Pass 2 (white background)**: `camera.backgroundColor = Color.white` →
     render → `ReadPixels()` → `whiteTex`
   - **Alpha computation**: For each pixel:
     `alpha = 1 - (white.r - black.r)` (since `rendered = vessel * a + bg * (1-a)`)
     Final pixel = `(black.r / alpha, black.g / alpha, black.b / alpha, alpha)`
   - This produces a clean, transparent-background vessel image with correct
     premultiplied alpha, even with semi-transparent parts.

4. **Restore**: Parts are moved back to their original layer, the temporary
   camera is destroyed.

**NW Thumbnails:** `CaptureNWThumbnail()` renders a smaller
northwest-perspective view (the angle KSP uses for craft browser thumbnails)
for the `CraftThumb` system.

### Deferred Rendering Support

**Deferred** (blackrack) replaces every part and suit shader game-wide and
prepares only its own cameras. A third-party camera rendering those shaders on
the forward path therefore draws *nothing* in the flight scene — the clears run,
zero fragments land. (The editor happens to survive, which is what made this look
scene-specific rather than shader-specific.)

When Deferred is installed, the capture camera renders on the **deferred path**
instead. That needs two workarounds, both handled inside `VesselRenderer`:

- The deferred path **ignores MSAA**. A 2× supersample plus a CPU box-filter
  stands in for it.
- The deferred path **silently reverts an orthographic camera to forward**. The
  six ortho views become narrow-FOV perspective stand-ins (`FAKE_ORTHO_FOV`),
  whose foreshortening error hides inside the framing `PADDING`.

Without Deferred, the stock forward + MSAA path is untouched.

### ConformalDecals Capture — `DecalCapture`

Layer isolation only moves things that *have* a `Renderer`, and a conformal
decal has none. `ModuleConformalDecal` (and its `ModuleConformalFlag` /
`ModuleConformalText` subclasses) hooks `Camera.onPreCull` and `Graphics.DrawMesh`es
the **target part's** mesh with its projection material on a hardcoded **layer 0**.
Every decal — image, flag and text alike — was therefore culled out of the
blueprint and out of the shared-craft thumbnail, while KSP's own thumbnail camera
showed them.

`DecalCapture` re-issues that same draw on the isolation layer, for the capture
camera only, reading the module's already-computed mesh, material and property
block rather than re-deriving a projection. The mod's own layer-0 draw still
happens and is still culled, so nothing is drawn twice.

It also generates the texture of any **text** decal that has never rendered: a
text decal's lettering is a runtime-rendered texture, and the module's fields
carry the string while nothing is drawn until `UpdateText` has run once.

All reflection, like the LifeSupport adapters — no ConformalDecals reference in
the build, and a no-op without the mod.

### Cinematic Capture — `CinematicCapture`

`CinematicCapture` produces "hero shots" — real in-game screenshots at flight
milestones with the vessel, skybox, and objects of interest in frame.

**Camera pose computation (`ComputePose`):**

The camera is placed on the **sunlit side** of the vessel (determined by
finding the direction to `Sun.Instance.sun`) so the dominant light source
illuminates the faces pointing at the lens.

Three framing modes:

1. **Object mode** (e.g. rendezvous): Camera sits perpendicular to the line
   between the two vessels, on the sunlit side, framing both with FOV 40°.
2. **Backdrop mode** (e.g. orbit achieved): Camera sits mostly opposite the
   vessel-to-body direction with a sunlit lateral nudge (~16° off the body
   axis), so the celestial body sits behind the vessel. FOV 42°.
3. **Portrait mode** (e.g. staging, EVA): A lit three-quarter view of the
   vessel alone. FOV 35°.

**Execution (coroutine):**

1. Disable `FlightCamera` controller to prevent it from overwriting the
   transform.
2. Sync the `ScaledSpace` camera to the same scaled-space pose (prevents
   duplicate planet rendering).
3. Hide the HUD via `UIMasterController.HideUI()` + `GameEvents.onHideUI`.
4. `yield return WaitForEndOfFrame` → `ScreenCapture.CaptureScreenshot(path)`.
5. Restore everything: camera position/rotation/FOV, ScaledSpace camera, HUD,
   FlightCamera.

---

## Mission Contract System

### Contract Integration — `ContractIntegration`

`GKContractScenario` is a `ScenarioModule` registered for Space Center, Flight,
and Tracking Station. It bridges API contracts to KSP's stock contract system.

- **`InjectContract()`**: Tracks a mapping from API contract ID → stock contract
  GUID. The stock `GKMissionContract` appears in the Mission Control UI with
  the mission description, payment, difficulty, and due date.
- **`CompleteContract()`**: When the API confirms completion, marks all
  `GKMissionParameter`s as complete.
- **`CancelContract()`**: Cancels the stock contract when the API contract is
  cancelled/expired.
- **Vessel import tracking**: `HasImportedVessel()` / `MarkVesselImported()`
  prevent double-importing the same contract's vessel.

Persistence uses `OnLoad` / `OnSave` with `CONTRACT_MAPPINGS` and
`IMPORTED_VESSELS` ConfigNodes, defensively guarded to never let an exception
escape into KSP's `ScenarioRunner`.

### Contract Constraints — `ContractConstraints` / `PartClassifier`

`ContractConstraints` parses the `constraints` object from the API and enforces
part-usage rules:

| Constraint | Description |
|------------|-------------|
| `forbidden_parts` / `required_parts` | Part mentions (title substring match) |
| `forbidden_part_names` / `required_part_names` | Exact internal part names (resolved by the bot) |
| `forbidden_propellants` / `required_propellants` | Propellant resource names (e.g. `SolidFuel`) |
| `forbidden_engine_categories` / `required_engine_categories` | Engine type tokens (`solid`, `ion`, `nuclear`, `chemical`, `monoprop`, `rcs`, `electric`) |
| `forbidden_part_categories` / `required_part_categories` | Part category tokens (`heatshield`, `parachute`, `solarpanel`, `wheel`, `rtg`, `ladder`, `engine`, `rcs`, `reactionwheel`) |
| `max_parts` / `min_parts` | Total part count limits |
| `max_dv` / `min_dv` | Vacuum delta-v limits (m/s), with 0.5% tolerance |
| `min_crew` / `max_crew` | Crew count limits |
| `crew_traits` | Required or forbidden crew professions, matched on the exact `ProtoCrewMember.trait` string |

**Two enforcement modes:**

- **`IsForbidden(AvailablePart)`**: Per-part check, used by `EditorPartEnforcer`
  to hide forbidden parts in the VAB/SPH editor.
- **`CheckCraft(IEnumerable<Part>, deltaVVac)`**: Whole-craft validation at
  submit time. Returns a list of human-readable violation strings.

**Crew professions** are matched by the exact `ProtoCrewMember.trait` string on
both ends, which is what lets a contract written on a modded install still mean
something on one without it. Which *mod* defines a modded profession is the one
thing that string cannot express and no part walk can recover — so it is written
down twice and kept in sync by comment: `ContractConstraints.TraitMods` here and
`data/mission_constraints.py::_TRAIT_MODS` on the bot (the same convention as
`PartClassifier.GetEngineCategories` ↔ `ENGINE_CATEGORIES`).

Both tables are **closed**: an unlisted profession yields no mod name rather than
a guessed one. And only a *floor* names its mod — a ceiling ("no Kolonists") is
satisfied by not having the mod, so naming it would read as advice to install
something in order to obey a ban.

`PartClassifier` derives the semantic facts from live `Part` objects:

- Inspects `ModuleEngines` / `ModuleEnginesFX` for propellants and throttle-lock
  (solid rocket detection)
- Classifies engine types by propellant combination (e.g. LF without Oxidizer
  or IntakeAir → nuclear; XenonGas → ion)
- Checks for module presence by string name (e.g. `ModuleAblator` → heatshield,
  `ModuleParachute` → parachute)
- Falls back to title keyword matching for categories like "ladder", "RTG"

### Editor Enforcement — `EditorPartEnforcer`

A `KSPAddon(EditorAny)` that registers an `EditorPartListFilter` to hide parts
from the VAB/SPH palette during an active contract:

- **Mod-list filtering**: Parts whose `partUrl` folder isn't in the allowed
  mod list are hidden. Supports exclusion paths (`-Squad/Expansions` to block
  DLC).
- **Constraint filtering**: Parts that match `ContractConstraints.IsForbidden()`
  are hidden.

### Delta-V Validation — `CraftDeltaV`

`CraftDeltaV.TotalVacuum()` reads the stock `VesselDeltaV.TotalDeltaVVac`:
- In the editor: the full-fuel design value
- In flight: the current remaining value (fuel already burned is lost)
- Returns `-1` when unavailable (Δv readout disabled, calc not ready), and
  callers skip the check rather than failing

### Orbit Constraints — `OrbitConstraint`

A contract's orbital-regime requirement, parsed from the `orbit` sub-object the
bot attaches to `constraints` when the mission text names a specific orbit
(polar, equatorial, keostationary, Molniya, …).

It drives the submit-button gate: a craft whose reported orbital elements don't
match is blocked before upload, and the bot re-checks authoritatively on
`/submit`. Schema and tolerances mirror `data/orbit_constraints.py` and the
`ORBIT_*` values in `settings.py`.

Unlike part limits there is **no editor enforcement** — an orbit is a flight
state, not a part choice.

### Submission — `SubmissionSession`

`SubmissionSession` is the whole submission flow with the drawing taken out: the
classification rules a mission is submitted under, the scene/vessel validation
that enforces them client-side, the render capture and its freshness check, and
the coroutine that packs and uploads everything.

**Mission types** (AI-classified by the bot, cached server-side):

| Type | Must submit from | Sends |
|------|------------------|-------|
| `craft_build` | VAB / SPH | craft file + blueprint render / screenshot |
| `active_vessel` | Flight | craft + `.loadmeta` + telemetry + renders |
| `flag_design` | — | Discord only (there is no in-game flag upload) |

`CollectUsedModFolders` builds the dependency list that rides with the
submission. It deliberately **omits TweakScale**: a baked craft does not need it.

The screen itself is `UI/Gui/Panels/SubmitPanel.cs`, mounted in a draggable
`FloatWindow` rather than as a sidebar tab — submitting is read *against* the
scene behind it (the craft on the build stage, the navball in flight), and a
centred panel that owns the middle of the screen is the wrong shape for that.
The window pauses Physics Range Extender while it is up, which is why
`WindowPanel.OnWindowClosed` must never be skipped: the X, Escape and teardown
all close it behind the panel's back.

---

## Life Support, Rescue & Save Repair

### Life Support Adapters — `LifeSupport/`

All of this is **reflection-only** — the build references no life-support mod,
and every call is a safe no-op when the target isn't installed.

| Piece | Role |
|-------|------|
| `LsAdapterBase` + `ILifeSupportAdapter` | The contract every adapter implements |
| `UsiLsAdapter`, `TacLsAdapter`, `SnacksAdapter`, `KerbalismAdapter`, `DeepFreezeAdapter` | One adapter per optional mod |
| `LsReflect` | Defensive reflection helpers shared by all of them |
| `LifeSupportRegistry` | Which LS mods are installed here, and which one this install *runs* |
| `LifeSupportScan` | Which mod a craft is provisioned for and for how long — the flag shown on marketplace listings and contract embeds |

"Provisioned for" means which installed consumption mod's resources the craft
actually carries (`Supplies` → USI, `Food`/`Water`/`Oxygen` → TAC or Kerbalism,
`Snacks` → Snacks). A craft with no LS resources is tagged `none`. Endurance is
reported **per kerbal**; the display side derives the range for
1..`CrewCapacity`.

Rates are declared once per adapter (`DailyNeedPerKerbal`) and feed both the
endurance display and the ration kit. Kerbalism's are read from its live
`Profile.rules` rather than guessed.

### Emergency Freeze — `RescueImmunityGuardian`

This is what makes a rescue work between two players on **different** life-support
mods. It is three things, and all three have to hold:

1. **`RescueImmunityGuardian`** lifts the stranded crew out of the simulation
   entirely — each kerbal is removed from the wreck (remembering their part and
   seat) and parked at `rosterStatus = Dead` so KSP's respawn timer cannot revive
   them behind our back. Nothing consumes a kerbal that isn't aboard a vessel,
   so this holds uniformly across USI-LS, TAC-LS, Snacks and Kerbalism with no
   per-mod hacks. It is also the only part that works under Kerbalism's
   background simulation.
2. **`LsFreeze`** tells each installed mod to let go of them and, on thaw, hands
   them back with a clean slate. USI, TAC and Snacks reconstruct hunger from a
   stored "last fed" timestamp and would otherwise kill a kerbal the instant it
   is thawed after 200 days frozen.
3. **`LsRations`** stows a few days of the *rescuer's* resources aboard, since a
   wreck built for TAC carries nothing a USI save recognises.
   (`emergencyRationDays`, default 3.)

Crew thaw automatically at 10 km — outside load range, so the wreck is still
unloaded and KSP seats them on load — or from a button.

A thaw is **two** releases, not one, because the freeze imposes two states: the
LS mods have to let go (`LsFreeze.Thaw`) and so does the roster
(`ReleaseParked`). Every path that drops a freeze record must therefore thaw
first — including the path where the wreck is already gone, since Kerbalism's
`disabled` flag is saved and a kerbal frozen but never thawed would be exempt
from life support forever. Any path that drops a record without seating the crew
must release them too: ours back to `Available`, borrowed ones out of the roster.
Left parked they are KIA for the rest of the save, which the Astronaut Complex
shows as kerbals simply missing from the Available tab.

Gated by `enableEmergencyFreeze` (default on).

### Trait Repair — `TraitRepair`

Repairs a save whose roster holds a kerbal with a profession no installed mod
defines — the state `VesselTransfer.ApplyTrait` refuses to *create*, arrived at
by uninstalling a mod between sessions.

It is not cosmetic. A trait string is just a name; KSP resolves it to an
`EXPERIENCE_TRAIT` config on demand, and when nothing matches, `pcm.trait` keeps
the unresolvable name while `pcm.experienceTrait` stays null. Every stock screen
built out of `CrewListItem` (Astronaut Complex, crew assignment) reads
`experienceTrait.Title` and NullRefs part-way through drawing the list — taking
out the rest of the list, the other tabs, and any chance of telling which kerbal
caused it.

Three things make the repair safe:

- The **scan** (`BrokenCrew`, formatted by `VesselTransfer.FindUnresolvableTraitCrew`)
  stays read-only, because the trait string is the *only* record of the profession.
- `TraitRepair.Repair` runs **only from a button**, and copies the original into
  `PluginData/trait_repairs.cfg` **before** overwriting it.
- `RestoreRecovered` hands the profession back by itself once the defining mod is
  installed again — which makes the repair a **loan**, not a deletion.

Records are keyed by save folder as well as kerbal name, and are dropped only
once the profession is safely back on the kerbal (or the player has moved that
kerbal on themselves). The import path (`RememberDowngrades`) and the repair path
deliberately share one record file, since they are the same loss.

Two tables must not be confused: `ContractConstraints.TraitMods` is a **fact**
(which mod owns a profession), while `TraitRepair.StockEquivalent` is a
**usability guess** (which stock job is closest) — the same separation
`PartAliases` draws between substitutions and `LookAlikes`. Deciding what to
*write* asks `CanDefine` (strict); deciding what is *broken* asks `IsDefined`
(lenient, so an unready `GameDatabase` never reads as a roster full of broken
kerbals).

The button is reachable two ways, via `LocalNotifActions`: as a button on the
local notification (rendered and dispatched by `NotificationsPanel` without
either front end knowing what the action does), and as a card in the sidebar's
Tools panel — above the link gate and only while something is broken, so the fix
survives dismissing the notification.

---

## Checkpoint & Milestone Detection

### Checkpoint Detector — `CheckpointDetector`

Ticked each frame during flight. Detects milestones worth photographing:

**Polled detectors** (throttled to every 2 seconds):
- **Rendezvous**: Another crewed/probe vessel within 2,200m
- **Asteroid**: A `SpaceObject` (asteroid/comet) within 2,200m. Distinguishes
  comets via the `ModuleComet` PartModule.
- **Flyby**: The vessel enters a new non-home body's sphere of influence (SOI
  tracking runs every frame to catch the transition).

**Event-driven detectors** (via `GameEvents`):
- **EVA**: `onCrewOnEva` — a kerbal steps outside
- **Staging**: `onStageActivate` — stage separation while in space (pad
  staging is skipped)
- **Orbit**: `onVesselSituationChange` → `ORBITING`
- **Landing**: `onVesselSituationChange` → `LANDED`
- **Splashdown**: `onVesselSituationChange` → `SPLASHED`

**Debouncing:**
- Global cooldown: 45s between any two prompts
- Per-key cooldown: 15min before re-offering the same subject (keyed by
  `"kind:target"`, e.g. `"flyby:Mun"`)
- `Suspended` flag paused by the host during capture

When a checkpoint fires, it invokes a callback with a `Checkpoint` struct
containing the kind, title, message, label, and optional target vessel/body.
The host (`GeneKermanMod`) shows a `CheckpointPrompt` and, if accepted, starts
the `CinematicCapture` coroutine.

---

## Identity & Security

### Consent Gate — `Consent`

KSP's add-on rules (8.1) require an unambiguous in-game opt-in before any
personally-identifiable information is gathered or sent. That consent is stored
in its own file, `PluginData/consent.cfg` (node `GeneKermanConsent`) — separate
from `settings.cfg` — so the agreement is an explicit, standalone record.

- **Nothing is transmitted until it is accepted.** `ApiClient.TransmissionBlocked`
  short-circuits every request, and the link/login menu is gated behind it.
- `Consent.cs` re-reads the file when it changes on disk (mtime-checked), so a
  manual edit takes effect live.
- The **required policy version is server-driven**: `/api/v1/version/check`
  returns `policy_version` (from `config/policy` in Firestore, set with the
  `/policyversion` admin command). When the server requires a newer version than
  the one recorded, the mod blocks transmission and forces re-consent — a
  fleet-wide re-consent with no mod rebuild.

The opt-in is drawn by `UI/ConsentWindow.cs` in IMGUI, deliberately: porting it
to the canvas would put the gate on the surface the gate exists to disable.

### Device Identity — `DeviceId`

A random GUID generated once and persisted to `PluginData/device.id`. It is:

- Sent on every API request as `X-Device-Id`
- Bound to the account at link time by the server
- Immune to MAC rotation (not derived from hardware)
- Not personal data (random, per-install)

The server blocks unrecognized device IDs until the user approves them from
Discord. `GetMacAddress()` and `GetKspLog()` are only used when the player
files a moderation report.

### Version Integrity — `ModVersion`

- **`Current`**: Human-readable version string (e.g. `"1.0.0"`)
- **`Sha256`**: SHA-256 hash of the running `GeneKerman.dll` on disk, computed
  once and cached. Sent to the server for version gating.
- **`AttestDigest(nonce, offset, length)`**: Challenge-response —
  `SHA256(UTF8(nonce) || dll[offset..offset+length])`. The server recomputes
  the same over the published DLL; a mismatch means the client's DLL has been
  modified.

### Service Suspensions

A suspension is a **timed block on the API surface** — the KSP client and the
website — issued from the owner console. It is deliberately *not* a Discord ban
(`cogs/moderation.py` owns those) and not a wipe: balance, XP, contracts and
listings are untouched and waiting. There is no permanent option.

Refusal is `403 {"code": "suspended", reason, until}`, structured like the device
gate so the client can *draw* it: `ApiClient` routes it to
`GeneKermanMod.OnSuspended` → `UI/SuspendedWindow.cs` — reason, live countdown,
a "check again" button, and the reassurance that nothing was deleted. The
sidebar stops rendering behind it.

Sessions are deliberately **not** revoked. A revoked token would drop the mod to
its link screen, whose only offer — link again — would work and change nothing;
a live token means every request comes back carrying the explanation.

Expiry is resolved on read on both sides: the server checks `until > now` with
no sweeper, and the mod's `Update` frees itself on the clock and simply earns a
fresh 403 if it was wrong.

Unlike the update gate, `SuspendedWindow` has **no limited mode** behind it —
nothing here is fixable from the client side.

### Part Catalog Upload — `PartCatalogUploader`

Uploads the player's installed part list (`internal_name + display_title` for
every loaded part) to the server so the bot can resolve fuzzy part mentions in
mission constraints (e.g. "the Thud engine" → `liquidEngine2-2`).

- Runs at most once per session
- Hash-gated (FNV-1a of the sorted part list) — skips the upload if the catalog
  hasn't changed since the last upload

---

## Third-Party Mod Compatibility

All integrations are **reflection-based** with no compile-time dependency. If
the target mod isn't installed, every call is a safe no-op. The only hard
requirement is ModuleManager, which applies the `GeneKermanScale` patch.

| Mod | What we do about it | Where |
|-----|---------------------|-------|
| **ModuleManager** | *Required.* Patches `GeneKermanScale` onto every part prefab | `Patches/GeneKermanScale.cfg` |
| **TweakScale** (+ forks) | Bake absolute values on send so the craft needs no TweakScale on arrival; warn on a genuine version mismatch | `ScaleBridge`, `GeneKermanScale`, `TweakScaleGuard` |
| **KSP-Recall** | Its surface-attach `pos` encoding is why exports re-anchor `pos` from the live transform | `ScaleBridge` |
| **Textures Unlimited** | Carry the paint job's manifest; reconcile recolour modules per prefab on import | `TextureTransfer` |
| **RealFuels / Realism Overhaul** | Carry tank types and engine configs; reconcile to local fuels for a recipient without RF | `RealFuelsTransfer` |
| **ReStock / ReStock+** | Substitute equivalent parts in both directions (ReStock+ hides its stand-ins when the DLC is present) | `PartAliases` |
| **ConformalDecals** | Re-issue the decal draw on the isolation layer so decals appear in blueprints; render never-drawn text decals | `DecalCapture` |
| **Deferred** (blackrack) | Render the capture camera on the deferred path, with a supersample for MSAA and narrow-FOV stand-ins for ortho | `VesselRenderer` |
| **USI-LS / TAC-LS / Snacks / Kerbalism / DeepFreeze** | Endurance scan, freeze/thaw handshake, ration kit | `LifeSupport/` |
| **Physics Range Extender** | Temporarily disabled during a submission, and restored only if *we* disabled it | `PhysicsRangeManager` |
| **Click Through Blocker** | IMGUI windows drawn through CTB's `GUILayoutWindow` so clicks don't reach the game behind them | `ClickThroughHelper` |
| **CKAN** | `registry.json` read (indexed by **install path**, not folder) to map parts → mod identifiers | `CkanGenerator` |
| **Making History / Breaking Ground** | Treated as **dependencies, not stock** — keyed by two-segment path (`SquadExpansion/MakingHistory`), reported when missing but never written into a `.ckan` (CKAN can detect a DLC and never install one) | `CkanGenerator` |

### Physics Range Extender — `PhysicsRangeManager`

**Problem:** PRE inflates the physics bubble so many distant vessels stay
loaded. During a multi-vessel submission, this causes unstable spam-loading.

**Solution:** Before capture, `TryDisable()` probes for PRE's static enable
toggle (searching member names: `ModEnabled`, `Enabled`, `Active`,
`IsEnabled`, `enabled`), turns it off, resets all loaded vessels' ranges to
stock defaults, captures, then `Reenable()` restores PRE — but **only** if the
mod was the one that disabled it.

### Click Through Blocker — `ClickThroughHelper`

When installed, IMGUI windows are drawn through CTB's `GUILayoutWindow` instead
of the stock `GUILayout.Window`, preventing clicks on the mod's UI from also
reaching the game underneath. Resolved via reflection on
`ClickThroughFix.ClickThruBlocker.GUILayoutWindow`. The uGUI sidebar does not
need this — it holds an `InputLockManager` lock instead.

### A note on mod detection

Every part-walk detection path in this codebase resolves a part to its GameData
folder via `AvailablePart.partUrl`. That means it is **structurally blind** to
any mod that adds no parts — TweakScale, Textures Unlimited, RealFuels — which
is exactly why each of those has a side-channel block of its own.

And a GameData folder is **not** a mod. DeepFreeze installs
`REPOSoftTech/DeepFreeze` next to its companion `REPOSoftTech/BackgroundResources`,
and the `-Core` split many mods ship (`Firespitter`/`FirespitterCore`,
`NearFutureElectrical`/`NearFutureElectrical-Core`) puts a parts mod and a
plugin-only one in one folder. So CKAN's registry is indexed by **install path**
and a part resolves through the longest path prefix exactly one module owns; a
prefix two modules share is left out of the index so the walk drops past it
rather than guessing.

---

## UI System

The mod draws in **two** toolkits, and which one a screen uses is a decision, not
an accident.

### The uGUI sidebar (`UI/Gui/`) — the primary interface

A retained-mode Canvas UI in the `GeneKerman.UI.Gui` namespace. The toolbar
button opens it; the classic IMGUI main window it replaced is **gone**, and so
is `UI/CreateContractWindow.cs` (superseded outright by `ContractForm`, which
adds auctions, orbit/lat-lon/Δv margins, restriction modes and a permanence
gate the old window never had).

| Piece | Role |
|-------|------|
| `Theme.cs` | Design tokens, ported from the website's `globals.css`. The mod's only shared palette — the IMGUI files still use inline colors |
| `Sprites.cs` | Procedural 9-slice rounded-rect sprites (there is no Unity Editor on this machine) |
| `UIF.cs` | The fluent widget builder |
| `SidebarController.cs` | Owns the Canvas, the expand animation, the input locks |
| `FloatWindow.cs` | Draggable, clamped window shell |
| `ImageViewer.cs` | Full-screen zoom/pan lightbox for submission images (borrows textures, never owns them) |
| `PlayerPicker.cs` / `BodyPicker.cs` / `DatePicker.cs` | Shared pickers — players (quicksend + contracts), celestial bodies (rescues), an inline month grid |
| `Panels/` | The screens: missions, contract inbox, profile, feed, market (selling half), tools, settings |

**The panel is centred** and opens by expanding sideways out of the middle of the
screen. There is no pull-out tab and no near edge, so there is no VAB/SPH edge
mirroring to do. `AnimateExpand` drives `openAmount` and `ApplyExpand` is the
single writer of the transform: it scales `panelRect.localScale.x` through
`EaseOutExpo` from a centre pivot rather than animating the width, because a
per-frame width change re-runs the whole layout — and a layout squeezed near zero
makes every `Ellipsis` label render nothing, so the panel would flash empty on
the way out. The resting width still animates (400 ↔ 880 for master-detail),
because that one genuinely *is* a change of layout size.

**Five rules that are load-bearing:**

1. A Canvas renders independently of `OnGUI`, so it needs explicit
   `GameEvents.onHideUI` handling or it appears in every screenshot.
2. It needs an `InputLockManager` lock, or clicks reach the game behind it.
   `PointerOverSidebar` returns false outright while closed — a scaled-to-nothing
   panel still has a rect, and would otherwise hold a lock over a sliver of
   screen showing nothing.
3. A focused text box needs a second, wider lock (`KEYBOARDINPUT` plus the
   quick-save pair) held screen-wide, since a keystroke has no cursor position.
4. Every lock must be released in `Destroy()` — a leaked control lock outlives
   the mod.
5. **The TMP font is borrowed from KSP**, and it fails two ways that both look
   like "the sidebar has no text at all":
   - A scene load can unload the font's atlas while leaving the asset non-null.
     `onGameSceneLoadRequested` fires *before* the teardown and so cannot detect
     it — `SidebarController.UpdateAssets` polls for it and `UIF.RefreshFont()`
     re-points the live labels (`Theme.Alive` tests the material, not the
     reference).
   - A `TMP_FontAsset` ships **one** default material that every label not asking
     for its own *shares* — colour mask, stencil, z-test and clip rect all live
     on it, so anything in the game (flight is where it happens) can make every
     label invisible. `Theme.FontMaterial` is a private normalized clone taken in
     the main menu, `UIF.Label` assigns it, and `UIF.RefreshText()` re-asserts it
     **and rebuilds meshes synchronously** — `SetAllDirty` only *queues*, and a
     queued-but-unserviced label draws nothing, silently.

**Limited mode.** The acknowledged-update gate used to belong to the classic
window. The canvas now renders while `UpdateRequired && UpdateAcknowledged` and
narrows itself to the panels declaring `WorksOffline` (Tools, Settings) behind a
banner carrying Re-check / Download latest. Tools drops its two server-backed
cards there (quicksend, bug report) and keeps export, flag import and roster
repair, which are local. The empty `else if (UpdateRequired)` arm in
`GeneKermanMod.OnGUI` **must stay**: what it still does is stop the block below
it drawing the link window and the device prompt under the gate.

### What is still IMGUI

Exactly the set that draws when the canvas may not — porting any of them would
put a gate on the surface the gate exists to disable.

| Window | Purpose |
|--------|---------|
| `ConsentWindow` | First-run privacy/terms opt-in (KSP add-on rule 8.1) |
| `UpdateRequiredWindow` | Mandatory update gate |
| `SuspendedWindow` | Service-suspension notice — reason, countdown, "check again" |
| `DataPausedWindow` | Data-sharing paused notice |
| `DeviceVerifyWindow` | Device binding approval |
| `LinkWindow` | Discord account linking (the toolbar opens the sidebar only once linked) |
| `CheckpointPrompt` | "Capture this moment?" — time-critical, drawn over the game |
| `WebUiWindow` | Recovery path for a browser that never opened |

`GKSkin` defines the custom `GUISkin` these use, and all of them are drawn
through `ClickThroughHelper.Window()`.

### Where the notable screens live

- **Submission** — `Panels/SubmitPanel.cs` in a `FloatWindow`, because submitting
  is read *against* the scene behind it. Living on the canvas gives it the render
  gate (F2 and every capture hide the whole canvas, so submission no longer needs
  a window of its own to be hidden for a screenshot), the input lock, the font
  recovery and the teardown for free.
- **Contract inbox** — `Panels/ContractsPanel.cs`, carrying the classic window's
  mail furniture: week groups, a local bin and multi-select (`ContractInbox.cs`
  holds both, shared so trashing in one front end hides it in the other),
  mark-read/dismiss routed through `ClientState`'s `Request*` wrappers so the
  unread badge cannot drift from the list, and — in the editor, on an active
  contract — the contract's part limits plus the switch that arms
  `EditorPartEnforcer`.
- **Rescues** — issuing one is in `ContractForm`, behind an explicit "this is
  permanent" switch (sending destroys the issuer's vessel); spawning the wreck is
  in `ContractsPanel`'s active-contract actions. Both call `ContractCreation` /
  `RequestSpawnRescueWreck` rather than reimplementing, since the per-save dedup,
  the orbit-epoch freeze and the emergency-freeze registration hang off those.
- **Marketplace** — `Panels/MarketPanel.cs` is the **selling** half only.
  Browsing and buying stay on the website; what a browser cannot do is read the
  ship in the VAB and render it.
- **Bug reports** — filed from the Tools panel. Its action sits in `ToolActions`
  with the others but, alone among them, is not exposed on the `/gk/actions/*`
  bridge and has no card in the web screen: the attachment that makes a KSP bug
  diagnosable (`KSP.log`) can only be read from inside the running game.

---

## Browser UI Bridge — `Web/`

An **optional** second front end: the same account, drawn as a React page in the
player's own browser. Off by default (`enableWebUi = false`), switched on in the
sidebar's Settings panel. An existing install is never moved to a different UI
by a mod update.

**Why a local server at all.** KSP runs Unity 2019.4, which has no webview, and
shipping an embedded browser would mean 50–300 MB of per-platform native
binaries against a mod that is under a megabyte. So the UI runs in the player's
browser, and this server is what makes that safe and same-origin:

```
GET  /            → the built React bundle in GameData/BoundlessMissions/WebUI/
*    /api/v1/...  → proxied upstream with the session token attached in C#
*    /gk/...      → game state and actions only this process can perform
GET  /gk/events   → SSE push, tee'd from the notification socket
```

Because the page, its data and its game bridge share one origin, there is no
CORS, no mixed content, and **the session token never enters JavaScript**.

**The five-layer gate.** The bridge holds a 30-day session token and will attach
it to anything it proxies, so "only our page may call this" has to be airtight.
No layer is sufficient alone:

1. Bound to `127.0.0.1` only — nothing on the LAN can reach it.
2. Random ephemeral port per session — **defence in depth only.** A local
   process can scan 65k ports in under a second; this is never treated as
   security.
3. A one-time launch nonce, 15 s TTL, in the URL handed to the browser.
4. An `HttpOnly; SameSite=Strict` session cookie plus a CSRF token in a custom
   header.
5. Exact `Host` match (DNS-rebinding defence) plus `Origin` and `Sec-Fetch-Site`.

Layers 1, 2 and 5 live in `LocalServer`; `BridgeAuth` owns 3 and 4.

> **Known residual risk, documented rather than pretended away:**
> `Application.OpenURL` shells out to `xdg-open` on Linux, so the launch URL —
> nonce included — is briefly visible in `/proc` and `ps aux`. The 15 s TTL plus
> single use plus the browser consuming it in ~200 ms makes the window narrow,
> and a hostile local user on the same account could already just read
> `PluginData/session.token`.

**`ApiProxy` is a confused deputy by construction**, so its **allow-list is the
security boundary** — an endpoint not on the list cannot be reached with the
mod's token, whatever the page asks for.

**Threading.** The accept loop and every handler run off the main thread.
Anything that touches KSP goes through `MainThreadQueue`, drained by
`GeneKermanMod.Update()`. Long-running operations get a `JobRegistry` entry so a
page that reloads mid-install still learns the outcome.

Because the bridge binds a fresh ephemeral port every session,
`http://127.0.0.1:<port>` is a **different origin on every launch** — so
`localStorage` is empty each time and anything that must persist (starred
players, the inbox bin) lives in `PluginData` instead. See `Favorites.cs` and
`ContractInbox.cs`.

If the browser never opens, `UI/WebUiWindow.cs` is the recovery path: it shows
the origin so the player can open it by hand.

---

## Build & Deployment

### `build.sh`

```bash
cd "KSP Mod Side"
./build.sh
```

```
# 1. Build with dotnet (Release config, .NET 4.7.2 target)
#    dotnet build -c Release -p:GKChannel="$CHANNEL"
#    CHANNEL=production strips the debug self-test panel (GK_DEBUG_PANEL)

# 2. Print the DLL's SHA-256 — register it with the update gate via
#    /admin publishversion, or the admin console's Mod Version tab

# 3. Build the browser UI (repo-root WebUI/, via Vite → GameData/…/WebUI/)
#    Skipped, not fatal, when node_modules is absent — but then WebUI/ is
#    whatever was last built, which is what the manifest check below catches.

# 4. Prepare GameData/BoundlessMissions/
#    - Copy GeneKerman.dll → Plugins/
#    - Copy websocket-sharp.dll → Plugins/
#    - Copy toolbar icon → Textures/
#    - Copy Iconpack-1 UI icons → Textures/
#    - Create default settings.cfg if absent

# 5. Deploy to every KSP install in KSP_PATHS[]
#    - Preserve each install's existing settings.cfg (backup → copy → restore)
#    - Copy the whole GameData/BoundlessMissions/ into each KSP's GameData/
#      (WebUI/ is removed first — its assets are content-hashed, so a plain cp
#       would leave every previous build's files behind)
```

The Vite build stamps `WebUI/manifest.json` with `ModVersion.Current`, and the
mod **refuses to start the bridge** if that does not match the running DLL — a
stale bundle talking to a newer `/gk` surface is exactly the failure this
prevents.

**Deploy targets** (`KSP_PATHS` in `build.sh`) — three dev instances, each
testing a different thing:

| Instance | What it is for |
|----------|----------------|
| `KR-KSP` | The rendering-stack testbed — SSPX, TAC-LS, Kerbalism, kOS, TUFX, ReforgedRedux, KerbalEngineer, and **Deferred + TexturesUnlimited**. Deferred here is what `VesselRenderer`'s deferred path exists for |
| `RSS-RO` | Realism Overhaul / Real Solar System — RealFuels, FAR, Kopernicus+RSS. Tests `RealFuelsTransfer` against RO's part and resource rewrites |

To build without deploying:

```bash
cd "KSP Mod Side/GeneKerman"
dotnet build -c Release
```

Release packaging (the CKAN-shaped zip in `dist/`) is covered in
[`PACKAGING.md`](PACKAGING.md); the day-to-day toolchain notes live in
`DEV-SETUP.md` at the repo root.

### Project Configuration (`GeneKerman.csproj`)

- **Target**: .NET Framework 4.7.2 (KSP's runtime)
- **Output**: `bin/GeneKerman.dll` (class library, no entry point)
- **Assembly paths**: `KSPPath` defaults to the `KR-KSP` dev instance (`FK-KSP` was the reference until it was deleted on 2026-08-22). `ManagedPath` is probed, not assumed — `KSP_x64_Data/Managed` on KR-KSP/KR2-KSP, `KSP_Data/Managed` on RSS-RO — with a fallback to any sibling instance that has assemblies and one explicit error if none do;
  `ManagedPath` is `$(KSPPath)/KSP_Data/Managed` (**not** `KSP_x64_Data/Managed`
  on these installs). Override with `-p:KSPPath="..."` to compile against
  another install.
- **References**:
  - `Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll` (KSP's game API)
  - `UnityEngine.dll` plus the modules actually used: `CoreModule`,
    `IMGUIModule`, `UIModule`, `UnityEngine.UI`, `UnityWebRequestModule`,
    `ImageConversionModule`, `ScreenCaptureModule`, `TextRenderingModule`,
    `InputLegacyModule`, `AnimationModule`, `PhysicsModule`
  - `websocket-sharp.dll` (bundled in `lib/`, the only `Private=true` reference)
  - `ToolbarControl` and `ClickThroughBlocker` — referenced **conditionally**,
    only if present in the target install's GameData
- **Conditional symbols**: `GK_DEBUG_PANEL` is defined only off the `production`
  channel, so a shipped DLL contains none of `DebugTestPanel.cs`.

---

## Dependencies

| Dependency | Source | Purpose |
|------------|--------|---------|
| `Assembly-CSharp.dll` | KSP install | KSP game API (vessels, parts, contracts, UI, …) |
| `UnityEngine*.dll` | KSP install | Unity engine (rendering, coroutines, IMGUI, uGUI, textures) |
| `websocket-sharp.dll` | Bundled (`lib/`) | WebSocket client (KSP's Mono lacks `ClientWebSocket`) |
| **ModuleManager** | Required (separate install) | Patches `GeneKermanScale` onto every part prefab |

**Soft dependencies** — all optional, all reflection-based, all no-ops when
absent. See [Third-Party Mod Compatibility](#third-party-mod-compatibility) for
what each one actually does:

Click Through Blocker · ToolbarControl · TweakScale (+ forks) · KSP-Recall ·
Textures Unlimited · RealFuels / Realism Overhaul · ReStock / ReStock+ ·
ConformalDecals · Deferred · USI-LS · TAC-LS · Snacks · Kerbalism · DeepFreeze ·
Physics Range Extender · CKAN

The stock expansions (Making History, Breaking Ground) are treated as
dependencies rather than as stock — they are bought separately, and owning one
says nothing about the other.

---

## Configuration

`GameData/BoundlessMissions/PluginData/settings.cfg` (KSP `ConfigNode` format;
node name is `GeneKerman`). Everything here is editable from the sidebar's
Settings panel — hand-editing is supported but not required.

```
GeneKerman
{
    useOfficialServer = true
    serverProtocol = http
    serverHost = localhost
    serverPort = 5022
    marketplaceProtocol = https
    marketplaceAddress = boundlessmissions.com
    enableNotifications = true
    enableCheckpointPhotos = true
    enableDataGathering = true
    enableWebUi = false
    enableEmergencyFreeze = true
    emergencyRationDays = 3
    enablePartSubstitution = true
    enableTextureTransfer = true
    enableFuelConfigTransfer = true
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `useOfficialServer` | `true` | Use the official backend. When true the custom host/port below are kept but ignored, so toggling back restores them |
| `serverProtocol` / `serverHost` / `serverPort` | `http` / `localhost` / `5022` | The custom backend, stored in **three fields**: `//` is a comment delimiter in `ConfigNode` and would truncate a URL |
| `marketplaceProtocol` / `marketplaceAddress` | `https` / the official site | The website the Market panel opens. Split for the same reason. The old single `marketplaceUrl` key is migrated on load and never written back |
| `enableNotifications` | `true` | Notification push and polling |
| `enableCheckpointPhotos` | `true` | Offer the cinematic capture prompt at detected milestones |
| `enableDataGathering` | `true` | Send telemetry with submissions. Off shows the "data sharing paused" notice |
| `enableWebUi` | `false` | Open the browser UI instead of the sidebar. Absent means classic — an existing install is never moved to a different UI by an update |
| `enableEmergencyFreeze` | `true` | Freeze stranded rescue crew so they survive any life-support mod |
| `emergencyRationDays` | `3` | Days of the rescuer's resources stowed aboard a rescue wreck. Clamped at 0 — a hand-edited negative would size the kit backwards |
| `enablePartSubstitution` | `true` | Swap missing parts for local equivalents. Off still scans and reports |
| `enableTextureTransfer` | `true` | Carry Textures Unlimited paint jobs. Off still scans and warns |
| `enableFuelConfigTransfer` | `true` | Carry RealFuels tank/engine configs. Off still scans and warns |

Consent is deliberately **not** in this file — it lives in
`PluginData/consent.cfg` as a standalone record. See
[Consent Gate](#consent-gate--consent).

**Runtime data** (created automatically):

| Path | Contents |
|------|----------|
| `PluginData/settings.cfg` | The above |
| `PluginData/consent.cfg` | Accepted policy version and timestamp |
| `PluginData/device.id` | Stable per-install device identifier |
| `PluginData/session.token` | Current signed session token |
| `PluginData/sessions.cfg` | Known sessions |
| `PluginData/catalog.hash` | FNV-1a hash of the last-uploaded part catalog |
| `PluginData/favorites.cfg` | Starred players for the quicksend picker |
| `PluginData/trashed_contracts.txt` | The inbox's local bin (never sent to the server) |
| `PluginData/trait_repairs.cfg` | Original professions loaned out by `TraitRepair` |
| `PluginData/renders/` | Saved blueprint PNGs |
| `PluginData/screenshots/` | Saved screenshots |
| `GameData/GeneKerman/Flags/` | Content-addressed flag images that arrived with crafts |
| `<KSP>/GeneKerman_MissingMods/*.ckan` | Generated CKAN metapackages |
