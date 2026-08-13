# Life-support / DeepFreeze integration notes (Checkpoint 0)

All third-party access is **reflection only** — the build references the *normal* KSP
install (`/home/ayd/Documents/Kerbal Space Program`), which has none of these mods.
Every adapter must degrade to a safe no-op when its type/member isn't found, exactly
like `PhysicsRangeManager.cs` / `TweakScaleGuard.cs`.

## What's been verified via ikdasm (all five confirmed)

| Mod        | Assembly         | Source DLL                                                          |
|------------|------------------|--------------------------------------------------------------------|
| USI-LS     | `USILifeSupport` | Steam: `UmbraSpaceIndustries/LifeSupport/USILifeSupport.dll`        |
| TAC-LS     | `TacLifeSupport` | `compat-check/TAC-LS/.../TacLifeSupport.dll`                        |
| Snacks     | `SnacksUtils`    | `compat-check/Snacks/.../SnacksUtils.dll` (assembly ≠ "Snacks"!)    |
| Kerbalism  | `Kerbalism`      | `compat-check/Kerbalism/.../KerbalismBootstrap.dll` (loads .kbin)   |
| DeepFreeze | `DeepFreeze`     | `Documents KSP: REPOSoftTech/DeepFreeze/Plugins/DeepFreeze.dll`     |

All adapters now reflect against confirmed names. They still self-disable if an
assembly/member isn't found, so a different mod version degrades to a safe no-op.

## USI-LS — CONFIRMED (assembly `USILifeSupport`)

- `LifeSupport.LifeSupportManager` — singleton via static property `Instance`.
  - `LifeSupportStatus FetchKerbal(ProtoCrewMember crew)` — gets/creates the tracking
    record (new records start at "now").
  - `bool IsKerbalTracked(string kname)`
  - `void TrackKerbal(LifeSupportStatus status)` — persists an updated record.
  - `void UntrackKerbal(string kname)` — removes tracking entirely.
  - (`FetchVessel`, `IsVesselTracked`, `TrackVessel`, `UntrackVessel`, `ResetCache`.)
- `LifeSupport.LifeSupportStatus` — auto-properties (use getters/setters by name):
  `KerbalName`, `HomeBodyId`, `LastPlanet`, `LastMeal`, `LastEC`, `LastUpdate`,
  `IsGrouchy`, `OldTrait`, `LastAtHome`, `LastSOIChange`, `MaxOffKerbinTime`,
  `TimeEnteredVessel`, `CurrentVesselId`, `PreviousVesselId`.
- Resource consumed: `Supplies` (produces `Mulch`); also uses `ElectricCharge`.
- **Immunity mechanism:** while the wreck is unloaded, `UntrackKerbal(name)` keeps USI
  from accruing background starvation. As belt-and-suspenders we also, when the record
  exists, set `LastMeal = LastEC = LastUpdate = LastAtHome = now` and `TrackKerbal`.
  **Handoff:** `FetchKerbal` (re-creates a fresh record at "now") so the rescuer's LS
  takes over cleanly.

## TAC-LS — CONFIRMED (assembly `TacLifeSupport`, namespace `Tac`)

- `Tac.TacLifeSupport.Instance` (static field) → `gameSettings` (`Tac.TacGameSettings`)
  → `knownCrew` : KSP **`DictionaryValueList<string, Tac.CrewMemberInfo>`** (NOT a plain
  IDictionary — read via `LsReflect.GetByKey`).
- `Tac.CrewMemberInfo` UT fields: `lastUpdate`, `lastFood`, `lastWater`, **`lastO2`**
  (not "lastOxygen"), `lastEC`. (Also has a `DFfrozen` bool — TAC is DeepFreeze-aware.)
- Resources: `Food`, `Water`, `Oxygen`.
- Immunity: pin `lastUpdate/lastFood/lastWater/lastO2/lastEC` to now.

## Snacks — CONFIRMED (assembly **`SnacksUtils`**, namespace `Snacks`)

- `Snacks.SnacksScenario.Instance` (static) → `AstronautData GetAstronautData(ProtoCrewMember)`.
- `Snacks.AstronautData.lastUpdated` (UT double).
- Resource: `Snacks`.
- Immunity: set `lastUpdated = now`.

## Kerbalism — DETECT-ONLY (assembly `Kerbalism`, shipped via `KerbalismBootstrap`)

- Runs a background sim that owns kerbal rule state; cannot be paused per-kerbal. Adapter
  reports `IsInstalled` (matches `Kerbalism` or `KerbalismBootstrap`) + resource names
  (`Food`/`Water`/`Oxygen`) for tagging. Endurance left unknown (profile-driven).
- Under Kerbalism the only survivable stranded crew are **frozen** ones (DeepFreeze).

## DeepFreeze — CONFIRMED (assembly `DeepFreeze`, author REPOSoftTech)

- **Freezing is part-bound** and cannot be done on demand: `DF.DeepFreezer : PartModule`,
  and `DF.KerbalInfo` carries `partID`/`seatIdx`/`vesselID` — a frozen record requires a
  real cryopod seat. The `DF.DFWrapper` shipped in the DLL lives in namespace
  `MyPlugin_DFWrapper` (a copy-into-your-mod helper), so it is NOT a stable reflection
  target. ⇒ We cannot freeze a stranded rescue crew ourselves.
- We CAN **detect** frozen crew: `DF.DeepFreeze.Instance` (static field) → property
  `FrozenKerbals` : `Dictionary<string, DF.KerbalInfo>` (keyed by name).
- **Resolution of the CP0 open risk:** partless freeze is impossible. DeepFreeze support
  therefore means *recognising already-frozen crew* (frozen kerbals consume nothing under
  any mod, including Kerbalism) and leaving them alone. The adapter's Suspend/Activate are
  no-ops; `IsKerbalFrozen` drives the registry/guardian.

## Rescue immunity = roster STASIS (mod-agnostic — final design)

The per-mod timestamp poking was replaced with a single mechanism that works for every
LS mod (incl. Kerbalism) with no DeepFreeze dependency: **remove the stranded crew from
the simulation while the wreck drifts, then re-board them when a rescuer approaches.** A
kerbal that isn't aboard any vessel is consumed by nothing. See `RescueImmunityGuardian`.

- **Stash (on spawn):** for each non-frozen rescue kerbal, remove it from the wreck —
  loaded → `Part.RemoveCrewmember`; unloaded → drop from the `ProtoPartSnapshot`'s
  `protoModuleCrew` + `protoCrewNames` and `ProtoVessel.RemoveCrew`. Park it at
  `RosterStatus.Dead` (so KSP's respawn timer can't revive it). Remember the part by
  `flightID`. Crew already in a real DeepFreeze cryopod are left alone (inert; player
  thaws at the pod).
- **Revive (on contact / button):** when the active vessel is within `ReviveRadiusMeters`
  (10 km — well outside load range, so the wreck is still unloaded and KSP seats the crew
  on load), put them back — loaded → `Part.AddCrewmember` + `Vessel.SpawnCrew`; unloaded →
  add to the part's `protoModuleCrew`/`protoCrewNames` + `ProtoVessel.AddCrew` — and set
  `RosterStatus.Assigned`. A manual "Revive emergency stasis crew" button forces it.

Confirmed KSP crew APIs used: `ProtoPartSnapshot.protoModuleCrew`/`protoCrewNames`/
`flightID`/`RemoveCrew`; `ProtoVessel.AddCrew`/`RemoveCrew`; `Part.AddCrewmember`/
`RemoveCrewmember`; `Vessel.SpawnCrew`/`DespawnCrew`/`RebuildCrewList`;
`KerbalRoster.Kerbals(RosterStatus[])` (finds parked Dead crew). No Kerbalism block —
stasis defeats it. The LS adapters are now detection-only (tagging + endurance).

## Build facts

- `GeneKerman.csproj`: `<Compile Include="**/*.cs" />` → files under `LifeSupport/` are
  auto-compiled; `LangVersion 9.0`, `net472`. No new `<Reference>` needed (reflection).
