#!/usr/bin/env python3
"""
Data collection script for Phase 2 visual perception training.

Records screenshots + Phase 1 perception data for auto-labeling.
Phase 1's game-API-based detections serve as ground truth labels,
enabling training of visual perception models without manual annotation.

Usage:
    python scripts/collect_dataset.py --output data/raw/session_01

Requirements:
    - GTA V running with Phase 1 mod loaded and recording enabled
    - Python recorder receiving telemetry frames
"""

import argparse
import json
import struct
import threading
import time
from pathlib import Path

import cv2
import numpy as np


class DatasetCollector:
    """Records synchronized screenshots and perception data from GTA V."""

    def __init__(self, output_dir: str, host: str = "127.0.0.1", port: int = 21555,
                 capture_interval: float = 0.5):
        """
        Args:
            output_dir: Directory to save collected data
            host: Telemetry server host
            port: Telemetry server port
            capture_interval: Time between captures in seconds (0.5 = 2 FPS)
        """
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self.host = host
        self.port = port
        self.capture_interval = capture_interval

        # Subdirectories
        self.screenshots_dir = self.output_dir / "screenshots"
        self.screenshots_dir.mkdir(exist_ok=True)
        self.labels_dir = self.output_dir / "labels"
        self.labels_dir.mkdir(exist_ok=True)

        self._running = False
        self._frame_count = 0
        self._annotation_file = self.output_dir / "annotations.jsonl"

    def start(self) -> None:
        """Start collecting data from GTA V."""
        self._running = True
        self._thread = threading.Thread(target=self._collect_loop, daemon=True)
        self._thread.start()
        print(f"[Collector] Started. Saving to {self.output_dir}")
        print(f"[Collector] Capture rate: 1 frame every {self.capture_interval}s")

    def stop(self) -> None:
        """Stop collecting."""
        self._running = False
        print(f"\n[Collector] Stopped. Collected {self._frame_count} frames.")
        print(f"[Collector] Data saved to {self.output_dir}")

    def _collect_loop(self) -> None:
        """Main collection loop — captures from screen."""
        import socket

        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((self.host, self.port))
        sock.listen(1)
        sock.settimeout(1.0)

        print(f"[Collector] Waiting for GTA V connection on {self.host}:{self.port}...")

        try:
            client, addr = sock.accept()
            print(f"[Collector] GTA V connected from {addr}")
            client.settimeout(0.5)
        except socket.timeout:
            print("[Collector] No connection from GTA V. Is the mod running with recording enabled?")
            sock.close()
            return

        last_capture = 0.0

        while self._running:
            try:
                data = client.recv(4096)
                if not data:
                    break

                # Parse telemetry (simplified: just record with timestamp)
                now = time.time()
                if now - last_capture < self.capture_interval:
                    continue
                last_capture = now

                self._frame_count += 1
                frame_id = self._frame_count

                # Capture screenshot from GTA V window
                screenshot = self._capture_screen()
                if screenshot is not None:
                    img_path = self.screenshots_dir / f"frame_{frame_id:06d}.png"
                    cv2.imwrite(str(img_path), screenshot)

                    # Parse telemetry data and save annotations
                    annotation = self._parse_telemetry(data, frame_id)
                    annotation["screenshot"] = str(img_path.name)

                    with open(self._annotation_file, "a") as f:
                        f.write(json.dumps(annotation) + "\n")

                    if frame_id % 20 == 0:
                        print(f"[Collector] Captured {frame_id} frames...")

            except socket.timeout:
                continue
            except OSError:
                break

        client.close()
        sock.close()

    def _capture_screen(self) -> np.ndarray | None:
        """
        Capture GTA V game window screenshot.
        Uses platform-appropriate method.
        """
        try:
            # On Windows, use DXGI Desktop Duplication through the .asi plugin.
            # This is a placeholder — in production, the C++ .asi plugin
            # writes frames to a shared memory segment that Python reads.

            # Fallback: use MSS (pip install mss) for cross-platform screen capture
            import mss
            with mss.mss() as sct:
                monitor = sct.monitors[1]  # Primary monitor
                img = np.array(sct.grab(monitor))
                return img[:, :, :3]  # BGRA → BGR
        except ImportError:
            print("[Collector] MSS not installed. Install with: pip install mss")
            return None
        except Exception as e:
            print(f"[Collector] Screen capture failed: {e}")
            return None

    def _parse_telemetry(self, data: bytes, frame_id: int) -> dict:
        """
        Parse binary telemetry packet from Phase 1 C# mod.
        Extracts entities, traffic light state, lane info for auto-labeling.

        Format (matches TelemetryExporter binary protocol):
        [u64 frame_id][f32 speed][f32 steer_angle][f32 heading]
        [f32 pos_x][f32 pos_y][f32 pos_z]
        [f32 cmd_steer][f32 cmd_throttle][f32 cmd_brake][f32 cmd_target_speed]
        [u8 handbrake][u8 reverse]
        [f32 road_heading][i32 road_type][i32 lane_count]
        [u8 is_intersection][f32 dist_to_intersection]
        [byte traffic_light][byte collision_risk]
        [u16 entity_count][entity_data...][u16 decision_state]
        """
        try:
            offset = 0

            def read_f32():
                nonlocal offset
                val = struct.unpack_from("<f", data, offset)[0]
                offset += 4
                return val

            frame_id_raw = struct.unpack_from("<Q", data, offset)[0]; offset += 8
            speed = read_f32()
            steer_angle = read_f32()
            heading = read_f32()
            pos_x = read_f32()
            pos_y = read_f32()
            pos_z = read_f32()
            cmd_steer = read_f32()
            cmd_throttle = read_f32()
            cmd_brake = read_f32()
            cmd_target_speed = read_f32()

            handbrake = data[offset]; offset += 1
            reverse = data[offset]; offset += 1

            road_heading = read_f32()
            road_type = struct.unpack_from("<i", data, offset)[0]; offset += 4
            lane_count = struct.unpack_from("<i", data, offset)[0]; offset += 4

            is_intersection = data[offset]; offset += 1
            dist_intersection = read_f32()
            traffic_light = data[offset]; offset += 1
            collision_risk = data[offset]; offset += 1

            entity_count = struct.unpack_from("<H", data, offset)[0]; offset += 2
            entities = []
            for _ in range(min(entity_count, 20)):
                e_dist = read_f32()
                e_speed = read_f32()
                e_is_veh = data[offset]; offset += 1
                e_is_ped = data[offset]; offset += 1
                e_forward_cone = data[offset]; offset += 1
                e_oncoming = data[offset]; offset += 1
                e_ttc = read_f32()
                e_pos_x = read_f32()
                e_pos_y = read_f32()
                e_pos_z = read_f32()
                entities.append({
                    "distance": e_dist,
                    "speed": e_speed,
                    "is_vehicle": bool(e_is_veh),
                    "is_pedestrian": bool(e_is_ped),
                    "is_in_forward_cone": bool(e_forward_cone),
                    "is_oncoming": bool(e_oncoming),
                    "time_to_collision": e_ttc,
                })

            return {
                "frame_id": frame_id,
                "vehicle": {
                    "speed": speed,
                    "heading": heading,
                    "position": [pos_x, pos_y, pos_z],
                },
                "road": {
                    "heading": road_heading,
                    "type": road_type,
                    "lane_count": lane_count,
                    "is_intersection": bool(is_intersection),
                },
                "traffic_light_state": int(traffic_light),
                "collision_risk": int(collision_risk),
                "entities": entities,
                "entity_count": len(entities),
            }
        except (struct.error, IndexError):
            return {"frame_id": frame_id, "error": "parse_failed"}


def main():
    parser = argparse.ArgumentParser(description="Collect GTA V driving dataset")
    parser.add_argument("--output", default="data/raw/session_01", help="Output directory")
    parser.add_argument("--host", default="127.0.0.1", help="Telemetry host")
    parser.add_argument("--port", type=int, default=21555, help="Telemetry port")
    parser.add_argument("--interval", type=float, default=0.5,
                        help="Capture interval in seconds (0.5 = 2 FPS)")
    args = parser.parse_args()

    collector = DatasetCollector(
        output_dir=args.output,
        host=args.host,
        port=args.port,
        capture_interval=args.interval,
    )
    collector.start()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        collector.stop()


if __name__ == "__main__":
    main()
