#!/usr/bin/env python3
"""
CLI script to run real-time inference with a trained model.

Usage:
    python scripts/run_inference.py --model experiments/runs/exp01/best_model.pt

Before running:
1. GTA V must be running with Phase 1 mod loaded
2. Python recorder must be receiving telemetry
3. Model must be trained and available
"""

import argparse
import sys
import time

sys.path.insert(0, "..")

from gta_driving.config import GTAConfig
from gta_driving.policy.inference_engine import InferenceEngine
from gta_driving.control.safety_filter import SafetyFilter
from gta_driving.control.vehicle_commander import VehicleCommander


def main():
    parser = argparse.ArgumentParser(description="Run real-time driving inference")
    parser.add_argument("--model", required=True, help="Path to trained model checkpoint")
    parser.add_argument("--config", default=None, help="Path to YAML config file")
    parser.add_argument("--device", default="cuda", help="Device (cuda or cpu)")
    parser.add_argument("--no-safety", action="store_true", help="Disable safety filter")
    parser.add_argument("--dry-run", action="store_true",
                        help="Run inference without sending commands to GTA V")
    args = parser.parse_args()

    # Load config
    if args.config:
        config = GTAConfig.from_yaml(args.config)
    else:
        config = GTAConfig()

    config.inference.device = args.device
    if args.no_safety:
        config.inference.safety_enabled = False

    # Create inference engine
    engine = InferenceEngine(config, args.model)

    # Create safety filter
    safety = SafetyFilter(config)

    # Connect to GTA V (for sending commands)
    commander = None
    if not args.dry_run:
        commander = VehicleCommander()
        if not commander.connect():
            print("[Warning] Could not connect to GTA V. Running in dry-run mode.")
            commander = None

    # Start inference
    engine.start()

    print("[Inference] Running. Press Ctrl+C to stop.")
    print(f"[Inference] Avg inference time: ...")

    try:
        while True:
            # Get latest command
            result = engine.get_command()
            if result:
                cmd, confidence = result

                # Apply safety filter
                # Note: Phase 1 commands and collision risk would come from telemetry
                # This is a simplified example
                filtered_cmd = safety.filter(
                    model_cmd=cmd,
                    model_confidence=confidence,
                    phase1_cmd=None,  # Would come from telemetry
                    collision_risk=0,  # Would come from telemetry
                    vehicle_speed=0.0,  # Would come from telemetry
                )

                # Send to GTA V
                if commander:
                    commander.send_command_array(filtered_cmd)

                # Print status
                print(f"\r[Inference] steer={filtered_cmd[0]:+.3f} "
                      f"throttle={filtered_cmd[1]:.3f} "
                      f"brake={filtered_cmd[2]:.3f} "
                      f"conf={confidence:.2f} "
                      f"lat={engine.avg_inference_time_ms:.1f}ms "
                      f"intv={safety.intervention_rate:.2f}", end="")

            time.sleep(0.05)  # ~20 Hz

    except KeyboardInterrupt:
        print("\n[Inference] Stopping...")
    finally:
        engine.stop()
        if commander:
            commander.disconnect()
        print(f"[Inference] Final intervention rate: {safety.intervention_rate:.3f}")


if __name__ == "__main__":
    main()
