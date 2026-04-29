"""
Traditional CV-based lane detection using OpenCV.

Pipeline:
1. Convert to grayscale
2. Gaussian blur
3. Canny edge detection
4. Region of interest mask
5. Hough line transform
6. Lane line fitting (polynomial)
"""

import cv2
import numpy as np


class LaneDetector:
    """Detects lane lines from a front-facing camera image."""

    def __init__(self, config=None):
        self.canny_low = 50
        self.canny_high = 150
        self.hough_threshold = 50
        self.hough_min_line_length = 50
        self.hough_max_line_gap = 20

        if config:
            p = config.perception
            self.canny_low = p.canny_low
            self.canny_high = p.canny_high
            self.hough_threshold = p.hough_threshold
            self.hough_min_line_length = p.hough_min_line_length
            self.hough_max_line_gap = p.hough_max_line_gap

        # Lane history for temporal smoothing
        self._left_fit_history = []
        self._right_fit_history = []

    def detect(self, image: np.ndarray) -> dict:
        """Detect lane lines in the input image.

        Args:
            image: (H, W, 3) BGR image from GTA V

        Returns:
            dict with keys:
                left_fit: polynomial coefficients for left lane
                right_fit: polynomial coefficients for right lane
                lane_center_offset: offset from lane center in pixels
                curvature: estimated road curvature radius
                detected: whether lanes were successfully detected
        """
        h, w = image.shape[:2]
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)

        # Gaussian blur
        blurred = cv2.GaussianBlur(gray, (5, 5), 0)

        # Canny edge detection
        edges = cv2.Canny(blurred, self.canny_low, self.canny_high)

        # Region of interest (bottom half of image, trapezoid)
        mask = self._roi_mask(h, w)
        masked_edges = cv2.bitwise_and(edges, mask)

        # Hough line transform
        lines = cv2.HoughLinesP(
            masked_edges,
            rho=1,
            theta=np.pi / 180,
            threshold=self.hough_threshold,
            minLineLength=self.hough_min_line_length,
            maxLineGap=self.hough_max_line_gap,
        )

        if lines is None or len(lines) < 2:
            return {"detected": False, "lane_center_offset": 0.0}

        # Separate left and right lines
        left_lines, right_lines = self._separate_lines(lines, w)

        # Fit polynomial
        result = {"detected": False}

        if len(left_lines) > 0:
            left_fit = self._fit_polynomial(left_lines, h)
            self._left_fit_history.append(left_fit)
            if len(self._left_fit_history) > 5:
                self._left_fit_history.pop(0)
            result["left_fit"] = left_fit

        if len(right_lines) > 0:
            right_fit = self._fit_polynomial(right_lines, h)
            self._right_fit_history.append(right_fit)
            if len(self._right_fit_history) > 5:
                self._right_fit_history.pop(0)
            result["right_fit"] = right_fit

        if "left_fit" in result and "right_fit" in result:
            result["detected"] = True

            # Lane center offset
            y_eval = h - 1  # Bottom of image
            left_x = np.polyval(result["left_fit"], y_eval)
            right_x = np.polyval(result["right_fit"], y_eval)
            lane_center = (left_x + right_x) / 2.0
            image_center = w / 2.0
            result["lane_center_offset"] = (image_center - lane_center) / w  # Normalized

            # Curvature estimation
            result["curvature"] = self._estimate_curvature(
                result["left_fit"], result["right_fit"], y_eval
            )

        return result

    def _roi_mask(self, height: int, width: int) -> np.ndarray:
        """Create a trapezoidal region-of-interest mask."""
        mask = np.zeros((height, width), dtype=np.uint8)
        vertices = np.array([[
            (0, height),
            (0, int(height * 0.6)),
            (int(width * 0.4), int(height * 0.4)),  # Top left
            (int(width * 0.6), int(height * 0.4)),  # Top right
            (width, int(height * 0.6)),
            (width, height),
        ]], dtype=np.int32)
        cv2.fillPoly(mask, vertices, 255)
        return mask

    def _separate_lines(self, lines: np.ndarray, image_width: int
                        ) -> tuple[list, list]:
        """Separate Hough lines into left and right lane lines."""
        left_lines = []
        right_lines = []
        mid = image_width / 2

        for line in lines:
            x1, y1, x2, y2 = line[0]
            if x1 == x2:
                continue  # Vertical line, skip

            slope = (y2 - y1) / (x2 - x1)

            # Filter near-horizontal lines
            if abs(slope) < 0.3:
                continue

            if slope < 0 and x1 < mid and x2 < mid:
                left_lines.append(line[0])
            elif slope > 0 and x1 > mid and x2 > mid:
                right_lines.append(line[0])

        return left_lines, right_lines

    def _fit_polynomial(self, lines: list, image_height: int) -> np.ndarray:
        """Fit a 2nd-degree polynomial to lane line points."""
        points = []
        for x1, y1, x2, y2 in lines:
            points.append([x1, y1])
            points.append([x2, y2])

        points = np.array(points)
        if len(points) < 3:
            return np.array([0, 0, image_height / 2])

        # Fit polynomial: x = a*y² + b*y + c
        fit = np.polyfit(points[:, 1], points[:, 0], 2)
        return fit

    def _estimate_curvature(self, left_fit: np.ndarray, right_fit: np.ndarray,
                            y_eval: int) -> float:
        """Estimate road curvature radius in meters (approximate)."""
        # Conversion: pixels to meters (approximate for GTA V)
        ym_per_pix = 30 / 720  # meters per pixel in y
        xm_per_pix = 3.7 / 700  # meters per pixel in x

        # Refit in world space
        left_fit_m = np.polyfit(
            np.arange(len(left_fit)) * ym_per_pix,
            left_fit * xm_per_pix, 2
        )

        # Curvature radius: R = (1 + (2Ay + B)²)^(3/2) / |2A|
        a = left_fit_m[0]
        b = left_fit_m[1]
        y = y_eval * ym_per_pix

        if abs(a) < 1e-6:
            return float("inf")

        curvature = ((1 + (2 * a * y + b) ** 2) ** 1.5) / abs(2 * a)
        return curvature

    def draw_lanes(self, image: np.ndarray, result: dict) -> np.ndarray:
        """Draw detected lanes on the image for visualization."""
        vis = image.copy()
        h, w = vis.shape[:2]
        y_pts = np.linspace(h * 0.4, h - 1, num=20)

        if result.get("detected"):
            left_x = np.polyval(result["left_fit"], y_pts).astype(int)
            right_x = np.polyval(result["right_fit"], y_pts).astype(int)

            for i in range(len(y_pts) - 1):
                y1, y2 = int(y_pts[i]), int(y_pts[i + 1])
                cv2.line(vis, (left_x[i], y1), (left_x[i + 1], y2), (0, 255, 0), 3)
                cv2.line(vis, (right_x[i], y1), (right_x[i + 1], y2), (0, 255, 0), 3)

            # Fill lane area
            pts_left = np.column_stack([left_x, y_pts.astype(int)])
            pts_right = np.column_stack([right_x[::-1], y_pts[::-1].astype(int)])
            pts = np.vstack([pts_left, pts_right])
            overlay = vis.copy()
            cv2.fillPoly(overlay, [pts], (0, 255, 0))
            vis = cv2.addWeighted(vis, 0.7, overlay, 0.3, 0)

        return vis
