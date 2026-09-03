# The agent-drivable test bridge

A dev-build-only HTTP channel inside the running game, and a Python driver for it, so
the in-game state a crew bug actually lives in can be asserted on instead of squinted
at in the Astronaut Complex.

**It is not in the shipped mod.** Every line is behind `#if GK_DEBUG_PANEL`, and
`tools/assert_production_clean.sh` proves it against the built DLL.

---

## Why this exists

From `3108_security_audit.md`, Part 3:

> The mod changes have never been **run.** 24 files changed across the crew path […]
> all that is known is that they compile and that pure-function tests pass. […]
> **A crew bug is unrecoverable for the player**, so an in-game pass is the binding
> gate, not another read.

Four rounds of fixes landed on the crew-transfer path. Each compiled. Each passed its
tests. Three of them corrupted saves. The existing `DebugTestPanel` rows cover string
and set logic against a *stubbed* roster, and one of them says so in as many words:

> What it CANNOT check is the set itself: whether `CrewedNames` actually sees a
> deferred-removal wreck's crew in a real save is a live test, not this one.

This is that live test.

---

## Quick start

```bash
# 1. Build the dev channel. A production DLL has none of this in it.
cd "KSP Mod Side" && GK_CHANNEL=dev ./build.sh

# 2. Launch KSP (one or two instances) and load saves/gktest.

# 3. From the repo:
cd "KSP Mod Side/tools"
./gkrun.py list                                  # who is up
./gkrun.py state --a KR-KSP | less               # the whole snapshot
./gkrun.py run all --a KR-KSP --b KR2-KSP        # the T0–T7 pass
./gkrun.py watch --a KR-KSP                      # tail the event stream
```

Exit codes: `0` all selected scenarios passed · `1` something failed · `2` could not
connect · `3` a scenario was **not performed**.

`3` is not a pass. See "Manual steps" below.

---

## How a driver finds the game

The mod writes `GameData/BoundlessMissions/PluginData/debug_bridge.json` on start and
deletes it on shutdown:

```json
{"protocol":1,"port":41537,"token":"…","host":"127.0.0.1:41537",
 "tokenHeader":"X-GK-Debug-Token","modVersion":"1.0.0","install":"…","pid":12345}
```

The port is ephemeral and the token rotates every launch, so this file is the only
stable address, and because it lives inside each install's own GameData, two
instances running side by side are unambiguous. Written whole then moved into place,
so a driver polling for it never parses a truncated token.

A hard kill leaves the file behind, so the driver always confirms liveness with the
unauthenticated `/gk/debug/ping` before spending the token on what might now be a
different service on that port.

---

## Security shape

The bridge is a command channel that spawns vessels, removes them and edits rosters,
exactly the boundary `Web/ApiProxy.cs` says the browser bridge's allow-list exists to
hold. So:

| | |
|---|---|
| **Compiled out of production** | `#if GK_DEBUG_PANEL`, asserted by `assert_production_clean.sh` |
| **Separate listener** | its own port, never the browser bridge's, so there is no path from a page session to any of this |
| **Bearer token in a header** | never a cookie. Audit finding **LB2** (the `gk` cookie is host- not port-scoped, so another loopback service can replay cookie-only routes) is still open, and the brief forbids adding a cookie-only route |
| **Loopback only** | `http://127.0.0.1:PORT/`, never `+`, `*` or `localhost` |
| **Exact Host match** | Mono's prefix matcher lets `127.0.0.1` (no port) and `127.0.0.1:PORT.evil.com` through; this is what stops them |
| **No CORS headers, ever; OPTIONS → 405** | which is what makes the token header un-forgeable from another origin |

**What this is not.** The token sits in a file readable by the account running KSP, and
so does `PluginData/session.token`. A hostile process on that account has already won.
This is a boundary against everything *else* on loopback (another mod, a stray page, a
service on a neighbouring port), not against a local attacker.

---

## Why a second listener rather than routes on `LocalServer`

`LocalServer` already has the listener, the auth, the job registry and the SSE. It was
still the wrong host for this, for three reasons in order of weight:

1. It **refuses to start** without a version-matched WebUI bundle, and only runs when
   the player has switched `enableWebUi` on. Both are right for the browser UI, and
   both would make the harness unavailable in the ordinary case, which is testing a *sidebar*
   build with no bundle installed.
2. Keeping the command channel off the production listener means the shipped surface is
   not touched at all.
3. LB2, above.

What *is* reused verbatim: `MainThreadQueue` (the hard part, since KSP state is main-thread
only), `JobRegistry`, `EventStream`, `JobResult` and `LocalServer`'s static response
helpers. None of those are coupled to the browser listener.

---

## Routes

All under `/gk/debug/`, all requiring `X-GK-Debug-Token` except `ping`.

### Reads

| Route | What |
|---|---|
| `GET /ping` | liveness, unauthenticated, so a stale handshake is cheap to detect |
| `GET /state` | **everything, in one main-thread job** |
| `GET /roster` `GET /vessels` `GET /scenario` `GET /contracts` | the same sections alone |
| `GET /events` | SSE; notifications and job completions are tee'd here |
| `GET /jobs/{id}` | for a driver whose stream dropped |

`/state` is one request on purpose: an assertion about a hand-over needs the roster,
the vessel list and the scenario queues to be consistent *with each other*, and three
separate requests can straddle a frame in which a removal ran.

It carries:

- **identity**: including `accountId`, the field the whole crew-ownership fix rests
  on. The impersonation test is precisely "same `username`, different `accountId`".
- **vessels**: `pid` (the GUID every removal and scenario record keys on) *and*
  `persistentId` (KSP's separate uint), because the two are routinely confused and a
  test that cannot tell them apart cannot prove a removal hit the right hull. Crew read
  through `VesselTransfer.CrewOf`, so unloaded vessels resolve, and a rescue wreck is
  almost never loaded.
- **roster**: `status`, `traitResolves` (the null `experienceTrait` that NullRefs the
  Astronaut Complex mid-draw, reported as its own field), `borrowed`, `baseName`, and
  `aboard` computed from the *vessel list* rather than from `rosterStatus`. The two
  disagree in exactly the states worth testing: the freeze parks crew at `Dead` while
  they are still in a seat.
- **crew**: `crewedNow` from the real `CrewedNames()`, `ghostCandidates`,
  `brokenTraits`.
- **scenario**: all four persisted queues: pending removals (with
  `vesselStillPresent`), freeze records, spawned wrecks with their `crewRenames`, the
  import dedup set, outstanding submissions.
- **client**: the contract list as `ClientState` holds it, plus `contractsLoaded`.
  That flag is not decoration: `HomeboundCrewFor` draws a hard line between "nothing is
  attested" and "the list has not been fetched yet", and conflating them re-tags the
  issuer's own kerbals under the rescuer.

### Commands

| Route | Notes |
|---|---|
| `POST /actions/save` | the driver's sync primitive; refuses in flight |
| `POST /actions/remove-vessel` | `{pid, crew_fate}`. **`crew_fate` is required**, never defaulted. `RemoveVesselFromSave` defaults to `LeavesWithCraft`, which kills everyone aboard |
| `POST /actions/purge-ghosts` | runs the sweep; returns the count |
| `POST /actions/crew` | `{action: rename\|add\|remove\|status, …}`. `status` (Available/Dead/Missing) is a **fixture writer**: a ghost is the residue of a hand-over that went wrong and nothing in production creates one on request, so T5 has to place it. It writes an input; the selection rule under test is still the real `PurgeBorrowedGhostCrew`. `Assigned` is refused, being a claim about a vessel as well as the roster |
| `POST /actions/poll-imports` · `/actions/refresh` | kick the mod's own timers |
| `POST /actions/scene` | `SPACECENTER`/`TRACKSTATION` only → 202 + job id |
| `POST /actions/spawn-wreck` | `{contract_id}` → 202 + job id |
| `POST /actions/quicksend` | `{recipient_id, recipient_name, kind}` → 202 + job id |
| `POST /actions/issue-rescue` | `{contractor_id, …}` → 202 + job id. Drives `ContractCreation.Create`, the sidebar form's own path, not a post to the server, so the snapshot, the dedup and the orbit-epoch freeze all happen as in play. **Destroys the vessel being flown**, exactly as issuing does; back the save up first |
| `POST /actions/accept-contract` | `{contract_id, issuer_name}` → 202 + job id, through `ClientState.RequestAcceptContract` |

Two conventions worth knowing:

- **A refusal is HTTP 200 with `ok:false`.** The request was well-formed and the game
  said no ("you cannot spawn from the main menu"). A malformed request is 4xx. The
  driver turns the first into a `Refused` exception so a scenario cannot walk past a
  step that did nothing.
- **`Deferred` is a success.** Removing the vessel you are flying is *supposed* to
  queue. A driver that reads it as failure is testing the wrong thing.

Scene changes and spawns answer 202 rather than blocking: a load is 5–40 s on a modded
install, during which `Update()` does not run at all, so holding the request open would
burn the queue's 30 s timeout and report a false failure on a load that worked.

---

## T0–T7

The audit refers to this plan repeatedly and never writes it down, so these are derived
from what it says is unverified. Each names the finding it settles.

| | Scenario | Settles |
|---|---|---|
| **T0** | Bring-up: bridges live, throwaway save, two *distinct* linked accounts | baseline, since a save that already has ghosts makes T5 pass for the wrong reason |
| **T1** | Rescue round trip: tag applied on the way out, stripped on the way home | the full crew path; three of four repair rounds broke here |
| **T2** | Deferred removal: defers in flight, persists, fires at the Space Center | the queue that makes a hand-over survive quitting mid-flight |
| **T3** | Quickload rollback: no residue, and the accept echo re-asserts | `MaybeHandleGiftAccepted`, since a quicksend has no contract to re-derive intent from |
| **T4** | **Impersonation**: same display name, different account | **RM1**, the case the whole account-id change exists for |
| **T5** | Ghost sweep removes exactly what it predicts, never a frozen kerbal | `PurgeBorrowedGhostCrew` vs. the freeze parking crew at `Dead` |
| **T6** | Busy-crew refusal: `CrewedNames` over a real save, unloaded vessels included | **RM3**, and the exact gap `DebugTestPanel` says it cannot cover |
| **T7** | Trait downgrade: nothing unresolvable is ever written to the roster | `ApplyTrait` / `TraitRepair` |

Run order is `T0, T2, T5, T6, T7, T1, T3, T4`: cheapest and least destructive first,
and T4 last because it requires renaming a Discord account, which is a nuisance to undo.

### T4 is the one that matters

An attacker sets their Discord display name to the victim's and re-links. Under the old
model the victim's client computed `comingHome` from a name compare, took the branch
that skips the roster-collision check, and adopted the victim's own kerbals onto the
arriving vessel, and the next hand-over then deleted them permanently. It also fires with
**no attacker at all**: two players who share a nickname corrupt each other's rosters on
any craft exchange.

The assertion is **not** "the send was refused", because a send from a stranger is perfectly
legal. It is that the arrivals are renamed *aside*, that the victim's own kerbals are
untouched and still aboard whatever they were aboard, and that the two instances really
are different accounts (a two-player test against one account proves nothing, which T0
checks and T4 re-checks).

---

## Running it on ONE KSP install

You do not need two copies of the game. Every hand-over in this system goes through a
**server-side pending queue** (`/api/v1/craft/imports/pending`), so the sender and the
recipient are never required to be live at the same moment. The two-player cases can be
serialized: act as one side, swap, act as the other.

```bash
./gkrun.py run T4 --a KR-KSP --single --save-a gktest-a --save-b gktest-b
```

The driver then treats "issuer/victim" and "rescuer/attacker" as **roles** rather than
processes, and prompts you to move the install between them.

### Two saves are required, not optional

`--single` refuses to start without `--save-a` and `--save-b`, and refuses if they are
the same. If both roles share a save, the victim's kerbals and the arriving kerbals are
*the same roster entries*, and no observation distinguishes "the arrival was renamed
aside" from "the originals were simply still there". T4 would pass without testing
anything.

### What a swap costs

`sessions.cfg` parks tokens **by server URL, not by account**, and `ClearToken()`
removes the entry rather than parking it. So there is no account switcher: each swap is
unlink → fresh 6-digit code → re-link, plus a main-menu round trip to change save. The
link code's TTL is 3 minutes. T1 needs about four swaps, T4 two.

Device binding is not in the way: `device.id` is per install and trusted per user, so
each account trusts this machine on its own first link.

### The check that makes it sound

With two installs, nothing can make role A and role B the same account, because different
processes, different tokens. With one install nothing *structurally* prevents sending
to yourself, and a self-send succeeds, changes nothing, and passes every later
assertion. So:

- every role activation verifies the live `accountId` **and** `save` against what the
  role expects, and refuses rather than proceeding;
- a role whose identity is not yet known will **not** adopt an account already claimed
  by the other role. That is exactly the shape of "I confirmed the swap but forgot to
  do it";
- T0 asserts the two roles are different accounts and (in single mode) different saves;
  T1 and T4 re-assert distinctness before acting.

Confirming a swap you did not perform therefore produces a loud `FAIL`, not a green run.

### What genuinely degrades

Only one thing: you cannot observe both sides at the same instant. In practice that
costs nothing here, because every assertion in T0–T7 is about *persisted* state on one
side (a roster, a vessel list, a queue) and each is read while that side is live.
There is no invariant in this set that requires a simultaneous two-sided snapshot.

## Manual steps, and why SKIP ≠ PASS

KSP cannot be flown over HTTP. Typing a link code, flying a rendezvous, pressing F9:
these are `manual` steps: the driver prints what to do and waits for Enter.

**A scenario with an unperformed manual step reports `SKIP`, and the run exits `3`.** It
does not fall through to the assertions, because they would be read against a game in
which the step never happened and would mostly pass. The entire reason this harness
exists is that four rounds of fixes each passed their tests; a harness that reports
green for work nobody did reproduces that failure with more machinery.

`--non-interactive` skips every scenario with a manual step. Useful for the read-only
ones (T0, T5, T6's first half, T7's first half); useless for the rest.

---

## Safety

1. **`saves/gktest` only.** Every destructive helper calls `guard_save()` first, which
   refuses if the loaded save is anything else. Override with `--save` if you must; the
   default is never the permissive one.
2. **`persistent.sfs` is copied aside** per scenario before anything destructive.
3. The mod's own precondition checks refuse rather than half-act, so you get a sentence,
   not a `NullReferenceException` in `KSP.log` that the driver reads as a timeout.

---

## Cost

KSP is slow and stateful; the code is not the expensive part. One rescue round trip is
minutes of wall clock. So the driver consumes SSE pushes where there is an event
(`wait_for_event`) and sleeps with a floor where there is not (`wait_until`, minimum 1 s,
default 3 s). A tight polling loop is the one thing here that gets genuinely expensive.

---

## Files

| Path | |
|---|---|
| `GeneKerman/Web/DebugBridge.cs` | listener, token auth, handshake file |
| `GeneKerman/Web/DebugRoutes.cs` | the routes |
| `GeneKerman/VesselTransfer.cs` | `#if`-gated accessors for `CrewedNames` / `IsCrewingSomething` |
| `GeneKerman/ContractIntegration.cs` | `#if`-gated accessors for the import-dedup set and wreck records |
| `tools/gkbridge/client.py` | transport, handshake, SSE tap, save guard |
| `tools/gkbridge/scenarios.py` | T0–T7 |
| `tools/gkrun.py` | CLI |
| `tools/assert_production_clean.sh` | the shipping gate, wired into `build.sh` |

The `#if`-gated accessors exist so assertions call the **real** private helpers. A
harness that reimplements the logic it is checking passes exactly when the real code
fails, which is the one failure mode a harness must not have. The single deliberate
exception is `GhostCandidates`, which mirrors the sweep's selection because asking the
sweep what it *would* do is asking it to do it; T5 covers that drift by asserting the
prediction and the sweep agree.
