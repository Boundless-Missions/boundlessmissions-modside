"""
client.py — talk to one running KSP instance's debug bridge.

Stdlib only, deliberately: this has to run on whatever Python is on the machine
next to KSP, and a harness that needs its own install step is a harness that gets
skipped. urllib is enough — the bridge is loopback HTTP with one header.

Three things this module exists to get right, none of which are the HTTP:

  1. **Finding the instance.** The port is ephemeral and the token rotates every
     launch, so the only stable address is the handshake file the mod writes into
     its own GameData. That also makes two instances unambiguous: they differ by
     install path long before they differ by anything in the save.

  2. **Not touching a real save.** Every destructive helper goes through
     `guard_save()`. KSP writes persistent.sfs on its own schedule and a crew bug is
     unrecoverable for the player, so a harness that can be pointed at somebody's
     career by a typo is not worth having.

  3. **Waiting without polling.** KSP is slow and stateful — one rescue round trip
     is minutes of scene loads and physics settling. `events()` consumes the SSE
     stream the mod already tees notifications into, and `wait_until()` sleeps
     between checks with a floor. The brief is explicit that a tight polling loop is
     the one genuinely expensive thing here.
"""

from __future__ import annotations

import json
import os
import shutil
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime
from queue import Queue, Empty
from typing import Any, Callable, Iterator, Optional

PROTOCOL = 1
HANDSHAKE_REL = os.path.join("GameData", "BoundlessMissions", "PluginData", "debug_bridge.json")

# The only save any of this is allowed to touch. Overridable per-instance, but never
# defaulted away: the default has to be the throwaway, or one forgotten argument
# points the destructive half of the harness at a career.
DEFAULT_SAVE = "gktest"


class BridgeError(RuntimeError):
    pass


class Refused(BridgeError):
    """The game understood the request and said no. Distinct from a transport
    failure on purpose: a refusal is frequently the correct, asserted-on outcome
    (you cannot spawn from the main menu), while a transport failure never is."""


@dataclass
class Handshake:
    protocol: int
    port: int
    token: str
    host: str
    token_header: str
    mod_version: str
    install: str
    pid: int
    path: str

    @staticmethod
    def read(path: str) -> "Handshake":
        with open(path, "r", encoding="utf-8") as fh:
            d = json.load(fh)
        return Handshake(
            protocol=int(d.get("protocol", 0)),
            port=int(d["port"]),
            token=d["token"],
            host=d.get("host", f"127.0.0.1:{d['port']}"),
            token_header=d.get("tokenHeader", "X-GK-Debug-Token"),
            mod_version=d.get("modVersion", ""),
            install=d.get("install", ""),
            pid=int(d.get("pid", 0)),
            path=path,
        )


class Instance:
    """One running copy of KSP."""

    def __init__(self, root: str, name: Optional[str] = None,
                 expect_save: str = DEFAULT_SAVE, timeout: float = 45.0):
        self.root = os.path.abspath(root)
        self.name = name or os.path.basename(self.root.rstrip("/"))
        self.expect_save = expect_save
        self.timeout = timeout
        self._hs: Optional[Handshake] = None
        self._events: Optional["EventTap"] = None

    # ── discovery ────────────────────────────────────────────────────────

    @property
    def handshake_path(self) -> str:
        return os.path.join(self.root, HANDSHAKE_REL)

    def connect(self, wait: float = 0.0) -> Handshake:
        """Read the handshake and prove something is actually listening.

        The liveness check is not optional. The handshake is deleted on a clean
        shutdown, but a hard kill (or a Proton crash, which is how KSP usually
        goes) leaves it behind — and a stale file points at a port that may by
        then belong to something else entirely. /ping is unauthenticated
        precisely so this check costs nothing and never spends the token on a
        stranger.
        """
        deadline = time.time() + wait
        last: Optional[Exception] = None
        while True:
            try:
                hs = Handshake.read(self.handshake_path)
                if hs.protocol != PROTOCOL:
                    raise BridgeError(
                        f"{self.name}: bridge protocol {hs.protocol}, driver expects {PROTOCOL}. "
                        "Rebuild the mod (GK_CHANNEL=dev ./build.sh) or update the driver — "
                        "a mismatched field read as a pass is worse than not running."
                    )
                self._hs = hs
                pong = self._request("GET", "/gk/debug/ping", auth=False)
                if not pong.get("ok"):
                    raise BridgeError(f"{self.name}: bridge did not answer ping")
                return hs
            except BridgeError:
                raise
            except Exception as exc:  # missing file, dead port, half-written json
                last = exc
                if time.time() >= deadline:
                    raise BridgeError(
                        f"{self.name}: no live debug bridge at {self.handshake_path} ({exc}). "
                        "Is KSP running, and was the mod built with GK_CHANNEL=dev?"
                    ) from last
                time.sleep(1.0)

    @property
    def hs(self) -> Handshake:
        if self._hs is None:
            self.connect()
        assert self._hs is not None
        return self._hs

    # ── http ─────────────────────────────────────────────────────────────

    def _request(self, method: str, path: str, body: Optional[dict] = None,
                 auth: bool = True) -> dict:
        hs = self._hs if self._hs is not None else Handshake.read(self.handshake_path)
        url = f"http://127.0.0.1:{hs.port}{path}"
        data = json.dumps(body).encode("utf-8") if body is not None else None
        req = urllib.request.Request(url, data=data, method=method)
        # The server demands an exact Host match as its DNS-rebinding defence, and
        # urllib would otherwise send "127.0.0.1:PORT" anyway — set it explicitly so
        # the requirement is visible here rather than being an accident of urllib.
        req.add_header("Host", hs.host)
        if data is not None:
            req.add_header("Content-Type", "application/json")
        if auth:
            req.add_header(hs.token_header, hs.token)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8")
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", "replace")
            try:
                parsed = json.loads(raw)
            except Exception:
                parsed = {"error": raw}
            raise BridgeError(f"{self.name}: {method} {path} → HTTP {exc.code}: "
                              f"{parsed.get('error') or parsed.get('message') or raw}") from None
        except urllib.error.URLError as exc:
            raise BridgeError(f"{self.name}: {method} {path} unreachable: {exc.reason}") from None
        except Exception as exc:
            # Anything else the transport can raise — http.client.RemoteDisconnected is
            # the one that matters, thrown when a wedged KSP accepts the socket and then
            # dies before replying. Callers reason about BridgeError; letting a raw
            # http.client exception escape made `terminate` fail on exactly the instance
            # it most needed to kill.
            raise BridgeError(f"{self.name}: {method} {path} failed: "
                              f"{type(exc).__name__}: {exc}") from None
        return json.loads(raw) if raw else {}

    def get(self, path: str) -> dict:
        return self._request("GET", path)

    def post(self, path: str, body: Optional[dict] = None) -> dict:
        return self._request("POST", path, body if body is not None else {})

    def act(self, path: str, body: Optional[dict] = None) -> dict:
        """POST a command and raise on a refusal.

        Refusals arrive as HTTP 200 with ok:false — the game said no to a
        well-formed request — so they have to be turned into an exception here or
        a scenario silently continues past a step that did nothing.
        """
        out = self.post(path, body)
        if out.get("ok") is False:
            raise Refused(f"{self.name}: {path} refused: {out.get('message') or out.get('error')}")
        return out

    # ── reads ────────────────────────────────────────────────────────────

    def state(self) -> dict:
        return self.get("/gk/debug/state")

    def roster(self) -> list:
        return self.get("/gk/debug/roster")

    def vessels(self) -> list:
        return self.get("/gk/debug/vessels")

    def scenario(self) -> dict:
        return self.get("/gk/debug/scenario")

    def contracts(self) -> dict:
        return self.get("/gk/debug/contracts")

    def saves(self) -> dict:
        return self.get("/gk/debug/saves")

    # ── save control ─────────────────────────────────────────────────────

    def load_save(self, save: str, scene: str = "SPACECENTER",
                  file: str = "persistent", timeout: float = 400.0) -> dict:
        """Load a save folder and wait for it to actually be live.

        Waits on the state rather than on the job, because the job completes when the
        scene is up while what a caller means by "loaded" is that reads answer about
        the new save. The request itself answers 202 immediately — the game stops
        pumping the queue for the whole load, so a blocking call would time out on a
        load that worked.
        """
        out = self.act("/gk/debug/actions/load-save",
                       {"save": save, "scene": scene, "file": file})
        # Watch the JOB, not just the state. A failed load completes the job in
        # milliseconds with the reason on it, while the state simply never changes — so
        # polling state alone turns any failure into a silent wait for the full timeout.
        # That is not hypothetical: it cost four minutes and looked like a hung game
        # before the underlying NullReferenceException was found.
        self.await_job(out.get("job_id"), what=f"load of {save!r}", timeout=timeout)
        st = self.wait_until(
            lambda s: s.get("gameLoaded") and s.get("save") == save,
            f"save {save!r} to become live", timeout=60.0, interval=2.0)
        self.expect_save = save
        return st

    def await_job(self, job_id: Optional[str], what: str = "job",
                  timeout: float = 400.0, interval: float = 2.0) -> dict:
        """Block until a 202-style job finishes, and raise with its message if it failed.

        Every async route on this bridge answers with a job id and reports the outcome
        two ways — on the job record and on the event stream. Ignoring both and watching
        for a side effect instead is how a failure becomes a hang.
        """
        if not job_id:
            raise BridgeError(f"{self.name}: {what} returned no job id")
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                rec = self.get(f"/gk/debug/jobs/{job_id}")
            except BridgeError:
                # The game stops answering during a scene load; that is the normal case
                # here, not a failure.
                time.sleep(interval)
                continue
            state = rec.get("state")
            if state == "done":
                return rec
            if state == "error":
                raise Refused(f"{self.name}: {what} failed: {rec.get('message') or 'no reason given'}")
            time.sleep(interval)
        raise BridgeError(f"{self.name}: {what} did not finish within {timeout:.0f}s")

    def quicksave(self, name: str = "quicksave") -> dict:
        return self.act("/gk/debug/actions/quicksave", {"name": name})

    def quickload(self, name: str = "quicksave", timeout: float = 400.0) -> dict:
        """Roll the save back. The scene afterwards is the Space Center regardless of
        where the quicksave was taken — a harness wants a settled scene it can read,
        not a flight it would then have to wait on physics for."""
        save = self.state().get("save")
        out = self.act("/gk/debug/actions/quickload", {"name": name})
        self.await_job(out.get("job_id"), what="quickload", timeout=timeout)
        return self.wait_until(
            lambda s: s.get("gameLoaded") and s.get("save") == save
                      and s.get("scene") in ("SPACECENTER", "TRACKSTATION"),
            "the quickload to settle", timeout=60.0, interval=2.0)

    def change_scene(self, scene: str, timeout: float = 400.0) -> dict:
        """Change scene and wait for it, surfacing a refusal rather than hanging."""
        out = self.act("/gk/debug/actions/scene", {"scene": scene})
        if out.get("job_id"):
            self.await_job(out["job_id"], what=f"scene change to {scene}", timeout=timeout)
        return self.wait_until(lambda s: s.get("scene") == scene,
                               f"scene {scene}", timeout=60.0, interval=2.0)

    def fly_vessel(self, pid: str, timeout: float = 400.0) -> dict:
        """Enter flight on a specific vessel — the Tracking Station's "Fly" button.

        The only step of a two-instance test that previously required the mouse, which
        made it unreachable on a window that cannot be given focus.
        """
        self.act("/gk/debug/actions/fly-vessel", {"pid": pid})
        return self.wait_until(
            lambda s: s.get("scene") == "FLIGHT" and (s.get("activeVessel") or {}).get("pid") == pid,
            f"flight on {pid[:8]}", timeout=timeout, interval=4.0)

    def issue_rescue(self, contractor_id: str, *, contractor_name: str = "",
                     mission: str = "Bridge test rescue - bring the crew home.",
                     payment: int = 1000, body: str = "Kerbin",
                     timeout: float = 400.0, **kw) -> dict:
        """Issue a rescue against the vessel being flown, through the mod's own
        ContractCreation. Destroys that vessel, exactly as it does in play — back the
        save up first."""
        req = {"contractor_id": contractor_id, "contractor_name": contractor_name,
               "mission": mission, "payment": payment, "body": body}
        req.update(kw)
        out = self.act("/gk/debug/actions/issue-rescue", req)
        return self.await_job(out.get("job_id"), what="rescue issue", timeout=timeout)

    def accept_contract(self, contract_id: str, issuer_name: str = "",
                        timeout: float = 200.0) -> dict:
        out = self.act("/gk/debug/actions/accept-contract",
                       {"contract_id": contract_id, "issuer_name": issuer_name})
        return self.await_job(out.get("job_id"), what="contract accept", timeout=timeout)

    def spawn_wreck(self, contract_id: str, timeout: float = 400.0) -> dict:
        out = self.act("/gk/debug/actions/spawn-wreck", {"contract_id": contract_id})
        return self.await_job(out.get("job_id"), what="wreck spawn", timeout=timeout)

    def unlink(self) -> dict:
        return self.act("/gk/debug/actions/unlink")

    def ensure_identity(self, timeout: float = 60.0) -> dict:
        """Make sure accountId/username are populated, refreshing if they are not.

        Worth a helper because the failure it prevents is silent: an empty identity does
        not error anywhere, it just makes every ownership decision take the "stranger"
        branch, so fixtures and assertions quietly test the wrong path.
        """
        ident = (self.state().get("identity") or {})
        if ident.get("accountId") and ident.get("username"):
            return ident
        if not ident.get("linked"):
            raise BridgeError(f"{self.name}: not linked — cannot establish an identity")
        self.act("/gk/debug/actions/refresh")
        st = self.wait_until(
            lambda s: (s.get("identity") or {}).get("accountId")
                      and (s.get("identity") or {}).get("username"),
            "the profile fetch to populate the identity", timeout=timeout, interval=2.0)
        return st.get("identity") or {}

    # ── seeing the screen ────────────────────────────────────────────────

    def screenshot(self, path: str, width: int = 1280) -> str:
        """Save a PNG of the game's current frame and return the path."""
        hs = self.hs
        url = f"http://127.0.0.1:{hs.port}/gk/debug/screenshot?w={int(width)}"
        req = urllib.request.Request(url, method="GET")
        req.add_header("Host", hs.host)
        req.add_header(hs.token_header, hs.token)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                data = resp.read()
        except urllib.error.HTTPError as exc:
            raise BridgeError(f"{self.name}: screenshot failed: HTTP {exc.code}") from None
        except urllib.error.URLError as exc:
            raise BridgeError(f"{self.name}: screenshot unreachable: {exc.reason}") from None
        if not data.startswith(b"\x89PNG"):
            raise BridgeError(f"{self.name}: screenshot did not return a PNG "
                              f"({data[:120]!r})")
        with open(path, "wb") as fh:
            fh.write(data)
        return path

    # ── fixtures ─────────────────────────────────────────────────────────

    def rename_crew(self, name: str, to: str) -> dict:
        """Rename a roster entry. The T4 setup step: the attacker needs kerbals named
        exactly like the victim's."""
        return self.act("/gk/debug/actions/crew",
                        {"action": "rename", "name": name, "to": to})

    def add_crew(self, name: str, trait: str = "", status: str = "") -> dict:
        return self.act("/gk/debug/actions/crew",
                        {"action": "add", "name": name, "trait": trait,
                         "status": status})

    def set_crew_status(self, name: str, status: str) -> dict:
        """Available | Dead | Missing. The T5 fixture: a ghost is a borrowed kerbal
        the roster still holds at Dead or Missing after its craft vanished, and no
        production call produces that on demand."""
        return self.act("/gk/debug/actions/crew",
                        {"action": "status", "name": name, "status": status})

    def remove_crew(self, name: str) -> dict:
        return self.act("/gk/debug/actions/crew", {"action": "remove", "name": name})

    def quicksend(self, recipient_id: str, kind: str = "vessel",
                  recipient_name: str = "", timeout: float = 300.0) -> dict:
        """Hand the ACTIVE vessel (kind="vessel") or its blueprint (kind="craft") to
        another player, through the mod's own Tools-tab action.

        A live send needs flight, and needs the flight settled: KSP's ClearToSave
        refuses a craft that is still moving over the surface, which a freshly spawned
        or just-landed hull reports for a few seconds — and a craft resting on wheels
        reports it permanently (see GeneKermanMod.NotClearReason). Retried here rather
        than in every caller, because the transient case is by far the common one and a
        scenario that fell over on it would look like a hand-over bug.
        """
        last = None
        for attempt in range(6):
            try:
                out = self.act("/gk/debug/actions/quicksend",
                               {"recipient_id": recipient_id, "kind": kind,
                                "recipient_name": recipient_name})
                return self.await_job(out.get("job_id"), f"quicksend {kind}", timeout=timeout)
            except Refused as exc:
                last = exc
                if "moving over the surface" not in str(exc):
                    raise
                time.sleep(10.0)
        raise Refused(f"{self.name}: the flight never settled enough to hand over: {last}")

    def decide_gift(self, action: str, import_id: str = "",
                    timeout: float = 300.0) -> dict:
        """Accept or decline a quicksend offer, through GiftInbox's own entry points.

        Accepting is not a status flip on the server: it also runs the import on the
        spot where the scene allows, which is where the ownership tagging and the
        homebound attestation actually happen. Going through the API directly would
        leave all of that unrun and still look like a pass.
        """
        out = self.act("/gk/debug/actions/gift",
                       {"action": action, "import_id": import_id})
        return self.await_job(out.get("job_id"), f"gift {action}", timeout=timeout)

    def spawn_test_craft(self, ap: float = 100000, pe: float = 100000,
                         body: str = "") -> dict:
        """Copy the active vessel (crew included) into an orbit as a second hull.

        A copy, not a move — the import mints a fresh pid, so CheatDetection's watchdog
        baselines it rather than judging it. Teleporting the same craft there with the
        F12 menu would taint it, the taint would spread across the EVA crew transfer a
        rescue ends with, and the server would refuse the submission."""
        # Identity first. The server refuses without it, but doing it here means the
        # caller gets a working fixture instead of an error they have to know how to
        # fix: LinkedUsername/LinkedAccountId come from the profile fetch, not from
        # holding a token, so a session that loaded its save over the bridge has never
        # populated them.
        self.ensure_identity()
        payload = {"ap": ap, "pe": pe}
        if body:
            payload["body"] = body
        out = self.act("/gk/debug/actions/spawn-test-craft", payload)

        # Let the situation settle before reporting. KSP recomputes Vessel.situation on
        # its own update cycle, so a read taken in the same breath as the spawn can catch
        # the vessel still labelled FLYING before it is reclassified — which looked like
        # a placement bug the first time and was very likely just an early sample.
        # Waiting turns that ambiguity into an answer: if it is still not ORBITING after
        # this, the placement really is wrong and worth chasing.
        pid = out.get("pid")
        if pid:
            def settled(st: dict) -> bool:
                for v in (st.get("vessels") or []):
                    if v.get("pid") == pid:
                        return v.get("situation") not in ("FLYING", "", None)
                return False
            try:
                st = self.wait_until(settled, "the spawned vessel's situation to settle",
                                     timeout=45.0, interval=2.0)
                for v in (st.get("vessels") or []):
                    if v.get("pid") == pid:
                        out["situation"] = v.get("situation")
                        out["loaded"] = v.get("loaded")
            except BridgeError:
                # Not fatal — report what it is stuck as, and let the caller judge.
                cur = next((v for v in (self.state().get("vessels") or [])
                            if v.get("pid") == pid), {})
                out["situation"] = cur.get("situation")
                out["situation_unsettled"] = True
        return out

    def ui(self) -> dict:
        """The live uGUI hierarchy. Note what is absent: KSP draws a good deal of its
        interface in legacy IMGUI, which creates no objects, so anything not listed
        here cannot be inspected or clicked from inside the process at all.

        Rects are screen pixels, top-left origin, with `cx`/`cy` already centred — the
        same frame the pointer report and any screenshot use. Callers do no maths.
        """
        return self.get("/gk/debug/ui")

    def find_ui(self, text: str, *, by: str = "any", interactable: Optional[bool] = None,
                on_screen: bool = True) -> list:
        """Elements whose label, name or path contains `text` (case-insensitive)."""
        needle = text.lower()
        out = []
        for e in (self.ui().get("elements") or []):
            if on_screen and not e.get("onScreen", True):
                continue
            if interactable is not None and bool(e.get("interactable")) != interactable:
                continue
            hay = {
                "label": e.get("label", ""),
                "name": e.get("name", ""),
                "path": e.get("path", ""),
            }
            fields = hay.values() if by == "any" else [hay.get(by, "")]
            if any(needle in (f or "").lower() for f in fields):
                out.append(e)
        return out

    def click_ui(self, text: str, *, by: str = "any", index: Optional[int] = None,
                 require_interactable: bool = True) -> dict:
        """Find a control by text and click its centre, verifying the raycast first.

        Preferred over click_at: a label survives a layout change, a coordinate does not.
        Refuses on an ambiguous or disabled match rather than picking one — a harness
        that guesses which of three identical buttons you meant is worse than one that
        stops.
        """
        hits = self.find_ui(text, by=by)
        if not hits:
            raise BridgeError(f"{self.name}: no on-screen UI element matching {text!r}")
        if require_interactable:
            live = [h for h in hits if h.get("interactable")]
            if not live:
                raise BridgeError(
                    f"{self.name}: {len(hits)} element(s) match {text!r} but none are "
                    "interactable — something is probably modal over them")
            hits = live
        # index defaults to None rather than 0 so an explicit index=0 is honoured. With a
        # default of 0 the two were indistinguishable, and the ambiguity guard fired even
        # when the caller had already chosen — making the escape hatch it points at
        # unusable.
        if index is None:
            if len(hits) > 1:
                names = [f"{h.get('name')}:{h.get('label')!r}" for h in hits[:6]]
                raise BridgeError(
                    f"{self.name}: {text!r} is ambiguous ({len(hits)} matches: {names}). "
                    "Narrow it, or pass index=")
            index = 0
        if index >= len(hits):
            raise BridgeError(
                f"{self.name}: index={index} but only {len(hits)} match(es) for {text!r}")
        e = hits[index]
        return self.click_at(int(e["cx"]), int(e["cy"]), expect_hover=e.get("name"))

    # ── pointer control ──────────────────────────────────────────────────
    #
    # Injection is ydotool (kernel uinput); the readback is the game itself. Both
    # halves are necessary and neither is sufficient.
    #
    # Wayland exposes no cursor position, so without the readback this is open-loop.
    # And libinput applies pointer ACCELERATION to injected relative motion — ydotoold
    # says so at startup ("not disabling mouse pointer acceleration", because xinput is
    # absent) — so a single large relative jump systematically overshoots, and no fixed
    # scale factor corrects it because the curve is velocity-dependent.
    #
    # Hence: move, ask the game where the pointer actually ended up, correct. Converges
    # in two or three passes and is immune to whatever curve is in effect.

    YDOTOOL_SOCKET = "/run/user/1000/.ydotool_socket"

    def _ydotool(self, *args: str) -> None:
        import subprocess
        env = dict(os.environ, YDOTOOL_SOCKET=self.YDOTOOL_SOCKET)
        res = subprocess.run(["ydotool", *args], env=env,
                             capture_output=True, text=True, timeout=15)
        if res.returncode != 0:
            raise BridgeError(f"ydotool {' '.join(args)} failed: {res.stderr.strip()}")

    def pointer(self) -> dict:
        """Where the GAME thinks the pointer is, plus what is under it."""
        return self.ui().get("pointer") or {}

    def move_mouse(self, x: int, y: int, tolerance: int = 2, tries: int = 40,
                   _retry_after_focus: bool = True) -> dict:
        """Put the pointer at screen (x, y), top-left origin, and prove it landed.

        Raises rather than returning quietly wrong: a click at an unverified position is
        how UI automation edits the wrong thing.
        """
        # Prove input reaches THIS instance before injecting anything large. With two
        # instances running, a window that silently refuses focus sends every event to
        # the other game — so the corner-pin below would fling the wrong game's pointer
        # and a later click would act on the wrong save.
        if _retry_after_focus:
            from . import instances as _inst
            _inst.ensure_focused(self)

        # Pin to a corner first. Any single relative move is accelerated, but a move far
        # larger than the screen saturates at the corner whatever the curve — that is
        # the one position establishable open-loop.
        self._ydotool("mousemove", "-x", "-20000", "-y", "-20000")
        time.sleep(0.05)
        self._ydotool("mousemove", "-x", str(int(x)), "-y", str(int(y)))
        time.sleep(0.12)

        # Adaptive gain. The acceleration curve is unknown and velocity-dependent, so
        # rather than assume it, measure it: each correction records what was requested
        # and the readback says what was delivered, and the ratio steers the next one.
        # A fixed step cap cannot work — measured here, a jump overshot by 564px while a
        # 40px cap over 6 tries could correct at most 240, so the loop ran out of room
        # and reported failure on a pointer that was merely far away.
        gain = 2.0
        prev: Optional[tuple] = None   # (requested_x, requested_y, before_x, before_y)
        last: dict = {}

        def _clamp(v: float, lo: float, hi: float) -> float:
            return lo if v < lo else (hi if v > hi else v)

        for _ in range(tries):
            last = self.pointer()
            if not last or "x" not in last:
                raise BridgeError(f"{self.name}: no pointer readback — is the mod build current?")
            cx, cy = float(last["x"]), float(last["y"])
            dx, dy = x - cx, y - cy
            if abs(dx) <= tolerance and abs(dy) <= tolerance:
                return last

            if prev is not None:
                req_x, req_y, before_x, before_y = prev
                moved_x, moved_y = cx - before_x, cy - before_y
                # Only re-estimate off a request big enough for the ratio to mean
                # something; a 1px request tells you nothing about the curve.
                if abs(req_x) >= 4 and abs(moved_x) >= 1:
                    gain = _clamp(abs(moved_x / req_x), 0.5, 10.0)
                elif abs(req_y) >= 4 and abs(moved_y) >= 1:
                    gain = _clamp(abs(moved_y / req_y), 0.5, 10.0)

            req_x = int(_clamp(dx / gain, -300, 300))
            req_y = int(_clamp(dy / gain, -300, 300))
            # Never request zero while still outside tolerance, or the loop stalls one
            # pixel short forever.
            if req_x == 0 and abs(dx) > tolerance:
                req_x = 1 if dx > 0 else -1
            if req_y == 0 and abs(dy) > tolerance:
                req_y = 1 if dy > 0 else -1

            prev = (req_x, req_y, cx, cy)
            self._ydotool("mousemove", "-x", str(req_x), "-y", str(req_y))
            time.sleep(0.06)

        # Almost always a focus problem rather than a coordinate one: ydotool injects into
        # whatever the compositor considers focused, so if anything else took focus the
        # events never reach KSP and the readback simply never moves. Recover once, then
        # give up — retrying forever against a genuinely stuck pointer would hide it.
        if _retry_after_focus:
            try:
                from . import instances as _inst
                if _inst.focus(self):
                    time.sleep(0.4)
                    return self.move_mouse(x, y, tolerance=tolerance, tries=tries,
                                           _retry_after_focus=False)
            except Exception:
                pass

        raise BridgeError(
            f"{self.name}: pointer would not settle at ({x},{y}); game reports "
            f"({last.get('x')},{last.get('y')}) hovering {last.get('hovered')!r}. "
            "Focus was re-asserted and it still would not move — check that KSP is "
            "visible and that ydotoold is running.")

    def park_pointer(self) -> None:
        """Move the pointer somewhere it cannot hover anything.

        KSP's application launcher opens its panels on HOVER, so a cursor left resting in
        a corner after a click sequence silently holds a stock window open — observed:
        the Messages panel stuck open because automation finished with the pointer at
        (2552, 1), on top of its launcher button. Nothing was broken, but the game looked
        broken, which for a harness is nearly as bad.

        Mid-screen-left is chosen because it is over the 3D scene in every KSP scene and
        over no UI in any of them.
        """
        try:
            self.move_mouse(int(self.ui()["screen"]["w"] * 0.25),
                            int(self.ui()["screen"]["h"] * 0.55))
        except Exception:
            pass

    def click_at(self, x: int, y: int, button: str = "left",
                 expect_hover: Optional[str] = None) -> dict:
        """Move, verify the position, optionally confirm what is under the cursor, click.

        `expect_hover` is the rail worth using: it checks the raycast names the control
        you meant BEFORE the button goes down, rather than discovering afterwards that
        the layout moved.
        """
        pos = self.move_mouse(x, y)
        if expect_hover and expect_hover.lower() not in (pos.get("hovered") or "").lower():
            raise BridgeError(
                f"{self.name}: refusing to click — expected to be over {expect_hover!r} "
                f"but the pointer is over {pos.get('hovered')!r}")
        codes = {"left": "0xC0", "right": "0xC1", "middle": "0xC2"}
        self._ydotool("click", codes.get(button, "0xC0"))
        return pos

    # ── safety ───────────────────────────────────────────────────────────

    def guard_save(self) -> str:
        """Refuse to go on unless the loaded save is the throwaway.

        Called before anything destructive. Named and separate rather than folded
        into each command because the check is the point: the bridge itself is
        happy to delete a vessel out of any save you point it at, and this is the
        only thing standing between a typo and someone's career.
        """
        st = self.state()
        save = st.get("save") or ""
        if not st.get("gameLoaded"):
            raise BridgeError(f"{self.name}: no save is loaded.")
        if save != self.expect_save:
            raise BridgeError(
                f"{self.name}: loaded save is '{save}', expected '{self.expect_save}'. "
                "Refusing to touch it. Load the throwaway save, or pass --save."
            )
        return save

    def backup_save(self, tag: str = "") -> str:
        """Copy persistent.sfs aside before a run.

        Cheap insurance against the exact failure this whole harness exists to
        catch — three of the four repair rounds on this path corrupted saves, and
        a corrupted gktest that cannot be rolled back costs an afternoon of
        re-staging fixtures.
        """
        src = os.path.join(self.root, "saves", self.expect_save, "persistent.sfs")
        if not os.path.exists(src):
            raise BridgeError(f"{self.name}: no persistent.sfs at {src}")
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        suffix = f"-{tag}" if tag else ""
        dst = os.path.join(os.path.dirname(src), f"persistent.gkbackup-{stamp}{suffix}.sfs")
        shutil.copy2(src, dst)
        return dst

    # ── waiting ──────────────────────────────────────────────────────────

    def wait_until(self, predicate: Callable[[dict], bool], what: str,
                   timeout: float = 300.0, interval: float = 3.0) -> dict:
        """Sleep until /state satisfies a predicate.

        The interval floor is deliberate and is the cost note from the brief made
        mechanical: a scene load is 5-40s on a modded install and a rescue round
        trip is minutes, so a sub-second loop buys nothing and spends the whole
        budget. Prefer `wait_for_event` when there is an event to wait on; this is
        for state that changes with no notification behind it (a removal firing, a
        roster settling).
        """
        interval = max(interval, 1.0)
        deadline = time.time() + timeout
        last: dict = {}
        while time.time() < deadline:
            try:
                last = self.state()
                if predicate(last):
                    return last
            except BridgeError:
                # A scene load stops Update(), so requests time out for its whole
                # duration. That is the normal case here, not a failure.
                pass
            time.sleep(interval)
        raise BridgeError(f"{self.name}: timed out after {timeout:.0f}s waiting for {what}")

    def events(self) -> "EventTap":
        if self._events is None:
            self._events = EventTap(self)
            self._events.start()
        return self._events

    def wait_for_event(self, match: Callable[[str, dict], bool], what: str,
                       timeout: float = 300.0) -> tuple:
        return self.events().wait(match, what, timeout)

    def close(self) -> None:
        if self._events is not None:
            self._events.stop()
            self._events = None

    def __enter__(self) -> "Instance":
        self.connect()
        return self

    def __exit__(self, *exc) -> None:
        self.close()


class EventTap:
    """Background SSE reader.

    A thread rather than a poll loop because that is the whole point of the stream:
    a craft arriving, a gift being declined and a contract changing state are all
    notifications, and the alternative is asking every few seconds for minutes on
    end. Events are buffered from the moment the tap starts, so a scenario can
    subscribe, act, and then wait — without racing the event it caused.
    """

    def __init__(self, inst: Instance):
        self.inst = inst
        self.q: "Queue[tuple]" = Queue()
        # Buffered events as [event, data, consumed]. The consumed flag is what
        # separates the two things callers want from one method: a scenario waiting
        # for the event its own action caused must be able to match one that arrived
        # before it started waiting, while a caller waiting twice for "any event"
        # must get two different ones. Without it, a match-anything predicate
        # re-returns the first buffered event forever.
        self.seen: list = []
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run, name=f"sse-{self.inst.name}", daemon=True)
        self._thread.start()

    def _run(self) -> None:
        hs = self.inst.hs
        url = f"http://127.0.0.1:{hs.port}/gk/debug/events"
        req = urllib.request.Request(url, method="GET")
        req.add_header("Host", hs.host)
        req.add_header(hs.token_header, hs.token)
        try:
            with urllib.request.urlopen(req, timeout=None) as resp:
                event = "message"
                for raw in resp:
                    if self._stop.is_set():
                        return
                    line = raw.decode("utf-8", "replace").rstrip("\r\n")
                    if line.startswith(":"):
                        continue  # heartbeat
                    if line.startswith("event:"):
                        event = line[6:].strip()
                    elif line.startswith("data:"):
                        payload = line[5:].strip()
                        try:
                            data = json.loads(payload) if payload else {}
                        except Exception:
                            data = {"raw": payload}
                        self.q.put((event, data))
                        event = "message"
        except Exception:
            # The stream dies on shutdown and on every scene load that kills the
            # connection. Not fatal: wait() falls back to its timeout, and the
            # state predicates are always the authority.
            pass

    def wait(self, match: Callable[[str, dict], bool], what: str, timeout: float = 300.0) -> tuple:
        """Return the first unconsumed event matching `match`, waiting if need be."""
        deadline = time.time() + timeout
        # Anything already buffered and not yet handed out counts — a scenario that
        # acts and then waits must not lose the event its own action produced.
        for entry in self.seen:
            if not entry[2] and match(entry[0], entry[1]):
                entry[2] = True
                return (entry[0], entry[1])
        while time.time() < deadline:
            try:
                event, data = self.q.get(timeout=min(5.0, max(0.5, deadline - time.time())))
            except Empty:
                continue
            entry = [event, data, False]
            self.seen.append(entry)
            if match(event, data):
                entry[2] = True
                return (event, data)
        raise BridgeError(f"{self.inst.name}: timed out after {timeout:.0f}s waiting for event {what}")

    def drain(self) -> list:
        """Every event seen so far, consumed or not. For a post-mortem, not a wait."""
        while True:
            try:
                event, data = self.q.get_nowait()
                self.seen.append([event, data, False])
            except Empty:
                return [(e, d) for e, d, _used in self.seen]

    def stop(self) -> None:
        self._stop.set()


def discover(instances_root: str, names: Optional[list] = None,
             expect_save: str = DEFAULT_SAVE) -> list:
    """Every install under a directory that currently has a live bridge."""
    found = []
    if not os.path.isdir(instances_root):
        return found
    for entry in sorted(os.listdir(instances_root)):
        if names and entry not in names:
            continue
        root = os.path.join(instances_root, entry)
        if not os.path.isdir(root):
            continue
        inst = Instance(root, name=entry, expect_save=expect_save)
        if os.path.exists(inst.handshake_path):
            found.append(inst)
    return found
