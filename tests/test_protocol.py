"""Protocol tests: tools/gta_client.py against tools/mock_game.py.

These prove the client and the protocol rules agree with the mock. They do NOT
prove anything about the real mod; that needs tools/smoke_test.py on Windows.

    python -m unittest discover -s tests -v
"""
import os
import socket
import sys
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from gta_client import GameClient, GameConnectionError, NoAck  # noqa: E402
from mock_game import MockGame  # noqa: E402

A = {"id": "a", "name": "机场", "x": 300.0, "y": 0.0, "z": 30.0}
B = {"id": "b", "name": "海滩", "x": 300.0, "y": 400.0, "z": 5.0}
C = {"id": "c", "name": "医院", "x": 0.0, "y": 400.0, "z": 30.0}
# Far enough that the mock car is still on the way while the test sends more commands.
FAR = {"id": "a", "name": "机场", "x": 4800.0, "y": 0.0, "z": 30.0}


class ProtocolTest(unittest.TestCase):
    game_kwargs = {}

    def setUp(self):
        self.game = MockGame(port=0, speedup=60.0, heartbeat_s=0.05, **self.game_kwargs).start()
        self.client = GameClient(port=self.game.port).connect()

    def tearDown(self):
        self.client.close()
        self.game.stop()

    def wait_event(self, name, timeout=5.0):
        deadline = time.monotonic() + timeout
        seen = []
        while time.monotonic() < deadline:
            ev = self.client.next_event(timeout=0.2)
            if ev is None:
                continue
            seen.append(ev.get("event"))
            if ev.get("event") == name:
                return ev
        self.fail(f"no '{name}' event within {timeout}s; saw {seen}")

    def state(self):
        return self.client.request("get_state")["state"]


class HappyPath(ProtocolTest):
    def test_two_stops_in_order_with_explicit_continue(self):
        ack = self.client.request("set_route", mission_id="m1", autostart=True, stops=[A, B])
        self.assertTrue(ack["ok"])
        self.assertEqual(ack["nav"], "driving")
        self.assertEqual(self.wait_event("leg_started")["stop"]["id"], "a")

        arrived = self.wait_event("arrived")
        self.assertEqual(arrived["stop"]["id"], "a")
        self.assertFalse(arrived["final"])
        self.assertEqual(arrived["next"]["id"], "b")
        self.assertEqual(self.state()["nav"], "waiting")   # does not move on by itself

        self.assertTrue(self.client.request("continue", mission_id="m1")["ok"])
        arrived = self.wait_event("arrived")
        self.assertEqual(arrived["stop"]["id"], "b")
        self.assertTrue(arrived["final"])
        self.assertEqual(self.wait_event("mission_completed")["skipped"], 0)
        st = self.state()
        self.assertEqual((st["nav"], st["queue"]), ("idle", []))

    def test_load_without_autostart_does_not_move(self):
        self.assertTrue(self.client.request("set_route", mission_id="m1", stops=[A])["ok"])
        time.sleep(0.2)
        st = self.state()
        self.assertEqual(st["nav"], "idle")
        self.assertEqual([s["id"] for s in st["queue"]], ["a"])
        self.assertTrue(self.client.request("start", mission_id="m1")["ok"])
        self.wait_event("mission_completed")

    def test_heartbeat_reports_position_and_target(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[FAR])
        self.wait_event("leg_started")
        deadline = time.monotonic() + 2
        while self.client.last_state is None and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertIsNotNone(self.client.last_state)
        self.assertIn("pos", self.client.last_state)


class Rejections(ProtocolTest):
    def assertRejected(self, ack, error):
        self.assertFalse(ack["ok"], ack)
        self.assertEqual(ack["error"], error)

    def test_invalid_routes_leave_existing_queue_untouched(self):
        self.client.request("set_route", mission_id="keep", stops=[A, B])
        before = self.state()
        self.assertRejected(self.client.request("set_route", stops=[]), "invalid_route")
        self.assertRejected(self.client.request("set_route"), "invalid_route")
        self.assertRejected(self.client.request("set_route", mode="insert", stops=[A]), "invalid_route")
        self.assertRejected(self.client.request("set_route", stops=[A] * 21), "invalid_route")
        self.assertRejected(self.client.request("set_route", stops=[{"name": "x", "x": 1, "y": 2}]), "invalid_stop")
        self.assertRejected(self.client.request("set_route", stops=[{"name": "x", "x": "1", "y": 2, "z": 3}]), "invalid_stop")
        self.assertRejected(self.client.request("set_route", stops=[A, {"name": "far", "x": 99999, "y": 0, "z": 0}]), "invalid_stop")
        self.assertRejected(self.client.request("set_route", stops=["机场"]), "invalid_stop")
        after = self.state()
        self.assertEqual(before["queue"], after["queue"])
        self.assertEqual(after["mission_id"], "keep")

    def test_malformed_json_gets_bad_json_not_silence(self):
        self.client.send_raw("{not json")
        ev = self.client.next_event(timeout=2)
        self.assertEqual(ev["ack"]["error"], "bad_json")
        self.client.send_raw("[1,2,3]")
        ev = self.client.next_event(timeout=2)
        self.assertEqual(ev["ack"]["error"], "bad_json")

    def test_unknown_type_and_wrong_state(self):
        self.assertRejected(self.client.request("teleport"), "unknown_type")
        self.assertRejected(self.client.request("continue"), "invalid_state")
        self.assertRejected(self.client.request("retry"), "invalid_state")
        self.assertRejected(self.client.request("start"), "empty_queue")
        self.assertRejected(self.client.request("skip"), "empty_queue")

    def test_stale_mission_commands_are_refused(self):
        self.client.request("set_route", mission_id="old", autostart=True, stops=[FAR, B])
        self.client.request("set_route", mission_id="new", autostart=True, stops=[C])
        self.assertRejected(self.client.request("skip", mission_id="old"), "stale_mission")
        self.assertRejected(self.client.request("continue", mission_id="old"), "stale_mission")
        self.assertRejected(self.client.request("set_route", mission_id="old", mode="append", stops=[B]), "stale_mission")
        self.assertEqual([s["id"] for s in self.state()["queue"]], ["c"])


class Edits(ProtocolTest):
    def test_append_keeps_remaining_stops_and_mission(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[FAR, B])
        self.wait_event("leg_started")
        self.assertTrue(self.client.request("set_route", mission_id="m1", mode="append", stops=[C])["ok"])
        st = self.state()
        self.assertEqual([s["id"] for s in st["queue"]], ["a", "b", "c"])
        self.assertEqual(st["mission_id"], "m1")
        self.assertEqual(st["nav"], "driving")

    def test_replace_cancels_old_mission(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[FAR, B])
        self.wait_event("leg_started")
        self.client.request("set_route", mission_id="m2", autostart=True, stops=[C])
        cancelled = self.wait_event("cancelled")
        self.assertEqual((cancelled["mission_id"], cancelled["reason"]), ("m1", "replaced"))
        self.assertEqual(self.wait_event("leg_started")["mission_id"], "m2")

    def test_stop_clears_everything_and_is_idempotent(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[FAR, B])
        self.wait_event("leg_started")
        self.assertTrue(self.client.request("stop")["ok"])
        self.assertEqual(self.wait_event("cancelled")["reason"], "stop")
        self.assertTrue(self.client.request("stop")["ok"])
        st = self.state()
        self.assertEqual((st["nav"], st["queue"], st["autopilot"]), ("idle", [], False))

    def test_skip_is_not_reported_as_arrival(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[FAR, B])
        self.wait_event("leg_started")
        self.assertTrue(self.client.request("skip", mission_id="m1")["ok"])
        self.assertEqual(self.wait_event("skipped")["stop"]["id"], "a")
        self.assertEqual(self.wait_event("leg_started")["stop"]["id"], "b")
        self.assertEqual(self.wait_event("arrived")["stop"]["id"], "b")
        self.assertEqual(self.wait_event("mission_completed")["skipped"], 1)


class Stuck(ProtocolTest):
    game_kwargs = {"stuck_legs": (1,)}

    def test_stuck_keeps_stop_then_retry_arrives(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[A, B])
        stuck = self.wait_event("stuck")
        self.assertEqual(stuck["stop"]["id"], "a")
        st = self.state()
        self.assertEqual(st["nav"], "stuck")
        self.assertEqual([s["id"] for s in st["queue"]], ["a", "b"])   # not dropped, not "arrived"
        self.assertTrue(self.client.request("retry", mission_id="m1")["ok"])
        self.assertEqual(self.wait_event("leg_started")["retry"], 1)
        self.assertEqual(self.wait_event("arrived")["stop"]["id"], "a")


class OnFoot(ProtocolTest):
    game_kwargs = {"in_vehicle": False}

    def test_autostart_on_foot_is_refused_and_queue_unchanged(self):
        ack = self.client.request("set_route", mission_id="m1", autostart=True, stops=[A])
        self.assertEqual((ack["ok"], ack["error"]), (False, "no_vehicle"))
        self.assertEqual(self.state()["queue"], [])
        self.assertTrue(self.client.request("set_route", mission_id="m1", stops=[A])["ok"])
        self.assertEqual(self.client.request("start")["error"], "no_vehicle")


class NoAcks(ProtocolTest):
    game_kwargs = {"mute_acks": True}

    def test_silence_is_reported_as_no_ack_not_success(self):
        with self.assertRaises(NoAck):
            self.client.request("set_route", timeout=0.3, autostart=True, stops=[A])


class Dropped(ProtocolTest):
    game_kwargs = {"drop_after_events": 3}   # route_loaded, leg_started, then drop on "arrived"

    def test_disconnect_surfaces_as_event_and_error(self):
        self.client.request("set_route", mission_id="m1", autostart=True, stops=[A, B])
        self.wait_event("disconnected")
        self.assertFalse(self.client.connected)
        with self.assertRaises(GameConnectionError):
            self.client.request("get_state")


class DroppedBeforeAck(ProtocolTest):
    game_kwargs = {"drop_after_events": 2}   # drops after leg_started, before the ack is written

    def test_drop_before_ack_fails_fast_as_connection_error(self):
        started = time.monotonic()
        with self.assertRaises(GameConnectionError):
            self.client.request("set_route", timeout=5, mission_id="m1", autostart=True, stops=[A, B])
        self.assertLess(time.monotonic() - started, 2)


class NotRunning(unittest.TestCase):
    def test_connect_refused_is_a_clear_error(self):
        s = socket.socket()
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
        s.close()
        with self.assertRaises(GameConnectionError):
            GameClient(port=port, connect_timeout=1).connect()


class LegacyV1(ProtocolTest):
    def test_old_messages_still_work(self):
        self.client.send_raw('{"type":"set_waypoints","waypoints":[{"name":"机场","x":300,"y":0,"z":30}]}')
        self.wait_event("route_loaded")
        st = self.state()
        self.assertEqual((st["nav"], len(st["queue"])), ("idle", 1))   # legacy load never autostarts
        self.client.send_raw('{"type":"command","action":"stop"}')
        self.wait_event("cancelled")


if __name__ == "__main__":
    unittest.main()
