"""
PyTorch Dataset for GTA V driving data stored in HDF5 format.

Each HDF5 file contains frames with:
- /frames: (N, 3, 66, 200) normalized images
- /speed, /steer, /throttle, /brake: (N,) control signals
- /command: (N,) navigation commands (0=straight, 1=left, 2=right)
"""

import random
from pathlib import Path
from typing import Optional

import h5py
import numpy as np
import torch
from torch.utils.data import Dataset


class GTADrivingDataset(Dataset):
    """Loads recorded GTA V driving sessions for training."""

    def __init__(
        self,
        data_dir: str,
        split: str = "train",
        sequence_length: int = 5,
        image_size: tuple[int, int] = (200, 66),
        augment: bool = True,
        command_weight_sampling: bool = True,
    ):
        self.data_dir = Path(data_dir)
        self.split = split
        self.sequence_length = sequence_length
        self.image_size = image_size
        self.augment = augment and split == "train"
        self.command_weight_sampling = command_weight_sampling

        # Find all HDF5 files in the split directory
        split_dir = self.data_dir / split
        self.files = sorted(split_dir.glob("*.h5"))

        # Build index: (file_idx, frame_idx) for each valid sequence
        self.indices: list[tuple[int, int]] = []
        for file_idx, filepath in enumerate(self.files):
            with h5py.File(filepath, "r") as f:
                num_frames = len(f["speed"])
                # Valid sequences need 'sequence_length' consecutive frames
                for frame_idx in range(num_frames - sequence_length + 1):
                    self.indices.append((file_idx, frame_idx))

        print(f"[Dataset] {split}: {len(self.indices)} sequences from {len(self.files)} files")

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, idx: int) -> dict[str, torch.Tensor]:
        file_idx, frame_idx = self.indices[idx]

        with h5py.File(self.files[file_idx], "r") as f:
            # Load sequence of images
            start = frame_idx
            end = frame_idx + self.sequence_length

            images = f["frames"][start:end].astype(np.float32)
            speed = f["speed"][start:end].astype(np.float32)
            steer = f["steer"][start:end].astype(np.float32)
            throttle = f["throttle"][start:end].astype(np.float32)
            brake = f["brake"][start:end].astype(np.float32)

            # Command (use first frame's command for the sequence)
            command = int(f["command"][start])

        # Data augmentation
        if self.augment and random.random() < 0.5:
            images = self._augment_horizontal_flip(images)
            steer = -steer  # Mirror steering
            # Flip command: left ↔ right
            if command == 1:
                command = 2
            elif command == 2:
                command = 1

        if self.augment:
            images = self._augment_brightness_contrast(images)
            images = self._augment_noise(images)

        # Convert to tensors
        images_tensor = torch.from_numpy(images)  # (T, C, H, W)
        speed_tensor = torch.from_numpy(speed).unsqueeze(-1)  # (T, 1)

        # Target: last frame's controls
        target = torch.tensor([
            steer[-1],
            throttle[-1],
            brake[-1],
        ], dtype=torch.float32)

        return {
            "images": images_tensor,
            "speed": speed_tensor,
            "command": torch.tensor(command, dtype=torch.long),
            "target": target,
        }

    # ---- Augmentation methods ----

    def _augment_horizontal_flip(self, images: np.ndarray) -> np.ndarray:
        return images[:, :, :, ::-1].copy()

    def _augment_brightness_contrast(self, images: np.ndarray) -> np.ndarray:
        """Random brightness/contrast adjustment (±20%)."""
        factor = 1.0 + random.uniform(-0.2, 0.2)
        return np.clip(images * factor, 0.0, 1.0)

    def _augment_noise(self, images: np.ndarray) -> np.ndarray:
        """Add small Gaussian noise."""
        noise = np.random.normal(0, 0.01, images.shape).astype(np.float32)
        return np.clip(images + noise, 0.0, 1.0)


def collate_sequences(batch: list[dict]) -> dict[str, torch.Tensor]:
    """Custom collate function for sequences of varying lengths."""
    return {
        "images": torch.stack([item["images"] for item in batch]),
        "speed": torch.stack([item["speed"] for item in batch]),
        "command": torch.stack([item["command"] for item in batch]),
        "target": torch.stack([item["target"] for item in batch]),
    }
