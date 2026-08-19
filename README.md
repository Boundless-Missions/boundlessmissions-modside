# Boundless Missions — KSP Mod Side

> A KSP 1.12.x plugin that connects a player's game to the Boundless Missions
> backend, enabling AI-driven mission contracts, vessel/craft transfers between
> players, real-time notifications, cinematic milestone captures, and automated
> mod-dependency management — all from inside the stock game.

---

## Table of Contents

1. [High-Level Architecture](#high-level-architecture)
2. [Directory Layout](#directory-layout)
3. [Source File Map](#source-file-map)
4. [Core Systems](#core-systems)
   - [Lifecycle & State Machine — `GeneKermanMod`](#lifecycle--state-machine--genekermanmod)
   - [Networking — `ApiClient`](#networking--apiclient)
   - [Real-Time Push — `NotificationSocket`](#real-time-push--notificationsocket)
5. [Vessel & Craft Transfer Pipeline](#vessel--craft-transfer-pipeline)
   - [Serialization — `VesselTransfer`](#serialization--vesseltransfer)
   - [Data Collection — `VesselDataCollector`](#data-collection--vesseldatacollector)
   - [Craft Installation — `CraftInstaller`](#craft-installation--craftinstaller)
6. [Side-Channel Data Blocks](#side-channel-data-blocks)
   - [Custom Flags — `FlagTransfer`](#custom-flags--flagtransfer)
   - [TweakScale Bridge — `ScaleBridge` / `GeneKermanScale` / `TweakScaleGuard`](#tweakscale-bridge--scalebridge--genekermanscale--tweakscaleguard)
   - [CKAN Mod Dependency — `CkanGenerator`](#ckan-mod-dependency--ckangenerator)
   - [Craft Thumbnails — `CraftThumb`](#craft-thumbnails--craftthumb)
7. [Visual Rendering](#visual-rendering)
   - [Blueprint Renderer — `VesselRenderer`](#blueprint-renderer--vesselrenderer)
   - [Cinematic Capture — `CinematicCapture`](#cinematic-capture--cinematiccapture)
8. [Mission Contract System](#mission-contract-system)
   - [Contract Integration — `ContractIntegration`](#contract-integration--contractintegration)
   - [Contract Constraints — `ContractConstraints` / `PartClassifier`](#contract-constraints--contractconstraints--partclassifier)
   - [Editor Enforcement — `EditorPartEnforcer`](#editor-enforcement--editorpartenforcer)
   - [Delta-V Validation — `CraftDeltaV`](#delta-v-validation--craftdeltav)
9. [Checkpoint & Milestone Detection](#checkpoint--milestone-detection)
   - [Checkpoint Detector — `CheckpointDetector`](#checkpoint-detector--checkpointdetector)
10. [Identity & Security](#identity--security)
    - [Device Identity — `DeviceId`](#device-identity--deviceid)
    - [Version Integrity — `ModVersion`](#version-integrity--modversion)
    - [Part Catalog Upload — `PartCatalogUploader`](#part-catalog-upload--partcataloguploader)
11. [Third-Party Mod Compatibility](#third-party-mod-compatibility)
    - [Physics Range Extender — `PhysicsRangeManager`](#physics-range-extender--physicsrangemanager)
    - [Click Through Blocker — `ClickThroughHelper`](#click-through-blocker--clickthroughhelper)
    - [KVV Detection — `KVVIntegration`](#kvv-detection--kvvintegration)
12. [UI System](#ui-system)
13. [Build & Deployment](#build--deployment)
14. [Dependencies](#dependencies)
15. [Configuration](#configuration)

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  KSP Game Process (Unity / Mono / .NET 4.7.2)                   │
│                                                                 │
│  ┌─────────────────────┐    ┌────────────────────┐              │
│  │  GeneKermanMod      │◄──►│  ApiClient         │──► HTTP/S    │
│  │  (MonoBehaviour     │    │  (UnityWebRequest) │    REST API  │
│  │   Singleton)        │    └────────────────────┘              │
│  │                     │    ┌────────────────────┐              │
│  │  • UI Windows       │◄──►│  NotificationSocket│──► WebSocket │
│  │  • Lifecycle mgmt   │    │  (websocket-sharp) │              │
│  │  • Coroutine host   │    └────────────────────┘              │
│  └──────────┬──────────┘                                        │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Transfer Pipeline                                       │   │
│  │  VesselTransfer ─► FlagTransfer ─► ScaleBridge           │   │
│  │       ─► CkanGenerator ─► CraftThumb ─► CraftInstaller   │   │
│  └──────────────────────────────────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Visual                                                  │   │
│  │  VesselRenderer (orthographic blueprints, layer 30)      │   │
│  │  CinematicCapture (in-game hero shots, flight camera)    │   │
│  └──────────────────────────────────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Contract System                                         │   │
│  │  ContractIntegration ◄─► ContractConstraints             │   │
│  │  EditorPartEnforcer ◄─► PartClassifier ◄─► CraftDeltaV   │   │
│  └──────────────────────────────────────────────────────────┘   │
│             │                                                   │
│  ┌──────────▼───────────────────────────────────────────────┐   │
│  │  Milestone Detection                                     │   │
│  │  CheckpointDetector (proximity/SOI/event scanning)       │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

The mod is a single Unity `MonoBehaviour` (`GeneKermanMod`) that attaches to a
persistent `GameObject`. It communicates with a remote server over HTTP REST
(via `ApiClient`) and WebSocket (via `NotificationSocket`). All networking uses
Unity's `UnityWebRequest` for HTTP and the external `websocket-sharp` library
for WebSocket.

---

## Directory Layout

```
KSP Mod Side/
├── build.sh                          # Build + deploy script
├── logo.png / logo_38.png            # Toolbar icons
├── Iconpack-1/                       # UI icon assets
├── LICENSE
├── GeneKerman/                       # C# source (the plugin project)
│   ├── GeneKerman.csproj             # .NET 4.7.2 project file
│   ├── lib/                          # Pre-built dependency (websocket-sharp.dll)
│   ├── UI/                           # IMGUI window classes
│   │   ├── Gui/                      # The uGUI sidebar: the interface (panels, windows)
│   │   ├── Gui/Panels/SubmitPanel.cs # Mission submission window (uGUI, draggable)
│   │   ├── LinkWindow.cs             # Discord account linking
│   │   ├── GKSkin.cs                 # Custom IMGUI skin/style
│   │   ├── CheckpointPrompt.cs       # Milestone capture prompt
│   │   ├── NotificationPopup.cs      # Toast notification overlay
│   │   ├── DeviceVerifyWindow.cs     # Device binding approval
│   │   └── UpdateRequiredWindow.cs   # Version gate dialog
│   ├── *.cs                          # Core systems (see Source File Map)
│   ├── bin/                          # Build output (GeneKerman.dll)
│   └── obj/                          # Intermediate build files
│
└── GameData/BoundlessMissions/       # Deployable mod folder (copied into KSP)
    ├── GeneKerman.version            # AVC version file (KSP 1.12.x)
    ├── Patches/
    │   └── GeneKermanScale.cfg       # ModuleManager patch: @PART[*]:FINAL
    ├── Plugins/                      # GeneKerman.dll + websocket-sharp.dll
    ├── PluginData/                   # Runtime data (settings.cfg, device.id, renders)
    └── Textures/                     # Toolbar icons, UI icons
```

---

## Source File Map

| File | Lines | Role |
|------|------:|------|
| `GeneKermanMod.cs` | 785 | **Entry point.** MonoBehaviour singleton, lifecycle, state machine, toolbar, coroutine host, notification polling |
| `ApiClient.cs` | 1,151 | **HTTP networking.** All REST calls, auth token management, version gating (426), device binding (403) |
| `NotificationSocket.cs` | 281 | **WebSocket push.** Real-time notifications, keepalive, exponential backoff reconnect |
| `VesselTransfer.cs` | ~1,100 | **Vessel serialization.** Export/import live vessels via `ConfigNode`, crew embedding, multi-vessel fleet transfers |
| `VesselRenderer.cs` | ~900 | **Blueprint rendering.** Orthographic vessel renders, dual-pass alpha, layer 30 isolation, NW thumbnails |
| `VesselDataCollector.cs` | 337 | **Telemetry.** Captures vessel snapshots (orbit, mass, cost, crew), reads craft files, screenshots |
| `FlagTransfer.cs` | 507 | **Flag carriage.** Embed/extract custom flag images, content-addressed SHA-256 naming, runtime `GameDatabase` injection |
| `ScaleBridge.cs` | 445 | **TweakScale snapshot.** Captures live rescaled values on sender, neutralizes TweakScale on receiver |
| `GeneKermanScale.cs` | 303 | **Scale applicator.** PartModule that re-applies absolute scale values without TweakScale's exponent math |
| `TweakScaleGuard.cs` | 194 | **TweakScale version warning.** Embeds sender's TS version, warns recipient on mismatch |
| `ScaleEditorReapply.cs` | 69 | **Editor undo/redo fix.** Re-asserts scaled geometry after editor undo/redo rebuilds |
| `CkanGenerator.cs` | 617 | **Mod dependency management.** Maps parts to mods, reads CKAN registry, generates metapackage `.ckan` files |
| `CraftInstaller.cs` | 191 | **Craft file writer.** Strips side-channel blocks, places craft in correct `Ships/VAB|SPH` directory |
| `CraftThumb.cs` | 161 | **Thumbnail carriage.** Embeds/extracts NW-view thumbnail PNGs for the KSP craft browser |
| `CraftDeltaV.cs` | 56 | **Delta-V reader.** Reads stock VesselDeltaV for mission limit validation |
| `ContractIntegration.cs` | 314 | **Stock contract bridge.** Injects API missions as stock contracts in Mission Control |
| `ContractConstraints.cs` | 260 | **Mission limits.** Parses forbidden/required parts, propellants, categories, Δv and crew-count limits |
| `PartClassifier.cs` | 190 | **Part analysis.** Derives propellant types, engine categories, part categories from live modules |
| `EditorPartEnforcer.cs` | 137 | **Editor filter.** Hides forbidden parts in VAB/SPH part list during active contract |
| `CheckpointDetector.cs` | 348 | **Milestone detection.** Detects rendezvous, flyby, asteroid, EVA, staging, orbit, landing |
| `CinematicCapture.cs` | 284 | **Hero shots.** Sunlit camera pose computation, HUD toggle, ScaledSpace sync |
| `DeviceId.cs` | 117 | **Device identity.** Stable per-install GUID, MAC address for reports, KSP.log reader |
| `ModVersion.cs` | 94 | **Version integrity.** DLL SHA-256 hash, challenge-response attestation |
| `PartCatalogUploader.cs` | 93 | **Part catalog sync.** Uploads installed part list to server, FNV-1a hash gate |
| `PhysicsRangeManager.cs` | 217 | **PRE integration.** Temporarily disables Physics Range Extender during submissions |
| `ClickThroughHelper.cs` | 115 | **CTB integration.** Routes windows through Click Through Blocker when available |
| `KVVIntegration.cs` | 62 | **KVV detection.** Checks for Kronal Vessel Viewer (informational only; built-in renderer used) |
| `MiniJSON.cs` | ~300 | **JSON library.** Lightweight serializer/deserializer, no external dependencies |

**UI Directory (`UI/`):**

| File | Lines | Role |
|------|------:|------|
| `ClientState.cs` | ~890 | The account: profile, missions, contracts and the notification feed — fetch, cache, de-dup, unread count and every action coroutine. Headless; the sidebar's panels, the browser bridge and the notification socket all read this one copy |
| `UI/Gui/Panels/SubmitPanel.cs` + `SubmissionSession.cs` | ~370 + ~1,290 | Mission submission — vessel selection, constraint validation, multi-vessel fleet. The screen is a draggable uGUI window (`UI/Gui/FloatWindow.cs`); every rule and the upload itself live in the session beside it |
| `UI/Gui/ContractForm.cs` | ~600 | Contract creation — description, rewards, constraints, mod-list mode, auctions and rescues |
| `LinkWindow.cs` | ~260 | Discord account linking flow |
| `GKSkin.cs` | ~200 | Custom IMGUI `GUISkin` with styled buttons, labels, scrollviews |
| `CheckpointPrompt.cs` | ~100 | Milestone capture yes/no prompt |
| `NotificationPopup.cs` | ~110 | Toast-style notification overlay (clickable, auto-dismiss) |
| `DeviceVerifyWindow.cs` | ~70 | Device binding approval dialog |
| `UpdateRequiredWindow.cs` | ~90 | Mandatory update gate dialog |

---

## Core Systems

### Lifecycle & State Machine — `GeneKermanMod`

`GeneKermanMod` is the mod's entry point. It is a Unity `MonoBehaviour` marked
with `[KSPAddon(KSPAddon.Startup.MainMenu, true)]` and `DontDestroyOnLoad`,
making it a persistent singleton that survives scene changes.

**Responsibilities:**

- **Initialization**: Creates the `ApiClient`, `NotificationSocket`, and
  `CheckpointDetector` instances. Loads settings from
  `PluginData/settings.cfg`. Registers the KSP Application Launcher toolbar
  button.
- **Update loop** (`Update()`): Ticks the notification socket, drains queued
  WebSocket notifications, polls the API on a timer fallback (when the socket
  is down), ticks the checkpoint detector during flight.
- **Scene awareness**: Hooks `GameEvents.onGameSceneLoadRequested` to reset
  state, re-register event hooks, and adjust UI visibility across Space Center,
  Flight, Editor, and Tracking Station scenes.
- **Coroutine host**: Provides `RunCoroutine()` so non-MonoBehaviour classes
  (like `ApiClient`) can start Unity coroutines.
- **Notification handling**: Processes notification dicts from the socket/poll,
  dispatches them to the appropriate handler (contract update, vessel delivery,
  flag delivery, version poke, etc.), and surfaces them via
  `NotificationPopup`.
- **Settings management**: Persists `serverUrl`, `checkInterval`,
  `enableNotifications`, `enableKVV`, `enableContractInjection`, and
  `enableCheckpointCapture` to `settings.cfg`.

**Key state:**

```
Instance     : static singleton reference
ApiClient    : the networking client
Socket       : the WebSocket handler
Detector     : the checkpoint detector
PluginDataPath : GameData/BoundlessMissions/PluginData (runtime storage root)
```

### Networking — `ApiClient`

`ApiClient` encapsulates all HTTP communication with the server. Every API call
is a Unity coroutine using `UnityWebRequest`.

**Request flow:**

1. Every request includes headers: `Authorization: Bearer <token>`,
   `X-Device-Id: <deviceId>`, `X-Mod-Version: <version>`,
   `X-Mod-Hash: <sha256>`.
2. **Version gate (HTTP 426)**: If the server returns 426 Upgrade Required, the
   client shows the `UpdateRequiredWindow` and blocks all further API calls
   until the player updates. The `NotificationSocket` also triggers a re-check
   when it receives a `"version"` frame.
3. **Device binding (HTTP 403)**: If the server returns 403 with a
   `device_verify` body, the player's device hasn't been approved yet —
   `DeviceVerifyWindow` is shown with instructions to approve from Discord.
4. **Auth token**: Stored in `PluginData/auth_token`. Acquired during the
   Discord linking flow (`/api/v1/link`).

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
3. `ScaleBridge.SnapshotIntoVesselNode()` captures TweakScale-computed values.
4. `FlagTransfer.EmbedFlagsInNode()` embeds custom flag images.
5. `CkanGenerator.EmbedModsInNode()` embeds the mod dependency manifest.
6. The final `ConfigNode` is serialized to text for upload.

**Import (receiving a vessel):**

1. Text is parsed via `ConfigNode.Load()` using a **temporary file** on disk
   (because `ConfigNode.Parse()` is unreliable in KSP's Mono runtime).
2. `CkanGenerator.ExtractCheckAndStripMods()` strips and processes GKMODS.
3. `ScaleBridge.NeutralizeTweakScaleForImport()` removes TweakScale MODULE
   nodes from rescaled parts (so `GeneKermanScale` is the sole authority).
4. `FlagTransfer.ExtractAndInstallFlags()` installs flag images and strips
   GKFLAG nodes.
5. Crew data from GKCREW nodes is applied to the `ProtoCrewMember` roster.
6. The vessel is injected into the current game via
   `FlightGlobals.Vessels.Add()` / `ProtoVessel.Load()`.

**Export (sending a .craft file):**

1. `.craft` bytes are read from disk.
2. `ScaleBridge.SnapshotIntoCraftBytes()` snapshots TweakScale values and
   neutralizes TweakScale in one pass.
3. `FlagTransfer.EmbedFlagsInCraft()` appends GKFLAG blocks as raw text
   (never re-serializes the craft body via `ConfigNode.ToString()`, which would
   wrap it in a spurious `root {}` node that KSP's craft loader rejects).
4. `TweakScaleGuard.EmbedVersionInCraft()` appends a GKTSVER block.
5. `CkanGenerator.EmbedModsInCraft()` appends a GKMODS block.
6. `CraftThumb.EmbedThumbForCurrentCraft()` appends a GKTHUMB block.

**Import (receiving a .craft file):**

Handled by `CraftInstaller.Install()` in strict reverse-append order:

1. `CraftThumb.CheckAndStripFromCraft()` — strip GKTHUMB (appended last)
2. `CkanGenerator.CheckAndStripFromCraft()` — strip GKMODS
3. `TweakScaleGuard.CheckAndStripFromCraft()` — strip GKTSVER, warn on mismatch
4. `FlagTransfer.StripAndInstallFlagsFromCraft()` — strip GKFLAG, install flags
5. Parse craft header for type (`VAB`/`SPH`)
6. Write cleaned craft to `saves/<save>/Ships/<VAB|SPH>/`
7. Drop `.gkmods` sidecar for editor re-check
8. Install thumbnail to `thumbs/` folder

> **Critical invariant**: The `.craft` body is never round-tripped through
> `ConfigNode`. Side-channel blocks are appended as raw text at the end and
> stripped as raw text from the end. This preserves the byte-for-byte integrity
> of the craft file, which KSP's craft loader is very strict about.

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
craft to disk. It:

1. Decompresses gzip-compressed craft data (magic bytes `0x1F 0x8B`)
2. Runs the side-channel strip pipeline (GKTHUMB → GKMODS → GKTSVER → GKFLAG)
3. Parses the craft type header (`type = VAB|SPH`)
4. Writes to `saves/<save>/Ships/<type>/` with collision-avoidance numbering
5. Writes `.loadmeta` sidecar if provided
6. Calls `CkanGenerator.OnCraftInstalled()` for missing-mod detection
7. Calls `CraftThumb.InstallThumbnail()` for the craft browser

---

## Side-Channel Data Blocks

The mod carries auxiliary data alongside vessel/craft transfers using a system
of **side-channel blocks** — structured text nodes appended to or embedded
within KSP's `ConfigNode` serialization format.

For `.craft` files (raw text), blocks are **appended at the end** and
**stripped in reverse order** on import:

```
<original craft body>
GKFLAG          ← appended first
{ ... }
GKTSVER         ← appended second
{ ... }
GKMODS          ← appended third
{ ... }
GKTHUMB         ← appended last, stripped first
{ ... }
```

For vessel `ConfigNode`s, blocks are embedded as child nodes (`GKFLAG`,
`GKCREW`, `GKMODS`) and removed after extraction.

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

- **Export**: Appends a `GKTSVER { ver = <version> }` block to crafts that
  contain a TweakScale MODULE.
- **Import**: Compares the sender's version against the local install. Posts a
  screen warning if TweakScale is missing or the version differs.
- **Version detection**: Probes `AssemblyLoader.loadedAssemblies` for the
  `Scale` assembly (exact name match — avoids companions like
  `TweakScaleCompanion_*` or `Scale_Redist`). Falls back to whichever assembly
  defines `TweakScale.TweakScale`.

### CKAN Mod Dependency — `CkanGenerator`

**Problem:** A `.craft` stores only part *names*, never which mod each part
came from. A recipient who's missing a mod just sees "this craft has missing
parts" with no way to know what to install.

**Solution:** Map parts to mods at export time and generate a CKAN metapackage
on import.

**Export:**

1. For each non-stock part, determine its GameData folder from
   `AvailablePart.partUrl` (first path segment).
2. If CKAN is installed, read `CKAN/registry.json` to map folders to CKAN
   identifiers (cached for the session).
3. Include inventory items (stock `ModuleInventoryPart` STOREDPART nodes, KIS
   `ModuleKISInventory` ITEM nodes) — parts inside containers would otherwise
   be missed.
4. Embed as a `GKMODS` block with `MOD { folder, ckan, name }` entries.

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

`VesselRenderer` produces clean orthographic blueprint images of vessels — the
primary visual submitted with missions.

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

**Two enforcement modes:**

- **`IsForbidden(AvailablePart)`**: Per-part check, used by `EditorPartEnforcer`
  to hide forbidden parts in the VAB/SPH editor.
- **`CheckCraft(IEnumerable<Part>, deltaVVac)`**: Whole-craft validation at
  submit time. Returns a list of human-readable violation strings.

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
the target mod isn't installed, every call is a safe no-op.

### Physics Range Extender — `PhysicsRangeManager`

**Problem:** PRE inflates the physics bubble so many distant vessels stay
loaded. During a multi-vessel submission, this causes unstable spam-loading.

**Solution:** Before capture, `TryDisable()` probes for PRE's static enable
toggle (searching member names: `ModEnabled`, `Enabled`, `Active`,
`IsEnabled`, `enabled`), turns it off, resets all loaded vessels' ranges to
stock defaults, captures, then `Reenable()` restores PRE — but **only** if the
mod was the one that disabled it.

### Click Through Blocker — `ClickThroughHelper`

When installed, mod windows are drawn through CTB's `GUILayoutWindow` instead
of the stock `GUILayout.Window`, preventing clicks on the mod's UI from also
reaching the game underneath (placing parts in the editor, interacting with
the flight scene, etc.). Resolved via reflection on
`ClickThroughFix.ClickThruBlocker.GUILayoutWindow`.

### KVV Detection — `KVVIntegration`

Checks for Kronal Vessel Viewer (informational only). The mod always uses its
built-in `VesselRenderer` for vessel captures regardless of KVV's presence.

---

## UI System

The mod uses Unity's **IMGUI** (`OnGUI`) system — the only UI toolkit
universally available across all KSP scenes. All windows are drawn through
`ClickThroughHelper.Window()` (→ CTB when available, stock fallback otherwise).

**`GKSkin`** defines a custom `GUISkin` with styled buttons, labels, text
fields, and scroll views.

| Window | Scene(s) | Purpose |
|--------|----------|---------|
| `SidebarController` (uGUI) | All | Primary interface — missions, contract inbox, profile, feed, marketplace, tools, settings |
| `SubmitPanel` (draggable window) | Flight, Editor | Mission submission — vessel selection, blueprint render, constraint validation, multi-vessel fleet selection |
| `ContractForm` (sidebar) | All | Contract creation — description, rewards, difficulty, constraints, mod-list, auctions, rescues |
| `LinkWindow` | All | Discord account linking flow — enter link code, paste in Discord |
| `CheckpointPrompt` | Flight | "Capture this moment?" yes/no prompt for detected milestones |
| `NotificationPopup` | All | Toast-style overlay — clickable text, auto-dismiss timer |
| `DeviceVerifyWindow` | All | Device binding approval — shows when a new device needs Discord approval |
| `UpdateRequiredWindow` | All | Mandatory update gate — blocks all functionality until the player updates |

---

## Build & Deployment

### `build.sh`

```bash
# 1. Build with dotnet (Release config, .NET 4.7.2 target)
dotnet build -c Release

# 2. Print the DLL's SHA-256 (register with /admin publishversion in Discord)

# 3. Prepare GameData/BoundlessMissions/
#    - Copy GeneKerman.dll → Plugins/
#    - Copy websocket-sharp.dll → Plugins/
#    - Copy toolbar icon → Textures/
#    - Copy Iconpack-1 UI icons → Textures/
#    - Create default settings.cfg if absent

# 4. Deploy to KSP instance(s) in KSP_PATHS[]
#    - Preserve each install's existing settings.cfg (backup → copy → restore)
#    - Copy entire GameData/BoundlessMissions/ into each KSP's GameData/
```

The script deploys to multiple KSP installs simultaneously (a "normal" instance
and a "heavymod" Steam instance for TweakScale/mod-compatibility testing).

### Project Configuration (`GeneKerman.csproj`)

- **Target**: .NET Framework 4.7.2 (KSP's runtime)
- **Output**: `bin/GeneKerman.dll` (class library, no entry point)
- **References**:
  - `Assembly-CSharp.dll` (KSP's main game assembly — contracts, vessels,
    parts, etc.)
  - `UnityEngine.dll`, `UnityEngine.CoreModule.dll`,
    `UnityEngine.ImageConversionModule.dll`, `UnityEngine.IMGUIModule.dll`,
    `UnityEngine.InputLegacyModule.dll`, `UnityEngine.UI.dll` (Unity engine)
  - `websocket-sharp.dll` (bundled in `lib/`)
- **Assembly references**: Resolved from `KSP_x64_Data/Managed/` (hardcoded
  relative path)

---

## Dependencies

| Dependency | Source | Purpose |
|------------|--------|---------|
| `Assembly-CSharp.dll` | KSP install | KSP game API (vessels, parts, contracts, UI, etc.) |
| `UnityEngine*.dll` | KSP install | Unity engine (rendering, coroutines, IMGUI, textures) |
| `websocket-sharp.dll` | Bundled (`lib/`) | WebSocket client (KSP's Mono lacks `ClientWebSocket`) |
| **ModuleManager** | Required (separate install) | Patches `GeneKermanScale` onto every part prefab |

**Soft dependencies** (optional, reflection-based):
- Click Through Blocker — prevents UI click-through
- TweakScale — compatibility bridge for rescaled parts
- Physics Range Extender — temporarily disabled during submissions
- CKAN — reads `registry.json` for mod identifier mapping

---

## Configuration

`GameData/BoundlessMissions/PluginData/settings.cfg`:

```
GeneKerman
{
    serverUrl = http://localhost:5022
    checkInterval = 600
    enableNotifications = true
    enableKVV = true
    enableContractInjection = true
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `serverUrl` | `http://localhost:5022` | Backend API URL |
| `checkInterval` | `600` | Fallback notification poll interval (seconds) when WebSocket is down |
| `enableNotifications` | `true` | Enable notification polling/push |
| `enableKVV` | `true` | Enable vessel blueprint rendering |
| `enableContractInjection` | `true` | Inject API missions as stock contracts |

**Runtime data** (created automatically in `PluginData/`):
- `device.id` — stable per-install device identifier
- `auth_token` — API authentication token (from Discord linking)
- `catalog.hash` — FNV-1a hash of last-uploaded part catalog
- `settings.cfg` — user configuration
- `renders/` — saved blueprint PNGs
- `screenshots/` — saved screenshots
