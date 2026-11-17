"""Turn one user utterance into a validated Decision.

Pipeline:  local rules  ->  LLM (tool call)  ->  deterministic guards  ->  degraded keyword mode

The LLM is never trusted with coordinates and never executes anything. It picks one
of four tools; this module checks the result against the gazetteer and the current
queue, and anything doubtful becomes a question to the user instead of an action.
"""
import json
import re
from dataclasses import dataclass, field

from .gazetteer import norm
from .llm import LLMError

MAX_STOPS = 20


@dataclass
class Decision:
    kind: str                       # route | clarify | control | say | confirm_yes | confirm_no | failed
    stops: list = None              # route: the full remaining itinerary after the change (place ids / "@n")
    question: str = None            # clarify
    option_ids: list = None         # clarify: candidate place ids, if the question is "which one?"
    action: str = None              # control: stop | continue | skip | retry | status
    text: str = None                # say / failed
    notes: list = field(default_factory=list)   # things the user should see next to the plan
    source: str = "rule"            # rule | llm | fallback


@dataclass
class Context:
    nav: str = "idle"                               # idle | driving | waiting | stuck
    queue: list = field(default_factory=list)       # [{"id": str|None, "name": str}] remaining stops, in order
    history: list = field(default_factory=list)     # [(role, text)], oldest first, short
    pending: str = None                             # None | "confirm" | "clarify"
    pending_option_ids: list = field(default_factory=list)
    scope_text: str = ""                            # earlier user turns of the same unfinished request


# --------------------------------------------------------------------------
# 1. Local rules: exact phrases only, no substring matching, no model latency.
# --------------------------------------------------------------------------

def _set(*phrases):
    return {norm(p) for p in phrases}


STOP = _set("停", "停下", "停车", "停止", "停下来", "快停", "快停下", "紧急停车", "刹车", "别开了", "不去了", "都不去了",
            "取消导航", "取消路线", "取消行程", "全部取消", "结束导航", "stop", "cancel all", "abort")
CANCEL_WORD = _set("取消", "cancel")
CONTINUE = _set("继续", "继续走", "继续开", "继续吧", "下一站", "去下一站", "continue", "next")
GO = _set("出发", "走", "走吧", "开车", "go")
RETRY = _set("重试", "再试一次", "再试试", "再试一下", "retry")
SKIP = _set("跳过", "跳过这站", "跳过这一站", "跳过当前站", "这站不去了", "skip")
STATUS = _set("状态", "到哪了", "到哪儿了", "到哪里了", "还有多远", "还要多久", "现在去哪", "status", "where are we")
YES = _set("是", "是的", "好", "好的", "确认", "确定", "可以", "行", "对", "对的", "嗯", "没问题", "ok", "okay", "yes", "y")
NO = _set("不", "不用", "不用了", "不要", "不行", "不对", "否", "算了", "n", "no")


def local_rule(text, ctx):
    t = norm(text)
    if not t:
        return Decision("say", text="")
    if t in STOP:
        return Decision("control", action="stop")
    if ctx.pending == "confirm":
        if t in YES or t in GO:
            return Decision("confirm_yes")
        if t in NO or t in CANCEL_WORD:
            return Decision("confirm_no")
    if t in CANCEL_WORD:
        return Decision("control", action="stop")
    if t in RETRY:
        return Decision("control", action="retry")
    if t in SKIP:
        return Decision("control", action="skip")
    if t in STATUS:
        return Decision("control", action="status")
    if t in CONTINUE or t in GO:
        return Decision("control", action="retry" if ctx.nav == "stuck" else "continue")
    if ctx.pending == "clarify" and ctx.pending_option_ids and t.isdigit():
        n = int(t)
        if 1 <= n <= len(ctx.pending_option_ids):
            return None  # handled by interpret(): the number is rewritten to the option's name
    return None


# --------------------------------------------------------------------------
# 2. LLM
# --------------------------------------------------------------------------

TOOLS = [
    {"type": "function", "function": {
        "name": "propose_route",
        "description": "用户想设定或修改行程。stops 是修改之后【完整的剩余行程】，按到达顺序排列。",
        "parameters": {"type": "object", "properties": {
            "stops": {"type": "array", "items": {"type": "string"}, "description": "地点 id 列表，只能用地点表或当前队列里的 id"},
            "unresolved": {"type": "array", "items": {"type": "string"}, "description": "用户提到、但地点表里没有的地点原话"},
        }, "required": ["stops"]}}},
    {"type": "function", "function": {
        "name": "ask_user",
        "description": "指令有歧义、自相矛盾或信息不足时向用户提问，不要猜。",
        "parameters": {"type": "object", "properties": {
            "question": {"type": "string"},
            "options": {"type": "array", "items": {"type": "string"}, "description": "可选：候选地点 id"},
        }, "required": ["question"]}}},
    {"type": "function", "function": {
        "name": "control",
        "description": "不改变行程内容的控制指令。",
        "parameters": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["stop", "continue", "skip", "retry", "status"]},
        }, "required": ["action"]}}},
    {"type": "function", "function": {
        "name": "say",
        "description": "与导航无关的闲聊或无法归类的话，简短回应。",
        "parameters": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}}},
]

SYSTEM_TEMPLATE = """你是 GTA5 车载导航助手的"指令理解"模块。你不开车，也不知道坐标；你只负责把用户的话变成一次工具调用。

【地点表】每行：id | 名称 | 别名 | 类别
{places}

【泛称】
{ambiguity}

【当前状态】
导航状态：{nav}
剩余行程（按顺序）：{queue}

【规则】
1. 每次必须调用且只调用一个工具，不要直接输出文字。
2. 只能使用地点表或剩余行程里出现的 id，绝不编造 id，绝不输出坐标。
3. propose_route.stops 是改动后的完整剩余行程：
   - 全新路线：只列新地点。
   - 追加（"再加一站""然后再去"）：原行程全部保留，新地点接在后面。
   - 删除或替换（"不去X了""把X换成Y"）：在原行程上改，其余站点和顺序保持不变。
   - 插入（"先去X""顺路先去X"）：把X放到合适的位置，其余保持不变。
4. 用户提到地点表里没有的地点：把原话放进 unresolved。不要忽略它，也不要拿相近的地点顶替。
5. 泛称没有默认项、用户又没说清是哪一个：调用 ask_user，并在 options 里给出候选 id。有默认项就用默认项。
6. 指令自相矛盾（例如"先去X，又不要去X"）或看不出想去哪：调用 ask_user，说清楚哪里不确定。
7. 出现在否定里的地点不是目的地（"不去机场了"不代表要去机场）。
8. 注意顺序词："去A之前先去B"的顺序是 B、A；"最后去C"表示C在末尾。
9. 停下、取消全部、继续、跳过当前站、重试、询问进度，用 control。
10. 和导航无关的话用 say，一句话回应。

【示例】
用户：先去机场接个人，不过出发前先回趟麦克家 → propose_route(stops=["michael_house","lsia"])
用户：去赌场，然后去火星基地 → propose_route(stops=["casino"], unresolved=["火星基地"])
用户：带我去医院 → ask_user(question="你要去哪家医院？", options=[...全部医院 id...])
（剩余行程 lsia → casino）用户：再加一站天文台 → propose_route(stops=["lsia","casino","observatory"])
（剩余行程 lsia → casino）用户：不去机场了，改去高尔夫 → propose_route(stops=["golf","casino"])
用户：先去机场，算了别去机场 → ask_user(question="你前后说的不一致：要去机场还是不去？")"""


def build_messages(text, ctx, gazetteer):
    if ctx.queue:
        queue = " → ".join(f"{_queue_token(i, s)}({s.get('name', '')})" for i, s in enumerate(ctx.queue))
    else:
        queue = "（空）"
    system = SYSTEM_TEMPLATE.format(places=gazetteer.prompt_table(), ambiguity=gazetteer.ambiguity_table(),
                                    nav=ctx.nav, queue=queue)
    messages = [{"role": "system", "content": system}]
    for role, content in ctx.history[-8:]:
        messages.append({"role": role, "content": content})
    messages.append({"role": "user", "content": text})
    return messages


def _queue_token(index, stop):
    """Stops without a gazetteer id (picked on the in-game map) are referred to as @1, @2, ..."""
    return stop.get("id") or f"@{index + 1}"


def parse_tool_call(message):
    """Returns (name, args) or (None, None). Accepts a proper tool call or a JSON object in the content."""
    calls = message.get("tool_calls") or []
    if calls:
        fn = (calls[0] or {}).get("function") or {}
        name, raw = fn.get("name"), fn.get("arguments")
        try:
            args = json.loads(raw) if isinstance(raw, str) else (raw or {})
        except ValueError:
            return None, None
        return (name, args) if isinstance(args, dict) else (None, None)
    content = message.get("content") or ""
    match = re.search(r"\{[\s\S]*\}", content)
    if match:
        try:
            obj = json.loads(match.group())
        except ValueError:
            return None, None
        if isinstance(obj, dict):
            name = obj.get("tool") or obj.get("name")
            args = obj.get("args") or obj.get("arguments") or obj.get("parameters")
            if isinstance(name, str) and isinstance(args, dict):
                return name, args
    return None, None


def _validate(name, args, text, ctx, gazetteer):
    """Returns (Decision, None) or (None, feedback_for_the_model)."""
    if name == "say":
        reply = args.get("text")
        if not isinstance(reply, str) or not reply.strip():
            return None, "say.text 不能为空"
        return Decision("say", text=reply.strip()[:200], source="llm"), None

    if name == "control":
        action = args.get("action")
        if action not in ("stop", "continue", "skip", "retry", "status"):
            return None, "control.action 必须是 stop/continue/skip/retry/status 之一"
        return Decision("control", action=action, source="llm"), None

    if name == "ask_user":
        question = args.get("question")
        if not isinstance(question, str) or not question.strip():
            return None, "ask_user.question 不能为空"
        options = [o for o in (args.get("options") or []) if isinstance(o, str) and gazetteer.get(o)]
        return Decision("clarify", question=question.strip()[:200], option_ids=options, source="llm"), None

    if name != "propose_route":
        return None, "只能调用 propose_route / ask_user / control / say 之一"

    stops = args.get("stops")
    unresolved = args.get("unresolved") or []
    if not isinstance(stops, list) or not all(isinstance(s, str) for s in stops):
        return None, "propose_route.stops 必须是 id 字符串数组"
    if not isinstance(unresolved, list):
        unresolved = []
    unresolved = [str(u).strip() for u in unresolved if str(u).strip()]

    queue_tokens = {_queue_token(i, s) for i, s in enumerate(ctx.queue)}
    bad = [s for s in stops if not gazetteer.get(s) and s not in queue_tokens]
    if bad:
        return None, ("这些 id 不存在：" + "、".join(bad) + "。只能用地点表里的 id；"
                      "如果用户说的地点不在表里，请把原话放进 unresolved。")

    # collapse immediate repeats ("去机场，去机场")
    cleaned = [s for i, s in enumerate(stops) if i == 0 or s != stops[i - 1]]
    if len(cleaned) > MAX_STOPS:
        return Decision("clarify", question=f"一次最多 {MAX_STOPS} 站，你说的有 {len(cleaned)} 站，可以分几次说吗？", source="llm"), None

    # Unknown places: never drop them silently, never run a partial route.
    if unresolved:
        lines, option_ids = [], []
        for phrase in unresolved[:3]:
            near = [pid for pid in gazetteer.search(phrase) if pid not in option_ids]
            option_ids += near
            hint = "，相近的有：" + "、".join(gazetteer.name(p) for p in near) if near else ""
            lines.append(f"我的地点库里没有「{phrase}」{hint}。")
        lines.append("可以换个说法，或在游戏地图上选点。原行程没有改动。")
        return Decision("clarify", question="\n".join(lines), option_ids=option_ids, source="llm"), None

    if not cleaned:
        if ctx.queue:
            return Decision("clarify", question="这样行程就空了。你是想取消全部行程吗？说「停下」即可取消。", source="llm"), None
        return Decision("clarify", question="我没听出要去哪里，可以直接说地点名，比如「先去赌场，再去天文台」。", source="llm"), None

    # Guard against "picked one at random": a new stop that the user only named by a generic word.
    scope = (ctx.scope_text + " " + text).strip()
    old = queue_tokens
    notes = []
    for pid in cleaned:
        if pid in old or pid.startswith("@") or pid in ctx.pending_option_ids:
            continue
        group = gazetteer.ambiguity_for(pid, scope)
        if group is not None:
            term = next((t for t in group["terms"] if norm(t) in norm(scope)), group["terms"][0])
            if not group.get("default"):
                return Decision("clarify", question=f"「{term}」有好几个，你要去哪一个？",
                                option_ids=list(group["candidates"]), source="llm"), None
            if pid == group["default"]:
                notes.append(f"「{term}」按默认理解为{gazetteer.name(pid)}")
            else:
                notes.append(f"「{term}」理解为{gazetteer.name(pid)}（不是默认的{gazetteer.name(group['default'])}）")
        elif not gazetteer.mentioned_literally(pid, scope):
            notes.append(f"{gazetteer.name(pid)}是根据你的描述推断的")
    return Decision("route", stops=cleaned, notes=notes, source="llm"), None


def ask_llm(text, ctx, gazetteer, llm):
    """One model call, plus at most one corrective retry. Raises LLMError if unusable."""
    messages = build_messages(text, ctx, gazetteer)
    feedback = None
    for attempt in range(2):
        if feedback:
            messages = messages + [{"role": "user", "content": "（系统提示：上一次输出无效。" + feedback + " 请重新调用一个工具。）"}]
        message = llm.chat(messages, TOOLS)
        name, args = parse_tool_call(message)
        if name is None:
            feedback = "你没有调用工具。"
            continue
        decision, feedback = _validate(name, args, text, ctx, gazetteer)
        if decision is not None:
            return decision
    raise LLMError("bad_response", "model output invalid twice: " + (feedback or ""))


# --------------------------------------------------------------------------
# 3. Degraded mode: no usable model.
# --------------------------------------------------------------------------

_EDIT_MARKERS = ("不", "别", "取消", "改", "换", "删", "去掉", "除了", "加", "顺便", "顺路", "之前", "以前", "跳过")


def fallback_parse(text, ctx, gazetteer, why):
    prefix = f"（模型不可用：{why}）"
    if any(m in text for m in _EDIT_MARKERS):
        return Decision("failed", source="fallback",
                        text=prefix + "这句话我没法只靠关键词可靠理解，原行程没有改动。"
                             "可以稍后再试，或直接说「去A，然后去B」，也可以在游戏地图上选点。")
    ids, groups = gazetteer.find_in_text(text)
    if groups:
        g = groups[0]
        return Decision("clarify", source="fallback", option_ids=list(g["candidates"]),
                        question=prefix + f"「{g['terms'][0]}」有好几个，你要去哪一个？")
    if not ids:
        return Decision("failed", source="fallback",
                        text=prefix + "我没能从这句话里认出地点，原行程没有改动。可以稍后再试，或在游戏地图上选点。")
    cleaned = [s for i, s in enumerate(ids) if i == 0 or s != ids[i - 1]]
    return Decision("route", stops=cleaned, source="fallback",
                    notes=[prefix + "以下是按关键词出现顺序匹配的结果，请仔细核对"])


# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------

def interpret(text, ctx, gazetteer, llm):
    """Never raises. Always returns a Decision."""
    text = (text or "").strip()
    decision = local_rule(text, ctx)
    if decision is not None:
        return decision

    # "2" in answer to a numbered question -> the option's name
    t = norm(text)
    if ctx.pending == "clarify" and ctx.pending_option_ids and t.isdigit() and 1 <= int(t) <= len(ctx.pending_option_ids):
        text = gazetteer.name(ctx.pending_option_ids[int(t) - 1])

    if llm is None:
        return fallback_parse(text, ctx, gazetteer, "未配置")
    try:
        return ask_llm(text, ctx, gazetteer, llm)
    except LLMError as e:
        reason = {"timeout": "超时", "network": "网络错误", "http": "接口报错",
                  "bad_response": "输出不合格式", "not_configured": "未配置 API key"}.get(e.kind, e.kind)
        return fallback_parse(text, ctx, gazetteer, reason)
    except Exception as e:  # a bug here must not take the agent down
        return fallback_parse(text, ctx, gazetteer, f"内部错误 {type(e).__name__}")
