"""
instances.py — start, stop and focus KSP itself.

The bridge can drive everything *inside* a running KSP. This is the layer outside it:
the restart cycle, which in practice dominated the first day of using the harness — every
mod change costs a full quit/relaunch, because the DLL binds at startup and nothing can
reload it in place.

Three design points, all forced by the environment rather than chosen:

  * **Instances are identified by the pid the mod itself reports.** DebugBridge writes its
    process id into the handshake file, so "which KSP is KR2-KSP" is answered by the game
    rather than guessed. Two instances are both `KSP_x64.exe` under Proton with the same
    window title, so a name or caption match cannot tell them apart — and killing or
    focusing the wrong one is exactly the mistake worth engineering out.

  * **Launching goes through Steam.** These are non-Steam shortcuts run under Proton;
    starting the exe directly would miss the compatibility runtime, the prefix and the
    environment entirely. `steam steam://rungameid/<appid>` is the only faithful route.
    The appid is discovered by looking for the instance's own name inside each
    compatdata prefix's Player.log, because the ids change whenever a shortcut is
    re-added — a hardcoded one silently addresses a different game.

  * **Focus goes through KWin scripting.** This is Wayland: there is no `xdotool
    windowactivate`, and an XWayland client cannot raise itself. KWin will do it, but only
    from a script it loads, so the script is written to a temp file, loaded, run and
    unloaded per call. Ugly, and the only thing that works without installing anything.
"""

from __future__ import annotations

import os
import re
import signal
import subprocess
import tempfile
import time
from typing import Optional

from .client import BridgeError, Instance

STEAM_COMPATDATA = os.path.expanduser("~/.local/share/Steam/steamapps/compatdata")
PLAYER_LOG_REL = os.path.join(
    "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow",
    "Squad", "Kerbal Space Program", "Player.log")

_appid_cache: dict = {}


# ── discovery ────────────────────────────────────────────────────────────

def steam_appid(instance_name: str) -> Optional[str]:
    """The Steam appid whose Proton prefix belongs to this instance.

    Found by looking for the instance's directory name inside each prefix's Player.log
    rather than from a table: Steam re-mints the id whenever a non-Steam shortcut is
    re-added, and a stale hardcoded id does not fail — it launches a different game.
    (The id recorded in CLAUDE.md was already stale when this was written.)
    """
    if instance_name in _appid_cache:
        return _appid_cache[instance_name]
    if not os.path.isdir(STEAM_COMPATDATA):
        return None

    # Match on the install PATH, not the bare directory name. A name alone is far too
    # loose: FAK1's own Player.log contains the strings "Stock-1", "Stock-1-Bare" and
    # "Stock-1-Mini" (they are KSP's own internal names), so a name search returns
    # FAK1's prefix as a candidate for Stock-1. It happened to lose the mtime tiebreak,
    # which is luck rather than correctness — and the cost of getting it wrong is
    # launching a different game.
    #
    # Wine writes the path as `Z:\home\ayd\...`, so both separators are tried.
    unix = os.path.join("Documents", "KSP DEV Instances", instance_name)
    needles = [unix.encode(), unix.replace("/", "\\").encode()]

    best = None
    for entry in sorted(os.listdir(STEAM_COMPATDATA)):
        log = os.path.join(STEAM_COMPATDATA, entry, PLAYER_LOG_REL)
        if not os.path.isfile(log):
            continue
        try:
            with open(log, "rb") as fh:
                blob = fh.read(2_000_000)
        except OSError:
            continue
        # The path must be followed by a separator, or "Stock-1" would still match
        # "Stock-1-Mini" sitting at the same place in a path.
        if not any(n + b"\\" in blob or n + b"/" in blob for n in needles):
            continue
        # Most recently touched wins among genuine matches: an instance moved or
        # re-added leaves its path in an older prefix's log too.
        mtime = os.path.getmtime(log)
        if best is None or mtime > best[1]:
            best = (entry, mtime)
    _appid_cache[instance_name] = best[0] if best else None
    return _appid_cache[instance_name]


def host_pids(inst: Instance) -> list:
    """Host process ids belonging to this instance, reaper first.

    NOT the pid in the handshake file. That comes from
    Process.GetCurrentProcess().Id inside Wine, which reports the *prefix's* pid — 288 on
    a live instance here — and signalling it on the host would hit an unrelated process
    or nothing at all. This was very nearly used to kill things.

    Identity comes from two independent markers, both stable and both per-instance:
    Steam's reaper carries `SteamLaunch AppId=<appid>` in its command line, and every
    process in the tree has the instance directory as its cwd. Either alone is enough to
    recognise one; requiring the tree to match at least one keeps a second instance (same
    binary, same window title) from being caught up in it.
    """
    appid = steam_appid(inst.name)
    root = os.path.realpath(inst.root)
    marker = f"SteamLaunch AppId={appid}" if appid else None

    reaper, others = [], []
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        pid = int(entry)
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as fh:
                cmdline = fh.read().replace(b"\0", b" ").decode("utf-8", "replace")
            cwd = os.path.realpath(os.readlink(f"/proc/{pid}/cwd"))
        except (OSError, PermissionError):
            continue
        if marker and marker in cmdline:
            reaper.append(pid)
        elif cwd == root and ("KSP" in cmdline or "proton" in cmdline.lower()
                              or "pressure-vessel" in cmdline or "steam.exe" in cmdline):
            others.append(pid)
    return reaper + others


def running_pid(inst: Instance) -> Optional[int]:
    """Whether this instance is up, as a pid — the reaper when we can find it.

    Falls back to the bridge answering, since an instance can be running with its process
    tree unreadable (a different user, an odd launcher) and "the bridge replies" is the
    only fact that actually matters to a caller asking if it is up.
    """
    pids = host_pids(inst)
    if pids:
        return pids[0]
    try:
        inst.connect()
        return -1   # up, but the tree could not be identified
    except BridgeError:
        return None


# ── focus ────────────────────────────────────────────────────────────────

_KWIN_SCRIPT = """
var appClass = "%s";
var targets = %s;
var list = (typeof workspace.windowList === "function")
    ? workspace.windowList()
    : (typeof workspace.clientList === "function" ? workspace.clientList() : []);
for (var i = 0; i < list.length; i++) {
    var w = list[i];
    var match = false;
    // Steam stamps each non-Steam shortcut's window with resourceClass
    // "steam_app_<appid>", which is exact and per-instance. Both KSP windows are
    // captioned "Kerbal Space Program", so this is the only reliable discriminator.
    if (appClass) {
        try {
            match = (String(w.resourceClass) === appClass) ||
                    (String(w.resourceName) === appClass);
        } catch (e) { match = false; }
    }
    if (!match && targets.length) {
        try { match = (targets.indexOf(w.pid) !== -1); } catch (e) { match = false; }
    }
    if (match) {
        try { w.minimized = false; } catch (e) {}
        if ("activeWindow" in workspace) { workspace.activeWindow = w; }
        else { workspace.activeClient = w; }
        break;
    }
}
"""


def focused(inst: Instance) -> bool:
    """Does injected input actually reach THIS instance?

    The only trustworthy test, because nothing else is available: nudge the pointer by
    one pixel and ask the game whether its own Input.mousePosition moved. KWin's
    loadScript/start returns nothing useful, so `focus()` cannot tell success from a
    window that silently refused to take focus — and when it refuses, ydotool's events
    go to whatever IS focused, which with two instances up means the other game.

    Measured: a move aimed at Stock-2 moved Stock-1's pointer from (0,1) to (1400,801)
    while Stock-2's never changed. A click in that state acts on the wrong save, which
    is the one failure mode a two-instance harness must not have.

    One pixel is deliberate — harmless wherever it lands if this instance is not the
    one receiving it.
    """
    try:
        before = inst.pointer()
        if "x" not in before:
            return False
        env = dict(os.environ, YDOTOOL_SOCKET=Instance.YDOTOOL_SOCKET)
        subprocess.run(["ydotool", "mousemove", "-x", "1", "-y", "1"],
                       env=env, capture_output=True, timeout=10)
        time.sleep(0.15)
        after = inst.pointer()
        return (after.get("x"), after.get("y")) != (before.get("x"), before.get("y"))
    except Exception:
        return False


def ensure_focused(inst: Instance, tries: int = 3) -> None:
    """Focus this instance and PROVE input reaches it, or raise.

    Raising is the point. Returning False would let a caller carry on and drive another
    instance by accident; there is no safe way to continue from here.
    """
    for _ in range(tries):
        if focused(inst):
            return
        focus(inst, verify=False)
        time.sleep(0.6)
    raise BridgeError(
        f"{inst.name}: input is not reaching this instance — focus could not be taken. "
        "Another window (possibly the other KSP) is receiving the events, so no mouse "
        "action will be attempted. Raise this instance's window manually, or run mouse "
        "work against one instance at a time.")


def focus(inst: Instance, allow_caption_fallback: bool = True, timeout: float = 10.0,
          verify: bool = True) -> bool:
    """Raise and focus this instance's window. Returns whether input now reaches it.

    Matches on pid first. The caption fallback is opt-out because with two instances
    running both windows are titled "Kerbal Space Program", so falling back would focus
    an arbitrary one — fine when only one is up, wrong exactly when it matters.

    ## Why it verifies

    It used to return True whenever the three `qdbus6` calls did not throw, which is
    not the same question. KWin's `loadScript`/`start` reports nothing about whether a
    window actually took focus, so a silent refusal came back as success — and ydotool
    then delivered every keystroke to whatever *was* focused, which with two instances
    up is the other game.

    That is not a hypothetical either: it cost an hour of this session. Keys aimed at
    one instance opened, refreshed and joined a server in the other, while the intended
    one sat untouched, and every symptom pointed at the panel's own logic rather than at
    the window manager. `focused()` was already sitting right here as the only
    trustworthy test.

    `verify=False` is the cheap path for a caller that will prove it some other way —
    `ensure_focused` does, so it uses it and does not pay for the check twice.
    """
    pids = host_pids(inst)
    if not pids:
        try:
            inst.connect()
        except BridgeError:
            return False

    # KWin reports a window's host pid, so the whole tree is offered: the window is
    # created by one of the Wine processes, not by the reaper we key the tree on.
    # resourceClass is the real discriminator; the pid list is only a fallback for a
    # window Steam did not stamp. The old caption fallback is GONE: both instances are
    # captioned "Kerbal Space Program", so it focused whichever came first — which is
    # why input aimed at one instance kept landing in the other.
    appid = steam_appid(inst.name)
    app_class = f"steam_app_{appid}" if appid else ""
    script = _KWIN_SCRIPT % (
        app_class,
        "[" + ",".join(str(p) for p in pids) + "]")
    stamp = int(time.time() * 1000)
    plugin = f"gkfocus{stamp}"
    # mkstemp, not a predictable /tmp name. KWin EXECUTES this file, and /tmp is shared
    # and world-writable: the old `f"/tmp/gk_focus_{int(time.time()*1000)}.js"` is a name
    # another local account can pre-create as a symlink (so `open(..., "w")` truncates
    # whatever it points at) or replace between the write and loadScript (so KWin runs
    # their JavaScript in this session, with access to every window and to D-Bus). The
    # window is a millisecond-granularity race against a monotonic counter, which is
    # narrow and not nothing. mkstemp is O_EXCL|O_CREAT at mode 0600 with an
    # unpredictable name — the same reason Website/deploy.sh uses `mktemp -d`.
    fd, path = tempfile.mkstemp(prefix="gk_focus_", suffix=".js")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            fh.write(script)
        # loadScript → start → unloadScript. KWin keeps a loaded script registered under
        # its plugin name, so a unique name per call avoids "already loaded" on a second
        # focus, and the unload keeps KWin's script list from growing all session.
        subprocess.run(["qdbus6", "org.kde.KWin", "/Scripting",
                        "org.kde.kwin.Scripting.loadScript", path, plugin],
                       capture_output=True, text=True, timeout=timeout)
        subprocess.run(["qdbus6", "org.kde.KWin", "/Scripting",
                        "org.kde.kwin.Scripting.start"],
                       capture_output=True, text=True, timeout=timeout)
        subprocess.run(["qdbus6", "org.kde.KWin", "/Scripting",
                        "org.kde.kwin.Scripting.unloadScript", plugin],
                       capture_output=True, text=True, timeout=timeout)
        if not verify:
            return True
        # A raise is not instant, and asking too early reports a failure that was
        # only earliness. Two looks rather than one long sleep: the usual case
        # answers on the first.
        for _ in range(2):
            if focused(inst):
                return True
            time.sleep(0.4)
        return False
    except Exception:
        return False
    finally:
        try: os.remove(path)
        except OSError: pass


# ── lifecycle ────────────────────────────────────────────────────────────

def terminate(inst: Instance, save_first: bool = True, timeout: float = 60.0) -> bool:
    """Close KSP. Returns True once the bridge is gone.

    `save_first` asks the game to write persistent.sfs before dying, and is on by default
    because a killed KSP saves nothing: anything the harness did since the last save would
    be silently discarded, which is indistinguishable from the mod failing to persist it —
    the exact confusion this harness exists to avoid. It is skipped automatically in
    flight, where the mod refuses to save anyway.
    """
    pids = host_pids(inst)
    if not pids:
        return True

    if save_first:
        try:
            st = inst.state()
            if st.get("gameLoaded") and st.get("scene") != "FLIGHT":
                inst.post("/gk/debug/actions/save")
                time.sleep(1.5)
        except Exception:
            # Deliberately broad. This is a best-effort courtesy save on the way to a
            # kill, and the instance most in need of killing is the one too wedged to
            # answer — letting its failure propagate would abort the shutdown.
            pass

    # Reaper first: Steam's wrapper brings the tree down with it, so the rest usually
    # never need signalling. Re-read the tree between passes rather than reusing the
    # first list — most of those pids are already gone by then.
    for sig in (signal.SIGTERM, signal.SIGKILL):
        for pid in host_pids(inst):
            try:
                os.kill(pid, sig)
            except (ProcessLookupError, PermissionError):
                continue
        deadline = time.time() + (timeout if sig == signal.SIGTERM else 20.0)
        while time.time() < deadline:
            if not host_pids(inst):
                break
            time.sleep(1.0)
        if not host_pids(inst):
            break

    # The handshake is deleted by a clean shutdown but survives a kill, so a stale file
    # would make the next connect() attempt a dead port. Clear it here.
    try:
        if os.path.exists(inst.handshake_path):
            os.remove(inst.handshake_path)
    except OSError:
        pass
    return not host_pids(inst)


def launch(inst: Instance, wait: float = 300.0) -> bool:
    """Start KSP via Steam and wait for its bridge to come up."""
    appid = steam_appid(inst.name)
    if not appid:
        raise BridgeError(
            f"{inst.name}: no Steam appid found. Looked for the instance name inside each "
            f"prefix's Player.log under {STEAM_COMPATDATA}. Is it added to Steam as a "
            "non-Steam game, and has it been run at least once?")
    # Non-Steam shortcuts are not launched by their appid. Steam addresses them by a
    # 64-bit "gameid" built as (appid << 32) | 0x02000000; passing the bare appid is
    # accepted by the URL handler and silently starts nothing, which is what the first
    # version of this did.
    gameid = (int(appid) << 32) | 0x02000000
    subprocess.Popen(["steam", f"steam://rungameid/{gameid}"],
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # Clear a stale handshake first. A hard-killed KSP leaves one behind, and connect()
    # would spend its whole wait pinging a dead port that may since have been taken by
    # something else entirely.
    try:
        if os.path.exists(inst.handshake_path):
            os.remove(inst.handshake_path)
    except OSError:
        pass
    try:
        inst.connect(wait=wait)
        return True
    except BridgeError:
        return False


def restart(inst: Instance, wait: float = 300.0, save_first: bool = True) -> bool:
    """Close KSP and bring it back with the current DLL.

    The whole point of this module: a mod change is only live after a full relaunch, and
    doing that by hand was the slowest step in every verification round.
    """
    terminate(inst, save_first=save_first)
    time.sleep(3.0)   # let Steam notice the game exited before asking it to start again
    return launch(inst, wait=wait)
