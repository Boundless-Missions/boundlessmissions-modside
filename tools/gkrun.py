#!/usr/bin/env python3
"""
gkrun.py — run the in-game test pass against one or two live KSP instances.

    ./gkrun.py list                       # which instances have a live bridge
    ./gkrun.py state --a KR-KSP           # dump one instance's snapshot
    ./gkrun.py run T0 --a KR-KSP --b KR2-KSP
    ./gkrun.py run all --a KR-KSP --b KR2-KSP
    ./gkrun.py watch --a KR-KSP           # tail the event stream

Both instances must be running KSP with a dev-channel build:

    cd "KSP Mod Side" && GK_CHANNEL=dev ./build.sh

Nothing here works against a production DLL, by design — see Web/DebugBridge.cs.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from gkbridge import BridgeError, Instance, discover  # noqa: E402
from gkbridge import instances as inst_mgr  # noqa: E402
from gkbridge import scenarios as S  # noqa: E402

DEFAULT_ROOT = os.path.expanduser("~/Documents/KSP DEV Instances")


def make(root: str, name: str, save: str) -> Instance:
    path = name if os.path.isabs(name) else os.path.join(root, name)
    return Instance(path, name=os.path.basename(path.rstrip("/")), expect_save=save)


def cmd_list(args) -> int:
    found = discover(args.root, expect_save=args.save)
    if not found:
        print(f"No live bridges under {args.root}.")
        print("Is KSP running, and was the mod built with GK_CHANNEL=dev ./build.sh ?")
        return 1
    for inst in found:
        try:
            hs = inst.connect()
            st = inst.state()
            ident = st.get("identity") or {}
            print(f"  {inst.name:12s} port={hs.port:<6} mod={hs.mod_version:8s} "
                  f"scene={st.get('scene'):14s} save={st.get('save')!r} "
                  f"user={ident.get('username')!r} account={ident.get('accountId')!r}")
        except BridgeError as exc:
            print(f"  {inst.name:12s} STALE — {exc}")
    return 0


def cmd_state(args) -> int:
    inst = make(args.root, args.a, args.save)
    inst.connect()
    print(json.dumps(inst.state(), indent=2))
    return 0


def cmd_watch(args) -> int:
    inst = make(args.root, args.a, args.save)
    inst.connect()
    # flush=True throughout: this is a live tail, and Python block-buffers stdout the
    # moment it is redirected to a file or a pipe — so a run captured to a log shows
    # nothing at all until the process exits, which for a tail is never.
    print(f"tailing {inst.name} — ctrl-c to stop", flush=True)
    tap = inst.events()
    try:
        while True:
            event, data = tap.wait(lambda e, d: True, "anything", timeout=3600)
            print(f"  {event}: {json.dumps(data)[:300]}", flush=True)
    except KeyboardInterrupt:
        return 0
    except BridgeError as exc:
        print(f"stream ended: {exc}")
        return 1
    finally:
        inst.close()


def cmd_focus(args) -> int:
    inst = make(args.root, args.a, args.save)
    if inst_mgr.focus(inst):
        print(f"focused {inst.name}")
        return 0
    print(f"could not focus {inst.name} (is it running?)")
    return 1


def cmd_kill(args) -> int:
    inst = make(args.root, args.a, args.save)
    ok = inst_mgr.terminate(inst, save_first=not args.no_save)
    print(f"{inst.name}: {'closed' if ok else 'still running'}")
    return 0 if ok else 1


def cmd_launch(args) -> int:
    inst = make(args.root, args.a, args.save)
    print(f"launching {inst.name} (appid {inst_mgr.steam_appid(inst.name)}) …", flush=True)
    ok = inst_mgr.launch(inst, wait=args.wait)
    print(f"{inst.name}: {'up' if ok else 'did not come up in time'}")
    return 0 if ok else 1


def cmd_restart(args) -> int:
    inst = make(args.root, args.a, args.save)
    print(f"restarting {inst.name} …", flush=True)
    ok = inst_mgr.restart(inst, wait=args.wait, save_first=not args.no_save)
    if ok:
        st = inst.state()
        print(f"{inst.name}: up — scene {st.get('scene')} loaded={st.get('gameLoaded')}")
    else:
        print(f"{inst.name}: did not come back within {args.wait:.0f}s")
    return 0 if ok else 1


def cmd_run(args) -> int:
    which = S.ORDER if args.scenario == "all" else [s.upper() for s in args.scenario.split(",")]
    unknown = [w for w in which if w not in S.SCENARIOS]
    if unknown:
        print(f"unknown scenario(s): {unknown}. Known: {', '.join(S.ORDER)}")
        return 2

    if args.single and args.b:
        print("❌ --single and --b are mutually exclusive: --single means one install "
              "playing both roles.")
        return 2
    if args.single and not (args.save_a and args.save_b):
        print("❌ --single needs --save-a and --save-b, and they must differ.\n"
              "   Two saves is not a convenience here: if both roles share one save, the\n"
              "   victim's kerbals and the arriving kerbals are the same roster entries,\n"
              "   and nothing can tell 'renamed aside' from 'the originals were there'.")
        return 2
    if args.single and args.save_a == args.save_b:
        print("❌ --save-a and --save-b must be different saves.")
        return 2

    a = make(args.root, args.a, args.save_a or args.save)
    b = make(args.root, args.b, args.save) if args.b else None
    try:
        a.connect(wait=args.wait)
        if b:
            b.connect(wait=args.wait)
    except BridgeError as exc:
        print(f"❌ {exc}")
        return 2

    # Subscribe before anything acts, so no scenario can race the event it causes.
    a.events()
    if b:
        b.events()

    saves = {"a": args.save_a or args.save, "b": args.save_b} if args.single else None
    if args.single:
        print(f"single-instance mode: {a.name} plays both roles "
              f"(A={args.save_a!r}, B={args.save_b!r}); you will be asked to swap.")

    results = []
    for name in which:
        _title, _fn, needs = S.SCENARIOS[name]
        if needs == 2 and b is None and not args.single:
            print(f"\n── {name}: skipped, needs a second player (--b, or --single)")
            results.append(S.Result(name, _title, S.SKIP, [], "needs --b or --single"))
            continue
        ctx = S.Ctx(a, b, interactive=not args.non_interactive,
                    single=args.single, saves=saves)
        results.append(S.run(name, ctx))

    a.close()
    if b:
        b.close()

    print("\n" + "═" * 72)
    worst = 0
    for r in results:
        mark = {"PASS": "✅", "FAIL": "❌", "SKIP": "⏸ "}[r.status]
        n_ok = sum(1 for c in r.checks if c.status == S.PASS)
        print(f"  {mark} {r.scenario}  {r.status:4s}  {n_ok}/{len(r.checks)} checks  {r.title}")
        for c in r.failed:
            print(f"        ✗ {c.name}" + (f" — {c.detail}" if c.detail else ""))
        if r.note:
            # Printed for FAIL too: a scenario that failed before its manual step did
            # not run to completion, and saying so stops the failure list being read
            # as the whole picture.
            label = "not performed" if r.status == S.SKIP else "stopped before"
            print(f"        {label}: {r.note}")
        if r.status == S.FAIL:
            worst = 1
        elif r.status == S.SKIP and worst == 0:
            worst = 3

    if worst == 1:
        print("\n❌ FAILURES above. Do not publish.")
    elif worst == 3:
        print("\n⏸  Some scenarios were not performed. A skipped scenario is NOT a pass —\n"
             "   the point of this harness is that four rounds of fixes each passed their\n"
             "   tests and three corrupted saves anyway.")
    else:
        print("\n✅ All selected scenarios passed.")
    return worst


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="where the KSP instances live")
    p.add_argument("--save", default="gktest",
                   help="the ONLY save the harness may touch (default: gktest)")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("list").set_defaults(fn=cmd_list)

    pf = sub.add_parser("focus"); pf.add_argument("--a", required=True); pf.set_defaults(fn=cmd_focus)

    pk = sub.add_parser("kill"); pk.add_argument("--a", required=True)
    pk.add_argument("--no-save", action="store_true",
                    help="do not ask the game to save before closing (a killed KSP saves nothing)")
    pk.set_defaults(fn=cmd_kill)

    pl = sub.add_parser("launch"); pl.add_argument("--a", required=True)
    pl.add_argument("--wait", type=float, default=300.0)
    pl.set_defaults(fn=cmd_launch)

    prs = sub.add_parser("restart"); prs.add_argument("--a", required=True)
    prs.add_argument("--wait", type=float, default=300.0)
    prs.add_argument("--no-save", action="store_true")
    prs.set_defaults(fn=cmd_restart)

    ps = sub.add_parser("state"); ps.add_argument("--a", required=True); ps.set_defaults(fn=cmd_state)
    pw = sub.add_parser("watch"); pw.add_argument("--a", required=True); pw.set_defaults(fn=cmd_watch)

    pr = sub.add_parser("run")
    pr.add_argument("scenario", help="'all', or e.g. T0 or T0,T4")
    pr.add_argument("--a", required=True, help="primary instance (issuer / victim)")
    pr.add_argument("--b", help="second instance (rescuer / attacker)")
    pr.add_argument("--single", action="store_true",
                    help="one KSP install plays both roles, swapping account+save between "
                         "them. Sound because every hand-over goes through a server-side "
                         "pending queue, so the two sides never need to be live at once. "
                         "Requires --save-a and --save-b.")
    pr.add_argument("--save-a", help="single mode: the save role A (issuer/victim) lives in")
    pr.add_argument("--save-b", help="single mode: the save role B (rescuer/attacker) lives in")
    pr.add_argument("--wait", type=float, default=0.0,
                    help="seconds to wait for a bridge to appear (for launching KSP alongside)")
    pr.add_argument("--non-interactive", action="store_true",
                    help="do not prompt; scenarios needing a manual step report SKIP")
    pr.set_defaults(fn=cmd_run)

    args = p.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
