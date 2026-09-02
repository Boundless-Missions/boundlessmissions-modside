#!/usr/bin/env bash
#
# assert_production_clean.sh — prove the shipped DLL contains no test bridge.
#
# The inverse of how the dev channel was verified: that grepped GK_DEBUG_PANEL
# strings *out* of a dev DLL to confirm they were there. This asserts they are
# absent from a production one.
#
# It matters because DebugBridge is a command channel that spawns vessels, removes
# them and edits rosters — precisely the boundary Web/ApiProxy.cs:5-9 says the
# browser bridge's allow-list exists to hold. #if GK_DEBUG_PANEL is what keeps it out
# of a shipped build, and a preprocessor gate is exactly the kind of protection that
# fails silently: delete one #if and everything still compiles, still passes every
# test, and ships a remote-control server to every player.
#
# Two halves, and the second is the one that earns the script:
#
#   1. The production DLL must contain none of the debug strings.
#   2. A dev DLL must contain ALL of them.
#
# Without (2) this script passes just as happily when the strings have been renamed,
# the file deleted, or the grep silently stopped matching — a green light that proves
# nothing. (2) is what makes the red light in (1) mean something.
#
# Note the encoding: .NET stores string literals as UTF-16LE in the #US metadata
# heap, so plain `strings` finds none of them and a naive grep reports a clean DLL
# no matter what is in it. `strings -a -el` is not an optimisation here, it is the
# difference between a real check and one that always passes.
#
# Usage:  tools/assert_production_clean.sh            # builds both channels itself
#         tools/assert_production_clean.sh --no-build # check whatever is in bin/
#
# Exit 0 = clean. Non-zero = do not publish.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/../GeneKerman"
DLL="$PROJECT_DIR/bin/GeneKerman.dll"

# Every string the debug bridge introduces. Add to this list whenever a route is
# added — a route not named here is a route this script does not defend.
MARKERS=(
  "/gk/debug/"
  "/gk/debug/state"
  "/gk/debug/roster"
  "/gk/debug/vessels"
  "/gk/debug/scenario"
  "/gk/debug/contracts"
  "/gk/debug/events"
  "/gk/debug/ping"
  "/gk/debug/jobs/"
  "/gk/debug/actions/save"
  "/gk/debug/actions/remove-vessel"
  "/gk/debug/actions/purge-ghosts"
  "/gk/debug/actions/repair-traits"
  "/gk/debug/actions/restore-traits"
  "/gk/debug/actions/poll-imports"
  "/gk/debug/actions/refresh"
  "/gk/debug/actions/scene"
  "/gk/debug/actions/fly-vessel"
  "/gk/debug/actions/spawn-wreck"
  "/gk/debug/actions/quicksend"
  "/gk/debug/actions/gift"
  "/gk/debug/actions/issue-rescue"
  "/gk/debug/actions/accept-contract"
  "/gk/debug/actions/load-save"
  "/gk/debug/actions/quicksave"
  "/gk/debug/actions/quickload"
  "/gk/debug/actions/unlink"
  "/gk/debug/saves"
  "/gk/debug/screenshot"
  "/gk/debug/ui"
  "/gk/debug/actions/crew"
  "/gk/debug/actions/spawn-test-craft"
  "debug_bridge.json"
  "X-GK-Debug-Token"
  "GK-DebugBridge"
  # DebugTestPanel.cs — the OTHER file behind `#if GK_DEBUG_PANEL`, and until now
  # named by no marker at all. The list was written route-first, so it proved the
  # debug BRIDGE was absent while saying nothing about the panel that binds F12,
  # writes GameData/GK_EVIL/pwn.dll as a traversal probe, and drives the real
  # CraftInstaller and FlagTransfer with hostile inputs. Deleting or mis-editing
  # that one `#if` shipped an F12 panel to every player and this script reported
  # clean.
  "GK_EVIL"
  "GeneKerman - Security Self-Test"
  # Metadata NAMES, findable only now that the extraction reads UTF-8 as well. These are
  # the two `#if GK_DEBUG_PANEL` regions that carry no string literal at all — the
  # `#if`-gated accessors the harness calls so its assertions run against the REAL
  # private helpers instead of a re-implementation. A marker list drawn from string
  # literals is a list of what happens to be easy to grep, not of what the gate protects.
  "DebugCrewedNames"        # VesselTransfer.cs
  "DebugApplyTrait"         # VesselTransfer.cs
  "DebugIsCrewingSomething" # VesselTransfer.cs
  "DebugImportedVessels"    # ContractIntegration.cs
  "DebugRescueWrecks"       # ContractIntegration.cs
  "DebugRescueSubmittedPids" # ContractIntegration.cs
  # ...and the type names of the three whole-file regions, which the route literals
  # already covered indirectly. Named explicitly so a rename that keeps the routes but
  # moves them into production is still caught.
  "DebugTestPanel"
)

# NOTE: GKScenarioTrace is deliberately NOT a marker. It has an `#else` stub, so the
# NAME is present in both channels and only its body is gated — asserting its absence
# would fail every production build. If that stub is ever removed, add it here.

# One string from the production surface. If this ever goes missing the DLL was not
# built, or was built wrong, and every "absent" result above is meaningless.
CANARY="/gk/actions/quicksend"

need_strings() {
  if ! command -v strings >/dev/null 2>&1; then
    echo "❌ 'strings' (binutils) is required and was not found."
    exit 2
  fi
}

# UTF-16LE, because that is how the literals are actually stored. See the header.
#
# Extracted once into a temp file rather than re-run per marker, and NOT as
# `strings … | grep -q`: under `set -o pipefail` grep -q exits at the first match,
# strings takes SIGPIPE, and the pipeline reports 141 — so every lookup returns
# "absent" and the whole script reports a clean DLL no matter what is in it. That is
# the precise failure this check exists to not have, and it was live here until the
# canary caught it.
STRINGS_CACHE=""
extract_strings() {
  STRINGS_CACHE="$(mktemp)"
  # BOTH encodings, not just UTF-16LE.
  #
  # .NET stores string LITERALS in the #US heap as UTF-16LE, which is what `-el` finds
  # and why the header says what it says. But every type, method, field and property
  # NAME lives in the #Strings heap as UTF-8 — so a `#if GK_DEBUG_PANEL` region that
  # contributes no string literal was structurally invisible to this check. Measured on
  # a dev build: DebugCrewedNames, DebugRescueWrecks, DebugTestPanel and GKScenarioTrace
  # all appear with ascii=1+ and utf16=0. Two of the eleven gated regions consist purely
  # of such accessors, so the script proved nothing about them while reporting clean.
  { strings -a -el "$DLL"; strings -a "$DLL"; } > "$STRINGS_CACHE"
}
cleanup() { [ -n "$STRINGS_CACHE" ] && rm -f "$STRINGS_CACHE"; }
trap cleanup EXIT

dll_has() {
  grep -qF -- "$1" "$STRINGS_CACHE"
}

build() {
  local channel="$1"
  echo "  building channel=$channel …"
  if ! ( cd "$PROJECT_DIR" && dotnet build -c Release --no-incremental -p:GKChannel="$channel" >/dev/null 2>&1 ); then
    echo "❌ build failed for channel=$channel"
    exit 2
  fi
}

need_strings
DO_BUILD=1
[ "${1:-}" = "--no-build" ] && DO_BUILD=0

fail=0

# ── 1. Production must be clean ──────────────────────────────────────────────
[ "$DO_BUILD" = 1 ] && build production
if [ ! -f "$DLL" ]; then echo "❌ no DLL at $DLL"; exit 2; fi
extract_strings

if ! dll_has "$CANARY"; then
  echo "❌ canary string '$CANARY' is missing from the production DLL."
  echo "   The DLL was not built, or not built as production. Nothing below is trustworthy."
  exit 2
fi

for m in "${MARKERS[@]}"; do
  if dll_has "$m"; then
    echo "❌ PRODUCTION DLL CONTAINS '$m' — the debug bridge is shipping. DO NOT PUBLISH."
    fail=1
  fi
done
[ "$fail" = 0 ] && echo "✅ production DLL: none of the ${#MARKERS[@]} debug markers present."

# ── 2. Dev must contain them, or (1) proves nothing ──────────────────────────
if [ "$DO_BUILD" = 1 ]; then
  build dev
  extract_strings
  missing=0
  for m in "${MARKERS[@]}"; do
    if ! dll_has "$m"; then
      echo "❌ dev DLL is MISSING '$m' — the check above cannot detect a leak of it."
      missing=1
    fi
  done
  if [ "$missing" = 0 ]; then
    echo "✅ dev DLL: all ${#MARKERS[@]} markers present, so the production check is meaningful."
  else
    fail=1
  fi

  # Leave bin/ holding a shippable DLL rather than the dev one. A script that
  # silently leaves a debug build in the output directory is a trap for the next
  # person who runs build.sh's packaging step.
  build production
  echo "   (bin/ rebuilt as production)"
fi

exit $fail
