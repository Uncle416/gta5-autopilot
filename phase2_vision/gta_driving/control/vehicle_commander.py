"""
TCP client that sends control commands back to the GTA V C# mod.

The Phase 1 mod listens for external commands and can blend them
with its own rule-based outputs. This enables Phase 2 model control.
"""

import socket
import struct
import threading
import time
from typing import Optional

import numpy as np


class VehicleCommander:
    """Sends driving commands to GTA V via TCP."""

    def __init__(self, host: str = "127.0.0.1", port: int = 21556):
        self.host = host
        self.port = port
        self._socket: Optional[socket.socket] = None
        self._connected = False
        self._lock = threading.Lock()

    def connect(self) -> bool:
        """Connect to the GTA V mod command receiver."""
        try:
            self._socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self._socket.settimeout(2.0)
            self._socket.connect((self.host, self.port))
            self._connected = True
            print(f"[Commander] Connected to GTA V at {self.host}:{self.port}")
            return True
        except (ConnectionRefusedError, socket.timeout, OSError) as e:
            print(f"[Commander] Failed to connect: {e}")
            self._connected = False
            return False

    def disconnect(self) -> None:
        """Disconnect from GTA V."""
        self._connected = False
        if self._socket:
            try:
                self._socket.close()
            except OSError:
                pass
            self._socket = None

    def send_command(self, steer: float, throttle: float, brake: float,
                     handbrake: bool = False, reverse: bool = False) -> bool:
        """Send a control command to GTA V.

        Args:
            steer: -1.0 (full left) to 1.0 (full right)
            throttle: 0.0 to 1.0
            brake: 0.0 to 1.0
            handbrake: whether to engage handbrake
            reverse: whether to reverse

        Returns:
            True if command was sent successfully
        """
        if not self._connected:
            return False

        with self._lock:
            try:
                # Binary protocol:
                # [f32 steer][f32 throttle][f32 brake][u8 flags]
                # flags: bit0=handbrake, bit1=reverse
                flags = 0
                if handbrake:
                    flags |= 1
                if reverse:
                    flags |= 2

                packet = struct.pack("<fffb",
                                     steer, throttle, brake, flags)
                self._socket.sendall(packet)
                return True
            except (OSError, ConnectionError):
                self._connected = False
                return False

    def send_command_array(self, cmd: np.ndarray, handbrake: bool = False,
                           reverse: bool = False) -> bool:
        """Send command as numpy array [steer, throttle, brake]."""
        return self.send_command(
            float(cmd[0]), float(cmd[1]), float(cmd[2]),
            handbrake=handbrake, reverse=reverse,
        )
