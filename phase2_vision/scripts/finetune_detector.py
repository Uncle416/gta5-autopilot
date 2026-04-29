#!/usr/bin/env python3
"""
Fine-tune YOLOv8 on GTA V screenshots using Phase 1 auto-labels.

Uses the dataset collected by collect_dataset.py, where Phase 1's
EntityDetector outputs serve as ground truth bounding box labels.

Usage:
    python scripts/finetune_detector.py \
        --data data/raw/session_01 \
        --model yolov8n.pt \
        --epochs 50 \
        --output experiments/detector.pt
"""

import argparse
import json
from pathlib import Path


def convert_to_yolo_format(annotations_file: str, images_dir: str, output_dir: str):
    """
    Convert JSONL annotations to YOLO format.

    YOLO format: one .txt file per image, each line:
        class_id cx cy w h  (all normalized to [0, 1])

    class_id mapping: 0=car, 1=person, 2=traffic_light
    """
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    labels_dir = output_dir / "labels"
    labels_dir.mkdir(exist_ok=True)
    images_output_dir = output_dir / "images"
    images_output_dir.mkdir(exist_ok=True)

    import shutil

    class_map = {"car": 0, "motorcycle": 0, "bus": 0, "truck": 0,
                 "person": 1, "traffic_light": 2}

    converted = 0
    with open(annotations_file, "r") as f:
        for line in f:
            ann = json.loads(line)
            if "error" in ann:
                continue

            frame_id = ann["frame_id"]
            screenshot_name = ann.get("screenshot", f"frame_{frame_id:06d}.png")
            img_path = Path(images_dir) / screenshot_name

            if not img_path.exists():
                continue

            # Copy image to output
            shutil.copy(img_path, images_output_dir / screenshot_name)

            # Get image dimensions (assume 1280x720, read actual for accuracy)
            try:
                from PIL import Image
                with Image.open(img_path) as img:
                    img_w, img_h = img.size
            except Exception:
                img_w, img_h = 1280, 720

            # Create label file
            label_name = Path(screenshot_name).stem + ".txt"
            label_path = labels_dir / label_name

            with open(label_path, "w") as lf:
                for entity in ann.get("entities", []):
                    class_name = "car" if entity.get("is_vehicle") else "person"
                    class_id = class_map.get(class_name, 0)

                    # Phase 1 gives distance + angle, not bbox.
                    # Convert to approximate bbox using heuristics:
                    # - distance → bbox height (closer = bigger)
                    # - angle → bbox center x
                    # - assume standard aspect ratio
                    distance = entity["distance"]
                    angle = entity.get("angle", 0)

                    if distance < 1 or distance > 80:
                        continue

                    # bbox height: inversely proportional to distance
                    bbox_h = min(0.8, max(0.03, 200.0 / (distance * img_h / 720.0)))
                    # bbox width: proportional to height (typical vehicle aspect ratio)
                    bbox_w = bbox_h * 1.5 if class_name == "car" else bbox_h * 0.5

                    # bbox center x from angle
                    cx = 0.5 + angle * 0.5
                    # bbox center y: middle of image for distant, bottom for close
                    cy = 0.4 + 0.3 * (distance / 50.0)

                    # Clamp to [0, 1]
                    cx = max(0.0, min(1.0, cx))
                    cy = max(0.0, min(1.0, cy))

                    lf.write(f"{class_id} {cx:.6f} {cy:.6f} {bbox_w:.6f} {bbox_h:.6f}\n")

            converted += 1

    print(f"[Format] Converted {converted} images to YOLO format in {output_dir}")
    return converted


def create_yaml_config(output_dir: str, yaml_path: str):
    """Create YOLO data config YAML."""
    import yaml

    output_dir = Path(output_dir).absolute()
    config = {
        "path": str(output_dir),
        "train": "images",
        "val": "images",  # Same for now, ideally split
        "names": {0: "vehicle", 1: "person", 2: "traffic_light"},
    }

    with open(yaml_path, "w") as f:
        yaml.dump(config, f)
    print(f"[Config] Wrote {yaml_path}")


def main():
    parser = argparse.ArgumentParser(description="Fine-tune YOLOv8 on GTA V data")
    parser.add_argument("--data", required=True, help="Path to collected dataset directory")
    parser.add_argument("--model", default="yolov8n.pt", help="Base YOLO model")
    parser.add_argument("--epochs", type=int, default=50, help="Training epochs")
    parser.add_argument("--batch", type=int, default=16, help="Batch size")
    parser.add_argument("--imgsz", type=int, default=640, help="Image size")
    parser.add_argument("--output", default="experiments/detector.pt", help="Output model path")
    parser.add_argument("--device", default="cuda", help="Device (cuda/cpu)")
    args = parser.parse_args()

    data_dir = Path(args.data)
    annotations_file = data_dir / "annotations.jsonl"
    screenshots_dir = data_dir / "screenshots"

    if not annotations_file.exists():
        print(f"Error: annotations.jsonl not found in {args.data}")
        print("Run collect_dataset.py first.")
        return

    # Convert to YOLO format
    yolo_dir = data_dir / "yolo_format"
    count = convert_to_yolo_format(str(annotations_file), str(screenshots_dir), str(yolo_dir))

    if count == 0:
        print("Error: No valid images found. Check your dataset.")
        return

    # Create config
    yaml_path = yolo_dir / "data.yaml"
    create_yaml_config(str(yolo_dir), str(yaml_path))

    # Train with YOLO
    try:
        from ultralytics import YOLO
    except ImportError:
        print("Error: ultralytics not installed. Run: pip install ultralytics")
        return

    print(f"\n[Training] Fine-tuning {args.model} on {count} images...")
    model = YOLO(args.model)
    results = model.train(
        data=str(yaml_path),
        epochs=args.epochs,
        batch=args.batch,
        imgsz=args.imgsz,
        device=args.device,
        project="experiments",
        name="detector_finetune",
        exist_ok=True,
    )

    # Save best model
    import shutil
    best_path = Path("experiments/detector_finetune/weights/best.pt")
    if best_path.exists():
        shutil.copy(best_path, args.output)
        print(f"[Done] Best model saved to {args.output}")

    print(f"[Done] Results: {results}")


if __name__ == "__main__":
    main()
