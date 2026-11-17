"""Fake game end of the GTA5AutoPilot TCP protocol v2 (docs/PROTOCOL.md).

It does not simulate driving. It speaks the same JSON protocol as the mod so the
agent can be developed and tested without GTA V, including failure cases that are
hard to trigger in the real game. Validation rules and error codes mirror
EntryPoint.cs; if the two ever disagree, the real mod wins and this file is wrong.

Run standalone:
    python mock_game.py --port 21556 --speedup 20
"""
import argparse
import json
import math
import queue
import socket
import threading
import time

ARRIVAL_RADIUS = 25.0
MAX_STOPS = 20


class MockGame:
    def __init__(self, port=21556, speedup=20.0, speed_mps=25.0, start_pos=(0.0, 0.0, 30.0),
                 in_vehicle=True, stuck_legs=(), stuck_after_m=50.0, mute_acks=False,
                 drop_after_events=None, heartbeat_s=1.0):
        self.port = port
        self.speedup = speedup              # simulated seconds per real second
        self.speed_mps = speed_mps
        self.pos = list(start_pos)
        self.in_vehicle = in_vehicle
        self.stuck_legs = set(stuck_legs)   # leg numbers (1-based, counted per mission) that get stuck once
        self.stuck_after_m = stuck_after_m
        self.mute_acks = mute_acks          # scenario: accept commands but never answer
        self.drop_after_events = drop_after_events  # scenario: close all connections after N events
        self.heartbeat_s = heartbeat_s

        self.queue = []                     # list of dict(id, name, x, y, z)
        self.nav = "idle"
        self.autopilot = False
        self.mission_id = None
        self.skipped = 0
        self.leg_no = 0
        self.leg_retries = 0
        self.leg_travelled = 0.0
        self.event_seq = 0
        self.state_seq = 0
        self.clock_ms = 0
        self.local_counter = 0
        self.events_sent = 0

        self._inbound = queue.Queue()
        self._clients = {}
        self._clients_lock = threading.Lock()
        self._next_client = 0
        self._running = False
        self._listener = None

    # ---- lifecycle -----------------------------------------------------
    def start(self):
        self._listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._listener.bind(("127.0.0.1", self.port))
        self.port = self._listener.getsockname()[1]
        self._listener.listen(8)
        self._running = True
        threading.Thread(target=self._accept_loop, daemon=True).start()
        threading.Thread(target=self._tick_loop, daemon=True).start()
        return self

    def stop(self):
        self._running = False
        try:
            self._listener.close()
        except OSError:
            pass
        self._drop_clients()

    def _drop_clients(self):
        with self._clients_lock:
            socks = list(self._clients.values())
            self._clients.clear()
        for s in socks:
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                s.close()
            except OSError:
                pass

    # ---- network threads (never touch game state) ----------------------
    def _accept_loop(self):
        while self._running:
            try:
                sock, _ = self._listener.accept()
            except OSError:
                return
            with self._clients_lock:
                self._next_client += 1
                cid = self._next_client
                self._clients[cid] = sock
            threading.Thread(target=self._read_loop, args=(cid, sock), daemon=True).start()

    def _read_loop(self, cid, sock):
        buf = b""
        try:
            while self._running:
                chunk = sock.recv(65536)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    raw, buf = buf.split(b"\n", 1)
                    line = raw.decode("utf-8", errors="replace").strip()
                    if line:
                        self._inbound.put((cid, line))
        except OSError:
            pass
        finally:
            with self._clients_lock:
                self._clients.pop(cid, None)

    def _send(self, cid, obj):
        with self._clients_lock:
            sock = self._clients.get(cid)
        if sock is None:
            return
        try:
            sock.sendall(json.dumps(obj, ensure_ascii=False).encode("utf-8") + b"\n")
        except OSError:
            with self._clients_lock:
                self._clients.pop(cid, None)

    def _broadcast(self, obj):
        with self._clients_lock:
            cids = list(self._clients)
        for cid in cids:
            self._send(cid, obj)

    # ---- "script thread" -----------------------------------------------
    def _tick_loop(self):
        dt_real = 0.02
        last_hb = time.monotonic()
        while self._running:
            time.sleep(dt_real)
            self.clock_ms += int(dt_real * self.speedup * 1000)
            while True:
                try:
                    cid, line = self._inbound.get_nowait()
                except queue.Empty:
                    break
                self._handle_line(cid, line)
            self._drive(dt_real * self.speedup)
            if time.monotonic() - last_hb >= self.heartbeat_s:
                last_hb = time.monotonic()
                self._broadcast(self._state())

    def _drive(self, dt):
        if not (self.autopilot and self.nav == "driving" and self.queue):
            return
        if not self.in_vehicle:
            return
        stop = self.queue[0]
        dx, dy = stop["x"] - self.pos[0], stop["y"] - self.pos[1]
        dist = math.hypot(dx, dy)
        if self.leg_no in self.stuck_legs and self.leg_travelled >= self.stuck_after_m:
            self.stuck_legs.discard(self.leg_no)
            self.nav = "stuck"
            self._emit("stuck", stop=self._stop_json(stop), dist_m=round(dist, 1),
                       stationary_ms=90000, retries=self.leg_retries)
            return
        if dist < ARRIVAL_RADIUS:
            self._arrive(stop, dist)
            return
        step = min(self.speed_mps * dt, dist - ARRIVAL_RADIUS + 1.0)
        self.pos[0] += dx / dist * step
        self.pos[1] += dy / dist * step
        self.leg_travelled += step

    def _arrive(self, stop, dist):
        self.queue.pop(0)
        final = not self.queue
        self.nav = "idle" if final else "waiting"
        self._emit("arrived", stop=self._stop_json(stop), near=False, dist_m=round(dist, 1), final=final,
                   next=None if final else self._stop_json(self.queue[0]))
        if final:
            self.autopilot = False
            self._emit("mission_completed", skipped=self.skipped)

    # ---- protocol ------------------------------------------------------
    def _handle_line(self, cid, line):
        try:
            obj = json.loads(line)
        except json.JSONDecodeError as e:
            return self._nack(cid, None, "bad_json", str(e))
        if not isinstance(obj, dict):
            return self._nack(cid, None, "bad_json", "not a JSON object")
        rid = obj.get("id")
        type_ = obj.get("type")
        req_mission = obj.get("mission_id")

        if type_ in ("hello", "get_state"):
            return self._ack(cid, rid, state=self._state())
        if type_ == "set_route":
            return self._set_route(cid, rid, obj, legacy=False)
        if type_ == "set_waypoints":
            return self._set_route(cid, rid, obj, legacy=True)
        if type_ == "command":
            if obj.get("action") == "continue":
                self._continue()
                return self._ack(cid, rid)
            if obj.get("action") == "stop":
                self._stop_all("stop")
                return self._ack(cid, rid)
            return self._nack(cid, rid, "unknown_type", "unknown action")

        if type_ in ("start", "continue", "retry", "skip"):
            if req_mission is not None and req_mission != self.mission_id:
                return self._nack(cid, rid, "stale_mission", f"current mission is {self.mission_id or 'none'}")

        if type_ == "start":
            if self.nav == "idle":
                err = self._start_mission()
                return self._ack(cid, rid) if err is None else self._nack(cid, rid, err, "cannot start")
            return self._nack(cid, rid, "invalid_state", "already running")
        if type_ == "continue":
            if self.nav != "waiting":
                return self._nack(cid, rid, "invalid_state", "not waiting at a stop")
            if not self.in_vehicle:
                return self._nack(cid, rid, "no_vehicle", "cannot continue")
            self._continue()
            return self._ack(cid, rid)
        if type_ == "retry":
            if self.nav != "stuck":
                return self._nack(cid, rid, "invalid_state", "cannot retry")
            if not self.in_vehicle:
                return self._nack(cid, rid, "no_vehicle", "cannot retry")
            self.leg_retries += 1
            self.nav = "driving"
            self._begin_leg(retry=True)
            return self._ack(cid, rid)
        if type_ == "skip":
            if not self.queue:
                return self._nack(cid, rid, "empty_queue", "cannot skip")
            was_active = self.nav != "idle"
            skipped = self.queue.pop(0)
            self.skipped += 1
            self._emit("skipped", stop=self._stop_json(skipped), remaining=len(self.queue))
            if self.queue and was_active:
                self.nav = "driving"
                self._begin_leg()
            elif not self.queue:
                self.nav = "idle"
                if was_active:
                    self.autopilot = False
                    self._emit("mission_completed", skipped=self.skipped)
            return self._ack(cid, rid)
        if type_ == "stop":
            self._stop_all("stop")
            return self._ack(cid, rid)
        return self._nack(cid, rid, "unknown_type", f"unknown type: {type_ or '(missing)'}")

    def _set_route(self, cid, rid, obj, legacy):
        mode = "replace" if legacy else (obj.get("mode") or "replace")
        if mode not in ("replace", "append"):
            return self._nack(cid, rid, "invalid_route", "mode must be replace or append")
        arr = obj.get("waypoints" if legacy else "stops")
        if not isinstance(arr, list) or not arr or len(arr) > MAX_STOPS:
            return self._nack(cid, rid, "invalid_route", f"stops must contain 1..{MAX_STOPS} items")
        stops = []
        for i, item in enumerate(arr):
            problem, stop = self._parse_stop(item)
            if problem:
                return self._nack(cid, rid, "invalid_stop", f"stops[{i}]: {problem}")
            stops.append(stop)
        req_mission = obj.get("mission_id")
        autostart = (not legacy) and obj.get("autostart") is True

        if mode == "append":
            if req_mission is not None and self.mission_id is not None and req_mission != self.mission_id:
                return self._nack(cid, rid, "stale_mission", f"current mission is {self.mission_id}")
            if len(self.queue) + len(stops) > MAX_STOPS:
                return self._nack(cid, rid, "invalid_route", f"queue would exceed {MAX_STOPS} stops")
            will_start = autostart and self.nav == "idle"
            if will_start and not self.in_vehicle:
                return self._nack(cid, rid, "no_vehicle", "cannot autostart; queue unchanged")
            if self.mission_id is None:
                self.mission_id = req_mission or self._new_local_id()
            self.queue.extend(stops)
            self._emit("route_loaded", mode="append", queue=self._queue_json())
            if will_start:
                self._start_mission()
            return self._ack(cid, rid)

        if autostart and not self.in_vehicle:
            return self._nack(cid, rid, "no_vehicle", "cannot autostart; queue unchanged")
        if self.nav != "idle":
            self._emit("cancelled", reason="replaced")
        self.autopilot = False
        self.mission_id = req_mission or self._new_local_id()
        self.skipped = 0
        self.leg_no = 0
        self.queue = stops
        self.nav = "idle"
        self._emit("route_loaded", mode="replace", queue=self._queue_json())
        if autostart:
            self._start_mission()
        return self._ack(cid, rid)

    @staticmethod
    def _parse_stop(item):
        if not isinstance(item, dict):
            return "not an object", None
        vals = {}
        for key, lo, hi in (("x", -4500, 5000), ("y", -4500, 8500), ("z", -200, 1500)):
            v = item.get(key)
            if isinstance(v, bool) or not isinstance(v, (int, float)) or math.isnan(v) or math.isinf(v):
                return f"{key} missing or not a number", None
            if v < lo or v > hi:
                return f"{key} out of range", None
            vals[key] = float(v)
        name = item.get("name")
        name = str(name)[:80] if isinstance(name, (str, int, float)) and str(name).strip() else "Unknown"
        sid = item.get("id")
        vals.update(id=None if sid is None else str(sid), name=name)
        return None, vals

    def _start_mission(self):
        if not self.queue:
            return "empty_queue"
        if not self.in_vehicle:
            return "no_vehicle"
        if self.mission_id is None:
            self.mission_id = self._new_local_id()
        self.autopilot = True
        self.nav = "driving"
        self._begin_leg()
        return None

    def _continue(self):
        if self.nav == "waiting" and self.queue:
            self.autopilot = True
            self.nav = "driving"
            self._begin_leg()

    def _begin_leg(self, retry=False):
        if not retry:
            self.leg_retries = 0
            self.leg_no += 1
        self.leg_travelled = 0.0
        self._emit("leg_started", stop=self._stop_json(self.queue[0]), retry=self.leg_retries, remaining=len(self.queue))

    def _stop_all(self, reason):
        had = self.nav != "idle" or bool(self.queue)
        self.queue = []
        self.nav = "idle"
        self.autopilot = False
        if had:
            self._emit("cancelled", reason=reason)

    def _new_local_id(self):
        self.local_counter += 1
        return f"local-{self.local_counter}"

    # ---- message builders ----------------------------------------------
    @staticmethod
    def _stop_json(stop):
        return {"id": stop.get("id"), "name": stop.get("name", "")}

    def _queue_json(self):
        return [self._stop_json(s) for s in self.queue]

    def _state(self):
        self.state_seq += 1
        cur = None
        if self.queue and self.nav != "idle":
            s = self.queue[0]
            cur = dict(self._stop_json(s), dist_m=round(math.hypot(s["x"] - self.pos[0], s["y"] - self.pos[1]), 1))
        return {"type": "state", "seq": self.state_seq, "clock_ms": self.clock_ms, "nav": self.nav,
                "autopilot": self.autopilot, "braking": False, "mission_id": self.mission_id, "blocked": None,
                "in_vehicle": self.in_vehicle,
                "pos": {"x": round(self.pos[0], 1), "y": round(self.pos[1], 1), "z": round(self.pos[2], 1)},
                "speed_mps": self.speed_mps if (self.nav == "driving" and self.autopilot) else 0,
                "current": cur, "queue": self._queue_json(), "style": 0.15}

    def _ack(self, cid, rid, **extra):
        if self.mute_acks:
            return
        msg = dict(extra, type="ack", id=rid, ok=True, mission_id=self.mission_id, nav=self.nav)
        self._send(cid, msg)

    def _nack(self, cid, rid, error, message):
        if self.mute_acks:
            return
        self._send(cid, {"type": "ack", "id": rid, "ok": False, "error": error, "message": message,
                         "mission_id": self.mission_id, "nav": self.nav})

    def _emit(self, name, **data):
        self.event_seq += 1
        msg = dict(data, type="event", event=name, seq=self.event_seq, clock_ms=self.clock_ms, mission_id=self.mission_id)
        self._broadcast(msg)
        self.events_sent += 1
        if self.drop_after_events is not None and self.events_sent >= self.drop_after_events:
            self.drop_after_events = None
            self._drop_clients()


def main():
    ap = argparse.ArgumentParser(description="Fake GTA5AutoPilot game end for development")
    ap.add_argument("--port", type=int, default=21556)
    ap.add_argument("--speedup", type=float, default=20.0, help="simulated seconds per real second")
    ap.add_argument("--no-vehicle", action="store_true", help="player is on foot")
    ap.add_argument("--stuck-leg", type=int, action="append", default=[], help="leg number that gets stuck once")
    ap.add_argument("--mute-acks", action="store_true", help="never answer requests")
    ap.add_argument("--drop-after-events", type=int, help="disconnect all clients after N events")
    args = ap.parse_args()
    game = MockGame(port=args.port, speedup=args.speedup, in_vehicle=not args.no_vehicle,
                    stuck_legs=args.stuck_leg, mute_acks=args.mute_acks,
                    drop_after_events=args.drop_after_events).start()
    print(f"mock game listening on 127.0.0.1:{game.port} (Ctrl+C to quit)")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        game.stop()


if __name__ == "__main__":
    main()
