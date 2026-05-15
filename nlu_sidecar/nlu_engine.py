"""NLU engine: natural language -> structured waypoint list.

Uses Ollama with a small local model (Qwen2.5-1.5B) to extract waypoints
from user's natural language input, then matches names to GTA V coordinates
via the gazetteer.
"""
import json
import re
from difflib import get_close_matches
import config

# Lazy-import ollama so the module loads even without it installed
ollama = None


def _get_ollama():
    global ollama
    if ollama is None:
        import ollama as _ollama
        ollama = _ollama
    return ollama


def _load_gazetteer() -> dict:
    with open(config.GAZETTEER_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


def _fuzzy_match(name: str, gazetteer: dict, cutoff: float = 0.5) -> dict | None:
    """Fuzzy match a place name against the gazetteer."""
    name_lower = name.strip().lower()
    keys = list(gazetteer.keys())

    # Exact match first (case-insensitive)
    for key in keys:
        if key.lower() == name_lower:
            return {"name": key, **gazetteer[key]}

    # Fuzzy match
    matches = get_close_matches(name_lower, [k.lower() for k in keys], n=1, cutoff=cutoff)
    if matches:
        # Find original key
        for key in keys:
            if key.lower() == matches[0]:
                return {"name": key, **gazetteer[key]}

    return None


def extract_waypoints(user_input: str) -> tuple[list[dict], str]:
    """Parse user input and return (waypoints, response_message).

    Uses Ollama LLM for name extraction, then matches against gazetteer
    for coordinates. Returns the list of waypoints and a user-facing message.
    """
    gazetteer = _load_gazetteer()
    place_names = ", ".join(gazetteer.keys())

    ollama_client = _get_ollama()

    prompt = f"""你是一个导航坐标提取助手。从用户的话中提取出行经地点列表，按到达先后顺序输出。

已知地点及其坐标:
{place_names}

用户说: "{user_input}"

请用JSON格式输出，只包含地名(无需坐标):
{{"places": ["地点1", "地点2", "地点3"]}}

如果用户的话中没有明确的地点，返回空列表。只输出JSON，不要其他内容。"""

    try:
        response = ollama_client.chat(
            model=config.OLLAMA_MODEL,
            messages=[{"role": "user", "content": prompt}],
            options={"temperature": 0.1, "num_predict": 256},
        )
        raw = response["message"]["content"].strip()
    except Exception as e:
        return [], f"模型调用失败: {e}"

    # Extract JSON from response (may have markdown fences)
    json_match = re.search(r'\{[\s\S]*\}', raw)
    if not json_match:
        return [], f"无法解析模型输出: {raw[:200]}"

    try:
        data = json.loads(json_match.group())
    except json.JSONDecodeError:
        return [], f"JSON解析失败: {raw[:200]}"

    places = data.get("places", [])
    if not places:
        return [], "未能识别出地点，请重新描述你的目的地。"

    waypoints = []
    unknown = []
    for place_name in places:
        match = _fuzzy_match(place_name, gazetteer)
        if match:
            waypoints.append({"name": match["name"], "x": match["x"], "y": match["y"], "z": match["z"]})
        else:
            unknown.append(place_name)

    # Build response message
    lines = []
    if waypoints:
        lines.append(f"已识别 {len(waypoints)} 个导航点:")
        for i, wp in enumerate(waypoints):
            lines.append(f"  {i + 1}. {wp['name']} ({wp['x']}, {wp['y']})")

    if unknown:
        names = ", ".join(unknown)
        lines.append(f"未识别: {names}（请在游戏地图上设标点后用NumPad2手动设定）")

    return waypoints, "\n".join(lines)


def extract_yes_no(user_input: str) -> str | None:
    """Check if user input means yes or no. Returns 'continue', 'stop', or None."""
    text = user_input.strip().lower()

    yes_patterns = ["是", "yes", "y", "好", "ok", "继续", "去", "走", "出发", "行", "可以", "对"]
    no_patterns = ["否", "no", "n", "不", "停", "停车", "别", "算了", "取消"]

    for pat in yes_patterns:
        if pat in text:
            return "continue"
    for pat in no_patterns:
        if pat in text:
            return "stop"

    return None


if __name__ == "__main__":
    # Quick test
    while True:
        try:
            inp = input("\n导航指令> ")
            if inp.lower() in ("exit", "quit", "q"):
                break
            wps, msg = extract_waypoints(inp)
            print(msg)
            if wps:
                yn = input("发送至游戏? (y/n): ")
                if yn.lower() == "y":
                    import tcp_client
                    tcp_client.send_waypoints(wps)
        except KeyboardInterrupt:
            break
        except EOFError:
            break
