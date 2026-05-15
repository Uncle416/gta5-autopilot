"""TCP client that sends JSON commands to the C# mod."""
import json
import socket
import config


def send_message(data: dict) -> bool:
    """Send a JSON message to the C# mod via TCP. Returns True on success."""
    try:
        payload = json.dumps(data, ensure_ascii=False) + "\n"
        with socket.create_connection((config.TCP_HOST, config.TCP_PORT), timeout=3) as sock:
            sock.sendall(payload.encode("utf-8"))
        return True
    except (ConnectionRefusedError, socket.timeout, OSError) as e:
        print(f"[TCP] Failed to send: {e}")
        return False


def send_waypoints(waypoints: list[dict]) -> bool:
    """Send a waypoint list to the C# mod."""
    msg = {"type": "set_waypoints", "waypoints": waypoints}
    return send_message(msg)


def send_command(action: str) -> bool:
    """Send a control command: 'continue' or 'stop'."""
    msg = {"type": "command", "action": action}
    return send_message(msg)
