"""
Training loop for driving policy models.

Handles:
- Training/validation split evaluation
- Loss computation (weighted MSE for steer, throttle, brake)
- Model checkpointing
- TensorBoard logging
"""

import time
from pathlib import Path
from typing import Optional

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.cuda.amp import GradScaler, autocast
from torch.utils.data import DataLoader
from torch.utils.tensorboard import SummaryWriter
from tqdm import tqdm

from .models import create_model
from .dataset import GTADrivingDataset, collate_sequences


class DrivingTrainer:
    """Trains end-to-end driving policy models."""

    def __init__(self, config):
        self.config = config
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

        # Create model
        self.model = create_model(config).to(self.device)

        # Loss function: weighted MSE
        self.steer_weight = config.training.steer_loss_weight
        self.throttle_weight = config.training.throttle_loss_weight
        self.brake_weight = config.training.brake_loss_weight

        # Optimizer
        self.optimizer = torch.optim.AdamW(
            self.model.parameters(),
            lr=config.training.learning_rate,
            weight_decay=config.training.weight_decay,
        )

        # Scheduler
        if config.training.scheduler == "cosine":
            self.scheduler = torch.optim.lr_scheduler.CosineAnnealingWarmRestarts(
                self.optimizer, T_0=config.training.cosine_t0
            )
        elif config.training.scheduler == "plateau":
            self.scheduler = torch.optim.lr_scheduler.ReduceLROnPlateau(
                self.optimizer, mode="min", patience=5, factor=0.5
            )
        else:
            self.scheduler = None

        # Mixed precision
        self.scaler = GradScaler(enabled=config.training.use_amp)

        # Logging
        self.writer: Optional[SummaryWriter] = None
        self.best_val_loss = float("inf")
        self.best_epoch = 0
        self.patience_counter = 0

    def train(self, data_dir: str, output_dir: str) -> None:
        """Full training loop."""
        output_dir = Path(output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)
        self.writer = SummaryWriter(log_dir=str(output_dir / "logs"))

        # Create datasets
        train_dataset = GTADrivingDataset(
            data_dir, split="train",
            sequence_length=self.config.data.sequence_length,
            augment=True,
        )
        val_dataset = GTADrivingDataset(
            data_dir, split="val",
            sequence_length=self.config.data.sequence_length,
            augment=False,
        )

        train_loader = DataLoader(
            train_dataset,
            batch_size=self.config.training.batch_size,
            shuffle=True,
            num_workers=self.config.training.num_workers,
            collate_fn=collate_sequences,
            pin_memory=True,
        )
        val_loader = DataLoader(
            val_dataset,
            batch_size=self.config.training.batch_size,
            shuffle=False,
            num_workers=self.config.training.num_workers,
            collate_fn=collate_sequences,
            pin_memory=True,
        )

        print(f"[Trainer] Device: {self.device}")
        print(f"[Trainer] Train samples: {len(train_dataset)}, Val: {len(val_dataset)}")
        print(f"[Trainer] Model params: {sum(p.numel() for p in self.model.parameters()):,}")

        for epoch in range(self.config.training.num_epochs):
            # Training
            self.model.train()
            train_loss = 0.0
            train_mae = 0.0

            pbar = tqdm(train_loader, desc=f"Epoch {epoch+1}/{self.config.training.num_epochs}")
            for batch in pbar:
                loss, metrics = self._train_step(batch)
                train_loss += loss
                train_mae += metrics["steer_mae"]
                pbar.set_postfix({"loss": f"{loss:.4f}", "steer_mae": f"{metrics['steer_mae']:.4f}"})

            train_loss /= len(train_loader)
            train_mae /= len(train_loader)

            # Validation
            val_loss, val_metrics = self._validate(val_loader)

            # Logging
            self.writer.add_scalar("Loss/train", train_loss, epoch)
            self.writer.add_scalar("Loss/val", val_loss, epoch)
            self.writer.add_scalar("MAE_Steer/train", train_mae, epoch)
            self.writer.add_scalar("MAE_Steer/val", val_metrics["steer_mae"], epoch)
            self.writer.add_scalar("MAE_Throttle/val", val_metrics["throttle_mae"], epoch)
            self.writer.add_scalar("LR", self.optimizer.param_groups[0]["lr"], epoch)

            print(f"  Epoch {epoch+1}: train_loss={train_loss:.4f}, val_loss={val_loss:.4f}, "
                  f"steer_mae={val_metrics['steer_mae']:.4f}")

            # Scheduler step
            if self.scheduler:
                if isinstance(self.scheduler, torch.optim.lr_scheduler.ReduceLROnPlateau):
                    self.scheduler.step(val_loss)
                else:
                    self.scheduler.step()

            # Checkpoint
            if val_loss < self.best_val_loss:
                self.best_val_loss = val_loss
                self.best_epoch = epoch
                self.patience_counter = 0
                self._save_checkpoint(output_dir / "best_model.pt", epoch, val_loss)
            else:
                self.patience_counter += 1

            # Early stopping
            if self.patience_counter >= self.config.training.early_stopping_patience:
                print(f"[Trainer] Early stopping at epoch {epoch+1}")
                break

        print(f"[Trainer] Training complete. Best val_loss={self.best_val_loss:.4f} at epoch {self.best_epoch+1}")
        self.writer.close()

    def _train_step(self, batch: dict) -> tuple[float, dict]:
        """Single training step. Returns (loss, metrics_dict)."""
        images = batch["images"].to(self.device)
        speed = batch["speed"].to(self.device)
        command = batch["command"].to(self.device)
        target = batch["target"].to(self.device)

        self.optimizer.zero_grad()

        with autocast(enabled=self.config.training.use_amp):
            # Forward pass depends on model architecture
            if isinstance(self.model, torch.nn.Module):
                # ResNet18CIL needs command; others may not
                try:
                    pred = self.model(images, command=command, speed=speed)
                except TypeError:
                    # Temporal models expect (B,T,C,H,W)
                    pred = self.model(images, speed)

            loss = self._compute_loss(pred, target)

        self.scaler.scale(loss).backward()

        # Gradient clipping
        self.scaler.unscale_(self.optimizer)
        torch.nn.utils.clip_grad_norm_(
            self.model.parameters(), self.config.training.gradient_clip_norm
        )

        self.scaler.step(self.optimizer)
        self.scaler.update()

        with torch.no_grad():
            metrics = self._compute_metrics(pred, target)

        return loss.item(), metrics

    @torch.no_grad()
    def _validate(self, loader: DataLoader) -> tuple[float, dict]:
        """Validation loop."""
        self.model.eval()
        total_loss = 0.0
        all_metrics = {"steer_mae": 0.0, "throttle_mae": 0.0, "brake_mae": 0.0}

        for batch in loader:
            images = batch["images"].to(self.device)
            speed = batch["speed"].to(self.device)
            command = batch["command"].to(self.device)
            target = batch["target"].to(self.device)

            try:
                pred = self.model(images, command=command, speed=speed)
            except TypeError:
                pred = self.model(images, speed)

            loss = self._compute_loss(pred, target)
            total_loss += loss.item()

            metrics = self._compute_metrics(pred, target)
            for k in all_metrics:
                all_metrics[k] += metrics[k]

        n = len(loader)
        return total_loss / n, {k: v / n for k, v in all_metrics.items()}

    def _compute_loss(self, pred: torch.Tensor, target: torch.Tensor) -> torch.Tensor:
        """Weighted MSE loss."""
        steer_loss = F.mse_loss(pred[:, 0], target[:, 0])
        throttle_loss = F.mse_loss(pred[:, 1], target[:, 1])
        brake_loss = F.mse_loss(pred[:, 2], target[:, 2])

        return (
            self.steer_weight * steer_loss
            + self.throttle_weight * throttle_loss
            + self.brake_weight * brake_loss
        )

    def _compute_metrics(self, pred: torch.Tensor, target: torch.Tensor) -> dict:
        """Compute per-dimension MAE."""
        return {
            "steer_mae": F.l1_loss(pred[:, 0], target[:, 0]).item(),
            "throttle_mae": F.l1_loss(pred[:, 1], target[:, 1]).item(),
            "brake_mae": F.l1_loss(pred[:, 2], target[:, 2]).item(),
        }

    def _save_checkpoint(self, path: Path, epoch: int, val_loss: float) -> None:
        torch.save({
            "epoch": epoch,
            "model_state_dict": self.model.state_dict(),
            "optimizer_state_dict": self.optimizer.state_dict(),
            "val_loss": val_loss,
            "config": self.config,
        }, path)
