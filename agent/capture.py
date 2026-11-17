"""Record the car's current in-game position as the verified coordinates of a place.

Park on the road at the place, in the driver's seat, then:

    python -m agent.capture lsia
    python -m agent.capture --list          # show which places are still unverified
"""
import argparse
import os
import sys

from .gazetteer import Gazetteer

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from gta_client import GameClient, GameConnectionError, NoAck  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("place_id", nargs="?")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=21556)
    args = ap.parse_args()
    gaz = Gazetteer()

    if args.list or not args.place_id:
        todo = [p for p in gaz.places.values() if not p.get("verified")]
        print(f"{len(gaz.places) - len(todo)}/{len(gaz.places)} verified. Unverified:")
        for p in todo:
            print(f"  {p['id']:<20} {p['name']}")
        return 0
    if not gaz.get(args.place_id):
        print(f"unknown place id: {args.place_id}. Closest: {', '.join(gaz.search(args.place_id)) or '(none)'}")
        return 1
    try:
        with GameClient(args.host, args.port) as client:
            state = client.request("get_state").get("state") or {}
    except (GameConnectionError, NoAck) as e:
        print(f"could not read position: {e}")
        return 2
    pos = state.get("pos")
    if not pos:
        print("the game did not report a position")
        return 2
    if not state.get("in_vehicle"):
        print("refusing: you are on foot. Capture from the driver's seat, parked on a road the AI can reach.")
        return 3
    old = gaz.get(args.place_id)
    gaz.set_coordinates(args.place_id, pos["x"], pos["y"], pos["z"])
    print(f"{old['name']} ({args.place_id}): ({old['x']}, {old['y']}, {old['z']}) -> ({pos['x']}, {pos['y']}, {pos['z']})  verified")
    return 0


if __name__ == "__main__":
    sys.exit(main())
