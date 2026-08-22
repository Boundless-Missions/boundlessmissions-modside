# Packaging Boundless Missions

## Pre-release checklist

Work through this before publishing a build. The first item is a hard blocker.

- [ ] **`KSP_VERSION_CHECK_ENABLED=true`** in the server's `.env`. It is currently `false`
      for development, because a local build's DLL hash is never the published one and the
      gate would block every test. Shipping with it off means *no* build can be rejected —
      including a modified `GeneKerman.dll` talking to the live server. `bot.py` prints a
      `SECURITY GATES DISABLED` banner on every start; that banner must be absent in
      production. **This one flag controls two mechanisms**: the `X-Mod-Hash` gate on
      every request, and challenge-response DLL attestation (`/api/v1/attest/*`), which
      verifies the same thing cryptographically instead of on the client's word. Turning
      it back on restores both; leaving it off disables both, so a tampered DLL is
      neither blocked nor reported.
- [ ] `KSP_DEVICE_BINDING_ENABLED` and `KSP_2FA_ENABLED` also `true` (same banner covers them).
- [ ] `ModVersion.Current` and `<Version>` in `GeneKerman.csproj` bumped and equal.
- [ ] `./build.sh --release` run clean, and the printed `/admin publishversion` line executed
      **before** the download goes public.
- [ ] Release zip contains no `PluginData` credentials — `--release` strips them, but the
      packaged listing is worth a glance (`session.token`, `sessions.cfg`, `consent.cfg`,
      `favorites.cfg`, `settings.cfg` must all be absent).
- [ ] Privacy policy and the docs page reflect anything new the build transmits or stores.


How a release is cut and why the CKAN metadata says what it says. `BoundlessMissions.netkan`
is JSON and cannot carry comments, so the reasoning lives here.

## Cutting a release

```bash
cd "KSP Mod Side"
./build.sh --release          # builds, stamps the version file, writes dist/BoundlessMissions-<version>.zip
```

`--release` does everything a normal build does, then packages `GameData/BoundlessMissions`
into `dist/`. It deliberately produces the **same** tree the netkan's `filter` expects, so
the two cannot drift — if you add a file that must not ship, add it to both.

Version comes from one place: `ModVersion.Current` in `GeneKerman/ModVersion.cs`. `build.sh`
generates `GeneKerman.version` from it on every build. Bump `ModVersion.Current` and the
`<Version>` in `GeneKerman.csproj` together.

After uploading, register the DLL's SHA256 (printed by `build.sh`) with
`/admin publishversion` so the server-side version gate recognises the build. A release
whose hash is not registered will be rejected by the gate on every gated request.

## Why `settings.cfg` is filtered out of the CKAN install

CKAN owns every file it installs, and replaces them on upgrade. `settings.cfg` holds the
player's server choice and their notification, checkpoint-photo and data-sharing toggles —
losing that on every update would be user-hostile, and silently re-enabling data sharing
would be a rule 8.2 problem, not just an annoyance.

So it is not shipped at all. `ApiClient.LoadSettings` writes a default on first run
(`GeneKermanMod.Start` creates `PluginData/` before `new ApiClient()`, so the directory is
always there), which is the outcome a fresh install wants anyway.

The other `PluginData` files — `session.token`, `sessions.cfg`, `consent.cfg`,
`favorites.cfg` — are never shipped, so CKAN does not track them and leaves them alone.
That is what it should do: `consent.cfg` in particular must survive an upgrade, because
re-prompting for consent on every version bump would train players to click through it.

## Dependencies

**`ModuleManager` — required.** `Patches/GeneKermanScale.cfg` is an `@PART[*]:FINAL` patch
that attaches the `GeneKermanScale` module to every part. Without MM the module is never
attached and transferred craft with rescaled parts load wrong.

**`ClickThroughBlocker` — recommended, not required.** `ClickThroughHelper.cs` resolves it
by reflection at runtime (`AssemblyLoader` → `ClickThroughFix.ClickThruBlocker`) and falls
back to stock `GUILayout.Window` when it is absent. It makes the windows behave properly
over the editor and flight scenes, but nothing breaks without it.

**`ToolbarController` — not a dependency**, despite the `<Reference>` in `GeneKerman.csproj`.
That reference is dead: the toolbar button is created with stock
`ApplicationLauncher.AddModApplication` (`GeneKermanMod.cs:552`) and no type from
ToolbarControl is used anywhere. Assembly references are resolved lazily by the CLR, so the
unused reference costs nothing at runtime — but listing it in the netkan would force every
player to install a mod they do not need.

**`KronalVesselViewer` — not listed at all.** It used to be a `suggests`, from back when
the plan was to hand vessel renders off to it. `VesselRenderer` does that job now and does
it unconditionally, so nothing the add-on does changes when KVV is installed and suggesting
it only pointed players at a mod that would do nothing for them here. `KVVIntegration.cs`
outlived that plan as a rename in front of `VesselRenderer` — its one live member forwarded
to `VesselRenderer.CaptureVessel`, and the KVV detection itself had no callers — and has
been deleted; `SubmissionSession` calls the renderer directly.

## Bundled assemblies

`Plugins/websocket-sharp.dll` ships alongside `GeneKerman.dll`. Other mods bundle their own
copies at their own paths, so there is no CKAN file conflict. If a future KSP-side library
mod ever *provides* it at a shared path, this becomes a `conflicts`/`provides` question.

## What the browser UI adds to a release

`WebUI/` is a static Vite bundle (~4 files) plus `manifest.json`, stamped with
`ModVersion.Current` at build time. The mod refuses to start the loopback bridge when that
manifest does not match the running DLL, which is what catches a half-applied manual
install — a real scenario, since CKAN and manual installs coexist.

Assets are content-hashed, so a release zip must contain **only** the current build's
chunks. `build.sh` clears the destination `WebUI/` before copying for exactly this reason;
`--release` packages from `GameData/`, which has just been rebuilt, so it inherits that.

## Antivirus / EDR note for the release description

The mod opens a listening socket (loopback only) and launches the default browser when
browser mode is enabled. That combination is a mild heuristic trigger for some AV and EDR
products. Mention it in the release notes and forum post *before* someone files a "your mod
is malware" thread — the socket binds `127.0.0.1` on an ephemeral port and never `0.0.0.0`,
and browser mode is off by default.
