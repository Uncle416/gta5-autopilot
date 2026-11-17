"""Client for the GTA5AutoPilot TCP protocol v2 (see docs/PROTOCOL.md).

Standard library only. Keeps three things apart on purpose:
  * the socket write succeeded        -> request() did not raise ConnectionError
  * the mod accepted the command      -> request() returned an ack with ok == True
  * the car actually did something    -> an event arrived (next_event / events)
"""
import json
import queue
import socket
import threading
import time


class GameConnectionError(Exception):
    """Could not connect, or the connection dropped."""


class NoAck(Exception):
    """The request was written to the socket but no ack came back in time.

    The command may or may not have been executed. Call get_state to find out.
    """


class GameClient:
    def __init__(self, host="127.0.0.1", port=21556, log_path=None, connect_timeout=3.0):
        self.host, self.port = host, port
        self._sock = None
        self._send_lock = threading.Lock()
        self._acks = {}                 # id -> queue.Queue(1)
        self._acks_lock = threading.Lock()
        self._events = queue.Queue()
        self._next_id = 0
        self.connected = False
        self.last_state = None          # most recent heartbeat
        self.last_state_at = None       # time.monotonic() of that heartbeat
        self._log = open(log_path, "a", encoding="utf-8") if log_path else None
        self._connect_timeout = connect_timeout

    # ---- connection ----------------------------------------------------
    def connect(self):
        try:
            self._sock = socket.create_connection((self.host, self.port), timeout=self._connect_timeout)
        except OSError as e:
            raise GameConnectionError(f"cannot connect to {self.host}:{self.port}: {e}") from e
        self._sock.settimeout(None)
        self.connected = True
        threading.Thread(target=self._read_loop, name="gta-client-read", daemon=True).start()
        return self

    def close(self):
        self.connected = False
        try:
            if self._sock:
                self._sock.close()
        except OSError:
            pass
        if self._log:
            self._log.close()
            self._log = None

    def __enter__(self):
        return self.connect()

    def __exit__(self, *exc):
        self.close()

    # ---- requests ------------------------------------------------------
    def request(self, type_, timeout=3.0, **fields):
        """Send one request and wait for its ack. Returns the ack dict (check ack['ok'])."""
        if not self.connected:
            raise GameConnectionError("not connected")
        self._next_id += 1
        req_id = f"c{self._next_id}"
        msg = {"id": req_id, "type": type_}
        msg.update(fields)
        waiter = queue.Queue(maxsize=1)
        with self._acks_lock:
            self._acks[req_id] = waiter
        try:
            self.send_raw(json.dumps(msg, ensure_ascii=False))
            try:
                ack = waiter.get(timeout=timeout)
                if ack is None:  # woken by the reader because the connection dropped
                    raise GameConnectionError(f"connection lost while waiting for ack of {type_}; outcome unknown")
                return ack
            except queue.Empty:
                if not self.connected:
                    raise GameConnectionError("connection lost while waiting for ack") from None
                raise NoAck(f"no ack for {type_} within {timeout}s (game paused, map open, or mod not responding)") from None
        finally:
            with self._acks_lock:
                self._acks.pop(req_id, None)

    def send_raw(self, line):
        """Write one raw line (used by tests to send malformed input)."""
        self._record("send", line)
        try:
            with self._send_lock:
                self._sock.sendall(line.encode("utf-8") + b"\n")
        except OSError as e:
            self.connected = False
            raise GameConnectionError(f"send failed: {e}") from e

    # ---- events --------------------------------------------------------
    def next_event(self, timeout=None):
        """Next event dict, or None on timeout. A {'event': 'disconnected'} item marks a lost connection."""
        try:
            return self._events.get(timeout=timeout)
        except queue.Empty:
            return None

    def drain_events(self):
        out = []
        while True:
            try:
                out.append(self._events.get_nowait())
            except queue.Empty:
                return out

    # ---- internals -----------------------------------------------------
    def _read_loop(self):
        buf = b""
        try:
            while True:
                chunk = self._sock.recv(65536)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    raw, buf = buf.split(b"\n", 1)
                    self._handle_line(raw.decode("utf-8", errors="replace").strip())
        except OSError:
            pass
        finally:
            was_connected = self.connected
            self.connected = False
            with self._acks_lock:
                for waiter in self._acks.values():   # fail pending requests now, not after their timeout
                    try:
                        waiter.put_nowait(None)
                    except queue.Full:
                        pass
            if was_connected:
                self._events.put({"type": "event", "event": "disconnected"})

    def _handle_line(self, line):
        if not line:
            return
        self._record("recv", line)
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            return
        if not isinstance(msg, dict):
            return
        kind = msg.get("type")
        if kind == "ack":
            with self._acks_lock:
                waiter = self._acks.get(msg.get("id"))
            if waiter is not None:
                waiter.put(msg)
            else:
                # ack for a raw line or an already timed-out request
                self._events.put({"type": "event", "event": "_orphan_ack", "ack": msg})
        elif kind == "state":
            self.last_state = msg
            self.last_state_at = time.monotonic()
        elif kind == "event":
            self._events.put(msg)

    def _record(self, direction, line):
        if self._log:
            self._log.write(json.dumps({"t": round(time.time(), 3), "dir": direction, "line": line}, ensure_ascii=False) + "\n")
            self._log.flush()
