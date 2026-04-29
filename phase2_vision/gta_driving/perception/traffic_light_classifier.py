"""
Traffic light classifier using a lightweight CNN.

Takes an image crop of a traffic light and classifies its state:
0 = no light, 1 = green, 2 = yellow, 3 = red

Architecture: Small custom CNN (~50K params), fast inference.
For better accuracy, can be swapped with a fine-tuned ResNet-18.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F


class TrafficLightCNN(nn.Module):
    """Lightweight CNN for traffic light state classification.

    Input: (B, 3, 32, 32) normalized image crop
    Output: (B, 4) logits for [none, green, yellow, red]
    """

    def __init__(self, num_classes: int = 4):
        super().__init__()
        self.conv = nn.Sequential(
            # Block 1: 32x32 → 16x16
            nn.Conv2d(3, 16, 3, padding=1), nn.BatchNorm2d(16),
            nn.ReLU(), nn.MaxPool2d(2),
            # Block 2: 16x16 → 8x8
            nn.Conv2d(16, 32, 3, padding=1), nn.BatchNorm2d(32),
            nn.ReLU(), nn.MaxPool2d(2),
            # Block 3: 8x8 → 4x4
            nn.Conv2d(32, 64, 3, padding=1), nn.BatchNorm2d(64),
            nn.ReLU(), nn.MaxPool2d(2),
        )
        self.fc = nn.Sequential(
            nn.Flatten(),
            nn.Linear(64 * 4 * 4, 128),
            nn.ReLU(),
            nn.Dropout(0.3),
            nn.Linear(128, num_classes),
        )
        self._init_weights()

    def _init_weights(self):
        for m in self.modules():
            if isinstance(m, nn.Conv2d):
                nn.init.kaiming_normal_(m.weight, mode="fan_out", nonlinearity="relu")
            elif isinstance(m, nn.BatchNorm2d):
                nn.init.constant_(m.weight, 1)
                nn.init.constant_(m.bias, 0)
            elif isinstance(m, nn.Linear):
                nn.init.normal_(m.weight, 0, 0.01)
                nn.init.constant_(m.bias, 0)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.conv(x)


class TrafficLightClassifier:
    """Classifier wrapper with preprocessing and inference."""

    STATE_NAMES = {0: "none", 1: "green", 2: "yellow", 3: "red"}

    def __init__(self, model_path: Optional[str] = None, device: str = "cuda"):
        self.device = torch.device(
            device if torch.cuda.is_available() and device == "cuda" else "cpu"
        )
        self.model = TrafficLightCNN(num_classes=4).to(self.device)

        if model_path:
            self.model.load_state_dict(
                torch.load(model_path, map_location=self.device)
            )
        self.model.eval()

        # Input normalization
        self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1, 3, 1, 1)
        self.std = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1, 3, 1, 1)

    @torch.no_grad()
    def classify(self, image_crop: np.ndarray) -> tuple[int, float]:
        """Classify a traffic light image crop.

        Args:
            image_crop: (H, W, 3) RGB image of a traffic light, any size

        Returns:
            (state, confidence) where state is 0=none, 1=green, 2=yellow, 3=red
        """
        import cv2

        # Preprocess: resize to 32x32, normalize
        resized = cv2.resize(image_crop, (32, 32))
        tensor = torch.from_numpy(resized).permute(2, 0, 1).float().div(255.0)
        tensor = tensor.unsqueeze(0).to(self.device)

        # Normalize with ImageNet stats
        tensor = (tensor - self.mean) / self.std

        # Inference
        logits = self.model(tensor)
        probs = F.softmax(logits, dim=1)
        state = int(probs.argmax(dim=1).item())
        confidence = float(probs[0, state].item())

        return state, confidence

    def train_from_data(
        self,
        train_dir: str,
        val_dir: str,
        output_path: str,
        epochs: int = 30,
        batch_size: int = 64,
        lr: float = 1e-3,
    ):
        """Train the classifier from labeled image data.

        Expected directory structure:
            train_dir/
                none/     ← images with no traffic light
                green/    ← green light images
                yellow/   ← yellow light images
                red/      ← red light images
        """
        from pathlib import Path

        from torch.utils.data import DataLoader
        from torchvision import datasets, transforms

        transform = transforms.Compose([
            transforms.Resize((32, 32)),
            transforms.ToTensor(),
            transforms.Normalize(mean=[0.485, 0.456, 0.406],
                                 std=[0.229, 0.224, 0.225]),
        ])

        train_dataset = datasets.ImageFolder(train_dir, transform=transform)
        val_dataset = datasets.ImageFolder(val_dir, transform=transform)

        train_loader = DataLoader(train_dataset, batch_size=batch_size, shuffle=True)
        val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False)

        optimizer = torch.optim.AdamW(self.model.parameters(), lr=lr)
        scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs)
        criterion = nn.CrossEntropyLoss()

        best_acc = 0.0
        for epoch in range(epochs):
            # Training
            self.model.train()
            train_loss = 0.0
            for images, labels in train_loader:
                images, labels = images.to(self.device), labels.to(self.device)
                optimizer.zero_grad()
                logits = self.model(images)
                loss = criterion(logits, labels)
                loss.backward()
                optimizer.step()
                train_loss += loss.item()

            # Validation
            self.model.eval()
            correct, total = 0, 0
            with torch.no_grad():
                for images, labels in val_loader:
                    images, labels = images.to(self.device), labels.to(self.device)
                    logits = self.model(images)
                    pred = logits.argmax(dim=1)
                    correct += (pred == labels).sum().item()
                    total += labels.size(0)

            acc = correct / total
            scheduler.step()

            if acc > best_acc:
                best_acc = acc
                torch.save(self.model.state_dict(), output_path)

            if (epoch + 1) % 5 == 0:
                print(f"Epoch {epoch+1}/{epochs}: loss={train_loss/len(train_loader):.4f}, "
                      f"val_acc={acc:.3f}")

        print(f"Training complete. Best val_acc: {best_acc:.3f}")
        return best_acc


# Optional import
from typing import Optional
import numpy as np
