#!/usr/bin/env python3
"""
CLI script to start recording a GTA V driving session.

Usage:
    python scripts/record_session.py --output data/raw

Before running:
1. Start GTA V with the Phase 1 autopilot mod loaded
2. Enable autopilot (Numpad0)
3. Enable recording (Numpad3)
4. Run this script to capture telemetry on the Python side
"""

import argparse
import signal
import sys
import time

sys.path.insert(0, "..")

from gta_driving.data_pipeline.recorder import TelemetryRecorder


def main():
    parser = argparse.ArgumentParser(description="Record GTA V driving session")
    parser.add_argument("--host", default="127.0.0.1", help="Telemetry server host")
    parser.add_argument("--port", type=int, default=21555, help="Telemetry server port")
    parser.add_argument("--output", default="data/raw", help="Output directory for HDF5 files")
    args = parser.parse_args()

    recorder = TelemetryRecorder(
        host=args.host,
        port=args.port,
        output_dir=args.output,
    )

    def signal_handler(sig, frame):
        print("\n[Recorder] Received stop signal")
        recorder.stop_server()
        sys.exit(0)

    signal.signal(signal.SIGINT, signal_handler)

    print(f"[Recorder] Starting. Press Ctrl+C to stop.")
    recorder.start_server()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        recorder.stop_server()


if __name__ == "__main__":
    main()
