"""Terminal chat with the navigation agent.

    python -m agent.cli                      # real game on 127.0.0.1:21556
    python -m agent.cli --port 21777 --allow-unverified     # against tools/mock_game.py

Set DEEPSEEK_API_KEY and/or DASHSCOPE_API_KEY first (see agent/llm.py). Without a key the
agent still runs in keyword mode and says so.
"""
import argparse
import os
import sys
import time

from .core import Agent
from .gazetteer import Gazetteer
from .llm import chain_from_env


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=21556)
    ap.add_argument("--allow-unverified", action="store_true", help="navigate to places whose coordinates were never captured in-game (mock only)")
    ap.add_argument("--no-confirm", action="store_true", help="execute plans without asking (keyword-mode plans are still confirmed)")
    ap.add_argument("--no-auto-continue", action="store_true", help="wait for 继续 at every intermediate stop")
    ap.add_argument("--dwell", type=float, default=3.0, help="seconds to wait at a stop before continuing")
    args = ap.parse_args()

    here = os.path.dirname(os.path.abspath(__file__))
    log_dir = os.path.join(here, "..", "tools", "logs")
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.join(log_dir, time.strftime("agent-%Y%m%d-%H%M%S.jsonl"))

    chain = chain_from_env()
    llm = chain if chain.providers else None

    def say(text):
        print("\n导航> " + text.replace("\n", "\n      ") + "\n你> ", end="", flush=True)

    agent = Agent(Gazetteer(), llm, say, host=args.host, port=args.port, log_path=log_path,
                  allow_unverified=args.allow_unverified, confirm=not args.no_confirm,
                  auto_continue=not args.no_auto_continue, dwell_s=args.dwell)

    print("GTA5 导航助手。直接说要去哪，例如「先去赌场，再去天文台」。说「停下」随时停车，Ctrl+C 退出。")
    print("模型：" + (" → ".join(f"{p.name}({p.model})" for p in chain.providers) if llm else "未配置 API key，仅关键词模式"))
    if agent.connect():
        print(f"已连接游戏 {args.host}:{args.port}")
    else:
        print(f"现在连不上游戏 {args.host}:{args.port}（游戏没开、mod 没加载或游戏暂停中）。我会在你下指令时再提示。")
        import threading
        threading.Thread(target=agent._reconnect_loop, daemon=True).start()

    try:
        while True:
            try:
                line = input("你> ")
            except EOFError:
                break
            agent.submit(line)
    except KeyboardInterrupt:
        pass
    finally:
        agent.close()
        print("\n已退出。注意：退出不会让车停下，行程仍在游戏里继续。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
