"""
scenarios.py — T0–T7, the in-game pass that 3108_security_audit.md calls the binding
gate before publication.

The plan is referred to throughout that audit and is written down nowhere, so these
are derived from what it says is unverified rather than transcribed. Each scenario
names the finding it settles; if a scenario passes and the finding is still open, the
scenario is wrong and should be fixed rather than trusted.

Three rules the file obeys, and the third is the one that matters:

  - Every assertion reads state the *mod* computed (VesselTransfer.CrewOf,
    CrewedNames, the persisted queues), not a re-derivation. A harness that
    reimplements the logic it is checking passes exactly when the real code fails.

  - A step KSP cannot be driven through — flying a rendezvous, a quickload, typing a
    link code — is a `manual` step. There is no attempt to fake one.

  - **A scenario with an unperformed manual step reports SKIPPED, never PASS.** The
    entire reason this exists is that four rounds of fixes each "passed their tests"
    and three of them corrupted saves. A harness that reports green for work nobody
    did reproduces that failure with more machinery.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Callable, List, Optional

from .client import BridgeError, Instance, Refused


# ── result plumbing ──────────────────────────────────────────────────────

PASS, FAIL, SKIP = "PASS", "FAIL", "SKIP"

# ── Wait budgets ─────────────────────────────────────────────────────────
#
# A `wait_until` returns the moment its condition holds, so a generous ceiling costs
# nothing when things work — it is only reached when something is genuinely stuck.
# Erring low is what costs: on 2026-09-02 a T1 run was CORRECT and still reported FAIL,
# because the issuer's deferred hand-over removal took longer than a 300 s wait on this
# machine and the scenario gave up moments before it landed (H21). A false failure is the
# expensive kind — it comes with a fix attached, and §9 of that day's write-up is what
# chasing one costs.
#
# The Stock instances run the Windows build under Proton, two at once, where a scene
# change is seconds to minutes rather than instant. Anything gated on a scene load, a
# removal firing, or a round trip through the server gets SLOW; the rest keep their own
# tighter, better-justified values.
SLOW = 900.0        # a scene change, a deferred removal, a cross-instance round trip
SLOWER = 1200.0     # the above plus a download and a vessel spawn


@dataclass
class Check:
    name: str
    status: str
    detail: str = ""


@dataclass
class Result:
    scenario: str
    title: str
    status: str
    checks: List[Check] = field(default_factory=list)
    note: str = ""

    @property
    def failed(self) -> List[Check]:
        return [c for c in self.checks if c.status == FAIL]


class Role:
    """One *side* of a two-player case — issuer/rescuer, victim/attacker.

    A role is not an instance. With two copies of KSP running it happens to be one,
    permanently. With a single copy it is a (account, save) pair that the operator
    swaps the one install into and out of, and the scenarios must not care which.

    Two saves are **required** in single-instance mode, and this is the reason: if
    both roles share a save then the victim's kerbals and the arriving kerbals are
    the same roster entries, and there is no observation that distinguishes "the
    arrival was renamed aside" from "the originals were simply still there". T4
    would pass without testing anything.

    `ensure()` is the load-bearing part. In two-instance mode nothing can make role A
    and role B the same account — they are different processes with different tokens.
    In single-instance mode nothing *structurally* prevents sending to yourself, which
    would make the whole impersonation case vacuous and green. So every activation
    verifies the live accountId and save against what the role expects, and refuses
    rather than proceeding when they disagree.
    """

    def __init__(self, key: str, inst: Instance, save: Optional[str] = None,
                 account_id: Optional[str] = None, shared: bool = False):
        self.key = key                  # "a" / "b"
        self.inst = inst
        self.save = save                # expected save folder; learned on first use
        self.account_id = account_id    # expected account; learned on first use
        self.username: Optional[str] = None
        self.shared = shared            # True when both roles live on one install

    def describe(self) -> str:
        return (f"{self.key.upper()}={self.username or '?'} "
                f"account={self.account_id or '?'} save={self.save or '?'}")


class RoleSwapNeeded(Exception):
    """Raised when the operator must move the single install onto another role."""


class Ctx:
    """What a scenario is handed: the roles, and a way to ask for a human."""

    def __init__(self, a: Instance, b: Optional[Instance] = None,
                 interactive: bool = True, log: Callable[[str], None] = print,
                 single: bool = False, saves: Optional[dict] = None):
        saves = saves or {}
        self.single = single
        self.role_a = Role("a", a, save=saves.get("a") or a.expect_save, shared=single)
        self.role_b = (Role("b", b or a, save=saves.get("b"), shared=single)
                       if (b is not None or single) else None)
        self.interactive = interactive
        self.log = log
        self.checks: List[Check] = []
        self._skipped: Optional[str] = None
        self._active: Optional[Role] = None

    # roles ----------------------------------------------------------------

    @property
    def a(self) -> Instance:
        """The primary instance, without activating anything.

        Kept for the single-instance scenarios (T2, T3, T5, T6, T7), which only ever
        have one side and so never need a swap.
        """
        return self.role_a.inst

    @property
    def b(self) -> Optional[Instance]:
        return self.role_b.inst if self.role_b is not None else None

    def use(self, key: str) -> Optional[Instance]:
        """Make `key` the live role and return its instance, or None if it could not be.

        With two instances this is a verification. With one it is a verification plus,
        when the wrong account is loaded, a manual swap. Returning None (rather than
        raising) lets a scenario stop cleanly the same way a declined manual step does.
        """
        role = self.role_a if key == "a" else self.role_b
        if role is None:
            self.check("second role available", False, "this case needs two players")
            return None

        if not role.shared:
            # Distinct installs: nothing to swap, but still confirm the role is what
            # it claims to be — a mislabelled --a/--b is otherwise invisible.
            if not self._verify(role, prompt_on_mismatch=False):
                return None
            self._active = role
            return role.inst

        if self._active is role and self._verify(role, prompt_on_mismatch=False):
            return role.inst

        # Learn the role on first contact rather than demanding it up front: whoever
        # is linked right now is a perfectly good definition of the first role.
        if role.account_id is None:
            st = role.inst.state()
            ident = st.get("identity") or {}
            other = self.role_b if role is self.role_a else self.role_a
            live_is_other = (other is not None and other.account_id is not None
                             and ident.get("accountId") == other.account_id)
            # Adoptable only if the live save is also this role's save — otherwise the
            # account happens to be right and the save is not, which is a swap, not an
            # adoption.
            if role.save and st.get("save") != role.save:
                live_is_other = True
            if not live_is_other:
                role.account_id = ident.get("accountId")
                role.username = ident.get("username")
                role.save = role.save or st.get("save")
                role.inst.expect_save = role.save
                self.note(f"role {key.upper()} adopted from the live client", role.describe())
                self._active = role
                return role.inst

        if not self._swap_to(role):
            return None
        self._active = role
        return role.inst

    def _swap_to(self, role: Role) -> bool:
        """Move the one install onto `role`. Two of the three steps are automatic.

        The save load and the unlink go over the bridge. What is left is the half that
        genuinely cannot be automated from here — linking needs a 6-digit code typed in
        Discord, which is a human action by design and not something a loopback bridge
        should be able to perform even if it could reach the API to try.
        """
        want_save = role.save
        want_who = role.username or role.account_id or "the other account"

        if want_save:
            try:
                self.log(f"      … loading save {want_save!r}")
                role.inst.load_save(want_save)
            except (BridgeError, Refused) as exc:
                self.check(f"load save {want_save!r} for role {role.key.upper()}", False, str(exc))
                return False

        # Only unlink when the live account is actually the wrong one. Unlinking is not
        # free — the token is dropped, not parked (sessions.cfg keys by server, not by
        # account), so a needless one costs the operator a 6-digit code.
        try:
            live = (role.inst.state().get("identity") or {}).get("accountId")
        except BridgeError as exc:
            self.check(f"read identity for role {role.key.upper()}", False, str(exc))
            return False

        if role.account_id and live == role.account_id:
            self.note(f"role {role.key.upper()} already linked", "no relink needed")
            return self._verify(role, prompt_on_mismatch=True)

        try:
            role.inst.unlink()
        except (BridgeError, Refused) as exc:
            self.check(f"unlink before switching to role {role.key.upper()}", False, str(exc))
            return False

        if not self.manual(
            f"Link this install as {want_who}: open the mod's link screen in KSP, then "
            "run the link command in Discord and type the 6-digit code.\n"
            "      (The save is already loaded and the previous account is unlinked.)"
        ):
            return False
        return self._verify(role, prompt_on_mismatch=True)

    def _verify(self, role: Role, prompt_on_mismatch: bool) -> bool:
        """Confirm the live client really is this role. The check a serialized run
        lives or dies on: a forgotten swap turns an attacker→victim send into a
        self-send, which succeeds, changes nothing, and passes every later assertion."""
        try:
            st = role.inst.state()
        except BridgeError as exc:
            self.check(f"role {role.key.upper()} reachable", False, str(exc))
            return False
        ident = st.get("identity") or {}
        other = self.role_b if role is self.role_a else self.role_a

        if role.account_id is None:
            # Adopting whatever is live is right for the FIRST role and catastrophic for
            # the second: if the operator confirms a swap that did not happen, the live
            # client is still the other role, and adopting it here would define both
            # roles as one account. Every later assertion then passes — the send goes to
            # the sender, nothing collides, nothing is adopted — and T4 reports green
            # having tested nothing at all. So an id already claimed by the other role is
            # refused rather than adopted.
            live_id = ident.get("accountId")
            if other is not None and other.account_id and live_id == other.account_id:
                self.check(f"role {role.key.upper()} is a different account from "
                           f"{other.key.upper()}", False,
                           f"the live client is still {live_id!r} — the swap did not happen. "
                           "Continuing would make this a self-send and pass vacuously.")
                return False
            role.account_id = live_id
            role.username = ident.get("username")
        if role.save is None:
            role.save = st.get("save")
        role.inst.expect_save = role.save

        acct_ok = bool(ident.get("accountId")) and ident.get("accountId") == role.account_id
        save_ok = st.get("save") == role.save
        if acct_ok and save_ok:
            return True

        detail = (f"live account={ident.get('accountId')!r} save={st.get('save')!r}, "
                  f"role {role.key.upper()} wants account={role.account_id!r} save={role.save!r}")
        if prompt_on_mismatch:
            self.check(f"role {role.key.upper()} is loaded after the swap", False, detail)
        else:
            self.check(f"role {role.key.upper()} is the account/save it claims to be",
                       False, detail)
        return False

    def roles_are_distinct(self) -> bool:
        """Both roles known, and genuinely different accounts AND different saves."""
        a, b = self.role_a, self.role_b
        if b is None or not a.account_id or not b.account_id:
            return False
        return a.account_id != b.account_id and (a.save != b.save if a.shared else True)

    # assertions -----------------------------------------------------------

    def check(self, name: str, ok: bool, detail: str = "") -> bool:
        self.checks.append(Check(name, PASS if ok else FAIL, detail))
        self.log(f"    {'✓' if ok else '✗'} {name}" + (f"  — {detail}" if detail and not ok else ""))
        return ok

    def note(self, name: str, detail: str) -> None:
        self.checks.append(Check(name, PASS, detail))
        self.log(f"    · {name}: {detail}")

    def manual(self, instruction: str) -> bool:
        """Ask the operator to do something in the game.

        Returns False in non-interactive mode, which marks the whole scenario
        SKIPPED. It does not fall through to the assertions: they would be read
        against a game in which the step never happened, and would mostly pass.
        """
        if not self.interactive:
            self._skipped = instruction
            self.log(f"    ⏸ MANUAL STEP NOT PERFORMED (non-interactive): {instruction}")
            return False
        self.log("")
        self.log(f"    ⏸ MANUAL: {instruction}")
        try:
            answer = input("      press Enter when done, or 's' to skip this scenario: ").strip().lower()
        except EOFError:
            self._skipped = instruction
            return False
        if answer == "s":
            self._skipped = instruction
            return False
        return True

    @property
    def skipped(self) -> Optional[str]:
        return self._skipped


# ── shared reads ─────────────────────────────────────────────────────────

def roster_of(state: dict) -> List[dict]:
    return state.get("roster") or []


def by_name(state: dict, name: str) -> Optional[dict]:
    for k in roster_of(state):
        if k.get("name") == name:
            return k
    return None


def borrowed_names(state: dict) -> List[str]:
    return [k["name"] for k in roster_of(state) if k.get("borrowed")]


def vessel_by_pid(state: dict, pid: str) -> Optional[dict]:
    for v in state.get("vessels") or []:
        if v.get("pid") == pid:
            return v
    return None


def assert_roster_healthy(ctx: Ctx, inst: Instance, label: str) -> dict:
    """The three standing invariants. Asserted at the start AND end of every
    scenario, because most of these bugs are only visible as residue.

    Every one of them is trivially true of an empty roster, so the readability check
    comes first and fails rather than passing quietly. Observed live: the game held
    four kerbals, the vessel list and CrewedNames() both saw them, and the roster read
    returned zero — which would have scored "no ghosts, no broken traits, no
    duplicates" as three passes.
    """
    st = inst.state()
    if not st.get("rosterOk", True):
        ctx.check(f"{label}: roster is readable", False,
                  f"{st.get('rosterError')!r} — every roster assertion below would pass "
                  "on nothing, so they are not run")
        return st
    if st.get("gameLoaded") and not roster_of(st) and (st.get("crew") or {}).get("crewedNow"):
        # Belt and braces for a failure that reports no error: crew aboard vessels but
        # an empty roster is not a state a real save can be in.
        ctx.check(f"{label}: roster is readable", False,
                  "roster came back empty while CrewedNames() lists crew aboard vessels — "
                  "the read is broken, not the save")
        return st
    crew = st.get("crew") or {}
    ghosts = crew.get("ghostCandidates") or []
    broken = crew.get("brokenTraits") or []
    ctx.check(f"{label}: no borrowed ghost kerbals", not ghosts, f"found {ghosts}")
    ctx.check(f"{label}: every roster trait resolves", not broken, f"unresolvable: {broken}")
    dupes = _duplicate_names(st)
    ctx.check(f"{label}: no duplicate roster names", not dupes, f"duplicates: {dupes}")
    return st


def _duplicate_names(state: dict) -> List[str]:
    seen, dupes = set(), []
    for k in roster_of(state):
        n = k.get("name")
        if n in seen:
            dupes.append(n)
        seen.add(n)
    return dupes


def _crew_aboard_anything(state: dict, name: str) -> Optional[str]:
    for v in state.get("vessels") or []:
        if name in (v.get("crew") or []):
            return v.get("name")
    return None


# ══ T0 — bring-up ════════════════════════════════════════════════════════

def t0_bringup(ctx: Ctx) -> None:
    """T0 — the baseline every other scenario is asserted against.

    Worth being a scenario rather than a precondition because three of its checks are
    findings in their own right: a save that already contains ghosts or an
    unresolvable trait makes T5 and T7 pass for the wrong reason, an identity that is
    not linked makes every transfer scenario silently a no-op, and two roles that turn
    out to be the same account make T1 and T4 vacuous.
    """
    seen = []
    for key in ("a", "b"):
        role = ctx.role_a if key == "a" else ctx.role_b
        if role is None:
            continue
        inst = ctx.use(key)
        if inst is None:
            return
        st = inst.state()
        label = f"role {key.upper()}"
        ctx.check(f"{label}: bridge answers /state", bool(st.get("modVersion")))
        ctx.check(f"{label}: a save is loaded", bool(st.get("gameLoaded")))
        ctx.check(f"{label}: save is a throwaway ('{inst.expect_save}')",
                  st.get("save") == inst.expect_save, f"loaded '{st.get('save')}'")
        ident = st.get("identity") or {}
        ctx.check(f"{label}: linked to an account", bool(ident.get("linked")))
        ctx.check(f"{label}: reports an account id",
                  bool(ident.get("accountId")),
                  "empty accountId — every ownership decision falls back to the display "
                  "name, which is RM1 itself")
        ctx.check(f"{label}: contract scenario module present",
                  bool((st.get("scenario") or {}).get("present")),
                  "GKContractScenario missing: every queue-backed guard silently no-ops")
        ctx.note(f"{label}: identity",
                 f"username={ident.get('username')!r} accountId={ident.get('accountId')!r} "
                 f"save={st.get('save')!r} scene={st.get('scene')} "
                 f"vessels={len(st.get('vessels') or [])} roster={len(roster_of(st))}")
        assert_roster_healthy(ctx, inst, label)
        seen.append((key, ident.get("accountId"), st.get("save")))

    if ctx.role_b is not None:
        ctx.check("the two roles are different accounts",
                  ctx.role_a.account_id and ctx.role_b.account_id and
                  ctx.role_a.account_id != ctx.role_b.account_id,
                  f"{ctx.role_a.account_id} vs {ctx.role_b.account_id} — a two-player test "
                  "against one account proves nothing")
        if ctx.single:
            # Only in single-instance mode is this possible to get wrong. With two
            # installs the saves are in different directories by construction.
            ctx.check("the two roles use different saves",
                      ctx.role_a.save and ctx.role_b.save and ctx.role_a.save != ctx.role_b.save,
                      f"both roles on save {ctx.role_a.save!r} — the victim's kerbals and the "
                      "arriving kerbals would be the same roster entries, so no observation "
                      "can tell adoption from the originals being present. T4 cannot work.")
        ctx.note("roles", f"{ctx.role_a.describe()} | {ctx.role_b.describe()}")


# ══ T1 — rescue round trip ═══════════════════════════════════════════════

def t1_rescue_round_trip(ctx: Ctx) -> None:
    """T1 — issuer hands a wreck over, rescuer collects, crew come home.

    The full crew path end to end: the ownership tag applied on the way out and
    stripped on the way back, which is where three of the four repair rounds went
    wrong.

    Serialized deliberately, so it reads identically on one install or two: the
    server holds the import queue, so the issuer never has to be running while the
    rescuer flies. That decoupling is what makes single-instance testing sound rather
    than a compromise.
    """
    if ctx.role_b is None:
        ctx.check("two roles available", False, "T1 needs two players")
        return

    # ── issuer side: create the rescue ──
    issuer = ctx.use("a")
    if issuer is None:
        return
    issuer.guard_save()
    issuer.backup_save("t1")
    pre_i = assert_roster_healthy(ctx, issuer, "issuer/before")
    issuer_crew_before = {k["name"] for k in roster_of(pre_i)}
    # A *delta*, not an absolute. A save can legitimately already hold borrowed crew —
    # a rescuer mid-rescue always does — so asserting the issuer ends with none makes
    # the scenario unrunnable on any save that has been used before, and fails for a
    # reason that has nothing to do with the hand-over under test.
    borrowed_before = set(borrowed_names(pre_i))
    if borrowed_before:
        ctx.note("issuer already holds borrowed crew", str(sorted(borrowed_before)))

    # Issue and accept were manual only because nothing drove them, not because they
    # are what T1 tests. Everything this scenario actually asserts about the crew path
    # — the ownership tag, the freeze record, the sweep's treatment of frozen crew —
    # sits *after* them, so leaving them manual left those assertions unrun.
    st_i = issuer.state()
    if not ctx.check("the issuer is flying the vessel that is to become the wreck",
                     st_i.get("scene") == "FLIGHT" and st_i.get("activeVessel"),
                     f"scene={st_i.get('scene')} — enter flight on a crewed craft first"):
        return
    wreck = st_i["activeVessel"]
    aboard = [k["name"] for k in roster_of(st_i) if k.get("aboardPid") == wreck.get("pid")]
    ctx.note("stranded vessel", f"{wreck.get('name')} crew={aboard}")
    if not ctx.check("the stranded vessel has crew aboard", bool(aboard),
                     "a crewless rescue makes every crew assertion true of nothing"):
        return

    # The contractor is role B, whose account is not learned until it is activated —
    # which happens after this. Read it off the live instance instead, and refuse on an
    # empty one rather than posting a blank contractor_id: an identity that is empty
    # because a profile fetch failed takes the "stranger" branch everywhere, which is
    # the failure mode §3.3 and H8 both exist for.
    b_ident = ctx.b.ensure_identity()
    contractor_id = b_ident.get("accountId") or ""

    # Which inbound contracts the rescuer already had, so the one this run creates can
    # be identified by difference. Picking "the first offer in the inbox" instead is
    # only correct on a pristine account, and a scenario that silently adopts a
    # leftover offer from an earlier run reports on a hand-over it did not perform.
    ctx.b.act("/gk/debug/actions/refresh")
    try:
        pre_offers = {c.get("contract_id")
                      for c in ((ctx.b.state().get("client") or {}).get("contracts") or [])
                      if not c.get("is_outgoing")}
    except Exception:
        pre_offers = set()
    ctx.note("rescuer's existing inbound contracts", str(len(pre_offers)))
    ctx.role_b.account_id = ctx.role_b.account_id or contractor_id
    ctx.role_b.username = ctx.role_b.username or b_ident.get("username")
    if not ctx.check("the rescuer's account id is known", bool(contractor_id),
                     "role B has an empty identity — every ownership decision would "
                     "take the stranger branch and the scenario would test nothing"):
        return

    # No target orbit and no landing site: "anywhere on Kerbin". The delivery target is
    # not what T1 tests, and a tight one would make it fail on flying instead.
    res = issuer.issue_rescue(contractor_id,
                              contractor_name=b_ident.get("username") or "",
                              mission="Bridge test rescue - bring the crew home.")
    # await_job raises on a failed job, so reaching here is already the outcome; the
    # check states it so the run reads as a ledger rather than an absence of errors.
    ctx.check("the rescue was issued", res.get("state") == "done",
              f"issue said: {res.get('message')}")
    ctx.note("issue result", str(res.get("message")))

    # The vessel is not removed on the spot and must not be: it is the one being flown,
    # and KSP cannot delete the hull out from under the player — the same deferral T2
    # asserts. In play the next scene change is the player's own; here it has to be
    # made, or the wait below times out on behaviour that is correct.
    ctx.check("the handed-over vessel was not deleted under the player",
              vessel_by_pid(issuer.state(), wreck["pid"]) is not None,
              "the hull vanished mid-flight")
    ctx.log("      … leaving flight so the deferred removal can run")
    issuer.change_scene("SPACECENTER")

    # The permanence gate's real consequence, asserted rather than assumed: issuing a
    # rescue destroys the issuer's vessel. If it is still there, the wreck the rescuer
    # is about to spawn is a *duplicate*, and the whole ownership question is moot.
    st_after = issuer.wait_until(lambda x: vessel_by_pid(x, wreck["pid"]) is None,
                                 "the issuer's vessel to be handed over", timeout=SLOW)
    ctx.check("the issuer's vessel left their save",
              vessel_by_pid(st_after, wreck["pid"]) is None,
              "the issuer kept the ship they handed over — the rescuer's wreck is a copy")

    # ── rescuer side: accept, spawn, fly, submit ──
    rescuer = ctx.use("b")
    if rescuer is None:
        return
    rescuer.guard_save()
    rescuer.backup_save("t1")
    # Cheap, and the one mistake that would make everything below meaningless.
    ctx.check("issuer and rescuer are different accounts", ctx.roles_are_distinct(),
              f"{ctx.role_a.describe()} vs {ctx.role_b.describe()}")
    assert_roster_healthy(ctx, rescuer, "rescuer/before")

    # The distinction between "no contract" and "list not fetched" is load-bearing
    # (HomeboundCrewFor), so refresh and wait rather than assume.
    rescuer.act("/gk/debug/actions/refresh")
    rc = rescuer.wait_until(
        lambda st: (st.get("client") or {}).get("contractsLoaded")
                   and any(c.get("contract_id") not in pre_offers and not c.get("is_outgoing")
                           for c in ((st.get("client") or {}).get("contracts") or [])),
        "the offered rescue to reach the rescuer", timeout=SLOW)
    offered = [c for c in ((rc.get("client") or {}).get("contracts") or [])
               if not c.get("is_outgoing")
               and c.get("status") in ("pending", "offered")
               and c.get("contract_id") not in pre_offers]
    if offered:
        acc = rescuer.accept_contract(offered[0].get("contract_id"),
                                      ctx.role_a.username or "")
        ctx.check("the rescue was accepted", acc.get("state") == "done",
                  f"accept said: {acc.get('message')}")
        rescuer.act("/gk/debug/actions/refresh")
        rc = rescuer.wait_until(
            lambda st: any(c.get("status") == "active" and not c.get("is_outgoing")
                           and c.get("contract_id") not in pre_offers
                           for c in ((st.get("client") or {}).get("contracts") or [])),
            "the accepted rescue to go active", timeout=SLOW)

    active = [c for c in ((rc.get("client") or {}).get("contracts") or [])
              if c.get("status") == "active" and not c.get("is_outgoing")
              and c.get("contract_id") not in pre_offers]
    if not ctx.check("rescuer holds an active inbound contract", bool(active),
                     "nothing to rescue — was the contract accepted?"):
        return
    contract_id = active[0].get("contract_id")
    stranded = [str(k) for k in (active[0].get("rescue_kerbals") or [])]
    ctx.note("contract", f"{contract_id} stranded={stranded}")

    # Through ClientState, so the dedup, the orbit-epoch freeze and the
    # emergency-freeze registration all happen exactly as in normal play.
    rescuer.spawn_wreck(contract_id)
    st = rescuer.wait_until(
        lambda x: any(i.get("contractId") == contract_id
                      for i in ((x.get("scenario") or {}).get("immunities") or [])),
        "the wreck to spawn and register an emergency-freeze record", timeout=SLOWER)

    rec = next(i for i in st["scenario"]["immunities"] if i.get("contractId") == contract_id)
    ctx.check("wreck exists in the rescuer's save", bool(rec.get("vesselStillPresent")))
    frozen = [c.get("name") for c in (rec.get("crew") or [])]
    ctx.check("stranded crew are held by a freeze record", bool(frozen), f"record crew={frozen}")

    # The tag is the whole ownership mechanism: these are the issuer's kerbals sitting
    # in the rescuer's roster, and they must say so.
    tagged = borrowed_names(st)
    ctx.check("stranded crew carry the issuer's ownership tag",
              bool(frozen) and all(f in tagged for f in frozen),
              f"frozen={frozen} borrowed={tagged}")

    # A frozen kerbal is parked at Dead on purpose. If the sweep would take one, the
    # crew are gone before the rescuer ever reaches them.
    ghosts = (st.get("crew") or {}).get("ghostCandidates") or []
    ctx.check("the ghost sweep would not take the frozen crew",
              not set(ghosts) & set(frozen),
              f"sweep would remove frozen kerbals: {sorted(set(ghosts) & set(frozen))}")

    if not ctx.manual(
        "As the RESCUER: fly to the wreck, collect the stranded crew (they thaw at 10 km "
        "automatically), deliver them to the contract's target, and submit."
    ):
        return

    # ── back to the issuer: accept and receive ──
    issuer = ctx.use("a")
    if issuer is None:
        return
    if not ctx.manual(
        "As the ISSUER: accept the submission (sidebar → Contracts), then stay in the "
        "Space Center so the import can land."
    ):
        return

    issuer.act("/gk/debug/actions/poll-imports")
    post_i = issuer.wait_until(
        lambda x: all(by_name(x, n) for n in stranded) if stranded else True,
        "the stranded crew to arrive home in the issuer's roster", timeout=SLOWER)

    # The point of the whole exercise: coming home strips the tag. A returning kerbal
    # still wearing one is what PurgeBorrowedGhostCrew later deletes.
    for name in stranded:
        k = by_name(post_i, name)
        ctx.check(f"'{name}' is home under its own name", k is not None,
                  "not in the roster under the original name — the tag was not stripped, "
                  "or the arrival was renamed aside")
        if k:
            ctx.check(f"'{name}' is not marked borrowed", not k.get("borrowed"),
                      f"still tagged: {k.get('name')}")
    gained = set(borrowed_names(post_i)) - borrowed_before
    ctx.check("issuer gained no borrowed kerbals", not gained,
              f"newly borrowed after return: {sorted(gained)}")
    ctx.check("issuer's pre-existing crew all survived",
              issuer_crew_before <= {k["name"] for k in roster_of(post_i)},
              f"lost: {sorted(issuer_crew_before - {k['name'] for k in roster_of(post_i)})}")
    assert_roster_healthy(ctx, issuer, "issuer/after")


# ══ T2 — deferred removal ════════════════════════════════════════════════

def t2_deferred_removal(ctx: Ctx) -> None:
    """T2 — a removal queued against the vessel being flown must defer, persist,
    and fire on the next safe scene.

    The persisted queue is what makes a hand-over survive the player quitting
    mid-flight, and it is the mechanism whose entries, left unsettled, become the
    ghosts T5 is about.
    """
    inst = ctx.a
    inst.guard_save()
    inst.backup_save("t2")

    st = inst.state()
    if not ctx.check("in flight with an active vessel",
                     st.get("scene") == "FLIGHT" and st.get("activeVessel"),
                     f"scene={st.get('scene')} — load a flight first"):
        return

    av = st["activeVessel"]
    pid, name = av["pid"], av["name"]
    ctx.note("target", f"{name} ({pid}) crew={av.get('crew')}")

    # StaysInRoster: this is a mechanism test, not a crew test, and nobody's
    # kerbals should die to prove the queue works.
    out = inst.post("/gk/debug/actions/remove-vessel",
                    {"pid": pid, "crew_fate": "StaysInRoster"})
    ctx.check("the removal was accepted and recorded", out.get("queued") is True,
              f"response={out}")
    # Deferral is the expected outcome for the vessel being flown — KSP cannot delete
    # the hull out from under the player — so "not removed yet" is a pass here, and an
    # immediate removal would be the surprising answer.
    ctx.check("the focused vessel was not removed on the spot",
              out.get("removedNow") is False,
              "the vessel being flown was deleted immediately")

    sc = inst.scenario()
    queued = [r for r in (sc.get("pendingRemovals") or []) if r.get("pid") == pid]
    ctx.check("the removal is queued", bool(queued))
    if queued:
        ctx.check("the queued entry still sees the hull", bool(queued[0].get("vesselStillPresent")))
        ctx.note("queued fate", str(queued[0].get("crewFate")))

    ctx.log("      … switching to the Space Center (this is a real scene load)")
    inst.change_scene("SPACECENTER")

    st2 = inst.wait_until(lambda s: not vessel_by_pid(s, pid),
                          "the deferred removal to fire", timeout=180)
    ctx.check("the vessel is gone from the save", vessel_by_pid(st2, pid) is None)
    still = [r for r in ((st2.get("scenario") or {}).get("pendingRemovals") or [])
             if r.get("pid") == pid]
    ctx.check("the queue entry was consumed", not still,
              "entry survives its own removal — it will re-fire every scene load")
    assert_roster_healthy(ctx, inst, "after removal")


# ══ T3 — quickload rollback ══════════════════════════════════════════════

def t3_quickload_rollback(ctx: Ctx) -> None:
    """T3 — a quickload that rolls a removal back must not leave residue, and the
    accepted-gift echo must re-assert the removal.

    Two phases, and the first no longer needs a second account or a keystroke.

    **Phase 1 (automated).** Queue a removal against the vessel being flown — which
    defers, because KSP cannot delete the hull out from under the player — then
    quickload to a moment before it was queued. The ship must come back and the
    queue entry must be *gone*: a removal that survives the rollback deletes a ship
    the player has just undone the hand-over of, and it would re-fire on every
    scene load thereafter. That is what MergeCarriedRemovals' future-UT drop is
    for, and it is the half of the §3.2 fix nothing else exercises.

    Note what phase 1 does NOT prove: the server still holds the pending gift, so
    the correct end state is the sender keeping the ship *and* the recipient being
    able to accept it later. Reconciling those two is phase 2's job.

    **Phase 2 (manual).** A quicksend has no contract for ReconcileRescueVessels to
    re-derive intent from, which is why the accept path echoes vessel_pid at all
    (MaybeHandleGiftAccepted). Needs the other account, so it reports SKIP when the
    step is not performed rather than passing on phase 1 alone.
    """
    inst = ctx.a
    inst.guard_save()
    inst.backup_save("t3")

    st = inst.state()
    if not ctx.check("a flight is loaded",
                     st.get("scene") == "FLIGHT" and st.get("activeVessel"),
                     f"scene={st.get('scene')} — load a flight first"):
        return

    av = st["activeVessel"]
    pid, name = av["pid"], av["name"]
    ctx.note("target", f"{name} ({pid})")

    inst.quicksave("gk_t3")
    ut_saved = (inst.state().get("scenario") or {}).get("ut")

    # The drop rule is `queued_ut > loaded_ut + 1.0`, so a quicksave and a queue one
    # second apart land inside the guard band and the scenario would be measuring its
    # own timing rather than the mod. Wait for a gap several times the band, and
    # assert it — a test that cannot see the clock it depends on is not a test.
    ut_now = ut_saved
    deadline = time.time() + 90
    while (ut_now or 0) - (ut_saved or 0) < 10.0 and time.time() < deadline:
        time.sleep(2.0)
        ut_now = (inst.state().get("scenario") or {}).get("ut")
    gap = (ut_now or 0) - (ut_saved or 0)
    ctx.note("UT gap between quicksave and queue", f"{gap:.1f} s")
    ctx.check("the quicksave is far enough in the past to be outside the guard band",
              gap > 1.0,
              f"only {gap:.1f} s of game time elapsed — the result would be timing, "
              "not behaviour")

    # StaysInRoster: a mechanism test should not cost anybody their crew.
    out = inst.post("/gk/debug/actions/remove-vessel",
                    {"pid": pid, "crew_fate": "StaysInRoster"})
    ctx.check("the removal was accepted and recorded", out.get("queued") is True,
              f"response={out}")
    ctx.check("the focused vessel was not removed on the spot",
              out.get("removedNow") is False,
              "the vessel being flown was deleted immediately")

    sc = inst.scenario()
    ctx.check("the removal is queued before the rollback",
              any(r.get("pid") == pid for r in (sc.get("pendingRemovals") or [])),
              "nothing to roll back — the rest of this scenario would pass vacuously")

    ctx.log("      … quickloading (this is a real scene load)")
    inst.quickload("gk_t3")

    st2 = inst.wait_until(lambda s: s.get("scene") in ("FLIGHT", "SPACECENTER"),
                          "the quickload to settle", timeout=300)
    ctx.check("the vessel is back after the rollback",
              vessel_by_pid(st2, pid) is not None,
              "the rollback did not restore the ship the removal was queued against")

    still = [r for r in ((st2.get("scenario") or {}).get("pendingRemovals") or [])
             if r.get("pid") == pid]
    ctx.check("the rolled-back removal did NOT survive the quickload",
              not still,
              "the queue entry outlived the save state that created it — it will "
              "delete a ship the player has just rolled the hand-over back on, and "
              "re-fire on every scene load until it does")
    assert_roster_healthy(ctx, inst, "after rollback")

    # ── phase 2 ──────────────────────────────────────────────────────────
    #
    # No longer manual. It was, because the accept needed the other account and the
    # bridge could not reach it; `/gk/debug/actions/gift` closed that, so phase 2 now
    # performs its own quicksend cycle end to end instead of asking the operator to
    # have done one beforehand. That matters beyond convenience: the old prompt said
    # "skip unless you actually performed a quicksend of it before this run", which is
    # a precondition nobody could see from the result — a SKIP and a genuinely-unrun
    # phase looked identical.
    #
    # The case: a quicksend has no contract for ReconcileRescueVessels to re-derive
    # intent from, so if a quickload rolls the sender's removal back while the server
    # still holds the offer, nothing re-queues it. The accept echoes vessel_pid for
    # exactly this, and MaybeHandleGiftAccepted is what consumes the echo. Fail here
    # and the sender keeps a ship the recipient has also been given.
    if ctx.role_b is None:
        ctx.note("phase 2", "skipped — needs a second account")
        return

    friend = ctx.use("b")
    if friend is None:
        return
    f_ident = friend.state().get("identity") or {}
    f_account = f_ident.get("accountId")
    if not ctx.check("the recipient's account id is known", bool(f_account),
                     "an empty identity would make the send a no-op and phase 2 vacuous"):
        return

    sender = ctx.use("a")
    if sender is None:
        return
    if not ctx.check("sender and recipient are different accounts",
                     ctx.roles_are_distinct(),
                     "a self-send would pass every assertion below without exercising "
                     "the echo at all"):
        return

    # Back in flight on the target, and quicksave BEFORE the send so the rollback has
    # somewhere to land.
    st_p2 = sender.state()
    if st_p2.get("scene") != "FLIGHT":
        sender.fly_vessel(pid)
    sender.quicksave("gk_t3p2")
    ut_saved2 = (sender.state().get("scenario") or {}).get("ut")
    ut_now2, deadline2 = ut_saved2, time.time() + 90
    while (ut_now2 or 0) - (ut_saved2 or 0) < 10.0 and time.time() < deadline2:
        time.sleep(2.0)
        ut_now2 = (sender.state().get("scenario") or {}).get("ut")
    ctx.check("the phase-2 quicksave is outside the guard band",
              (ut_now2 or 0) - (ut_saved2 or 0) > 1.0,
              f"only {(ut_now2 or 0) - (ut_saved2 or 0):.1f} s of game time elapsed")

    try:
        sent = sender.quicksend(f_account, kind="vessel",
                                recipient_name=ctx.role_b.username or "")
    except Refused as exc:
        ctx.check("the sender could hand the vessel over", False, str(exc))
        return
    ctx.note("send", sent.get("message") or "sent")
    ctx.check("the send queued a removal against the flown vessel",
              any(r.get("pid") == pid
                  for r in ((sender.scenario() or {}).get("pendingRemovals") or [])),
              "nothing queued — the rollback below would have nothing to roll back")

    ctx.log("      … quickloading to before the send (a real scene load)")
    sender.quickload("gk_t3p2")
    st_rb = sender.wait_until(lambda s: s.get("scene") in ("FLIGHT", "SPACECENTER"),
                              "the quickload to settle", timeout=SLOW)
    # If the ship is not back, the rollback window was missed — the hand-over's own
    # ReturnToSpaceCenterRoutine saves and leaves flight 1.5 s after the send, and the
    # removal then executes at the Space Center before the quickload lands. Everything
    # after this point would then be read against a vessel that is gone for an ordinary
    # reason, and the re-assertion check below would pass without the echo doing
    # anything. Measured 2026-09-02: exactly that, `re-queueing` logged zero times while
    # the phase reported green (H22).
    #
    # So this is a stop, not a failed assertion among others: a phase that cannot
    # observe what it exists to observe reports INCONCLUSIVE rather than PASS, which is
    # the same rule as "a skipped scenario is not a pass".
    back = vessel_by_pid(st_rb, pid) is not None
    ctx.check("the vessel is back after the rollback", back,
              "the rollback did not restore the handed-over ship — most likely the "
              "auto-return removed it first, so this phase cannot test the echo")
    ctx.check("the rolled-back removal did NOT survive the quickload",
              not [r for r in ((st_rb.get("scenario") or {}).get("pendingRemovals") or [])
                   if r.get("pid") == pid],
              "the queue entry outlived the save that created it")
    if not back:
        ctx.note("phase 2 inconclusive",
                 "the ship was already gone before the accept, so the re-assertion "
                 "below would be vacuous — re-run with the quickload closer to the send")
        return

    # The server still holds the offer. Accepting it is what must bring the removal back.
    friend = ctx.use("b")
    if friend is None:
        return
    try:
        friend.decide_gift("accept")
    except Refused as exc:
        ctx.check("the recipient could accept the offer", False, str(exc))
        return

    sender = ctx.use("a")
    if sender is None:
        return
    st3 = sender.wait_until(
        lambda s: any(r.get("pid") == pid
                      for r in ((s.get("scenario") or {}).get("pendingRemovals") or []))
                  or vessel_by_pid(s, pid) is None,
        "the removal to be re-asserted after the rollback", timeout=SLOW)
    # Deliberately NOT "...or the vessel is gone". The ship being absent is exactly what
    # an unrelated removal leaves behind, and accepting it as evidence is what made this
    # phase report green while `re-queueing` was logged zero times. The queue entry is
    # the only thing that shows the ECHO acted; the vessel having left afterwards is a
    # consequence, not proof. The guard above guarantees it was present a moment ago.
    requeued = any(r.get("pid") == pid
                   for r in ((st3.get("scenario") or {}).get("pendingRemovals") or []))
    gone = vessel_by_pid(st3, pid) is None
    ctx.check("the rolled-back removal was re-asserted on accept", requeued or gone,
              "the sender kept a ship the recipient also has — the duplicate case")
    ctx.check("...and it was the accept echo that did it, not an unrelated removal",
              requeued,
              "the ship left without the removal ever being re-queued: the echo did not "
              "fire, and this phase would otherwise have passed on a coincidence")
    assert_roster_healthy(ctx, sender, "after re-assertion")


# ══ T4 — impersonation (the critical one) ════════════════════════════════

def t4_impersonation(ctx: Ctx) -> None:
    """T4 — RM1. The case the whole account-id identity change exists for, and the one
    the audit names as unverified.

    An attacker sets their Discord display name to the victim's and re-links. Under the
    old model the victim's client computed `comingHome` from a name compare, took the
    branch that skips the roster-collision check, and adopted the victim's own kerbals
    onto the arriving vessel — the next hand-over then deleted them permanently. It
    also fires with **no attacker at all**: two players who share a nickname corrupt
    each other's rosters on any craft exchange.

    The assertion is not "the send was refused" — a send from a stranger is perfectly
    legal. It is that the arrivals are renamed ASIDE, and the victim's own kerbals are
    untouched and still aboard whatever they were aboard.

    On one install this runs victim-first: read the victim's roster, swap to the
    attacker, send, swap back, receive. The send survives the swap because the server
    holds the import queue.
    """
    if ctx.role_b is None:
        ctx.check("two roles available", False, "T4 needs two players")
        return

    # ── victim: record what must survive ──
    victim = ctx.use("a")
    if victim is None:
        return
    victim.guard_save()
    victim.backup_save("t4")
    v_before = assert_roster_healthy(ctx, victim, "victim/before")
    v_ident = v_before.get("identity") or {}
    v_name, v_account = v_ident.get("username"), v_ident.get("accountId")
    victim_crew = {k["name"] for k in roster_of(v_before)}
    ctx.note("victim", f"username={v_name!r} accountId={v_account!r} roster={len(victim_crew)}")

    # Prefer kerbals that are demonstrably busy — aboard something. RM3 is specifically
    # that a busy roster entry must never be adopted, so those are the strongest targets.
    busy = [(k["name"], _crew_aboard_anything(v_before, k["name"]))
            for k in roster_of(v_before)
            if _crew_aboard_anything(v_before, k["name"])]
    ctx.note("victim crew currently aboard something", str(busy[:5]) or "none")
    target_names = [n for n, _ in busy[:2]] or sorted(victim_crew)[:2]
    if not ctx.check("there are victim kerbals to impersonate", bool(target_names),
                     "the victim's roster is empty — nothing to collide with"):
        return
    ctx.note("names the attacker will forge", str(target_names))
    v_vessels_before = {v["pid"] for v in (v_before.get("vessels") or [])}
    v_count_before = len(v_before.get("vessels") or [])

    # ── attacker: forge the name, send ──
    attacker = ctx.use("b")
    if attacker is None:
        return
    attacker.guard_save()
    attacker.backup_save("t4")

    if not ctx.manual(
        f"As the ATTACKER: make this account's display name exactly {v_name!r}, then "
        "unlink and re-link the mod so the forged name is on the token.\n"
        "        • A Discord-origin attacker: change the Discord display name.\n"
        "        • A WEBSITE account (an 'a_…' id, which is what this pairing uses): change "
        "its display_name — accounts.set_display_name(<id>, name), or the website's "
        "account page. It is a different field from the claimed unique `username`, so it "
        "collides with nothing; that is precisely the hole RM1 is about.\n"
        "        The re-link is NOT optional and no refresh substitutes for it: "
        "/api/v1/user/profile returns the token's `usr` claim, not the account record, so "
        "the forged name only reaches the client on a freshly minted token (measured "
        "2026-09-01 — renaming the account and refreshing left the client reporting the "
        "old name)."
    ):
        return

    a_st = attacker.state()
    a_ident = a_st.get("identity") or {}
    ctx.check("the attacker now reports the victim's display name",
              a_ident.get("username") == v_name,
              f"attacker={a_ident.get('username')!r} victim={v_name!r} — the spoof is not set up, "
              "so this run would not exercise RM1 at all")
    ctx.check("but they are still different accounts",
              bool(a_ident.get("accountId")) and a_ident.get("accountId") != v_account,
              f"attacker accountId={a_ident.get('accountId')!r} victim={v_account!r} — if these "
              "match, this is one account and the test proves nothing")
    # Re-linking mints a new session; the role's recorded account id must follow it or
    # the swap back would be checked against a stale value.
    ctx.role_b.account_id = a_ident.get("accountId")
    ctx.role_b.username = a_ident.get("username")

    if not ctx.manual(
        f"As the ATTACKER: crew a vessel with kerbals named exactly {target_names} "
        "(rename them in the Astronaut Complex), launch it, then quicksend it as a LIVE "
        f"vessel to {v_name!r}."
    ):
        return

    # ── victim: receive, and assert nothing of theirs moved ──
    victim = ctx.use("a")
    if victim is None:
        return
    if not ctx.manual("As the VICTIM: accept the incoming gift."):
        return

    victim.act("/gk/debug/actions/poll-imports")
    v_after = victim.wait_until(
        lambda x: len(x.get("vessels") or []) > v_count_before,
        "the attacker's vessel to arrive in the victim's save", timeout=600)

    for name in target_names:
        original_ship = _crew_aboard_anything(v_before, name)
        k = by_name(v_after, name)
        ctx.check(f"victim still has its own '{name}'", k is not None,
                  "the victim's own kerbal is gone — this is the corruption RM1 describes")
        if k:
            ctx.check(f"victim's '{name}' is still its own (not borrowed)",
                      not k.get("borrowed"),
                      "the victim's kerbal is now tagged as someone else's")
        if original_ship:
            now_ship = _crew_aboard_anything(v_after, name)
            ctx.check(f"victim's '{name}' is still aboard {original_ship!r}",
                      now_ship == original_ship,
                      f"moved to {now_ship!r} — the arrival was adopted onto the victim's own "
                      "roster entry (RM3)")

    new_ship = next((v for v in (v_after.get("vessels") or [])
                     if v["pid"] not in v_vessels_before), None)
    ctx.check("the arriving vessel is present", new_ship is not None)
    if new_ship:
        aboard = new_ship.get("crew") or []
        ctx.note("arriving vessel crew", f"{new_ship.get('name')}: {aboard}")
        collided = [n for n in aboard if n in target_names]
        ctx.check("arriving crew were renamed aside, not given the victim's names",
                  not collided,
                  f"arrivals kept the colliding names {collided} — they were adopted onto the "
                  "victim's roster entries, which is exactly RM1")
        untagged = [n for n in aboard if not _is_borrowed(v_after, n)]
        ctx.check("arriving crew are marked as borrowed", not untagged,
                  f"untagged arrivals: {untagged} — a foreign kerbal that reads as ours is "
                  "never handed back")

    dupes = _duplicate_names(v_after)
    ctx.check("no duplicate roster names after the arrival", not dupes, f"duplicates: {dupes}")
    ctx.check("victim's original crew all still present",
              victim_crew <= {k["name"] for k in roster_of(v_after)},
              f"lost: {sorted(victim_crew - {k['name'] for k in roster_of(v_after)})}")
    assert_roster_healthy(ctx, victim, "victim/after")


def _is_borrowed(state: dict, name: str) -> bool:
    k = by_name(state, name)
    return bool(k and k.get("borrowed"))


# ══ T5 — ghost sweep fidelity ════════════════════════════════════════════

def t5_ghost_sweep(ctx: Ctx) -> None:
    """T5 — PurgeBorrowedGhostCrew removes exactly what /state says it would, and
    never a frozen kerbal, never one of ours, never a live loan.

    The sweep is not cosmetic: KSP counts Missing crew against the hire limit and
    its applicant generator silently refuses any name that is a substring of an
    existing roster name, so a polluted roster reads as an Astronaut Complex with
    nobody to hire. But the sweep *deletes*, and the freeze deliberately parks live
    crew at Dead — so "would remove" and "must not remove" overlap by one rule.

    This builds its own roster. Read against whatever a save happens to hold, the
    scenario passes on an empty one (every claim is true of nothing) and, worse,
    cannot catch a prediction and a sweep that are wrong the *same* way — the two
    agree, so agreement proves nothing. The fixture is four entries chosen so that
    each clause of the selection rule is the only thing separating a name that must
    go from one that must stay:

        Valen's Bill Kerman   Dead      borrowed   -> swept   (the plain ghost)
        Jeb's Bob Kerman      Missing   borrowed   -> swept   (Missing, not only Dead)
        Ghosty Kerman         Dead      OURS       -> stays   (borrowed is load-bearing)
        Valen's Ada Kerman    Available borrowed   -> stays   (a live loan is not a ghost)

    The third is the one that matters most: our own dead are the thing a sweep over
    "Dead" alone would quietly delete, and a player's dead kerbals are not litter.
    """
    inst = ctx.a
    inst.guard_save()
    inst.backup_save("t5")

    must_go = ["Valen's Bill Kerman", "Jeb's Bob Kerman"]
    must_stay = ["Ghosty Kerman", "Valen's Ada Kerman"]
    fixture = [("Valen's Bill Kerman", "Dead"),
               ("Jeb's Bob Kerman", "Missing"),
               ("Ghosty Kerman", "Dead"),
               ("Valen's Ada Kerman", "Available")]

    existing = {k["name"] for k in roster_of(inst.state())}
    placed = []
    for name, status in fixture:
        if name in existing:
            inst.set_crew_status(name, status)
        else:
            inst.add_crew(name, status=status)
            placed.append(name)
    ctx.note("fixture placed", ", ".join(placed) or "already present")

    st = inst.state()
    ctx.check("the roster read succeeded",
              (st.get("crew") or {}).get("rosterOk") is not False,
              f"roster error: {(st.get('crew') or {}).get('rosterError')}")

    seeded = {k["name"]: k for k in roster_of(st)}
    for name, status in fixture:
        got = (seeded.get(name) or {}).get("status")
        ctx.check(f"fixture '{name}' is {status}", got == status,
                  f"roster reports {got!r}")

    crew = st.get("crew") or {}
    candidates = list(crew.get("ghostCandidates") or [])
    frozen = [c.get("name")
              for rec in ((st.get("scenario") or {}).get("immunities") or [])
              for c in (rec.get("crew") or [])]
    ctx.note("sweep candidates", str(sorted(candidates)) or "none")
    ctx.note("frozen (must survive)", str(frozen) or "none — no immunity records in this save")

    # Prediction, before anything is deleted. Checked against the fixture rather
    # than against the sweep, so a prediction that is wrong the same way the sweep
    # is wrong still fails here.
    for name in must_go:
        ctx.check(f"'{name}' is predicted a ghost", name in candidates,
                  "a borrowed kerbal at Dead/Missing is exactly what the sweep is for")
    for name in must_stay:
        ctx.check(f"'{name}' is NOT predicted a ghost", name not in candidates,
                  "the sweep deletes; this one must not be selected")

    ctx.check("no frozen kerbal is a sweep candidate",
              not set(candidates) & set(frozen),
              f"the sweep would delete frozen crew: {sorted(set(candidates) & set(frozen))}")

    before = {k["name"] for k in roster_of(st)}
    out = inst.act("/gk/debug/actions/purge-ghosts")
    removed = int(out.get("removed", 0))

    after_state = inst.state()
    after = {k["name"] for k in roster_of(after_state)}
    actually_gone = sorted(before - after)

    # The pair is the point: /state predicts without acting, the sweep acts without
    # explaining. Asserting they agree is what keeps the prediction honest as the
    # real selection changes.
    ctx.check("the sweep removed exactly the predicted candidates",
              sorted(candidates) == actually_gone,
              f"predicted {sorted(candidates)}, removed {actually_gone}")
    ctx.check("the reported count matches", removed == len(actually_gone),
              f"reported {removed}, actually {len(actually_gone)}")
    for name in must_go:
        ctx.check(f"'{name}' is gone", name not in after, "the ghost survived the sweep")
    for name in must_stay:
        ctx.check(f"'{name}' survived", name in after, "the sweep deleted what it must not")
    ctx.check("every frozen kerbal survived",
              all(f in after for f in frozen),
              f"lost frozen crew: {[f for f in frozen if f not in after]}")
    ctx.check("the sweep is idempotent",
              not ((inst.state().get("crew") or {}).get("ghostCandidates") or []),
              "candidates remain after a sweep")

    # The fixture is litter of our own making; the survivors would otherwise stay in
    # the save and read as a real finding on the next pass.
    for name in must_stay:
        try:
            inst.set_crew_status(name, "Available")
            inst.remove_crew(name)
        except Exception:
            pass


# ══ T6 — busy-crew adoption refusal ══════════════════════════════════════

def t6_busy_crew_refusal(ctx: Ctx) -> None:
    """T6 — RM3. A kerbal currently crewing something is never adopted by an
    arrival of the same name, even on the home branch.

    DebugTestPanel covers the predicate with a stubbed set and says so explicitly:
    'whether CrewedNames actually sees a deferred-removal wreck's crew in a real
    save is a live test, not this one.' This is that test. The reachable-honestly
    case is the issuer still holding the original stranded vessel when the delivery
    lands — removal deferred because they were flying it, or rolled back by a
    quickload.
    """
    inst = ctx.a
    inst.guard_save()
    inst.backup_save("t6")

    st = inst.state()
    crewed = set((st.get("crew") or {}).get("crewedNow") or [])
    ctx.note("CrewedNames() over the live save", f"{len(crewed)} names: {sorted(crewed)[:8]}")

    # This is the half DebugTestPanel cannot do: prove the set is derived from real
    # vessels, including unloaded ones, rather than being empty in practice.
    aboard_per_vessel = {v["name"]: (v.get("crew") or []) for v in (st.get("vessels") or [])
                         if v.get("crew")}
    expected = {n for names in aboard_per_vessel.values() for n in names}
    ctx.check("CrewedNames() sees every kerbal the vessel list says is aboard",
              expected <= crewed,
              f"missing from the busy set: {sorted(expected - crewed)} — an arrival could "
              "be adopted onto one of these")

    unloaded_with_crew = [v["name"] for v in (st.get("vessels") or [])
                          if v.get("crew") and not v.get("loaded")]
    ctx.check("the busy set covers UNLOADED crewed vessels too",
              all(n in crewed for v in (st.get("vessels") or [])
                  if v.get("crew") and not v.get("loaded")
                  for n in v["crew"]),
              "an unloaded wreck's crew are invisible to the guard — the exact deferred-"
              "removal case RM3 is about")
    ctx.note("unloaded crewed vessels", str(unloaded_with_crew) or "none "
             "(stage one: leave a crewed craft in orbit and re-run)")

    pending = (st.get("scenario") or {}).get("pendingRemovals") or []
    still_here = [r for r in pending if r.get("vesselStillPresent")]
    if still_here:
        names = [n for r in still_here for n in (r.get("crew") or [])]
        ctx.check("crew of a deferred-removal vessel are counted as busy",
                  all(n in crewed for n in names),
                  f"not busy: {[n for n in names if n not in crewed]}")
    else:
        ctx.note("deferred removals present", "none — run T2 first to stage one")

    # The arrival side, without a second player. A colliding arrival is a colliding
    # arrival: cloning this save's own crewed vessel produces crew whose names are
    # already in the roster and already aboard something, which is precisely the state
    # RM3 guards — and it runs through ResolveIncomingCrewName, the same function a
    # stranger's quicksend would. Needing a second account here was an assumption, not
    # a requirement, and it cost the scenario its automation for nothing.
    if st.get("scene") != "FLIGHT" or not st.get("activeVessel"):
        ctx.note("arrival side", "needs a crewed active vessel in flight to clone; "
                                 "read-only half only")
        return
    aboard_active = (st["activeVessel"].get("crew") or [])
    if not ctx.check("the active vessel is crewed", bool(aboard_active),
                     "nothing aboard to collide with"):
        return

    before_pids = {v["pid"] for v in (st.get("vessels") or [])}
    try:
        spawned = inst.spawn_test_craft(ap=180000, pe=170000)
    except (BridgeError, Refused) as exc:
        ctx.check("clone the active vessel to stage a collision", False, str(exc))
        return
    ctx.note("cloned", f"name={spawned.get('name')!r} pid={spawned.get('pid')!r}")

    after = inst.wait_until(
        lambda x: {v["pid"] for v in (x.get("vessels") or [])} - before_pids,
        "the cloned vessel to appear", timeout=120)

    new_pids = {v["pid"] for v in (after.get("vessels") or [])} - before_pids
    arrival = next((v for v in (after.get("vessels") or []) if v["pid"] in new_pids), None)
    ctx.check("the clone arrived", arrival is not None)
    if arrival:
        arrived_crew = arrival.get("crew") or []
        ctx.note("arriving crew", str(arrived_crew))
        # The assertion RM3 is about: the arrival must NOT be handed the roster entry
        # that is already flying, even though the owner id says it is ours.
        collided = [n for n in arrived_crew if n in aboard_active]
        ctx.check("arriving crew did not take the names of crew already aboard",
                  not collided,
                  f"arrival kept {collided} — a live roster entry was adopted, which "
                  "leaves two ProtoVessels pointing at one ProtoCrewMember")
        ctx.check("arriving crew are tagged as not-ours",
                  all(_is_borrowed(after, n) for n in arrived_crew) if arrived_crew else True,
                  f"untagged: {[n for n in arrived_crew if not _is_borrowed(after, n)]} — an "
                  "untagged duplicate is never swept and counts against the hire limit forever")
    for ship, names in aboard_per_vessel.items():
        for n in names:
            ctx.check(f"'{n}' is still aboard {ship!r}",
                      _crew_aboard_anything(after, n) == ship,
                      "a live roster entry was adopted by an arrival")
    ctx.check("no duplicate roster names", not _duplicate_names(after))
    assert_roster_healthy(ctx, inst, "after colliding arrival")


# ══ T7 — trait downgrade and repair ══════════════════════════════════════

def t7_trait_repair(ctx: Ctx) -> None:
    """T7 — a modded profession that this install cannot define must never be
    written onto a roster entry, and TraitRepair must hand it back.

    An unresolvable pcm.trait with a null experienceTrait NullRefs part-way through
    drawing any CrewListItem, so the Astronaut Complex and crew assignment stay
    broken while one is in the roster, naming neither the kerbal nor the trait. The
    'traitResolves' field exists for exactly this — it is that null, reported.
    """
    inst = ctx.a
    inst.guard_save()
    inst.backup_save("t7")

    st = inst.state()
    broken = (st.get("crew") or {}).get("brokenTraits") or []
    ctx.check("no unresolvable traits in the roster to start", not broken, f"found {broken}")

    unresolved = [k for k in roster_of(st) if not k.get("traitResolves")]
    ctx.check("every roster entry has a resolvable experienceTrait", not unresolved,
              f"{[k['name'] + ' (' + str(k.get('trait')) + ')' for k in unresolved]} — the "
              "Astronaut Complex will NullRef mid-draw")

    if not ctx.manual(
        "Import a craft whose crew carry a profession no mod here defines (send one from "
        "an instance running e.g. USI/Kerbalism with a modded profession), and accept it."
    ):
        return

    after = inst.state()
    still_unresolved = [k for k in roster_of(after) if not k.get("traitResolves")]
    ctx.check("the arriving crew were downgraded, not written unresolvable",
              not still_unresolved,
              f"unresolvable after import: {[k['name'] for k in still_unresolved]} — "
              "ApplyTrait let a trait through that this install cannot define")
    ctx.check("the roster still reports no broken traits",
              not ((after.get("crew") or {}).get("brokenTraits") or []))
    ctx.note("roster after import", f"{len(roster_of(after))} entries, "
             f"{len(borrowed_names(after))} borrowed")


# ── registry ─────────────────────────────────────────────────────────────

def t8_quicksend_crew_round_trip(ctx: Ctx) -> None:
    """T8 — §3.11. Lend a crewed ship to a friend, get it back, and your crew are
    still yours.

    The finding this settles was verified by hand and was **not** a code defect:
    `ApplyIncomingOwnershipTag` refusing an incoming name that claims to be ours is the
    RM1 defence T4 exists for, and it must not be weakened. The defect was that a
    quicksend had no equivalent of a rescue's `rescue_kerbals` — nothing the server had
    written down *before* the returning payload existed — so an honest return was
    indistinguishable from a forgery and took the refusal. The crew came home as
    "{friend}'s {me}'s Jeb", `borrowed`, with the originals gone from the roster; and
    borrowed crew are eligible for `PurgeBorrowedGhostCrew`, so lending a crewed ship
    could eventually *delete* the lender's kerbals.

    So this asserts both halves, and the second is the one that must never regress:

      * the outbound leg still tags (the friend receives "{owner}'s Jeb", borrowed),
        because a hand-over that arrived untagged would be the RM1 hole itself; and
      * the return leg strips, so the owner gets bare names back, un-borrowed — but
        only because the server attested them.

    Nothing here is faked: the sends go through `ToolActions.Quicksend` and the accepts
    through `GiftInbox.Accept`, which is what runs the import and therefore the tagging.
    A live-vessel send is a real hand-over, so this scenario genuinely moves a ship out
    of one save and into the other and back — which is also why it takes a backup of
    both first.

    Requires the two accounts to be **friends**: `/api/v1/craft/send` gates on
    `friends_db.are_friends`, and rightly so — a hand-over is not something a stranger
    may do to you. An un-friended pair reports that as a refusal rather than a failure
    of the crew logic.
    """
    if ctx.role_b is None:
        ctx.check("two roles available", False, "T8 needs two players")
        return

    # ── owner: find a crewed ship of their own to lend ──
    owner = ctx.use("a")
    if owner is None:
        return
    owner.guard_save()
    owner.backup_save("t8")
    o_before = assert_roster_healthy(ctx, owner, "owner/before")
    o_ident = o_before.get("identity") or {}
    o_name, o_account = o_ident.get("username"), o_ident.get("accountId")
    ctx.note("owner", f"username={o_name!r} accountId={o_account!r}")

    # The crew must be the owner's OWN — an untagged roster name. Lending borrowed
    # crew tests nothing here: they are not what the owner is owed back, and the
    # ledger deliberately never records them.
    candidates = []
    for v in (o_before.get("vessels") or []):
        crew = [c for c in (v.get("crew") or []) if "'s " not in c]
        if crew and len(crew) == len(v.get("crew") or []):
            candidates.append((v, crew))
    if not ctx.check("the owner has a crewed ship carrying only their own kerbals",
                     bool(candidates),
                     "no vessel here is crewed entirely by untagged (own) kerbals — "
                     "launch one, or bring one home, before running T8"):
        return
    vessel, lent_crew = candidates[0]
    ctx.note("the ship being lent", f"{vessel.get('name')!r} crew={lent_crew}")

    # ── outbound: owner → friend ──
    friend = ctx.use("b")
    if friend is None:
        return
    friend.guard_save()
    friend.backup_save("t8")
    f_ident = (friend.state().get("identity") or {})
    f_name, f_account = f_ident.get("username"), f_ident.get("accountId")
    if not ctx.check("the two roles are different accounts",
                     bool(f_account) and f_account != o_account,
                     f"owner={o_account!r} friend={f_account!r} — a self-send would pass "
                     "every assertion below without exercising anything"):
        return

    owner = ctx.use("a")
    if owner is None:
        return
    owner.fly_vessel(vessel["pid"])
    try:
        sent = owner.quicksend(f_account, kind="vessel", recipient_name=f_name or "")
    except Refused as exc:
        ctx.check("the owner could hand the ship over", False, str(exc))
        return
    ctx.note("send", sent.get("message") or "sent")

    # Do NOT force the scene change. The removal is deferred while the sender is
    # flying the very ship being handed over (§3.2), and the mod resolves that itself:
    # `ReturnToSpaceCenterRoutine` waits 1.5 s, SAVES, and then loads the Space Center.
    # That save is the durable half of the whole mechanism — it persists the queue and
    # re-times CurrentGame.UniversalTime. An earlier version of this scenario fired
    # /actions/scene as soon as the send returned, 0.8 s later, pre-empting the routine
    # and producing an *unsaved* exit from flight; that reverts the universe to the last
    # save, so the hand-over is genuinely in a discarded future and is correctly rolled
    # back. It looked exactly like a lost hand-over and was not one. Wait for the mod.
    owner.wait_until(lambda st: st.get("scene") == "SPACECENTER",
                     "the owner's client to return to the Space Center by itself",
                     timeout=SLOW, interval=4.0)
    o_sent = owner.wait_until(
        lambda st: not any(v.get("pid") == vessel["pid"] for v in (st.get("vessels") or [])),
        "the lent ship to leave the owner's save", timeout=SLOW, interval=4.0)
    ctx.check("the lent ship left the owner's save",
              not any(v.get("pid") == vessel["pid"] for v in (o_sent.get("vessels") or [])))
    ctx.check("its crew left the owner's roster with it",
              all(by_name(o_sent, c) is None for c in lent_crew),
              f"still present: {[c for c in lent_crew if by_name(o_sent, c)]} — a hand-over "
              "that leaves the crew behind has duplicated them")

    # ── the friend receives: tagged and borrowed, which is the RM1 behaviour ──
    friend = ctx.use("b")
    if friend is None:
        return
    f_pre = {k.get("name") for k in roster_of(friend.state())}
    try:
        friend.decide_gift("accept")
    except Refused as exc:
        ctx.check("the friend could accept the offer", False, str(exc))
        return
    tagged = [f"{o_name}'s {c}" for c in lent_crew]
    f_got = friend.wait_until(
        lambda st: all(by_name(st, t) is not None for t in tagged),
        "the lent crew to arrive tagged", timeout=SLOWER, interval=4.0)
    for t in tagged:
        k = by_name(f_got, t)
        ctx.check(f"{t!r} arrived under the owner's tag", k is not None)
        if k is not None:
            ctx.check(f"{t!r} is marked borrowed", bool(k.get("borrowed")),
                      "an arrival that does not read as borrowed can be kept by the "
                      "recipient's next hand-over")
    # The receiving half of RM1: an arrival must never displace a name already here.
    # Asserted against the roster read BEFORE the accept, so it is a real comparison
    # rather than a restatement of what was just read.
    lost = sorted(n for n in f_pre if by_name(f_got, n) is None)
    ctx.check("the friend's own roster survived the arrival intact", not lost,
              f"gone after the import: {lost} — an incoming node overwrote a kerbal "
              "already in this save")

    landed = next((v for v in (f_got.get("vessels") or [])
                   if any(t in (v.get("crew") or []) for t in tagged)), None)
    if not ctx.check("the ship itself arrived in the friend's save", landed is not None):
        return

    # ── the return leg: the whole point of the scenario ──
    friend.fly_vessel(landed["pid"])
    try:
        back = friend.quicksend(o_account, kind="vessel", recipient_name=o_name or "")
    except Refused as exc:
        ctx.check("the friend could hand the ship back", False, str(exc))
        return
    ctx.note("return send", back.get("message") or "sent")
    # Same again: the mod saves and leaves flight on its own. See above.
    friend.wait_until(lambda st: st.get("scene") == "SPACECENTER",
                      "the friend's client to return to the Space Center by itself",
                      timeout=SLOW, interval=4.0)
    f_after = friend.wait_until(
        lambda st: all(by_name(st, t) is None for t in tagged),
        "the borrowed crew to leave the friend's save", timeout=SLOW, interval=4.0)
    ctx.check("the borrowed crew left the friend's roster",
              all(by_name(f_after, t) is None for t in tagged))

    owner = ctx.use("a")
    if owner is None:
        return
    try:
        owner.decide_gift("accept")
    except Refused as exc:
        ctx.check("the owner could accept the return", False, str(exc))
        return
    o_after = owner.wait_until(
        lambda st: all(by_name(st, c) is not None for c in lent_crew),
        "the owner's own crew to come home", timeout=SLOWER, interval=4.0)

    for c in lent_crew:
        k = by_name(o_after, c)
        ctx.check(f"{c!r} came home under their own name", k is not None,
                  "the attestation did not reach the client, so the return took the "
                  "impersonation refusal — this is exactly §3.11")
        if k is not None:
            ctx.check(f"{c!r} is not borrowed", not k.get("borrowed"),
                      "their own kerbal reads as on loan from someone else; "
                      "PurgeBorrowedGhostCrew is entitled to delete them")

    # The double tag is the §3.11 signature, and is worth asserting by name rather than
    # only implying it through the checks above — it is what a regression looks like.
    doubles = [k.get("name") for k in roster_of(o_after)
               if (k.get("name") or "").count("'s ") > 1]
    ctx.check("nobody came home double-tagged", not doubles, f"double-tagged: {doubles}")

    assert_roster_healthy(ctx, owner, "owner/after")


SCENARIOS = {
    "T0": ("Bring-up: bridges live, throwaway save, distinct linked accounts", t0_bringup, 1),
    "T1": ("Rescue round trip: tag applied on the way out, stripped on the way home", t1_rescue_round_trip, 2),
    "T2": ("Deferred removal: defers in flight, persists, fires at the Space Center", t2_deferred_removal, 1),
    "T3": ("Quickload rollback: no residue, and the accept echo re-asserts", t3_quickload_rollback, 2),
    "T4": ("Impersonation (RM1): same display name, different account — arrivals renamed aside", t4_impersonation, 2),
    "T5": ("Ghost sweep: removes exactly what it predicts, never a frozen kerbal", t5_ghost_sweep, 1),
    "T6": ("Busy-crew refusal (RM3): CrewedNames over a real save, unloaded vessels included", t6_busy_crew_refusal, 1),
    "T7": ("Trait downgrade: nothing unresolvable is ever written to the roster", t7_trait_repair, 1),
    "T8": ("Quicksend crew round trip: lend a crewed ship and get your own crew back",
           t8_quicksend_crew_round_trip, 2),
}

ORDER = ["T0", "T2", "T5", "T6", "T7", "T1", "T3", "T4", "T8"]
"""Cheapest and least destructive first, and T4 last.

Not the numeric order on purpose: T0 gates everything, T5/T6/T7 are single-instance
reads that cost seconds, and T1/T3/T4 are the multi-minute two-player flows. T4 is
last because it requires renaming a Discord account, which is a nuisance to undo.
"""


def run(name: str, ctx: Ctx) -> Result:
    title, fn, _needs = SCENARIOS[name]
    ctx.log(f"\n── {name}: {title}")
    try:
        fn(ctx)
    except Refused as exc:
        ctx.checks.append(Check("scenario refused by the game", FAIL, str(exc)))
        ctx.log(f"    ✗ refused: {exc}")
    except BridgeError as exc:
        ctx.checks.append(Check("bridge error", FAIL, str(exc)))
        ctx.log(f"    ✗ bridge error: {exc}")

    # A failure outranks a skip. Assertions run before the manual step are real
    # results, and reporting a scenario that already found something wrong as merely
    # "not performed" buries it — the exit code drops from 1 to 3 and the finding
    # reads as an incomplete run. That is the green-over-broken shape this whole
    # harness exists because of, so the order here is deliberate.
    if any(c.status == FAIL for c in ctx.checks):
        status = FAIL
    elif ctx.skipped:
        status = SKIP
    else:
        status = PASS
    return Result(name, title, status, list(ctx.checks), ctx.skipped or "")
