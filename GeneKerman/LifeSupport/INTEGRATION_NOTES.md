# Life-support / DeepFreeze integration notes

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

All adapters reflect against confirmed names. They still self-disable if an
assembly/member isn't found, so a different mod version degrades to a safe no-op.

**Re-verifying later:** the USI / TAC / Snacks / DeepFreeze DLLs that were dumped for
these notes are no longer on this machine (`compat-check/` is gone; only
`KR-KSP/GameData/Kerbalism/Kerbalism.dll` remains). The member names below are what was
read out of the real assemblies at the time — treat this file as the record, and re-dump
with `ikdasm` if a mod update ever makes an adapter go quiet.

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
- **Freeze** (`SuspendKerbal`): `UntrackKerbal(name)`. With no record at all there is
  nothing for USI's background pass to age.
- **Thaw** (`ResumeKerbal`): `FetchKerbal(pcm)` (re-creates a record stamped "now"), then
  pin `LastMeal / LastEC / LastUpdate / LastAtHome / LastSOIChange / TimeEnteredVessel`
  to now and `TrackKerbal` it back. Both halves are needed: the fetch covers an untracked
  kerbal, the pin covers one USI re-tracked behind our back while the wreck was loaded —
  whose back-dated `LastMeal` would kill it the moment it boards.

## TAC-LS — CONFIRMED (assembly `TacLifeSupport`, namespace `Tac`)

- `Tac.TacLifeSupport.Instance` (static field) → `gameSettings` (`Tac.TacGameSettings`)
  → `knownCrew` : KSP **`DictionaryValueList<string, Tac.CrewMemberInfo>`** (NOT a plain
  IDictionary — read via `LsReflect.GetByKey`).
- `Tac.CrewMemberInfo` UT fields: `lastUpdate`, `lastFood`, `lastWater`, **`lastO2`**
  (not "lastOxygen"), `lastEC`. (Also has a `DFfrozen` bool — TAC is DeepFreeze-aware.)
- Resources: `Food`, `Water`, `Oxygen`.
- **Freeze and thaw both** pin `lastUpdate / lastFood / lastWater / lastO2 / lastEC` to
  now. TAC derives starvation purely from the gap to those stamps, so the pin on thaw is
  what erases however long the wreck drifted. No record = nothing to do: TAC starts the
  kerbal fresh by itself.

## Snacks — CONFIRMED (assembly **`SnacksUtils`**, namespace `Snacks`)

- `Snacks.SnacksScenario.Instance` (static) → `AstronautData GetAstronautData(ProtoCrewMember)`.
- `Snacks.AstronautData.lastUpdated` (UT double).
- Resource: `Snacks`.
- **Freeze and thaw both** set `lastUpdated = now`. `AstronautData` is a reference type
  held by the scenario, so writing the field on the fetched instance *is* the update.

## Kerbalism — CONFIRMED (assembly `Kerbalism`, shipped via `KerbalismBootstrap`)

Re-dumped from `KR-KSP/GameData/Kerbalism/Kerbalism.dll`. The earlier note here said
"detect-only, cannot be paused per-kerbal" — that was wrong: Kerbalism ships a public API
for exactly this, and its profile rates are readable.

- `KERBALISM.API.DisableKerbal(string k_name, bool disabled)` — public static. Sets
  `KerbalData.disabled`, which `KERBALISM.Rule.Execute` checks before touching a kerbal
  (alongside `KerbalData.rescue`, Kerbalism's own stranded-crew flag). A disabled kerbal
  consumes nothing and accrues nothing, under warp included. This is what DeepFreeze
  integrations use. **The flag is saved**, so a freeze that never thaws leaves a kerbal
  permanently exempt — every thaw path in the guardian must run.
- `KERBALISM.DB.ContainsKerbal(name)` / `DB.Kerbal(name)` → `KerbalData.rules` :
  `Dictionary<string, RuleData>`; `RuleData.problem` + `RuleData.time_since` are the
  accumulated deficit. Kerbalism stores accumulation rather than a "last fed" timestamp,
  so thaw *clears* these rather than resetting a clock.
- `KERBALISM.Profile.rules` : `List<Rule>`, each with `input` (resource), `rate`,
  `interval`. A rule with an interval consumes `rate` units per interval (eating: 0.1312
  Food per 10800 s = 2 meals/day); without one, `rate` is per second. `ElectricCharge`
  rules (climatization) are skipped — EC is generated, not stowed, and counting it would
  make every craft look like it had hours to live. This is where the Kerbalism endurance
  figure now comes from, instead of "unknown".
- Resources for tagging: `Food` / `Water` / `Oxygen`.

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

## Emergency freeze = stasis + LS handoff + rations (final design)

Rescue crew survive an arbitrary wait, in any combination of LS mods on either client,
because three separate things hold. Each covers a hole the others leave; dropping any one
of them breaks a real case. Owned by `RescueImmunityGuardian`, `LsFreeze`, `LsRations`.

**1. Stasis — nothing consumes them while they wait.** For each non-frozen rescue kerbal,
remove it from the wreck — loaded → `Part.RemoveCrewmember`; unloaded → drop from the
`ProtoPartSnapshot`'s `protoModuleCrew` + `protoCrewNames` and `ProtoVessel.RemoveCrew`.
Park it at `RosterStatus.Dead` (so KSP's respawn timer can't revive it) and remember the
part by `flightID`. A kerbal aboard no vessel is consumed by nothing, which is the only
approach that also holds under Kerbalism's background sim. Crew already in a real
DeepFreeze cryopod are left seated (inert; the player thaws them at the pod).

**2. LS handoff — they survive being thawed.** Stasis alone is not enough on the way out:
USI-LS, TAC-LS and Snacks all reconstruct hunger from a stored "last fed" UT, so a kerbal
re-boarded after 200 days of stasis is 200 days starved the instant it exists again.
`LsFreeze.Freeze` therefore also tells every installed adapter to let go (`SuspendKerbal`)
and `LsFreeze.Thaw` hands them back with a clean slate (`ResumeKerbal`) — see each mod's
section above for what that means per mod. Two rules fall out of this:

  - Freeze/thaw go through **every installed** adapter, not the primary one. A save can
    have two LS mods loaded and a kerbal only stays frozen if all of them let go.
  - **Every** path that drops a freeze record thaws first, including the one where the
    wreck was destroyed and nobody is coming. Kerbalism's `disabled` flag is persisted;
    a kerbal frozen and never thawed would be exempt from life support for the rest of
    the save.

**3. Emergency rations — the wreck has something they can eat.** The wreck was provisioned
by another player: a craft full of TAC Food/Water/Oxygen arriving in a USI save carries
nothing that save recognises, so thawing its crew aboard it is thawing them into a ship
with zero life support. `LsRations` stows `emergencyRationDays` (default 3) × crew of the
**local** mod's resources aboard, sized from the same `DailyNeedPerKerbal` the endurance
display uses. It is a top-up, not a refill — whatever the wreck already carries counts
towards the target, so a craft genuinely built for this LS mod gets nothing, and it is
idempotent (it runs at spawn and again at thaw). Resource injection: loaded →
`PartResourceList.Add(name, amount, maxAmount, flowState, isTweakable, hideFlow,
isVisible, PartResource.FlowMode.Both)`; unloaded → mutate the `ProtoPartResourceSnapshot`
and call `UpdateConfigNodeAmounts()` (the snapshot's own ConfigNode is what gets saved —
the fields alone are discarded), or add `new ProtoPartResourceSnapshot(node)` built from a
`RESOURCE` node. Guarded by `PartResourceLibrary.Instance.GetDefinition(name) != null`.

**Thaw trigger:** the active vessel within `ReviveRadiusMeters` (10 km — well outside load
range, so the wreck is still unloaded and KSP seats the crew on load), or the manual
"Thaw the crew now" button. Crew go back — loaded → `Part.AddCrewmemberAt` (or
`AddCrewmember` when their own seat is gone or taken); unloaded → the part's
`protoModuleCrew`/`protoCrewNames` + `ProtoVessel.AddCrew` — at `RosterStatus.Assigned`.

**Putting a kerbal back is three things**, and a thaw that does only the first leaves a
wreck that looks crewed and behaves as if it weren't (some control, no SAS, no portraits —
recovered only by EVA'ing and boarding again, which is KSP doing all three properly):

  - **The right part.** `ModuleCommand` counts crew and pilots in *its own*
    `part.protoModuleCrew`, so a pilot seated in a passenger cabin instead of the pod
    leaves the pod crewed but pilotless — `ControlLevel.PARTIAL_MANNED`, which is exactly
    "half control and SAS won't hold". The freeze record therefore carries the part's
    `flightID`, and the fallback (part gone or full) prefers a part with a `ModuleCommand`
    and says so in the log.
  - **A seat of its own.** `InternalModel.AssignToSeat` writes `seats[pcm.seatIdx]`
    *without* checking `taken`, so two kerbals carrying the same index share a chair and
    only the last one gets a `Kerbal` — the object a portrait is drawn from. The record
    carries `seatIdx`, and `pcm.seat`/`seatIdx`/`KerbalRef` are cleared on both freeze and
    thaw, since those point at scene objects from a vessel state that no longer exists.
  - **Telling KSP.** Seating by hand updates one list and notifies nothing:
    `Vessel.CrewWasModified` rebuilds the vessel crew cache and fires
    `onVesselCrewWasModified`, while `KerbalPortraitGallery` listens to
    `onVesselWasModified` — both are fired after a thaw. `Part.AddCrewmember*` also does
    not *spawn* the seated kerbal, so `InternalSeat.SpawnCrew()` is called for the seat we
    filled, and `Part.SpawnIVA()` for a touched part on the active vessel that has no
    interior yet.

**Never `DespawnCrew(); SpawnCrew();` back to back.** `Part.DespawnIVA` destroys the
interior with `Object.Destroy` (which completes at the *end* of the frame) and never nulls
`part.internalModel`, so `SpawnIVA`'s null check still passes in the same frame: it
re-seats the crew and spawns their `Kerbal`s into a model that dies moments later. The part
is left with no interior, every portrait it just registered is unregistered as the objects
die, and nothing rebuilds either until the player switches vessels.

**Cross-mod visibility:** the wreck's own LS provisioning is scanned on the issuer's
client at rescue creation and rides on the contract (`life_support`), so the rescuer's
window can say "built for TAC-LS, you run USI-LS" before they set off, and the freeze
record persists it (`builtWithLs`).

Confirmed KSP crew APIs used: `ProtoPartSnapshot.protoModuleCrew`/`protoCrewNames`/
`flightID`/`RemoveCrew`/`FindModule`; `ProtoVessel.AddCrew`/`RemoveCrew`;
`Part.AddCrewmember`/`AddCrewmemberAt`/`RemoveCrewmember`/`SpawnIVA`;
`InternalModel.seats`/`InternalSeat.taken`/`InternalSeat.SpawnCrew`;
`Vessel.CrewWasModified`/`RebuildCrewList`/`UpdateResourceSets`;
`GameEvents.onVesselWasModified`; `KerbalRoster.Kerbals(RosterStatus[])` (finds parked
Dead crew).

**Player switches** (`PluginData/settings.cfg`, also in the sidebar's Settings panel):
`enableEmergencyFreeze` (default true — off leaves the crew seated and starving normally)
and `emergencyRationDays` (default 3, 0 disables the ration kit only).

## Build facts

- `GeneKerman.csproj`: `<Compile Include="**/*.cs" />` → files under `LifeSupport/` are
  auto-compiled; `LangVersion 9.0`, `net472`. No new `<Reference>` needed (reflection).
