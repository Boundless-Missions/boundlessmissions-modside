# Packaging Boundless Missions

## Pre-release checklist

> **Check the distribution URLs resolve, and that we own what they name.** Every URL in
> `GameData/BoundlessMissions/GeneKerman.version` and in `BoundlessMissions.netkan` must
> return 200, and the GitHub organisation they name must be one this project controls.
> These two files are how KSP-AVC and CKAN tell players where to get the mod, and they
> pointed at `github.com/gk-ksp/…` — an organisation that did not exist and that anyone
> could have registered — while the AVC `URL` 404'd, which silently disabled the very
> update check that would have surfaced the broken link. Neither failure is loud.

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

## Where a release goes, and in what order

`build.sh --release` produces the zip. This is what to do with it. **The order is not
arbitrary** — the NetKAN submission is last because it is not a package, it is an
instruction to a bot: once merged, CKAN's indexer polls the `$kref` on a schedule and
publishes whatever it finds to every CKAN user with no further review. Everything it reads
has to be right before it starts reading.

```
1. build.sh --release        the zip, and the DLL hash it prints
2. /admin publishversion     register that hash — BEFORE the download is public
3. GitHub Release            tag = ModVersion.Current, NOT a prerelease
4. SpaceDock + forum thread  discovery
5. NetKAN PR                 last, and only once 3 is stable
```

**Step 2 is the blocker.** The pre-release checklist at the top of this file covers it, but
to say it plainly here: a release whose DLL hash is not registered is rejected by the
version gate on every gated request, so publishing the download first means every player
who installs it is broken until you catch up. Build, register, *then* upload.

### GitHub Release

The source of record, because `BoundlessMissions.netkan` names it in `$kref`. Three things
the indexer cares about:

- **The tag must match `ModVersion.Current`.** `$vref: #/ckan/ksp-avc` makes CKAN read the
  version out of `GeneKerman.version`, which `build.sh` generates from `ModVersion.cs`. If
  the tag and that file disagree, CKAN indexes the AVC value and the tag is decoration —
  confusing rather than broken, but it is the kind of drift the generated version file
  exists to prevent.
- **Do not mark it a prerelease.** CKAN's GitHub `$kref` skips prereleases by default, so a
  prerelease is simply never indexed. Every release before v1.0.0 was an `-alpha`; the
  first one meant for players must not be.
- **One asset, named `BoundlessMissions-<version>.zip`**, which is what `--release`
  produces. The early releases used three different naming schemes
  (`Interactive-Contracting-…`, `interactive_contracting_…`, `BoundlessMissions_…`);
  consistency is worth more than it looks once a bot is picking assets.

### SpaceDock

Where KSP players actually browse. It is a **mirror, not the source**, as long as `$kref`
names GitHub — which means it does not update itself and will go stale unless you upload
there on every release. If that turns out to be the step that gets forgotten, the fix is to
move `$kref` to `#/ckan/spacedock/<id>` and make SpaceDock the source instead; do not leave
both half-maintained.

Include the antivirus/EDR note (below) in the description. A mod that opens a loopback HTTP
listener and talks to a server will be asked about.

### The forum thread

Not required by CKAN, but it is the front door the community expects, and it is where the
add-on rules (licence stated, what the mod transmits) are seen to be met. Rule 8.1 consent
and the privacy policy link belong in the opening post, not only in-game.

### The NetKAN PR

Submit `BoundlessMissions.netkan` to `KSP-CKAN/NetKAN` (as of this writing it is **not**
indexed — the path 404s, so the first submission is still ahead). Check their CONTRIBUTING
at the time you submit rather than trusting this paragraph; the one thing that does not
change is that the metadata must already be true when the PR lands.

Two conflicts to resolve first, both currently live in the file:

- `ksp_version_min` / `ksp_version_max` (1.12.0 / 1.12.5) disagree with what `$vref` pulls
  from the AVC file (1.12.0 / 1.12.99). Keep ONE source of truth. Preferring the AVC file
  matches how the version itself is already handled, and means one generated file drives
  both.
- The `filter` list and what `--release` packages must stay in step, which is why
  "Cutting a release" says to change both together.

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

**Provenance.** Recorded here because it was recorded nowhere: the file is excluded by
`.gitignore`'s blanket `*.dll` (now un-ignored explicitly), so nothing in the repository
described where it came from, a fresh clone could not build — the csproj's own comment
notes that a missing `<Reference>` HintPath fails *silently* — and no one could diff it
against upstream or notice it changing.

| | |
|---|---|
| Upstream | `sta/websocket-sharp` (the canonical implementation) |
| Assembly version | `2.0.0.0`, targeting .NET Framework 2.0 |
| Size | 250368 bytes |
| sha256 | `fb06ffceb4f8789c893d2f292e5810927dd7266d3bad68df2cedb8775500e8be` |

Two things to know about it. It is **effectively unmaintained** upstream, and it parses
attacker-influenced frames from the notification socket — so it is worth re-checking its
advisory state before each release rather than assuming. And it is carried because KSP's
Mono has no usable `ClientWebSocket`; that is worth re-testing against 1.12.5 occasionally,
because dropping the dependency is better than auditing it.

## What the browser UI adds to a release

`WebUI/` is a static Vite bundle (~4 files) plus `manifest.json` and
`THIRD-PARTY-NOTICES.txt`, both stamped by `WebUI/vite.config.ts` at build time —
the manifest with `ModVersion.Current`, the notices with the licence text of every
package compiled into the bundle. Both must be in the zip: the notices file is how
the MIT/ISC/Apache-2.0 attribution requirements are met for a minified bundle that
only preserves React's and lucide's own banners, and the `filter` in
`BoundlessMissions.netkan` (`settings.cfg` alone) is deliberately narrow so neither
is dropped on a CKAN install. The mod refuses to start the loopback bridge when that
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
