"""
Vision Perception Bridge — the core of Phase 2.

Converts visual perception outputs (YOLO detections, lane detection,
traffic light classification) into the SensorData-like format that
Phase 1's DecisionEngine can consume.

The bridge is the "glue" between camera-based perception and rule-based control.
"""

import math
from dataclasses import dataclass, field
from typing import Optional

import numpy as np


@dataclass
class VisionEntityInfo:
    """Detected entity from visual perception (mirrors C# EntityInfo)."""
    bbox: tuple[float, float, float, float]  # (x1, y1, x2, y2) in pixels
    class_name: str          # "car", "person", "traffic_light"
    confidence: float        # detection confidence [0, 1]
    estimated_distance: float  # estimated distance in meters
    estimated_angle: float     # estimated horizontal angle from camera center (-1..1)
    is_in_lane: bool           # whether entity is in ego lane
    is_oncoming: bool          # whether entity is approaching


@dataclass
class VisionLaneInfo:
    """Lane detection result from visual perception."""
    detected: bool
    left_fit: Optional[np.ndarray]   # polynomial coefficients (ax² + bx + c)
    right_fit: Optional[np.ndarray]
    lane_center_offset: float         # normalized offset from lane center [-1, 1]
    curvature: float                  # estimated curvature radius (meters)
    lane_width_pixels: float          # lane width at bottom of image
    confidence: float                 # lane detection confidence [0, 1]


@dataclass
class VisionPerceptionResult:
    """Complete visual perception output for one frame."""
    entities: list[VisionEntityInfo] = field(default_factory=list)
    lane: Optional[VisionLaneInfo] = None
    traffic_light: int = 0  # 0=none, 1=green, 2=yellow, 3=red
    traffic_light_confidence: float = 0.0
    timestamp_ms: int = 0


class VisionPerceptionBridge:
    """
    Converts raw visual detection outputs into structured perception data
    that mimics Phase 1's game-API-based SensorData.

    Key responsibilities:
    1. Bounding box → distance estimation (geometric method)
    2. Detection filtering (confidence threshold, region of interest)
    3. Entity classification (vehicle/pedestrian/traffic_light)
    4. Lane offset computation
    5. Serialization for TCP transport to C# mod
    """

    # Camera/intrinsic parameters (calibrated per setup)
    # These should be tuned for the specific GTA V third-person camera setup
    CAMERA_HEIGHT_M = 5.0          # Camera height above ground (approx)
    CAMERA_FOV_VERTICAL = 60.0     # Vertical FOV in degrees
    CAMERA_FOV_HORIZONTAL = 90.0   # Horizontal FOV in degrees
    IMAGE_WIDTH = 1280
    IMAGE_HEIGHT = 720

    # Known real-world widths for distance estimation
    CAR_WIDTH_M = 1.8              # Average car width in meters
    PEDESTRIAN_WIDTH_M = 0.5       # Average pedestrian width
    TRAFFIC_LIGHT_WIDTH_M = 0.3    # Traffic light bulb width

    def __init__(self, image_width: int = 1280, image_height: int = 720,
                 confidence_threshold: float = 0.4):
        self.IMAGE_WIDTH = image_width
        self.IMAGE_HEIGHT = image_height
        self.confidence_threshold = confidence_threshold

    def process(
        self,
        detections: list[dict],
        lane_result: Optional[dict],
        traffic_light: int,
        traffic_light_conf: float,
        timestamp_ms: int = 0,
    ) -> VisionPerceptionResult:
        """
        Process raw perception outputs into a structured result.

        Args:
            detections: List of YOLO detection dicts
            lane_result: Lane detection result dict from LaneDetector
            traffic_light: Traffic light state (0=none, 1=green, 2=yellow, 3=red)
            traffic_light_conf: Classification confidence
            timestamp_ms: Frame timestamp

        Returns:
            Structured VisionPerceptionResult
        """
        result = VisionPerceptionResult(
            traffic_light=traffic_light,
            traffic_light_confidence=traffic_light_conf,
            timestamp_ms=timestamp_ms,
        )

        # Process lane detection
        if lane_result and lane_result.get("detected"):
            result.lane = VisionLaneInfo(
                detected=True,
                left_fit=lane_result.get("left_fit"),
                right_fit=lane_result.get("right_fit"),
                lane_center_offset=lane_result.get("lane_center_offset", 0.0),
                curvature=lane_result.get("curvature", float("inf")),
                lane_width_pixels=lane_result.get("lane_width_pixels", 0.0),
                confidence=0.8,
            )

        # Process detections
        for det in detections:
            if det.get("confidence", 0) < self.confidence_threshold:
                continue

            entity = self._detection_to_entity(det)
            if entity:
                result.entities.append(entity)

        # Sort by distance (closest first)
        result.entities.sort(key=lambda e: e.estimated_distance)

        return result

    def _detection_to_entity(self, det: dict) -> Optional[VisionEntityInfo]:
        """Convert a single YOLO detection to VisionEntityInfo."""
        x1, y1, x2, y2 = det["bbox"]
        class_name = det.get("class_name", "")
        confidence = det.get("confidence", 0.0)

        # Estimate distance from bounding box
        distance = self._estimate_distance(x1, y1, x2, y2, class_name)

        # Estimate horizontal angle (normalized to [-1, 1])
        center_x = (x1 + x2) / 2.0
        angle = (center_x - self.IMAGE_WIDTH / 2.0) / (self.IMAGE_WIDTH / 2.0)

        # Check if in ego lane (center of image = ego lane)
        is_in_lane = abs(angle) < 0.25  # Within ~25% of image width from center

        return VisionEntityInfo(
            bbox=(x1, y1, x2, y2),
            class_name=class_name,
            confidence=confidence,
            estimated_distance=distance,
            estimated_angle=angle,
            is_in_lane=is_in_lane,
            is_oncoming=False,  # Requires frame-to-frame tracking
        )

    def _estimate_distance(
        self, x1: float, y1: float, x2: float, y2: float, class_name: str
    ) -> float:
        """
        Estimate distance to object using bounding box geometry.

        Method: known_width / bbox_width ≈ distance / focal_length
        distance = (known_width * focal_length) / bbox_width

        This is the pinhole camera model simplification.
        """
        bbox_width = x2 - x1
        bbox_bottom_y = y2  # Bottom of bbox (closer to ground = further away)

        if bbox_width < 2:
            return 50.0  # Too small to estimate, assume far

        # Get known real-world width
        known_width = self.CAR_WIDTH_M
        if class_name == "person":
            known_width = self.PEDESTRIAN_WIDTH_M
        elif class_name == "traffic_light":
            known_width = self.TRAFFIC_LIGHT_WIDTH_M

        # Focal length in pixels (approximation)
        focal_length = self.IMAGE_WIDTH / (
            2.0 * math.tan(math.radians(self.CAMERA_FOV_HORIZONTAL / 2.0))
        )

        # Distance from width
        if bbox_width > 0:
            distance = (known_width * focal_length) / bbox_width
        else:
            distance = 50.0

        # Heuristic correction: objects lower in the image are closer
        # (in a forward-facing camera, road surface is at the bottom)
        # This helps compensate for perspective distortion
        vertical_position_ratio = bbox_bottom_y / self.IMAGE_HEIGHT
        if vertical_position_ratio > 0.5:  # Bottom half → likely on road, closer
            distance *= 1.0 - (vertical_position_ratio - 0.5) * 0.5

        return max(1.0, min(100.0, distance))

    def to_sensor_data_dict(self, result: VisionPerceptionResult) -> dict:
        """
        Convert VisionPerceptionResult to a dict matching Phase 1's SensorData format.
        This is what gets serialized and sent to the C# mod.
        """
        entities = []
        for e in result.entities:
            entities.append({
                "distance": e.estimated_distance,
                "angle": e.estimated_angle,
                "is_vehicle": e.class_name in ("car", "motorcycle", "bus", "truck"),
                "is_pedestrian": e.class_name == "person",
                "is_in_forward_cone": e.is_in_lane,
                "is_oncoming": e.is_oncoming,
                "bbox": list(e.bbox),
                "confidence": e.confidence,
            })

        return {
            "entities": entities,
            "entity_count": len(entities),
            "traffic_light_state": result.traffic_light,
            "traffic_light_confidence": result.traffic_light_confidence,
            "lane_offset": result.lane.lane_center_offset if result.lane else 0.0,
            "lane_detected": result.lane.detected if result.lane else False,
            "lane_curvature": result.lane.curvature if result.lane else float("inf"),
            "timestamp_ms": result.timestamp_ms,
        }
