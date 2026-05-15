"""Terminal chat interface for natural language navigation.

Usage:
    python chat_server.py

Then type natural language navigation instructions, e.g.:
    > 先去机场，再去海滩，最后回家
"""
import sys
import nlu_engine
import tcp_client


def print_banner():
    print("=" * 50)
    print("  GTA V NL Navigator — 自然语言导航助手")
    print("=" * 50)
    print("  示例: 先去机场，经过海滩，最后回麦克家")
    print("  命令: /help 帮助  /quit 退出  /wp 从地图设点")
    print("=" * 50)


def print_help():
    print("""
命令:
  直接输入自然语言   — 设定导航点，如 "去机场然后回家"
  /yes 或 /y        — 确认继续到下一站
  /no 或 /n         — 停止导航
  /quit 或 /q       — 退出程序
  /help             — 显示此帮助
  /wp               — 用游戏地图标点作为目的地 (由C#端NumPad2处理)

热键 (游戏中):
  Numpad7           — 继续到下一站
  Numpad8           — 停止导航
  Numpad2           — 从地图标点设目的地
""")


def main():
    print_banner()

    while True:
        try:
            user_input = input("\n> ").strip()
        except (KeyboardInterrupt, EOFError):
            print("\n再见!")
            break

        if not user_input:
            continue

        # Handle commands
        if user_input in ("/quit", "/q", "/exit"):
            print("再见!")
            break

        if user_input in ("/help", "/h"):
            print_help()
            continue

        if user_input in ("/yes", "/y"):
            if tcp_client.send_command("continue"):
                print("已发送: 继续到下一站")
            else:
                print("发送失败，请确认游戏正在运行且自动驾驶已启动")
            continue

        if user_input in ("/no", "/n"):
            if tcp_client.send_command("stop"):
                print("已发送: 停止导航")
            else:
                print("发送失败")
            continue

        if user_input == "/wp":
            print("请在游戏地图上放置标点，然后按 Numpad2 设定目的地")
            continue

        # Try yes/no intent first
        intent = nlu_engine.extract_yes_no(user_input)
        if intent:
            if tcp_client.send_command(intent):
                print("OK" if intent == "continue" else "已停止")
            continue

        # Otherwise, treat as navigation instruction
        print("正在解析...")
        waypoints, msg = nlu_engine.extract_waypoints(user_input)
        print(msg)

        if waypoints:
            yn = input("发送到游戏? (回车确认 / n 取消): ").strip()
            if yn.lower() not in ("n", "no", "不"):
                if tcp_client.send_waypoints(waypoints):
                    print("已发送到游戏！在游戏中按 Numpad0 启动自动驾驶")
                else:
                    print("发送失败，请确认游戏正在运行")


if __name__ == "__main__":
    main()
