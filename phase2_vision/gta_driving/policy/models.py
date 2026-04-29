"""
Driving policy model architectures for end-to-end autonomous driving.

Implements:
- PilotNet: NVIDIA's CNN for steering angle prediction
- ResNet18CIL: ResNet-18 backbone with Conditional Imitation Learning branches
- ResNet18Temporal: ResNet-18 + Transformer for temporal sequence modeling
"""

import torch
import torch.nn as nn
import torch.nn.functional as F
from torchvision import models


# ==================== PilotNet (Baseline) ====================

class PilotNet(nn.Module):
    """NVIDIA's end-to-end driving CNN.
    Input: (B, 3, 66, 200) normalized images
    Output: (B, 3) [steer, throttle, brake]
    """

    def __init__(self, output_dim: int = 3):
        super().__init__()
        self.conv = nn.Sequential(
            nn.Conv2d(3, 24, 5, stride=2), nn.ReLU(),
            nn.Conv2d(24, 36, 5, stride=2), nn.ReLU(),
            nn.Conv2d(36, 48, 5, stride=2), nn.ReLU(),
            nn.Conv2d(48, 64, 3), nn.ReLU(),
            nn.Conv2d(64, 64, 3), nn.ReLU(),
        )
        self.flatten = nn.Flatten()

        # Compute flattened size: 64 * 1 * 18 = 1152 for 66x200 input
        self.fc = nn.Sequential(
            nn.Linear(1152, 100), nn.ReLU(),
            nn.Linear(100, 50), nn.ReLU(),
            nn.Linear(50, 10), nn.ReLU(),
            nn.Linear(10, output_dim),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        x = self.conv(x)
        x = self.flatten(x)
        x = self.fc(x)
        # steer: tanh → [-1, 1], throttle/brake: sigmoid → [0, 1]
        steer = torch.tanh(x[:, 0:1])
        throttle = torch.sigmoid(x[:, 1:2])
        brake = torch.sigmoid(x[:, 2:3])
        return torch.cat([steer, throttle, brake], dim=1)


# ==================== ResNet-18 + CIL ====================

class ResNet18CIL(nn.Module):
    """Conditional Imitation Learning with ResNet-18 backbone.
    Branches for each command: straight, left, right.

    Input:
        image: (B, 3, H, W)
        command: (B,)  long tensor {0: straight, 1: left, 2: right}
        speed: (B, 1)  current vehicle speed in m/s
    Output: (B, 3) [steer, throttle, brake]
    """

    def __init__(self, num_commands: int = 3, output_dim: int = 3,
                 pretrained: bool = True, command_emb_dim: int = 64,
                 speed_emb_dim: int = 64):
        super().__init__()
        self.num_commands = num_commands

        # Image backbone
        resnet = models.resnet18(weights="IMAGENET1K_V1" if pretrained else None)
        self.backbone = nn.Sequential(*list(resnet.children())[:-1])  # Remove fc
        self.feature_dim = 512

        # Command embedding
        self.command_embed = nn.Embedding(num_commands, command_emb_dim)

        # Speed embedding
        self.speed_embed = nn.Sequential(
            nn.Linear(1, speed_emb_dim),
            nn.ReLU(),
        )

        # Combined feature dimension
        combined_dim = self.feature_dim + command_emb_dim + speed_emb_dim

        # Command-conditional branches
        self.branches = nn.ModuleList([
            nn.Sequential(
                nn.Linear(combined_dim, 256),
                nn.ReLU(),
                nn.Dropout(0.3),
                nn.Linear(256, 128),
                nn.ReLU(),
                nn.Linear(128, output_dim),
            )
            for _ in range(num_commands)
        ])

    def forward(self, image: torch.Tensor, command: torch.Tensor,
                speed: torch.Tensor) -> torch.Tensor:
        # Image features
        img_features = self.backbone(image).flatten(1)  # (B, 512)

        # Command embedding
        cmd_emb = self.command_embed(command)  # (B, command_emb_dim)

        # Speed embedding
        speed_emb = self.speed_embed(speed)  # (B, speed_emb_dim)

        # Concatenate all features
        combined = torch.cat([img_features, cmd_emb, speed_emb], dim=1)  # (B, combined_dim)

        # Route through command-specific branch
        outputs = []
        for i, branch in enumerate(self.branches):
            branch_out = branch(combined)  # (B, output_dim)
            outputs.append(branch_out)

        # Stack branches: (B, num_commands, output_dim)
        stacked = torch.stack(outputs, dim=1)

        # Select branch based on command
        command_mask = F.one_hot(command, num_classes=self.num_commands).float()
        command_mask = command_mask.unsqueeze(-1)  # (B, num_commands, 1)

        output = (stacked * command_mask).sum(dim=1)  # (B, output_dim)

        # Activation
        steer = torch.tanh(output[:, 0:1])
        throttle = torch.sigmoid(output[:, 1:2])
        brake = torch.sigmoid(output[:, 2:3])
        return torch.cat([steer, throttle, brake], dim=1)


# ==================== ResNet-18 + Temporal Transformer ====================

class ResNet18Temporal(nn.Module):
    """ResNet-18 backbone with temporal Transformer for sequence processing.

    Input:
        images: (B, seq_len, 3, H, W)
        speed: (B, seq_len, 1)
    Output: (B, 3) [steer, throttle, brake]
    """

    def __init__(self, seq_len: int = 5, output_dim: int = 3,
                 pretrained: bool = True, num_layers: int = 2,
                 num_heads: int = 4, hidden_dim: int = 256):
        super().__init__()
        self.seq_len = seq_len

        # Image backbone (shared across time steps)
        resnet = models.resnet18(weights="IMAGENET1K_V1" if pretrained else None)
        self.backbone = nn.Sequential(*list(resnet.children())[:-1])
        self.feature_dim = 512

        # Project image features + speed to hidden dim
        self.input_proj = nn.Linear(self.feature_dim + 1, hidden_dim)

        # Positional encoding
        self.pos_encoding = nn.Parameter(torch.randn(1, seq_len, hidden_dim) * 0.02)

        # Transformer encoder
        encoder_layer = nn.TransformerEncoderLayer(
            d_model=hidden_dim,
            nhead=num_heads,
            dim_feedforward=hidden_dim * 4,
            dropout=0.1,
            batch_first=True,
        )
        self.transformer = nn.TransformerEncoder(encoder_layer, num_layers=num_layers)

        # Output head
        self.head = nn.Sequential(
            nn.Linear(hidden_dim, 128),
            nn.ReLU(),
            nn.Dropout(0.3),
            nn.Linear(128, 64),
            nn.ReLU(),
            nn.Linear(64, output_dim),
        )

    def forward(self, images: torch.Tensor, speed: torch.Tensor) -> torch.Tensor:
        B, T, C, H, W = images.shape

        # Reshape for backbone: (B*T, C, H, W)
        images_flat = images.view(B * T, C, H, W)
        img_features = self.backbone(images_flat).flatten(1)  # (B*T, 512)
        img_features = img_features.view(B, T, -1)  # (B, T, 512)

        # Concatenate speed: (B, T, 512+1)
        combined = torch.cat([img_features, speed], dim=-1)

        # Project to hidden dim
        combined = self.input_proj(combined)  # (B, T, hidden_dim)

        # Add positional encoding
        combined = combined + self.pos_encoding[:, :T, :]

        # Transformer
        encoded = self.transformer(combined)  # (B, T, hidden_dim)

        # Use last time step output
        last_output = encoded[:, -1, :]  # (B, hidden_dim)

        # Output head
        output = self.head(last_output)

        steer = torch.tanh(output[:, 0:1])
        throttle = torch.sigmoid(output[:, 1:2])
        brake = torch.sigmoid(output[:, 2:3])
        return torch.cat([steer, throttle, brake], dim=1)


# ==================== Factory ====================

def create_model(config) -> nn.Module:
    """Create a driving policy model based on config."""
    arch = config.model.architecture

    if arch == "pilotnet":
        return PilotNet(output_dim=config.model.output_dim)
    elif arch == "resnet18_cil":
        return ResNet18CIL(
            num_commands=config.model.num_commands,
            output_dim=config.model.output_dim,
            pretrained=config.model.pretrained,
            command_emb_dim=config.model.command_embedding_dim,
            speed_emb_dim=config.model.speed_embedding_dim,
        )
    elif arch == "resnet18_lstm":
        return ResNet18Temporal(
            seq_len=config.data.sequence_length,
            output_dim=config.model.output_dim,
            pretrained=config.model.pretrained,
            num_layers=config.model.num_transformer_layers,
            num_heads=config.model.num_attention_heads,
        )
    else:
        raise ValueError(f"Unknown architecture: {arch}")
