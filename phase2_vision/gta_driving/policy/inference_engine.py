"""
Real-time inference engine for driving policy models.

Runs the trained model asynchronously, processing frames from
the GTA V capture pipeline and producing control commands.
"""

import queue
import threading
import time
from collections import deque
from pathlib import Path
from typing import Optional

import cv2
import numpy as np
import torch
import torch.nn as nn

from .models import create_model


class InferenceEngine:
    """Asynchronous inference loop for real-time driving."""

    def __init__(self, config, model_path: str):
        self.config = config
        self.device = torch.device(config.inference.device if torch.cuda.is_available() else "cpu")

        # Load model
        self.model = create_model(config).to(self.device)
        checkpoint = torch.load(model_path, map_location=self.device)
        self.model.load_state_dict(checkpoint["model_state_dict"])
        self.model.eval()

        # For TensorRT: would export to ONNX and build engine here
        self.use_tensorrt = config.inference.use_tensorrt

        # Frame buffer (ring buffer for temporal models)
        self.seq_len = config.data.sequence_length
        self.frame_buffer = deque(maxlen=self.seq_len)
        self.speed_buffer = deque(maxlen=self.seq_len)

        # Input/output queues
        self.input_queue = queue.Queue(maxsize=10)
        self.output_queue = queue.Queue(maxsize=10)

        # Inference thread
        self._running = False
        self._thread: Optional[threading.Thread] = None

        # Latest prediction
        self.latest_command: Optional[np.ndarray] = None
        self.latest_confidence: float = 0.0

        # Performance stats
        self.inference_times = deque(maxlen=100)

        print(f"[Inference] Model loaded on {self.device}")

    def start(self) -> None:
        """Start the async inference loop."""
        self._running = True
        self._thread = threading.Thread(target=self._inference_loop, daemon=True)
        self._thread.start()
        print("[Inference] Started")

    def stop(self) -> None:
        """Stop the inference loop."""
        self._running = False
        if self._thread:
            self._thread.join(timeout=3.0)
        print("[Inference] Stopped")

    def feed(self, image: np.ndarray, speed: float) -> None:
        """Feed a new frame to the inference engine.
        Called from the frame capture thread.
        """
        try:
            self.input_queue.put_nowait((image, speed))
        except queue.Full:
            # Drop frame if inference is falling behind
            pass

    def get_command(self) -> Optional[tuple[np.ndarray, float]]:
        """Get the latest control command.
        Returns (command_array, confidence) or None if no prediction yet.
        """
        try:
            cmd = self.output_queue.get_nowait()
            self.latest_command = cmd[0]
            self.latest_confidence = cmd[1]
            return cmd
        except queue.Empty:
            if self.latest_command is not None:
                return (self.latest_command, self.latest_confidence)
            return None

    @property
    def avg_inference_time_ms(self) -> float:
        """Average inference time in milliseconds."""
        if not self.inference_times:
            return 0.0
        return np.mean(self.inference_times) * 1000

    def _inference_loop(self) -> None:
        """Main inference loop running in a background thread."""
        while self._running:
            try:
                image, speed = self.input_queue.get(timeout=0.1)
            except queue.Empty:
                continue

            # Preprocess
            image_tensor = self._preprocess_image(image).to(self.device)
            speed_tensor = torch.tensor([[speed]], dtype=torch.float32).to(self.device)

            # Add to buffers
            self.frame_buffer.append(image_tensor)
            self.speed_buffer.append(speed_tensor.squeeze(0))

            # Need enough frames for a sequence
            if len(self.frame_buffer) < self.seq_len:
                continue

            # Run inference
            t_start = time.time()

            with torch.no_grad():
                with torch.cuda.amp.autocast(enabled=self.config.training.use_amp):
                    if self.config.model.architecture == "resnet18_cil":
                        # CIL model needs command (default to straight for inference)
                        batch_images = torch.stack(list(self.frame_buffer)).unsqueeze(0)  # (1,T,C,H,W)
                        batch_speed = torch.stack(list(self.speed_buffer)).unsqueeze(0)  # (1,T,1)
                        command = torch.tensor([0], dtype=torch.long).to(self.device)
                        prediction = self.model(batch_images[:, -1], command, batch_speed[:, -1])
                    else:
                        batch_images = torch.stack(list(self.frame_buffer)).unsqueeze(0)
                        batch_speed = torch.stack(list(self.speed_buffer)).unsqueeze(0)
                        prediction = self.model(batch_images, batch_speed)

            t_end = time.time()
            self.inference_times.append(t_end - t_start)

            # Convert to numpy
            cmd = prediction.squeeze(0).cpu().numpy()  # [steer, throttle, brake]

            # Compute confidence (inverse of prediction variance over recent frames)
            confidence = self._estimate_confidence(cmd)

            try:
                self.output_queue.put_nowait((cmd, confidence))
            except queue.Full:
                pass

    def _preprocess_image(self, image: np.ndarray) -> torch.Tensor:
        """Preprocess raw image for model input."""
        # Resize to model input size
        # image: (H, W, 3) in BGR (OpenCV) or RGB format
        h, w = self.config.data.image_height, self.config.data.image_width
        resized = cv2.resize(image, (w, h))
        # Convert to CHW, normalize to [0, 1]
        tensor = torch.from_numpy(resized).permute(2, 0, 1).float() / 255.0
        return tensor

    def _estimate_confidence(self, current_cmd: np.ndarray) -> float:
        """Estimate prediction confidence. Simplified: uses output magnitude sanity check."""
        steer, throttle, brake = current_cmd

        # Basic sanity: control values should be in valid range
        if abs(steer) > 1.0 or throttle < 0 or throttle > 1.0 or brake < 0 or brake > 1.0:
            return 0.0

        # Confidence decreases if throttle+brake are both high (contradictory)
        if throttle > 0.5 and brake > 0.5:
            return 0.3

        return 0.8  # Default moderate confidence for plausible outputs
