"""Agent tests: agent/ against tools/mock_game.py with a scripted fake model.

They check the agent's own logic (rules, guards, confirmation, staleness, error
handling). They say nothing about how well a real model understands Chinese; that
is what agent/eval.py measures.

    python -m unittest discover -s tests -v
"""
import json
import os
import sys
import threading
import time
import unittest

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tools"))
from agent.core import Agent  # noqa: E402
from agent.gazetteer import Gazetteer  # noqa: E402
from agent.llm import LLMError  # noqa: E402
from agent.nlu import Context, interpret  # noqa: E402
from mock_game import MockGame  # noqa: E402


def tool(name, **args):
    return {"content": None, "tool_calls": [{"type": "function", "function": {"name": name, "arguments": json.dumps(args, ensure_ascii=False)}}]}


class FakeLLM:
    """Returns scripted replies in order; records what it was asked."""

    def __init__(self, *replies, delay=0.0):
        self.replies = list(replies)
        self.calls = []
        self.delay = delay

    def chat(self, messages, tools=None):
        self.calls.append(messages)
        if self.delay:
            time.sleep(self.delay)
        if not self.replies:
            raise AssertionError("model was called more often than the test expected")
        reply = self.replies.pop(0)
        if isinstance(reply, Exception):
            raise reply
        return reply

    @property
    def last_user_text(self):
        return self.calls[-1][-1]["content"]


class AgentTest(unittest.TestCase):
    game_kwargs = {}
    agent_kwargs = {}
    speedup = 2000.0        # whole map in a blink; tests that need a car "on the way" lower it

    def setUp(self):
        self.game = MockGame(port=0, speedup=self.speedup, heartbeat_s=0.05, **self.game_kwargs).start()
        self.said = []
        self.gaz = Gazetteer()

    def make(self, *replies, **kw):
        self.llm = FakeLLM(*replies, delay=kw.pop("delay", 0.0))
        opts = dict(port=self.game.port, allow_unverified=True, async_llm=False, dwell_s=0.05,
                    reconnect=False, ack_timeout=1.0)
        opts.update(self.agent_kwargs)
        opts.update(kw)
        self.agent = Agent(self.gaz, self.llm, self.said.append, **opts)
        self.assertTrue(self.agent.connect())
        time.sleep(0.12)    # let one heartbeat arrive
        return self.agent

    def tearDown(self):
        if hasattr(self, "agent"):
            self.agent.close()
        self.game.stop()

    def wait_said(self, fragment, timeout=5.0):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for line in list(self.said):
                if fragment in line:
                    return line
            time.sleep(0.02)
        self.fail(f"agent never said {fragment!r}. It said:\n  " + "\n  ".join(self.said))

    def assertNeverSaid(self, fragment):
        self.assertFalse([s for s in self.said if fragment in s], self.said)

    def queue_ids(self):
        return [s["id"] for s in self.game.queue]


class NewRoute(AgentTest):
    def test_confirm_then_drive_all_stops_in_order(self):
        a = self.make(tool("propose_route", stops=["casino", "observatory"]))
        a.submit("先去赌场，再去天文台")
        prompt = self.wait_said("确认吗")
        self.assertIn("钻石赌场 → 伽利略天文台", prompt)
        self.assertEqual(self.game.queue, [])            # nothing sent before the user confirms
        a.submit("好")
        self.wait_said("全部行程结束")
        arrivals = [s for s in self.said if s.startswith("已到达")]
        self.assertIn("钻石赌场", arrivals[0])
        self.assertIn("伽利略天文台", arrivals[1])
        self.assertIn("所有站点都已到达", self.said[-1])

    def test_declined_plan_is_not_sent(self):
        a = self.make(tool("propose_route", stops=["casino"]))
        a.submit("去赌场")
        self.wait_said("确认吗")
        a.submit("不")
        self.wait_said("已放弃")
        self.assertEqual((self.game.queue, self.game.nav), ([], "idle"))

    def test_new_request_replaces_unconfirmed_plan(self):
        a = self.make(tool("propose_route", stops=["casino"]), tool("propose_route", stops=["golf"]))
        a.submit("去赌场")
        self.wait_said("钻石赌场")
        a.submit("还是去打高尔夫吧")
        self.wait_said("高尔夫")
        a.submit("好")
        self.wait_said("已下发")
        self.assertNotIn("casino", [s["id"] for s in self.game.queue] + [self.game.mission_id])
        self.wait_said("已到达洛圣都高尔夫俱乐部")


class SlowCar(AgentTest):
    speedup = 1.0           # 25 m/s: the car stays on its first leg for the whole test

    def start_route(self, *ids):
        a = self.make()
        self.llm.replies.append(tool("propose_route", stops=list(ids)))
        a.submit("出发去这些地方")
        self.wait_said("确认吗")
        a.submit("好")
        self.wait_said("正在前往")
        self.said.clear()
        return a

    def test_coordinates_come_from_gazetteer_not_from_model(self):
        a = self.make(tool("propose_route", stops=["casino"], x=1, y=2, z=3))
        a.submit("去赌场")
        self.wait_said("确认吗")
        a.submit("确认")
        self.wait_said("已下发")
        place = self.gaz.get("casino")
        self.assertEqual((self.game.queue[0]["x"], self.game.queue[0]["y"]), (float(place["x"]), float(place["y"])))

    def test_append_keeps_existing_stops(self):
        a = self.start_route("lsia", "casino")
        self.llm.replies.append(tool("propose_route", stops=["lsia", "casino", "vespucci_beach"]))
        a.submit("再加一站海滩")
        prompt = self.wait_said("确认吗")
        self.assertIn("追加", prompt)
        mission = self.game.mission_id
        a.submit("好")
        self.wait_said("已追加")
        self.assertEqual(self.queue_ids(), ["lsia", "casino", "vespucci_beach"])
        self.assertEqual(self.game.mission_id, mission)          # same mission, car not interrupted
        self.assertEqual(self.game.nav, "driving")

    def test_replace_one_stop_shows_diff_and_reroutes(self):
        a = self.start_route("lsia", "casino")
        self.llm.replies.append(tool("propose_route", stops=["golf", "casino"]))
        a.submit("不去机场了，改去高尔夫")
        prompt = self.wait_said("确认吗")
        self.assertIn("去掉：洛圣都国际机场", prompt)
        self.assertIn("新增：洛圣都高尔夫俱乐部", prompt)
        self.assertIn("改道", prompt)
        self.assertEqual(self.queue_ids(), ["lsia", "casino"])   # untouched until confirmed
        a.submit("好")
        self.wait_said("已下发")
        self.assertEqual(self.queue_ids(), ["golf", "casino"])

    def test_stop_is_handled_without_the_model(self):
        a = self.start_route("lsia", "casino")
        self.assertEqual(self.llm.replies, [])                   # any model call now would fail the test
        a.submit("停下")
        self.wait_said("已停止")
        self.assertEqual((self.game.queue, self.game.nav, self.game.autopilot), ([], "idle", False))

    def test_go_to_airport_is_not_mistaken_for_continue(self):
        a = self.start_route("casino")
        self.llm.replies.append(tool("propose_route", stops=["lsia"]))
        a.submit("去机场")                                        # contains 去; the old sidecar sent "continue"
        self.wait_said("洛圣都国际机场")
        self.assertEqual(len(self.llm.calls), 2)

    def test_late_model_answer_after_stop_is_discarded(self):
        a = self.start_route("lsia", "casino")
        self.agent.async_llm = True
        self.llm.delay = 0.4
        self.llm.replies.append(tool("propose_route", stops=["golf"]))
        a.submit("改去高尔夫")
        time.sleep(0.05)
        a.submit("停下")
        self.wait_said("已停止")
        time.sleep(0.7)                                           # the slow answer has arrived by now
        self.assertNeverSaid("高尔夫")
        self.assertEqual((self.game.queue, self.game.nav), ([], "idle"))

    def test_status_reports_target_and_rest(self):
        a = self.start_route("lsia", "casino")
        a.submit("到哪了")
        line = self.wait_said("正在前往洛圣都国际机场")
        self.assertIn("钻石赌场", line)

    def test_unchanged_route_is_reported_not_resent(self):
        a = self.start_route("lsia", "casino")
        mission = self.game.mission_id
        self.llm.replies.append(tool("propose_route", stops=["lsia", "casino"]))
        a.submit("先去机场再去赌场")
        self.wait_said("没有变化")
        self.assertEqual(self.game.mission_id, mission)


class Understanding(AgentTest):
    def test_unknown_place_asks_and_changes_nothing(self):
        a = self.make(tool("propose_route", stops=["casino"], unresolved=["火星基地"]))
        a.submit("去赌场，然后去火星基地")
        line = self.wait_said("没有「火星基地」")
        self.assertIn("原行程没有改动", line)
        self.assertNeverSaid("确认吗")                            # no partial route offered
        self.assertEqual(self.game.queue, [])

    def test_model_picking_a_hospital_at_random_is_turned_into_a_question(self):
        a = self.make(tool("propose_route", stops=["hospital_pillbox"]),
                      tool("propose_route", stops=["hospital_sandy"]))
        a.submit("带我去医院")
        line = self.wait_said("有好几个")
        self.assertIn("桑迪海岸医院", line)
        self.assertEqual(self.game.queue, [])
        options = self.agent._pending["option_ids"]
        a.submit(str(options.index("hospital_sandy") + 1))        # answer with the number
        self.assertEqual(self.llm.last_user_text, "桑迪海岸医院")
        self.assertIn("桑迪海岸医院", self.wait_said("确认吗"))

    def test_specific_name_is_not_questioned(self):
        a = self.make(tool("propose_route", stops=["hospital_sandy"]))
        a.submit("去桑迪海岸医院")
        self.wait_said("确认吗")
        self.assertNeverSaid("有好几个")

    def test_default_for_generic_word_is_stated(self):
        a = self.make(tool("propose_route", stops=["lsia"]))
        a.submit("去机场")
        self.assertIn("按默认理解为洛圣都国际机场", self.wait_said("确认吗"))

    def test_inferred_place_is_flagged(self):
        a = self.make(tool("propose_route", stops=["golf"]))
        a.submit("我想打两杆")
        self.assertIn("推断", self.wait_said("确认吗"))

    def test_invented_id_gets_one_corrective_retry(self):
        a = self.make(tool("propose_route", stops=["mars_base"]), tool("propose_route", stops=["casino"]))
        a.submit("去赌场")
        self.wait_said("钻石赌场")
        self.assertIn("mars_base", self.llm.last_user_text)       # the model was told what was wrong

    def test_contradiction_question_from_model_is_passed_on(self):
        a = self.make(tool("ask_user", question="你前后说的不一致：要去机场还是不去？"))
        a.submit("先去机场，又不要去机场")
        self.wait_said("不一致")
        self.assertEqual(self.game.queue, [])

    def test_prose_without_tool_call_never_counts_as_action(self):
        a = self.make({"content": "好的，马上带你去机场！"}, {"content": "没问题，出发！"})
        a.submit("去国际机场")
        line = self.wait_said("模型不可用")
        self.assertIn("输出不合格式", line)
        self.assertIn("确认吗", line)                              # keyword match is offered, not executed
        self.assertEqual(self.game.queue, [])

    def test_tool_call_as_json_in_content_is_accepted(self):
        a = self.make({"content": '```json\n{"tool": "propose_route", "args": {"stops": ["casino"]}}\n```'})
        a.submit("去赌场")
        self.wait_said("确认吗")


class ModelDown(AgentTest):
    def test_timeout_falls_back_to_keywords_and_always_confirms(self):
        a = self.make(LLMError("timeout", "x"), confirm=False)
        a.submit("先去国际机场，然后去天文台")
        line = self.wait_said("模型不可用：超时")
        self.assertIn("洛圣都国际机场 → 伽利略天文台", line)
        self.assertIn("确认吗", line)                              # even with confirm=False
        self.assertEqual(self.game.queue, [])

    def test_edit_sentence_is_refused_in_keyword_mode(self):
        a = self.make(LLMError("network", "x"))
        a.submit("不去机场了，改去天文台")
        line = self.wait_said("模型不可用：网络错误")
        self.assertIn("原行程没有改动", line)
        self.assertNeverSaid("确认吗")

    def test_no_key_configured(self):
        self.llm = None
        self.agent = Agent(self.gaz, None, self.said.append, port=self.game.port, allow_unverified=True,
                           async_llm=False, reconnect=False)
        self.agent.connect()
        self.agent.submit("去赌场")
        self.wait_said("未配置")


class GameSide(AgentTest):
    def test_unverified_coordinates_are_refused_by_default(self):
        a = self.make(tool("propose_route", stops=["casino"]), allow_unverified=False)
        a.submit("去赌场")
        self.wait_said("还没有在游戏里实测")
        self.assertNeverSaid("确认吗")

    def test_not_connected_is_said_plainly(self):
        self.llm = FakeLLM(tool("propose_route", stops=["casino"]))
        self.agent = Agent(self.gaz, self.llm, self.said.append, port=1, allow_unverified=True,
                           async_llm=False, reconnect=False)
        self.assertFalse(self.agent.connect())
        self.agent.submit("去赌场")
        self.wait_said("连不上游戏")
        self.agent.submit("停下")
        self.wait_said("小键盘 2")                                 # tells the user the manual way to stop


class OnFoot(AgentTest):
    game_kwargs = {"in_vehicle": False}

    def test_plan_is_kept_until_the_player_is_in_a_car(self):
        a = self.make(tool("propose_route", stops=["casino"]))
        a.submit("去赌场")
        self.wait_said("确认吗")
        a.submit("好")
        self.wait_said("不在车里")
        self.assertEqual(self.game.queue, [])
        self.game.in_vehicle = True
        a.submit("确认")
        self.wait_said("已下发")
        self.wait_said("已到达钻石赌场")


class StuckOnce(AgentTest):
    game_kwargs = {"stuck_legs": (1,)}

    def test_one_automatic_retry_then_arrives(self):
        a = self.make(tool("propose_route", stops=["casino"]))
        a.submit("去赌场")
        self.wait_said("确认吗")
        a.submit("好")
        self.wait_said("自动重试一次")
        self.wait_said("第 1 次重试")
        self.wait_said("已到达钻石赌场")


class StuckAsksUser(AgentTest):
    game_kwargs = {"stuck_legs": (1,)}
    agent_kwargs = {"max_auto_retry": 0}

    def test_skip_needs_the_user_and_is_never_called_arrival(self):
        a = self.make(tool("propose_route", stops=["casino", "observatory"]))
        a.submit("先去赌场再去天文台")
        self.wait_said("确认吗")
        a.submit("好")
        line = self.wait_said("没有算作到达")
        self.assertIn("跳过", line)
        self.assertEqual(self.game.nav, "stuck")                  # waits; does not skip on its own
        a.submit("跳过")
        self.wait_said("已跳过钻石赌场（没有到达）")
        self.assertIn("1 站是跳过的", self.wait_said("全部行程结束"))
        self.assertFalse([s for s in self.said if s.startswith("已到达钻石赌场")])


class NoAcks(AgentTest):
    game_kwargs = {"mute_acks": True}

    def test_missing_ack_is_reported_as_unknown_not_done(self):
        self.llm = FakeLLM()
        self.agent = Agent(self.gaz, self.llm, self.said.append, port=self.game.port, allow_unverified=True,
                           async_llm=False, reconnect=False, ack_timeout=0.3)
        self.agent.connect()
        self.agent.submit("停下")
        self.wait_said("没有回执")
        self.assertNeverSaid("已停止")


class Dropped(AgentTest):
    game_kwargs = {"drop_after_events": 3}
    speedup = 300.0

    def test_disconnect_is_announced(self):
        a = self.make(tool("propose_route", stops=["casino", "observatory"]))
        a.submit("先去赌场再去天文台")
        self.wait_said("确认吗")
        a.submit("好")
        self.wait_said("连接断了")


class Rules(unittest.TestCase):
    """interpret() with a model that must not be called."""

    def setUp(self):
        self.gaz = Gazetteer()

    def decide(self, text, **ctx):
        return interpret(text, Context(**ctx), self.gaz, FakeLLM())

    def test_exact_phrases_only(self):
        self.assertEqual(self.decide("停下").action, "stop")
        self.assertEqual(self.decide("停车！").action, "stop")
        self.assertEqual(self.decide("STOP").action, "stop")
        self.assertEqual(self.decide("继续", nav="waiting").action, "continue")
        self.assertEqual(self.decide("继续", nav="stuck").action, "retry")
        self.assertEqual(self.decide("跳过这一站").action, "skip")

    def test_yes_no_only_mean_something_while_confirming(self):
        self.assertEqual(self.decide("好", pending="confirm").kind, "confirm_yes")
        self.assertEqual(self.decide("取消", pending="confirm").kind, "confirm_no")
        self.assertEqual(self.decide("取消").action, "stop")

    def test_sentences_containing_trigger_words_go_to_the_model(self):
        for text in ("去机场", "在医院停一下再走", "不要停，继续去赌场", "好的我想去海滩"):
            llm = FakeLLM(tool("say", text="x"))
            interpret(text, Context(), self.gaz, llm)
            self.assertEqual(len(llm.calls), 1, text)

if __name__ == "__main__":
    unittest.main()
