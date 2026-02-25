import argparse
import io
import json
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

import numpy as np


_DLL_DIR_HANDLES: list[object] = []


def _candidate_site_packages() -> list[Path]:
    candidates: list[Path] = []
    candidates.append(Path(sys.prefix) / "Lib" / "site-packages")
    candidates.append(Path(__file__).resolve().parent / ".venv" / "Lib" / "site-packages")

    seen: set[str] = set()
    unique: list[Path] = []
    for path in candidates:
        key = str(path.resolve(strict=False)).lower()
        if key in seen:
            continue
        seen.add(key)
        unique.append(path)
    return unique


def _bootstrap_windows_cuda_dll_dirs() -> None:
    if os.name != "nt" or not hasattr(os, "add_dll_directory"):
        return

    subdirs = (
        "nvidia/cuda_runtime/bin",
        "nvidia/cublas/bin",
        "nvidia/cudnn/bin",
        "nvidia/nvjitlink/bin",
        "nvidia/cufft/bin",
        "nvidia/curand/bin",
        "nvidia/cusolver/bin",
        "nvidia/cusparse/bin",
    )

    # WHY: Resolve CUDA DLLs from venv-local nvidia wheels before Windows fallback PATH probing.
    for site_packages in _candidate_site_packages():
        for rel in subdirs:
            dll_dir = site_packages / rel
            if not dll_dir.is_dir():
                continue
            try:
                _DLL_DIR_HANDLES.append(os.add_dll_directory(str(dll_dir)))
            except OSError:
                continue


_bootstrap_windows_cuda_dll_dirs()

import onnxruntime
import yaml
from PIL import Image


@dataclass(frozen=True)
class _ParseqSession:
    session: onnxruntime.InferenceSession
    input_name: str
    output_name: str
    input_width: int
    input_height: int


class NdlOcrLiteEngine:
    """
    Minimal NDLOCR-Lite runtime for app-side feasibility testing.
    Input: image bytes (PNG/JPEG/...).
    Output JSON: {"lines":[{"text","box":[x,y,w,h],"confidence",...}, ...]}.
    """

    def __init__(
        self,
        model_dir: str | None = None,
        config_dir: str | None = None,
        device: str = "cpu",
        det_score_threshold: float = 0.2,
        det_conf_threshold: float = 0.25,
        det_iou_threshold: float = 0.2,
        text_class_name: str = "line_main",
        max_workers: int = 2,
        enable_timing_log: bool = True,
    ) -> None:
        root = Path(__file__).resolve().parent
        self._model_dir = Path(model_dir) if model_dir else root / "model"
        self._config_dir = Path(config_dir) if config_dir else root / "config"
        self._text_class_name = text_class_name
        self._device = (device or "cpu").strip().lower()
        self._det_conf_threshold = float(det_conf_threshold)
        self._enable_timing_log = bool(enable_timing_log)
        self._max_workers = max(1, int(max_workers))

        det_path = self._model_dir / "deim-s-1024x1024.onnx"
        rec30_path = self._model_dir / "parseq-ndl-16x256-30-tiny-192epoch-tegaki3.onnx"
        rec50_path = self._model_dir / "parseq-ndl-16x384-50-tiny-146epoch-tegaki2.onnx"
        rec100_path = self._model_dir / "parseq-ndl-16x768-100-tiny-165epoch-tegaki2.onnx"
        ndl_yaml_path = self._config_dir / "ndl.yaml"
        chars_yaml_path = self._config_dir / "NDLmoji.yaml"

        for required in (det_path, rec30_path, rec50_path, rec100_path, ndl_yaml_path, chars_yaml_path):
            if not required.exists():
                raise FileNotFoundError(f"Required file not found: {required}")

        self._providers = self._resolve_providers(self._device)
        self._class_names = self._load_class_names(ndl_yaml_path)
        self._char_list = self._load_char_list(chars_yaml_path)
        self._detector = self._create_detector_session(det_path)
        self._detector_input_name = self._detector.get_inputs()[0].name
        self._detector_size_input_name = self._detector.get_inputs()[1].name
        self._detector_output_names = [output.name for output in self._detector.get_outputs()]
        det_shape = self._detector.get_inputs()[0].shape
        self._detector_input_h = int(det_shape[2])
        self._detector_input_w = int(det_shape[3])
        self._det_score_threshold = float(det_score_threshold)
        self._det_iou_threshold = float(det_iou_threshold)

        self._rec30 = self._create_parseq_session(rec30_path)
        self._rec50 = self._create_parseq_session(rec50_path)
        self._rec100 = self._create_parseq_session(rec100_path)

    def recognize(self, image_bytes: bytes) -> str:
        t0 = time.perf_counter()

        t_decode0 = time.perf_counter()
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        image_np = np.array(image)
        img_h, img_w = image_np.shape[:2]
        t_decode1 = time.perf_counter()

        t_detect0 = time.perf_counter()
        detections = self._detect(image_np)
        line_detections = self._select_text_lines(detections)
        t_detect1 = time.perf_counter()

        t_rec0 = time.perf_counter()
        lines: list[dict[str, Any]] = []
        valid_items: list[tuple[int, np.ndarray, dict[str, Any]]] = []
        for line_idx, det in enumerate(line_detections):
            left, top, width, height = self._to_ltrbwh_clamped(det["box"], img_w, img_h)
            if width <= 0 or height <= 0:
                continue

            crop = image_np[top : top + height, left : left + width, :]
            if crop.size == 0:
                continue

            valid_items.append(
                (
                    line_idx,
                    crop,
                    {
                        "left": left,
                        "top": top,
                        "width": width,
                        "height": height,
                        "det": det,
                    },
                )
            )

        rec_results: list[tuple[int, dict[str, Any]]] = []
        worker_count = 1
        # WHY: Keep parallelism minimal and CPU-only; CUDA EP often performs better with serialized run().
        if len(valid_items) > 1 and self._device == "cpu":
            worker_count = min(self._max_workers, len(valid_items))
            with ThreadPoolExecutor(max_workers=worker_count) as pool:
                futures = [pool.submit(self._recognize_line, item) for item in valid_items]
                for future in as_completed(futures):
                    result = future.result()
                    if result is None:
                        continue
                    rec_results.append(result)
        else:
            for item in valid_items:
                result = self._recognize_line(item)
                if result is None:
                    continue
                rec_results.append(result)

        for _, line in sorted(rec_results, key=lambda item: item[0]):
            lines.append(line)

        t_rec1 = time.perf_counter()
        t_end = time.perf_counter()

        self._log_timing_summary(
            img_w=img_w,
            img_h=img_h,
            line_input_count=len(line_detections),
            line_valid_count=len(valid_items),
            line_output_count=len(lines),
            workers=worker_count,
            decode_ms=(t_decode1 - t_decode0) * 1000.0,
            detect_and_select_ms=(t_detect1 - t_detect0) * 1000.0,
            recognize_ms=(t_rec1 - t_rec0) * 1000.0,
            total_ms=(t_end - t0) * 1000.0,
            lines=lines,
        )

        return json.dumps({"lines": lines}, ensure_ascii=False)

    def _recognize_line(
        self,
        item: tuple[int, np.ndarray, dict[str, Any]],
    ) -> tuple[int, dict[str, Any]] | None:
        line_idx, crop, context = item
        left = int(context["left"])
        top = int(context["top"])
        width = int(context["width"])
        height = int(context["height"])
        det = context["det"]

        pred_char_count = int(round(float(det.get("pred_char_count", 100.0))))
        text, rec_score = self._recognize_with_cascade(crop, pred_char_count)
        det_score = float(det.get("confidence", 0.0))
        # WHY: Keep one ranking score for downstream filtering while preserving both component scores.
        combined = self._normalize_score(det_score * rec_score)

        return (
            line_idx,
            {
                "id": line_idx,
                "text": text,
                "box": [float(left), float(top), float(width), float(height)],
                "confidence": combined,
                "detection_confidence": self._normalize_score(det_score),
                "recognition_confidence": self._normalize_score(rec_score),
                "class_name": det.get("class_name", ""),
            },
        )

    def _log_timing_summary(
        self,
        *,
        img_w: int,
        img_h: int,
        line_input_count: int,
        line_valid_count: int,
        line_output_count: int,
        workers: int,
        decode_ms: float,
        detect_and_select_ms: float,
        recognize_ms: float,
        total_ms: float,
        lines: list[dict[str, Any]],
    ) -> None:
        if not self._enable_timing_log:
            return
        confidence_values = [self._normalize_score(line.get("confidence", 0.0)) for line in lines]
        if confidence_values:
            confidence_avg = float(sum(confidence_values) / len(confidence_values))
            confidence_min = float(min(confidence_values))
        else:
            confidence_avg = 0.0
            confidence_min = 0.0
        print(
            "stage=ndl_ocr_timing event=summary "
            f"device={self._device} image={img_w}x{img_h} "
            f"linesIn={line_input_count} linesValid={line_valid_count} linesOut={line_output_count} "
            f"workers={workers} decodeMs={decode_ms:.2f} detectSelectMs={detect_and_select_ms:.2f} "
            f"recognizeMs={recognize_ms:.2f} totalMs={total_ms:.2f} "
            f"confidenceAvg={confidence_avg:.4f} confidenceMin={confidence_min:.4f}.",
            file=sys.stderr,
            flush=True,
        )

    @staticmethod
    def _normalize_score(value: Any) -> float:
        try:
            score = float(value)
        except (TypeError, ValueError):
            return 0.0
        if not np.isfinite(score):
            return 0.0
        return float(max(0.0, min(1.0, score)))

    def _detect(self, image_np: np.ndarray) -> list[dict[str, Any]]:
        input_tensor, padded_w, padded_h = self._preprocess_detector(image_np)
        outputs = self._detector.run(
            self._detector_output_names,
            {
                self._detector_input_name: input_tensor,
                self._detector_size_input_name: np.array([[self._detector_input_h, self._detector_input_w]], dtype=np.int64),
            },
        )
        return self._postprocess_detector(outputs, padded_w, padded_h)

    def _preprocess_detector(self, image_np: np.ndarray) -> tuple[np.ndarray, int, int]:
        max_wh = int(max(image_np.shape[0], image_np.shape[1]))
        padded = np.zeros((max_wh, max_wh, 3), dtype=np.uint8)
        padded[: image_np.shape[0], : image_np.shape[1], :] = image_np

        resized = np.array(Image.fromarray(padded).resize((self._detector_input_w, self._detector_input_h)))
        resized = resized.astype(np.float32) / 255.0
        mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
        std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
        normalized = (resized - mean) / std
        chw = normalized.transpose(2, 0, 1)
        tensor = chw[np.newaxis, :, :, :].astype(np.float32)
        return tensor, max_wh, max_wh

    def _postprocess_detector(
        self,
        outputs: list[np.ndarray],
        padded_w: int,
        padded_h: int,
    ) -> list[dict[str, Any]]:
        if len(outputs) == 4:
            class_ids, boxes, scores, char_counts = outputs
            char_counts = np.squeeze(char_counts)
        elif len(outputs) == 3:
            class_ids, boxes, scores = outputs
            char_counts = np.array([100.0] * np.squeeze(scores).shape[0], dtype=np.float32)
        else:
            return []

        class_ids = np.squeeze(class_ids)
        boxes = np.squeeze(boxes)
        scores = np.squeeze(scores)

        if boxes.ndim == 1:
            boxes = boxes.reshape(1, -1)
            scores = np.array([scores], dtype=np.float32)
            class_ids = np.array([class_ids], dtype=np.float32)
            char_counts = np.array([char_counts], dtype=np.float32)

        valid = scores > self._det_conf_threshold
        boxes = boxes[valid]
        scores = scores[valid]
        class_ids = class_ids[valid]
        char_counts = char_counts[valid]
        if boxes.size == 0:
            return []

        scales = np.array(
            [
                padded_w / float(self._detector_input_w),
                padded_h / float(self._detector_input_h),
                padded_w / float(self._detector_input_w),
                padded_h / float(self._detector_input_h),
            ],
            dtype=np.float32,
        )
        boxes = (boxes[:, :4] * scales).astype(np.int32)
        detections: list[dict[str, Any]] = []
        for bbox, score, class_id, pred_char_count in zip(boxes, scores, class_ids, char_counts):
            mapped = int(class_id) - 1
            if mapped < 0 or mapped >= len(self._class_names):
                continue
            detections.append(
                {
                    "class_index": mapped,
                    "class_name": self._class_names[mapped],
                    "confidence": float(score),
                    "pred_char_count": float(pred_char_count),
                    "box": [int(bbox[0]), int(bbox[1]), int(bbox[2]), int(bbox[3])],
                }
            )
        return detections

    def _select_text_lines(self, detections: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
        preferred = [d for d in detections if d.get("class_name") == self._text_class_name]
        if preferred:
            source = preferred
        else:
            source = [d for d in detections if str(d.get("class_name", "")).startswith("line_")]

        # WHY: Keep deterministic output order for tests and downstream diff checks.
        return sorted(source, key=lambda d: (d["box"][1], d["box"][0]))

    def _recognize_with_cascade(self, crop: np.ndarray, pred_char_count: int) -> tuple[str, float]:
        if pred_char_count == 3:
            text, score = self._parseq_read_with_score(self._rec30, crop)
            if len(text) >= 25:
                text, score = self._parseq_read_with_score(self._rec50, crop)
                if len(text) >= 45:
                    text, score = self._parseq_read_with_score(self._rec100, crop)
            return text, score

        if pred_char_count == 2:
            text, score = self._parseq_read_with_score(self._rec50, crop)
            if len(text) >= 45:
                text, score = self._parseq_read_with_score(self._rec100, crop)
            return text, score

        return self._parseq_read_with_score(self._rec100, crop)

    def _parseq_read_with_score(self, parseq: _ParseqSession, crop: np.ndarray) -> tuple[str, float]:
        tensor = self._preprocess_parseq(crop, parseq.input_width, parseq.input_height)
        logits = parseq.session.run([parseq.output_name], {parseq.input_name: tensor})[0]
        token_logits = logits[0]  # [T, C]
        token_ids = np.argmax(token_logits, axis=1).astype(np.int32)
        stop = np.where(token_ids == 0)[0]
        end = int(stop[0]) if stop.size > 0 else int(token_ids.shape[0])

        selected = token_ids[:end]
        text_chars: list[str] = []
        for token_id in selected:
            if token_id <= 0:
                continue
            index = token_id - 1
            if 0 <= index < len(self._char_list):
                text_chars.append(self._char_list[index])
        text = "".join(text_chars)

        if end <= 0:
            return text, 0.0

        step_logits = token_logits[:end]
        maxes = np.max(step_logits, axis=1, keepdims=True)
        probs = np.exp(step_logits - maxes)
        denom = np.sum(probs, axis=1, keepdims=True)
        denom[denom == 0.0] = 1.0
        probs = probs / denom
        token_scores = probs[np.arange(end), selected]
        token_scores = np.clip(token_scores.astype(np.float64), 1e-8, 1.0)
        rec_score = float(np.exp(np.mean(np.log(token_scores))))
        return text, rec_score

    @staticmethod
    def _preprocess_parseq(crop: np.ndarray, input_w: int, input_h: int) -> np.ndarray:
        image = Image.fromarray(crop)
        if image.height > image.width:
            image = image.transpose(Image.ROTATE_90)
        resized = np.array(image.resize((input_w, input_h)), dtype=np.float32)
        resized = resized[:, :, ::-1]
        normalized = 2.0 * ((resized / 255.0) - 0.5)
        chw = normalized.transpose(2, 0, 1)
        return chw[np.newaxis, :, :, :].astype(np.float32)

    @staticmethod
    def _to_ltrbwh_clamped(xyxy: list[int], img_w: int, img_h: int) -> tuple[int, int, int, int]:
        x1, y1, x2, y2 = [int(v) for v in xyxy]
        left = max(0, min(img_w, min(x1, x2)))
        top = max(0, min(img_h, min(y1, y2)))
        right = max(0, min(img_w, max(x1, x2)))
        bottom = max(0, min(img_h, max(y1, y2)))
        return left, top, max(0, right - left), max(0, bottom - top)

    def _create_detector_session(self, model_path: Path) -> onnxruntime.InferenceSession:
        options = onnxruntime.SessionOptions()
        options.graph_optimization_level = onnxruntime.GraphOptimizationLevel.ORT_ENABLE_ALL
        return onnxruntime.InferenceSession(str(model_path), options, providers=self._providers)

    def _create_parseq_session(self, model_path: Path) -> _ParseqSession:
        options = onnxruntime.SessionOptions()
        options.graph_optimization_level = onnxruntime.GraphOptimizationLevel.ORT_ENABLE_ALL
        if self._device == "cpu":
            options.intra_op_num_threads = 1
            options.inter_op_num_threads = 1
        session = onnxruntime.InferenceSession(str(model_path), options, providers=self._providers)
        inp = session.get_inputs()[0]
        out = session.get_outputs()[0]
        shape = inp.shape
        return _ParseqSession(
            session=session,
            input_name=inp.name,
            output_name=out.name,
            input_width=int(shape[3]),
            input_height=int(shape[2]),
        )

    @staticmethod
    def _load_class_names(yaml_path: Path) -> list[str]:
        with yaml_path.open("r", encoding="utf-8") as fh:
            node = yaml.safe_load(fh)
        names = node.get("names", {})
        if isinstance(names, dict):
            return [str(names[key]) for key in sorted(names.keys(), key=lambda x: int(x))]
        if isinstance(names, list):
            return [str(item) for item in names]
        raise ValueError(f"Invalid class mapping in {yaml_path}")

    @staticmethod
    def _load_char_list(yaml_path: Path) -> list[str]:
        with yaml_path.open("r", encoding="utf-8") as fh:
            node = yaml.safe_load(fh)
        model = node.get("model", {}) if isinstance(node, dict) else {}
        charset = model.get("charset_train", "")
        if not charset:
            raise ValueError(f"charset_train was not found in {yaml_path}")
        return list(charset)

    @staticmethod
    def _resolve_providers(device: str) -> list[str]:
        if device == "cuda":
            available = set(onnxruntime.get_available_providers())
            if "CUDAExecutionProvider" in available:
                return ["CUDAExecutionProvider", "CPUExecutionProvider"]
        return ["CPUExecutionProvider"]


def _main() -> int:
    parser = argparse.ArgumentParser(description="Minimal NDLOCR-Lite model runner (single image).")
    parser.add_argument("--image", required=True, help="Input image path.")
    parser.add_argument("--model-dir", default=None, help="Model directory (default: NDLOCR/model).")
    parser.add_argument("--config-dir", default=None, help="Config directory (default: NDLOCR/config).")
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--json-out", default=None, help="Optional output json path.")
    parser.add_argument("--pretty", action="store_true", help="Pretty-print JSON.")
    args = parser.parse_args()

    image_path = Path(args.image)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    engine = NdlOcrLiteEngine(
        model_dir=args.model_dir,
        config_dir=args.config_dir,
        device=args.device,
    )
    payload = engine.recognize(image_path.read_bytes())
    data = json.loads(payload)

    rendered = json.dumps(data, ensure_ascii=False, indent=2 if args.pretty else None)
    if args.json_out:
        out_path = Path(args.json_out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(rendered, encoding="utf-8")
        print(f"Wrote JSON: {out_path}")
    else:
        print(rendered)

    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
