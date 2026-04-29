#!/usr/bin/env python3
"""
CLI script to train a driving policy model.

Usage:
    python scripts/train_policy.py --data data/processed --output experiments/runs/exp01

The data directory should contain train/ and val/ subdirectories with HDF5 files.
"""

import argparse
import sys
from pathlib import Path

sys.path.insert(0, "..")

from gta_driving.config import GTAConfig
from gta_driving.policy.trainer import DrivingTrainer


def main():
    parser = argparse.ArgumentParser(description="Train driving policy model")
    parser.add_argument("--data", required=True, help="Path to processed dataset")
    parser.add_argument("--output", required=True, help="Output directory for model checkpoints")
    parser.add_argument("--config", default=None, help="Path to YAML config file")
    parser.add_argument("--arch", default="resnet18_cil",
                        choices=["pilotnet", "resnet18_cil", "resnet18_lstm"],
                        help="Model architecture")
    parser.add_argument("--pretrained", action="store_true", default=True,
                        help="Use ImageNet pretrained backbone")
    parser.add_argument("--batch_size", type=int, default=32, help="Training batch size")
    parser.add_argument("--lr", type=float, default=1e-4, help="Learning rate")
    parser.add_argument("--epochs", type=int, default=100, help="Number of epochs")
    parser.add_argument("--device", default="cuda", help="Device (cuda or cpu)")
    args = parser.parse_args()

    # Load or create config
    if args.config:
        config = GTAConfig.from_yaml(args.config)
    else:
        config = GTAConfig()

    # Override from CLI
    config.model.architecture = args.arch
    config.model.pretrained = args.pretrained
    config.training.batch_size = args.batch_size
    config.training.learning_rate = args.lr
    config.training.num_epochs = args.epochs
    config.inference.device = args.device

    # Save config for reproducibility
    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)
    config.to_yaml(str(output_dir / "config.yaml"))

    # Train
    trainer = DrivingTrainer(config)
    trainer.train(data_dir=args.data, output_dir=args.output)

    print(f"[Train] Complete. Model saved to {output_dir}/best_model.pt")


if __name__ == "__main__":
    main()
