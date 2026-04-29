"""Central configuration for Phase 2 vision-based driving."""

from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional


@dataclass
class TelemetryConfig:
    """TCP telemetry receiver settings."""
    host: str = "127.0.0.1"
    port: int = 21555
    buffer_size: int = 4096


@dataclass
class DataConfig:
    """Data recording and dataset settings."""
    raw_dir: Path = Path("data/raw")
    processed_dir: Path = Path("data/processed")
    image_width: int = 200
    image_height: int = 66
    sequence_length: int = 5  # frames per training sample
    target_fps: int = 20
    # Data splitting
    train_ratio: float = 0.8
    val_ratio: float = 0.1
    # test_ratio = 1 - train_ratio - val_ratio = 0.1


@dataclass
class ModelConfig:
    """Model architecture settings."""
    architecture: str = "resnet18_cil"  # "pilotnet", "resnet18_cil", "resnet18_lstm"
    pretrained: bool = True
    num_commands: int = 3  # straight, left, right
    command_embedding_dim: int = 64
    speed_embedding_dim: int = 64
    # Temporal settings
    use_temporal: bool = True
    temporal_backbone: str = "transformer"  # "transformer", "lstm"
    num_transformer_layers: int = 2
    num_attention_heads: int = 4
    # Output
    output_dim: int = 3  # steer, throttle, brake


@dataclass
class TrainingConfig:
    """Training hyperparameters."""
    batch_size: int = 32
    learning_rate: float = 1e-4
    weight_decay: float = 1e-4
    num_epochs: int = 100
    early_stopping_patience: int = 15
    gradient_clip_norm: float = 1.0
    use_amp: bool = True  # Automatic Mixed Precision
    num_workers: int = 4
    # Scheduler
    scheduler: str = "cosine"  # "cosine", "step", "plateau"
    cosine_t0: int = 10
    # Loss weights
    steer_loss_weight: float = 1.0
    throttle_loss_weight: float = 0.5
    brake_loss_weight: float = 0.5


@dataclass
class InferenceConfig:
    """Real-time inference settings."""
    model_path: Optional[Path] = None
    device: str = "cuda"  # "cuda" or "cpu"
    inference_fps: int = 20
    use_tensorrt: bool = False
    # Safety filter
    safety_enabled: bool = True
    max_steer_deviation: float = 0.3  # Max deviation from Phase 1 before blending
    max_speed_deviation: float = 10.0  # m/s


@dataclass
class PerceptionConfig:
    """Perception pipeline settings."""
    # Lane detection
    canny_low: int = 50
    canny_high: int = 150
    hough_threshold: int = 50
    hough_min_line_length: int = 50
    hough_max_line_gap: int = 20
    # Object detection
    yolo_model: str = "yolov8n.pt"  # or path to fine-tuned model
    object_detection_confidence: float = 0.5
    # Traffic light classification
    traffic_light_input_size: int = 32


@dataclass
class GTAConfig:
    """Global config aggregating all sub-configs."""
    telemetry: TelemetryConfig = field(default_factory=TelemetryConfig)
    data: DataConfig = field(default_factory=DataConfig)
    model: ModelConfig = field(default_factory=ModelConfig)
    training: TrainingConfig = field(default_factory=TrainingConfig)
    inference: InferenceConfig = field(default_factory=InferenceConfig)
    perception: PerceptionConfig = field(default_factory=PerceptionConfig)

    @classmethod
    def from_yaml(cls, path: str) -> "GTAConfig":
        """Load config from YAML file."""
        import yaml
        with open(path, "r") as f:
            data = yaml.safe_load(f)
        return cls(**data)

    def to_yaml(self, path: str) -> None:
        """Save config to YAML file."""
        import yaml
        with open(path, "w") as f:
            yaml.dump(self.__dict__, f, default_flow_style=False)


# Default config instance
default_config = GTAConfig()
