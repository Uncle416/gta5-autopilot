"""
Object detection for vehicles and pedestrians using YOLO.

Uses a pre-trained YOLOv8 model that can be fine-tuned on GTA V screenshots
for better domain adaptation.

Dependencies: ultralytics (pip install ultralytics)
"""

from typing import Optional

import numpy as np

# YOLO is optional import — install with: pip install ultralytics
try:
    from ultralytics import YOLO
    HAS_YOLO = True
except ImportError:
    HAS_YOLO = False


class ObjectDetector:
    """Detects vehicles and pedestrians in GTA V frames."""

    # COCO class IDs relevant to driving
    CAR_CLASSES = {2, 3, 5, 7}  # car, motorcycle, bus, truck
    PERSON_CLASSES = {0}  # person
    TRAFFIC_LIGHT_CLASSES = {9}  # traffic light

    def __init__(self, model_path: str = "yolov8n.pt", confidence: float = 0.5,
                 device: str = "cuda"):
        self.confidence = confidence
        self.device = device

        if HAS_YOLO:
            self.model = YOLO(model_path)
            self.model.to(device)
        else:
            self.model = None
            print("[ObjectDetector] YOLO not installed. "
                  "Install with: pip install ultralytics")

    def detect(self, image: np.ndarray) -> list[dict]:
        """Detect objects in the input image.

        Args:
            image: (H, W, 3) BGR image

        Returns:
            List of dicts with keys:
                bbox: [x1, y1, x2, y2] in pixel coordinates
                class_id: integer class ID
                class_name: string label
                confidence: detection confidence [0, 1]
                center: (cx, cy) center point of the bbox
        """
        if self.model is None:
            return []

        results = self.model(image, verbose=False)
        detections = []

        for result in results:
            if result.boxes is None:
                continue

            boxes = result.boxes.xyxy.cpu().numpy()
            classes = result.boxes.cls.cpu().numpy().astype(int)
            confs = result.boxes.conf.cpu().numpy()

            for box, cls, conf in zip(boxes, classes, confs):
                if conf < self.confidence:
                    continue

                # Only keep relevant classes
                if cls not in (self.CAR_CLASSES | self.PERSON_CLASSES | self.TRAFFIC_LIGHT_CLASSES):
                    continue

                x1, y1, x2, y2 = box
                class_name = self._class_name(cls)

                detections.append({
                    "bbox": [float(x1), float(y1), float(x2), float(y2)],
                    "class_id": cls,
                    "class_name": class_name,
                    "confidence": float(conf),
                    "center": ((x1 + x2) / 2, (y1 + y2) / 2),
                })

        return detections

    def detect_cars(self, image: np.ndarray) -> list[dict]:
        """Convenience: detect only vehicles."""
        return [d for d in self.detect(image) if d["class_id"] in self.CAR_CLASSES]

    def detect_pedestrians(self, image: np.ndarray) -> list[dict]:
        """Convenience: detect only pedestrians."""
        return [d for d in self.detect(image) if d["class_id"] in self.PERSON_CLASSES]

    def detect_traffic_lights(self, image: np.ndarray) -> list[dict]:
        """Convenience: detect only traffic lights."""
        return [d for d in self.detect(image) if d["class_id"] in self.TRAFFIC_LIGHT_CLASSES]

    @staticmethod
    def _class_name(class_id: int) -> str:
        """Map COCO class ID to name."""
        names = {
            0: "person", 1: "bicycle", 2: "car", 3: "motorcycle",
            5: "bus", 7: "truck", 9: "traffic_light",
        }
        return names.get(class_id, f"class_{class_id}")

    def draw_detections(self, image: np.ndarray, detections: list[dict]) -> np.ndarray:
        """Draw detection bounding boxes on the image."""
        import cv2

        vis = image.copy()
        color_map = {
            "car": (0, 255, 0),       # Green
            "motorcycle": (0, 200, 0),
            "bus": (255, 255, 0),      # Cyan
            "truck": (255, 200, 0),
            "person": (0, 0, 255),     # Red
            "traffic_light": (255, 0, 255),  # Magenta
        }

        for det in detections:
            x1, y1, x2, y2 = [int(v) for v in det["bbox"]]
            color = color_map.get(det["class_name"], (128, 128, 128))

            cv2.rectangle(vis, (x1, y1), (x2, y2), color, 2)
            label = f"{det['class_name']} {det['confidence']:.2f}"
            cv2.putText(vis, label, (x1, y1 - 5),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.4, color, 1)

        return vis
