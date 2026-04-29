"""
Safety filter that validates and potentially overrides model predictions.

The safety filter acts as a guardrail between the vision model's output
and the actual vehicle controls. It can:
1. Override model output when collision is imminent (using Phase 1 data)
2. Blend model output with Phase 1 output when confidence is low
3. Clip control values to physically safe ranges
"""

import numpy as np


class SafetyFilter:
    """Validates and filters driving commands for safety."""

    def __init__(self, config):
        self.config = config
        self.enabled = config.inference.safety_enabled
        self.max_steer_deviation = config.inference.max_steer_deviation
        self.max_speed_deviation = config.inference.max_speed_deviation

        # Smoothing state
        self._prev_steer = 0.0
        self._prev_throttle = 0.0
        self._prev_brake = 0.0

        # Intervention counter
        self.intervention_count = 0
        self.total_frames = 0

    def filter(
        self,
        model_cmd: np.ndarray,
        model_confidence: float,
        phase1_cmd: np.ndarray | None,
        collision_risk: int,
        vehicle_speed: float,
    ) -> np.ndarray:
        """Filter/override model command for safety.

        Args:
            model_cmd: [steer, throttle, brake] from vision model
            model_confidence: Confidence estimate [0, 1]
            phase1_cmd: [steer, throttle, brake] from rule-based Phase 1 (or None)
            collision_risk: 0=none, 1=low, 2=medium, 3=high, 4=imminent
            vehicle_speed: Current vehicle speed in m/s

        Returns:
            Filtered [steer, throttle, brake] command
        """
        self.total_frames += 1

        if not self.enabled:
            return model_cmd

        steer, throttle, brake = model_cmd

        # --- Rule 1: Emergency override ---
        if collision_risk >= 3:  # High or imminent
            self.intervention_count += 1
            # Full brake, keep current steering (or steer away if possible)
            return np.array([steer * 0.5, 0.0, 1.0], dtype=np.float32)

        # --- Rule 2: Low confidence → blend with Phase 1 ---
        if model_confidence < 0.5 and phase1_cmd is not None:
            self.intervention_count += 1
            alpha = model_confidence / 0.5  # Linear blend: [0, 1]
            steer = alpha * steer + (1 - alpha) * phase1_cmd[0]
            throttle = alpha * throttle + (1 - alpha) * phase1_cmd[1]
            brake = alpha * brake + (1 - alpha) * phase1_cmd[2]

        # --- Rule 3: Steer deviation check ---
        if phase1_cmd is not None:
            steer_diff = abs(steer - phase1_cmd[0])
            if steer_diff > self.max_steer_deviation:
                self.intervention_count += 1
                # Clamp steer to within max deviation of Phase 1
                steer = np.clip(steer,
                                phase1_cmd[0] - self.max_steer_deviation,
                                phase1_cmd[0] + self.max_steer_deviation)

        # --- Rule 4: Physical plausibility ---
        # Can't accelerate and brake simultaneously
        if throttle > 0.3 and brake > 0.3:
            # Trust the larger one
            if throttle > brake:
                brake = 0.0
            else:
                throttle = 0.0

        # At high speed, limit steering magnitude
        max_steer_at_speed = 1.0
        if vehicle_speed > 30.0:  # ~108 km/h
            max_steer_at_speed = 0.3
        elif vehicle_speed > 20.0:  # ~72 km/h
            max_steer_at_speed = 0.5
        elif vehicle_speed > 10.0:
            max_steer_at_speed = 0.7

        steer = np.clip(steer, -max_steer_at_speed, max_steer_at_speed)

        # --- Rule 5: Smooth transitions ---
        smooth_factor = 0.3
        steer = self._prev_steer + smooth_factor * (steer - self._prev_steer)
        throttle = self._prev_throttle + smooth_factor * (throttle - self._prev_throttle)
        brake = self._prev_brake + smooth_factor * (brake - self._prev_brake)

        self._prev_steer = steer
        self._prev_throttle = throttle
        self._prev_brake = brake

        return np.array([steer, throttle, brake], dtype=np.float32)

    @property
    def intervention_rate(self) -> float:
        """Fraction of frames where safety filter intervened."""
        if self.total_frames == 0:
            return 0.0
        return self.intervention_count / self.total_frames

    def reset_stats(self) -> None:
        """Reset intervention statistics."""
        self.intervention_count = 0
        self.total_frames = 0
