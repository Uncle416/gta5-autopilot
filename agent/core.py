"""The agent: dialogue state, plan confirmation, execution over the TCP protocol, and
reactions to game events. See docs/AGENT.md for the behaviour this implements.

Threads: user input (submit), one worker per model call, the game event loop, timers.
All shared state is guarded by self._lock. Model calls run outside the lock, and their
results are applied only if no newer user input arrived in the meantime (self._epoch).
"""
import os
import sys
import threading
import time

from .nlu import Context, interpret, local_rule

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from gta_client import GameClient, GameConnectionError, NoAck  # noqa: E402

ERROR_TEXT = {
    "no_vehicle": "你现在不在车里。",
    "player_dead": "角色当前不可用（死亡或正在重生）。",
    "empty_queue": "行程是空的。",
    "invalid_state": "当前状态下不能这样做。",
    "stale_mission": "游戏里的任务已经变了，我的信息过期了。",
    "invalid_stop": "有站点的坐标不合法，游戏拒绝了这条路线。",
    "invalid_route": "游戏拒绝了这条路线。",
}
BLOCK_TEXT = {
    "no_vehicle": "你下车了，导航已暂停，行程保留。回到车上会自动继续。",
    "vehicle_destroyed": "车辆已损毁，导航已暂停，行程保留。换一辆车上车后会自动继续。",
    "player_dead": "角色死亡，导航已暂停，行程保留。",
}


class Agent:
    def __init__(self, gazetteer, llm, say, host="127.0.0.1", port=21556, log_path=None,
                 allow_unverified=False, confirm=True, auto_continue=True, dwell_s=3.0,
                 max_auto_retry=1, async_llm=True, reconnect=True, ack_timeout=3.0):
        self.gaz = gazetteer
        self.llm = llm
        self._say_cb = say
        self.host, self.port, self.log_path = host, port, log_path
        self.allow_unverified = allow_unverified
        self.confirm = confirm
        self.auto_continue = auto_continue
        self.dwell_s = dwell_s
        self.max_auto_retry = max_auto_retry
        self.async_llm = async_llm
        self.reconnect = reconnect
        self.ack_timeout = ack_timeout

        self._lock = threading.RLock()
        self._epoch = 0                 # bumped on every user input; stale model results are dropped
        self._closed = False
        self.client = None
        self.mission_id = None          # the mission this agent started most recently
        self._mission_counter = 0
        self._pending = None            # None | {"kind": "confirm", "plan": {...}} | {"kind": "clarify", ...}
        self._history = []              # [(role, text)]
        self._scope = ""                # user turns of the request that is still being clarified
        self._unconfirmed = None        # mission id sent without receiving an ack
        self._paused_notice_at = 0.0

    # ------------------------------------------------------------------
    # output
    # ------------------------------------------------------------------
    def _say(self, text, remember=True):
        if not text:
            return
        if remember:
            self._history.append(("assistant", text))
            del self._history[:-10]
        try:
            self._say_cb(text)
        except Exception:
            pass

    # ------------------------------------------------------------------
    # connection
    # ------------------------------------------------------------------
    def connect(self):
        """Connect once. Returns True/False; never raises."""
        try:
            client = GameClient(self.host, self.port, log_path=self.log_path).connect()
        except GameConnectionError:
            return False
        with self._lock:
            self.client = client
        threading.Thread(target=self._event_loop, args=(client,), name="agent-events", daemon=True).start()
        return True

    def close(self):
        self._closed = True
        with self._lock:
            if self.client:
                self.client.close()

    def _request(self, type_, **fields):
        """Returns (ack, None) or (None, problem_text). Never raises."""
        client = self.client
        if client is None or not client.connected:
            return None, "连不上游戏（mod 没在运行，或连接断了）。指令没有发出。"
        try:
            return client.request(type_, timeout=self.ack_timeout, **fields), None
        except NoAck:
            return None, "指令已发出，但游戏没有回执（可能暂停了或开着地图）。我不确定它是否生效。"
        except GameConnectionError:
            return None, "和游戏的连接断了，这条指令的结果未知。"

    def _game_state(self):
        ack, problem = self._request("get_state")
        if ack is None:
            return None, problem
        return ack.get("state") or {}, None

    def _reconnect_loop(self):
        delay = 1.0
        while not self._closed:
            time.sleep(delay)
            if self._closed:
                return
            if self.connect():
                with self._lock:
                    state, _ = self._game_state()
                    if state is None:
                        continue
                    self._say("已重新连上游戏。" + self._describe_state(state) + self._mission_drift(state))
                return
            delay = min(delay * 2, 15.0)

    def _mission_drift(self, state):
        if self.mission_id and state.get("mission_id") != self.mission_id and state.get("nav") != "idle":
            return " 注意：游戏里现在跑的不是我上次下发的行程，以游戏里的为准。"
        return ""

    # ------------------------------------------------------------------
    # user input
    # ------------------------------------------------------------------
    def submit(self, text):
        """Handle one user utterance. Returns immediately when the model is needed (async_llm)."""
        text = (text or "").strip()
        if not text:
            return
        with self._lock:
            self._epoch += 1
            epoch = self._epoch
            ctx = self._context()
            # Rules first, under the lock: "停下" must never wait for a model.
            decision = local_rule(text, ctx)
            self._history.append(("user", text))
            del self._history[:-10]
            if decision is not None:
                self._apply(decision, text)
                return
        if self.async_llm:
            threading.Thread(target=self._think, args=(text, ctx, epoch), name="agent-llm", daemon=True).start()
        else:
            self._think(text, ctx, epoch)

    def _think(self, text, ctx, epoch):
        decision = interpret(text, ctx, self.gaz, self.llm)     # slow; no lock held
        with self._lock:
            if epoch != self._epoch or self._closed:
                return      # the user said something newer (e.g. "停下"); this result is stale
            self._apply(decision, text)

    def _context(self):
        client = self.client
        state = client.last_state if client else None
        if state is None and client is not None and client.connected:
            state, _ = self._game_state()       # no heartbeat seen yet
        state = state or {}
        pending = self._pending or {}
        return Context(
            nav=state.get("nav", "idle"),
            queue=list(state.get("queue") or []),
            history=list(self._history),
            pending=pending.get("kind"),
            pending_option_ids=list(pending.get("option_ids") or []),
            scope_text=self._scope,
        )

    # ------------------------------------------------------------------
    # decisions
    # ------------------------------------------------------------------
    def _apply(self, d, text):
        if d.kind == "confirm_yes":
            plan = self._pending["plan"]
            self._execute(plan)
            return
        if d.kind == "confirm_no":
            self._pending = None
            self._scope = ""
            self._say("好，已放弃这次改动，原行程不变。")
            return

        # Anything else replaces a pending confirmation: an unconfirmed plan is never executed later.
        if self._pending and self._pending["kind"] == "confirm":
            self._pending = None
            self._scope = ""

        if d.kind == "say":
            self._scope = ""
            self._say(d.text)
        elif d.kind == "failed":
            self._say(d.text)
        elif d.kind == "clarify":
            self._scope = (self._scope + " " + text).strip()
            self._pending = {"kind": "clarify", "option_ids": list(d.option_ids or [])}
            question = d.question
            if d.option_ids:
                question += "\n" + "\n".join(f"  {i + 1}. {self.gaz.name(pid)}" for i, pid in enumerate(d.option_ids))
            self._say(question)
        elif d.kind == "control":
            self._pending = None
            self._scope = ""
            self._control(d.action)
        elif d.kind == "route":
            self._pending = None
            self._propose(d)

    def _propose(self, d):
        state, problem = self._game_state()
        if state is None:
            self._say(problem + " 行程没有改动。")
            return
        old = list(state.get("queue") or [])
        old_tokens = [s.get("id") or f"@{i + 1}" for i, s in enumerate(old)]
        new = list(d.stops)
        active = state.get("nav") != "idle"

        if not self.allow_unverified:
            unverified = [p for p in dict.fromkeys(new) if not p.startswith("@") and not self.gaz.get(p).get("verified")]
            if unverified:
                names = "、".join(f"{self.gaz.name(p)}({p})" for p in unverified)
                self._say(f"这些地点的坐标还没有在游戏里实测，我不会拿未经确认的坐标去导航：{names}。\n"
                          "把车停到该地点路边后运行 `python -m agent.capture <id>` 采点即可。行程没有改动。")
                self._scope = ""
                return

        def label(token):
            if token.startswith("@"):
                idx = int(token[1:]) - 1
                return old[idx].get("name", token) if 0 <= idx < len(old) else token
            return self.gaz.name(token)

        route_text = " → ".join(label(t) for t in new)
        if new == old_tokens:
            self._say(f"行程本来就是：{route_text}，没有变化。")
            self._scope = ""
            return

        if old_tokens and new[:len(old_tokens)] == old_tokens:
            mode = "append"
            added = new[len(old_tokens):]
            summary = f"在末尾追加：{'、'.join(label(t) for t in added)}。\n行程变为：{route_text}"
        else:
            mode = "replace"
            if any(t.startswith("@") for t in new):
                self._say("队列里有在地图上选的点，我没有它的坐标，没法在保留它的同时重排行程。"
                          "可以只在末尾追加，或者整体换一条新路线。行程没有改动。")
                self._scope = ""
                return
            removed = [t for t in old_tokens if t not in new]
            added = [t for t in new if t not in old_tokens]
            if not old_tokens:
                summary = f"路线：{route_text}"
            else:
                parts = []
                if removed:
                    parts.append("去掉：" + "、".join(label(t) for t in removed))
                if added:
                    parts.append("新增：" + "、".join(label(t) for t in added))
                if not removed and not added:
                    parts.append("调整了顺序")
                summary = f"行程改为：{route_text}\n（{'；'.join(parts)}）"
                current = old_tokens[0]
                if active and state.get("nav") == "driving" and new[0] != current:
                    summary += f"\n现在正开往{label(current)}，确认后会立刻改道去{label(new[0])}。"

        for note in d.notes:
            summary += "\n注：" + note

        plan = {"mode": mode, "new": new, "old": old_tokens, "added": added if mode == "append" else None}
        if self.confirm or d.source == "fallback":
            self._pending = {"kind": "confirm", "plan": plan}
            self._say(summary + "\n确认吗？（好 / 不）")
        else:
            self._say(summary)
            self._execute(plan)

    def _execute(self, plan):
        """Send a confirmed plan. On any failure the pending confirmation is kept so the user can retry."""
        state, problem = self._game_state()
        if state is None:
            self._pending = {"kind": "confirm", "plan": plan}
            self._say(problem + " 这次改动没有生效，连上后回复「确认」可以重发。")
            return
        current = [s.get("id") or f"@{i + 1}" for i, s in enumerate(state.get("queue") or [])]

        if plan["mode"] == "append":
            # Stops may have been reached while the user was deciding; appending is still correct.
            stops = [self.gaz.stop(t) for t in plan["added"]]
            fields = {"mode": "append", "autostart": True, "stops": stops}
            if state.get("mission_id"):
                fields["mission_id"] = state["mission_id"]
            sent_mission = state.get("mission_id")
        else:
            # Drop stops that were reached in the meantime, so they are not driven twice.
            visited = [t for t in plan["old"] if t not in current]
            new = [t for t in plan["new"] if t not in visited]
            if not new:
                self._pending = None
                self._say("这些站点在你确认之前都已经到过了，行程不需要再改。")
                return
            self._mission_counter += 1
            sent_mission = f"a{int(time.time())}-{self._mission_counter}"
            fields = {"mode": "replace", "autostart": True, "mission_id": sent_mission,
                      "stops": [self.gaz.stop(t) for t in new]}

        ack, problem = self._request("set_route", **fields)
        if ack is None:
            self._pending = {"kind": "confirm", "plan": plan}
            self._unconfirmed = sent_mission
            self._say(problem + " 说「状态」可以让我核对；需要的话回复「确认」重发。")
            return
        if not ack.get("ok"):
            err = ack.get("error")
            if err in ("no_vehicle", "player_dead"):
                self._pending = {"kind": "confirm", "plan": plan}
                self._say(ERROR_TEXT[err] + "行程没有改动。上车后回复「确认」，我再下发。")
            else:
                self._pending = None
                self._scope = ""
                self._say(f"游戏拒绝了这次改动：{ERROR_TEXT.get(err, err)}（{ack.get('message', '')}）行程没有改动。")
            return

        self._pending = None
        self._scope = ""
        self._unconfirmed = None
        self.mission_id = ack.get("mission_id") or sent_mission
        self._say("好的，已下发。" if plan["mode"] == "replace" else "好的，已追加。")

    def _control(self, action):
        if action == "status":
            state, problem = self._game_state()
            self._say(problem if state is None else self._describe_state(state) + self._mission_drift(state))
            return
        if action == "stop":
            ack, problem = self._request("stop")
            if ack is None:
                self._say(problem + " 如果车还在走，请直接按小键盘 2 或小数点键停车。")
            elif ack.get("ok"):
                self._say("已停止，行程已清空。")
            else:
                self._say("停止指令被拒绝：" + str(ack.get("error")) + "。请按小键盘 2 停车。")
            return

        ack, problem = self._request(action)
        if ack is None:
            self._say(problem)
            return
        if ack.get("ok"):
            self._say({"continue": "好，继续前往下一站。", "retry": "好，再试一次。", "skip": "好，跳过这一站。"}[action])
            return
        err = ack.get("error")
        if err == "invalid_state":
            nav = ack.get("nav")
            hint = {"continue": "现在没有停在中途站等待，所以没有可以「继续」的。",
                    "retry": "现在没有卡住，不需要重试。"}.get(action, ERROR_TEXT[err])
            self._say(f"{hint}（当前状态：{nav}）")
        else:
            self._say(ERROR_TEXT.get(err, str(err)))

    def _describe_state(self, state):
        nav = state.get("nav", "idle")
        queue = state.get("queue") or []
        names = [s.get("name", "?") for s in queue]
        cur = state.get("current") or {}
        if state.get("blocked"):
            return BLOCK_TEXT.get(state["blocked"], "导航已暂停：" + str(state["blocked"]))
        if nav == "idle":
            return "当前没有在导航。" + (f"已装载但未出发的行程：{' → '.join(names)}。" if names else "")
        rest = f"之后：{' → '.join(names[1:])}。" if len(names) > 1 else "这是最后一站。"
        dist = cur.get("dist_m")
        dist_text = f"，还有约 {dist:.0f} 米" if isinstance(dist, (int, float)) else ""
        if nav == "driving":
            if not state.get("autopilot", True):
                return f"导航被游戏内热键暂停了，目标是{cur.get('name', '?')}{dist_text}。{rest}"
            return f"正在前往{cur.get('name', '?')}{dist_text}。{rest}"
        if nav == "waiting":
            return f"已停在中途站，下一站是{cur.get('name', '?')}。{rest}"
        if nav == "stuck":
            return f"卡在去{cur.get('name', '?')}的路上{dist_text}。可以说「重试」「跳过」或「停下」。"
        return f"状态：{nav}"

    # ------------------------------------------------------------------
    # game events
    # ------------------------------------------------------------------
    def _event_loop(self, client):
        while not self._closed:
            ev = client.next_event(timeout=1.0)
            if ev is None:
                if not client.connected:
                    return
                self._check_heartbeat(client)
                continue
            try:
                with self._lock:
                    self._on_event(ev, client)
            except Exception as e:  # never let one bad event kill the loop
                self._say(f"（处理游戏事件时出错：{type(e).__name__}: {e}）", remember=False)
            if ev.get("event") == "disconnected":
                return

    def _check_heartbeat(self, client):
        """Heartbeat silence with the socket still up means the script thread is not ticking."""
        last = client.last_state_at
        st = client.last_state or {}
        if last is None or st.get("nav") in (None, "idle"):
            return
        silent = time.monotonic() - last
        if silent > 8 and time.monotonic() - self._paused_notice_at > 60:
            self._paused_notice_at = time.monotonic()
            self._say("有一会儿没收到游戏的状态了：游戏可能暂停了或开着地图，期间导航不会推进。", remember=False)

    def _on_event(self, ev, client):
        name = ev.get("event")
        mine = ev.get("mission_id") is not None and ev.get("mission_id") == self.mission_id
        stop = (ev.get("stop") or {}).get("name", "?")

        if name == "disconnected":
            self._say("和游戏的连接断了。车如果正在自动驾驶，会按游戏里的任务继续，但我暂时收不到进展。"
                      + ("我会自动重连。" if self.reconnect else ""))
            if self.reconnect and not self._closed:
                threading.Thread(target=self._reconnect_loop, name="agent-reconnect", daemon=True).start()
            return

        if self._unconfirmed and ev.get("mission_id") == self._unconfirmed:
            self.mission_id = self._unconfirmed
            self._unconfirmed = None
            self._pending = None
            mine = True
            self._say("刚才那条没收到回执的指令，游戏其实已经执行了。")

        if name == "leg_started":
            retry = ev.get("retry") or 0
            left = ev.get("remaining")
            self._say(f"{'第 ' + str(retry) + ' 次重试，' if retry else ''}正在前往{stop}"
                      + (f"（包括这站还剩 {left} 站）。" if left else "。"))
        elif name == "arrived":
            near = f"附近（离目标点约 {ev.get('dist_m', 0):.0f} 米，车开不到更近的位置）" if ev.get("near") else ""
            if ev.get("final"):
                self._say(f"已到达{stop}{near}。")
            else:
                nxt = (ev.get("next") or {}).get("name", "下一站")
                if self.auto_continue and mine:
                    self._say(f"已到达{stop}{near}。{self.dwell_s:g} 秒后前往{nxt}，说「停下」可以取消。")
                    timer = threading.Timer(self.dwell_s, self._auto_continue, args=(ev.get("mission_id"),))
                    timer.daemon = True
                    timer.start()
                else:
                    self._say(f"已到达{stop}{near}。说「继续」前往{nxt}。")
        elif name == "stuck":
            retries = ev.get("retries") or 0
            if mine and retries < self.max_auto_retry:
                self._say(f"去{stop}的路上卡住了（很久没有移动）。我先自动重试一次。")
                ack, problem = self._request("retry", mission_id=ev.get("mission_id"))
                if ack is None or not ack.get("ok"):
                    self._say("自动重试没有成功。" + (problem or ERROR_TEXT.get(ack.get("error"), str(ack.get("error"))))
                              + " 可以说「重试」「跳过」或「停下」。")
            else:
                tried = f"又卡住了，已重试 {retries} 次，我不再自动重试" if retries else "卡住了（很久没有移动）"
                self._say(f"去{stop}的路上{tried}。"
                          f"这一站还在行程里，没有算作到达。你来决定：「重试」「跳过」这一站，或「停下」。")
        elif name == "skipped":
            self._say(f"已跳过{stop}（没有到达）。")
        elif name == "blocked":
            self._say(BLOCK_TEXT.get(ev.get("reason"), "导航已暂停：" + str(ev.get("reason"))))
        elif name == "unblocked":
            self._say("条件恢复了，继续导航。")
        elif name == "paused":
            self._say("自动驾驶被游戏内热键关闭了，行程保留。按小键盘 0 或说「继续」恢复。")
        elif name == "resumed":
            self._say("自动驾驶已恢复。")
        elif name == "mission_completed":
            skipped = ev.get("skipped") or 0
            self._say("全部行程结束。" + (f"其中有 {skipped} 站是跳过的，没有到达。" if skipped else "所有站点都已到达。"))
        elif name == "cancelled":
            reason = ev.get("reason")
            if reason == "hotkey":
                self._say("行程在游戏里被热键取消了。")
            # "replaced" / "stop" follow a request that was already answered in the conversation.
        elif name == "route_loaded":
            if ev.get("mode") == "map":
                names = [s.get("name", "?") for s in ev.get("queue") or []]
                self._say(f"你在地图上加了一个点，现在的行程：{' → '.join(names)}。")

    def _auto_continue(self, mission_id):
        with self._lock:
            if self._closed or mission_id != self.mission_id:
                return
            ack, problem = self._request("continue", mission_id=mission_id)
            if ack is None:
                self._say("自动前往下一站失败：" + problem + " 说「继续」可以再试。")
            elif not ack.get("ok") and ack.get("error") not in ("invalid_state", "stale_mission"):
                # invalid_state / stale_mission: the user stopped or changed the route meanwhile. Stay quiet.
                self._say("没能自动前往下一站：" + ERROR_TEXT.get(ack.get("error"), str(ack.get("error"))) + " 说「继续」可以再试。")
