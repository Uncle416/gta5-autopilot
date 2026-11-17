"""Walking-skeleton test for the GTA5AutoPilot TCP protocol v2.

Run on the Windows machine with GTA V running and the rebuilt mod loaded
(or against tools/mock_game.py). Standard library only.

    python smoke_test.py state                 # 1. round trip: is the mod answering?
    python smoke_test.py negative              # 2. bad input is rejected, queue untouched (car does not move)
    python smoke_test.py capture airport       # 3. save where the car is now as a named place
    python smoke_test.py run airport beach     # 4. drive the captured places in order
    python smoke_test.py stop                  #    stop and clear the queue at any time

Every line sent and received is written to logs/session-<time>.jsonl. Keep those
files: they are the verification record, and the mock replays from them later.
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gta_client import GameClient, GameConnectionError, NoAck  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
PLACES_PATH = os.path.join(HERE, "captured_places.json")
LOG_DIR = os.path.join(HERE, "logs")


def load_places():
    if not os.path.exists(PLACES_PATH):
        return {}
    with open(PLACES_PATH, encoding="utf-8") as f:
        return json.load(f)


def open_client(args):
    os.makedirs(LOG_DIR, exist_ok=True)
    log = os.path.join(LOG_DIR, time.strftime("session-%Y%m%d-%H%M%S.jsonl"))
    client = GameClient(args.host, args.port, log_path=log).connect()
    print(f"connected to {args.host}:{args.port}  (log: {log})")
    return client


def cmd_state(client, args):
    ack = client.request("get_state")
    print(json.dumps(ack, ensure_ascii=False, indent=2))
    return 0 if ack.get("ok") else 1


def cmd_capture(client, args):
    ack = client.request("get_state")
    state = ack.get("state") or {}
    pos = state.get("pos")
    if not ack.get("ok") or not pos:
        print("could not read position:", ack)
        return 1
    if not state.get("in_vehicle"):
        print("warning: you are on foot. Capture from the driver's seat, parked on a road, so the AI can reach the spot.")
    places = load_places()
    places[args.name] = {"x": pos["x"], "y": pos["y"], "z": pos["z"], "captured_at": time.strftime("%Y-%m-%d %H:%M:%S")}
    with open(PLACES_PATH, "w", encoding="utf-8") as f:
        json.dump(places, f, ensure_ascii=False, indent=2)
    print(f"saved {args.name} = ({pos['x']}, {pos['y']}, {pos['z']}) -> {PLACES_PATH}")
    return 0


def cmd_stop(client, args):
    ack = client.request("stop")
    print("stop:", "ok" if ack.get("ok") else ack)
    return 0 if ack.get("ok") else 1


def cmd_run(client, args):
    places = load_places()
    missing = [n for n in args.names if n not in places]
    if missing:
        print("unknown place(s):", ", ".join(missing), "| captured:", ", ".join(places) or "(none)")
        return 1
    stops = [{"id": n, "name": n, "x": places[n]["x"], "y": places[n]["y"], "z": places[n]["z"]} for n in args.names]
    mission = time.strftime("smoke-%H%M%S")
    ack = client.request("set_route", mission_id=mission, mode="replace", autostart=True, stops=stops)
    if not ack.get("ok"):
        print(f"REJECTED: {ack.get('error')} - {ack.get('message')}")
        return 1
    print(f"accepted, mission {mission}, nav={ack.get('nav')}")

    deadline = time.monotonic() + args.timeout
    last_print = 0.0
    while time.monotonic() < deadline:
        ev = client.next_event(timeout=1.0)
        if ev is None:
            st = client.last_state
            if st and time.monotonic() - last_print > 5:
                last_print = time.monotonic()
                cur = st.get("current") or {}
                print(f"  ... nav={st.get('nav')} -> {cur.get('name')} {cur.get('dist_m')}m  {st.get('speed_mps')}m/s"
                      f"{'  BLOCKED:' + str(st.get('blocked')) if st.get('blocked') else ''}")
            continue
        name = ev.get("event")
        if ev.get("mission_id") not in (None, mission) and name != "disconnected":
            print(f"  (ignoring event from another mission: {name})")
            continue
        print(f"EVENT {name}: " + json.dumps({k: v for k, v in ev.items() if k not in ('type', 'event', 'seq', 'mission_id')}, ensure_ascii=False))
        if name == "arrived" and not ev.get("final"):
            time.sleep(args.dwell)
            ack = client.request("continue", mission_id=mission)
            print("  continue ->", "ok" if ack.get("ok") else f"{ack.get('error')}: {ack.get('message')}")
        elif name == "stuck":
            print("RESULT: STUCK. The mod is waiting for retry / skip / stop. Not counted as arrival.")
            return 2
        elif name == "mission_completed":
            print(f"RESULT: COMPLETED (skipped stops: {ev.get('skipped')})")
            return 0
        elif name == "cancelled":
            print(f"RESULT: CANCELLED ({ev.get('reason')})")
            return 3
        elif name == "disconnected":
            print("RESULT: CONNECTION LOST. The outcome in the game is unknown.")
            return 4
    print(f"RESULT: TIMEOUT after {args.timeout}s. Last state: {client.last_state}")
    return 5


def cmd_negative(client, args):
    """Each bad request must be refused with the expected error and must not change the queue."""
    before = client.request("get_state")["state"]
    results = []

    def expect(label, want_error, ack):
        ok = (not ack.get("ok")) and ack.get("error") == want_error
        results.append(ok)
        print(f"  [{'PASS' if ok else 'FAIL'}] {label}: expected {want_error}, got {ack.get('error') if not ack.get('ok') else 'ok:true'}")

    good = {"id": "p", "name": "p", "x": 100.0, "y": 100.0, "z": 30.0}
    expect("empty stops", "invalid_route", client.request("set_route", stops=[]))
    expect("stops missing", "invalid_route", client.request("set_route"))
    expect("bad mode", "invalid_route", client.request("set_route", mode="insert", stops=[good]))
    expect("missing z", "invalid_stop", client.request("set_route", stops=[{"name": "a", "x": 1, "y": 2}]))
    expect("string coordinate", "invalid_stop", client.request("set_route", stops=[{"name": "a", "x": "1", "y": 2, "z": 3}]))
    expect("out of map", "invalid_stop", client.request("set_route", stops=[good, {"name": "b", "x": 99999, "y": 0, "z": 0}]))
    expect("too many stops", "invalid_route", client.request("set_route", stops=[good] * 21))
    expect("unknown type", "unknown_type", client.request("teleport"))
    expect("stale mission", "stale_mission", client.request("skip", mission_id="not-the-current-mission"))
    if before.get("nav") != "waiting":
        expect("continue when not waiting", "invalid_state", client.request("continue"))
    if before.get("nav") != "stuck":
        expect("retry when not stuck", "invalid_state", client.request("retry"))

    client.drain_events()
    client.send_raw("{this is not json")
    ev = client.next_event(timeout=3.0)
    ack = (ev or {}).get("ack") or {}
    expect("malformed JSON", "bad_json", ack if ack else {"ok": True})

    after = client.request("get_state")["state"]
    unchanged = (before.get("queue") == after.get("queue") and before.get("nav") == after.get("nav")
                 and before.get("mission_id") == after.get("mission_id"))
    results.append(unchanged)
    print(f"  [{'PASS' if unchanged else 'FAIL'}] queue, nav and mission unchanged by rejected requests")
    print(f"{sum(results)}/{len(results)} passed")
    return 0 if all(results) else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=21556)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("state")
    sub.add_parser("negative")
    sub.add_parser("stop")
    p = sub.add_parser("capture")
    p.add_argument("name")
    p = sub.add_parser("run")
    p.add_argument("names", nargs="+")
    p.add_argument("--timeout", type=float, default=1800, help="give up after this many seconds")
    p.add_argument("--dwell", type=float, default=3.0, help="seconds to wait at each stop before continuing")
    args = ap.parse_args()

    try:
        client = open_client(args)
    except GameConnectionError as e:
        print(f"NOT CONNECTED: {e}\nIs GTA V running with the rebuilt mod, and is the game unpaused?")
        return 10
    try:
        return {"state": cmd_state, "negative": cmd_negative, "stop": cmd_stop,
                "capture": cmd_capture, "run": cmd_run}[args.cmd](client, args)
    except NoAck as e:
        print(f"NO ACK: {e}")
        return 11
    except GameConnectionError as e:
        print(f"CONNECTION LOST: {e}")
        return 12
    finally:
        client.close()


if __name__ == "__main__":
    sys.exit(main())
