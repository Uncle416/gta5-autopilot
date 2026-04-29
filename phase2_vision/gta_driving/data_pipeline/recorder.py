"""
TCP telemetry recorder. Receives telemetry frames from the GTA V C# mod
and writes them to HDF5 files for later training.
"""

import socket
import struct
import threading
import time
from pathlib import Path

import h5py
import numpy as np


class TelemetryRecorder:
    """Listens for telemetry data from GTA V and records to HDF5."""

    def __init__(self, host: str = "127.0.0.1", port: int = 21555,
                 output_dir: str = "data/raw"):
        self.host = host
        self.port = port
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)

        self._socket: socket.socket | None = None
        self._recording = False
        self._buffer: list[dict] = []
        self._session_id = 0
        self._frame_count = 0
        self._thread: threading.Thread | None = None

    def start_server(self) -> None:
        """Start TCP server to accept GTA V connection."""
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._socket.bind((self.host, self.port))
        self._socket.listen(1)
        self._socket.settimeout(1.0)
        print(f"[Recorder] Listening on {self.host}:{self.port}")

        self._recording = True
        self._thread = threading.Thread(target=self._accept_loop, daemon=True)
        self._thread.start()

    def stop_server(self) -> None:
        """Stop the TCP server and flush remaining data."""
        self._recording = False
        if self._thread:
            self._thread.join(timeout=5.0)
        if self._socket:
            self._socket.close()
        self._flush_to_disk()
        print(f"[Recorder] Stopped. Total frames: {self._frame_count}")

    def _accept_loop(self) -> None:
        """Accept connection and read frames."""
        while self._recording:
            try:
                client, addr = self._socket.accept()
                print(f"[Recorder] Connected from {addr}")
                self._session_id += 1
                self._read_frames(client)
                client.close()
            except socket.timeout:
                continue
            except OSError:
                break

    def _read_frames(self, client: socket.socket) -> None:
        """Read telemetry frames from client connection."""
        client.settimeout(0.5)
        buffer = b""

        while self._recording:
            try:
                data = client.recv(4096)
                if not data:
                    break
                buffer += data

                # Parse complete frames from buffer
                while len(buffer) >= 8:  # min frame header
                    frame_id = struct.unpack("<Q", buffer[:8])[0]
                    # Frame parsing depends on binary protocol version
                    # For now, store raw bytes
                    self._buffer.append({
                        "frame_id": frame_id,
                        "raw": buffer[:256],  # Fixed-size frames for simplicity
                    })
                    buffer = buffer[256:]
                    self._frame_count += 1

                # Periodic flush
                if len(self._buffer) >= 500:
                    self._flush_to_disk()

            except socket.timeout:
                continue
            except OSError:
                break

    def _flush_to_disk(self) -> None:
        """Write buffered data to HDF5 file."""
        if not self._buffer:
            return

        filepath = self.output_dir / f"session_{self._session_id:03d}.h5"
        print(f"[Recorder] Flushing {len(self._buffer)} frames to {filepath}")

        with h5py.File(filepath, "a") as f:
            # For initial implementation, store frame IDs and raw data
            for item in self._buffer:
                grp = f.create_group(f"frame_{item['frame_id']:06d}")
                grp.create_dataset("raw", data=np.frombuffer(item["raw"], dtype=np.uint8))

        self._buffer.clear()


if __name__ == "__main__":
    recorder = TelemetryRecorder()
    recorder.start_server()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        recorder.stop_server()
