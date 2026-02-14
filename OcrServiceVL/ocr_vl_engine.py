import io
import json
import logging
import os
import re
import sys
import tempfile
from collections import Counter
from pathlib import Path
from threading import Lock
from typing import Any

from PIL import Image

_DLL_DIR_HANDLES = []
_LOGGER = logging.getLogger(__name__)

# WHY: layout detection can return non-text block payloads; keep OCR lines text-focused.
_NON_TEXT_LABEL_HINTS = ("image", "img", "table", "formula", "chart", "figure")
_TEXT_LABEL_HINTS = ("text", "title", "paragraph", "caption", "list", "ocr")
_MARKUP_OR_ASSET_PATTERNS = (
    re.compile(r"<\s*/?\s*(img|div|table|figure|span|p)\b", re.IGNORECASE),
    re.compile(r"!\[[^\]]*]\([^)]+\)"),
    re.compile(r"\bimg_in_image_box[_\w-]*", re.IGNORECASE),
    re.compile(r"\bsrc\s*=\s*['\"]", re.IGNORECASE),
)


def _candidate_site_packages() -> list[Path]:
    candidates: list[Path] = []
    candidates.append(Path(sys.prefix) / "Lib" / "site-packages")
    candidates.append(Path(__file__).resolve().parent / ".venv" / "Lib" / "site-packages")

    seen = set()
    unique: list[Path] = []
    for path in candidates:
        resolved = str(path.resolve(strict=False)).lower()
        if resolved not in seen:
            seen.add(resolved)
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

    # WHY: make CUDA DLL lookup deterministic to the active venv, not global PATH.
    for site_packages in _candidate_site_packages():
        for rel in subdirs:
            dll_dir = site_packages / rel
            if dll_dir.is_dir():
                try:
                    _DLL_DIR_HANDLES.append(os.add_dll_directory(str(dll_dir)))
                except OSError:
                    continue


def _to_optional_bool(value: bool | None) -> bool | None:
    if value is None:
        return None
    return bool(value)


class PaddleOcrVlEngine:
    def __init__(
        self,
        device: str = "gpu:0",
        pipeline_version: str = "v1.5",
        max_pixels: int | None = None,
        layout_threshold: float | None = None,
        max_new_tokens: int | None = None,
        merge_layout_blocks: bool | None = None,
        use_ocr_for_image_block: bool | None = None,
        use_layout_detection: bool | None = None,
        enable_hpi: bool | None = None,
        use_tensorrt: bool | None = None,
        precision: str | None = None,
    ):
        # WHY: Skip external model source probing to reduce startup failures/latency in locked-down networks.
        os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
        _bootstrap_windows_cuda_dll_dirs()
        from paddleocr import PaddleOCRVL

        init_kwargs: dict[str, Any] = {
            "pipeline_version": (pipeline_version or "v1.5").strip() or "v1.5",
            "device": (device or "gpu:0").strip() or "gpu:0",
            "format_block_content": True,
            "use_queues": False,
            "enable_hpi": _to_optional_bool(enable_hpi),
            "use_tensorrt": _to_optional_bool(use_tensorrt),
            "precision": precision,
        }
        init_kwargs = {k: v for k, v in init_kwargs.items() if v is not None}
        self._engine = PaddleOCRVL(**init_kwargs)

        self._predict_kwargs: dict[str, Any] = {
            "merge_layout_blocks": _to_optional_bool(merge_layout_blocks),
            "use_ocr_for_image_block": _to_optional_bool(use_ocr_for_image_block),
            "use_layout_detection": _to_optional_bool(use_layout_detection),
            "max_new_tokens": max_new_tokens,
            "max_pixels": max_pixels,
            "layout_threshold": layout_threshold,
        }
        self._predict_kwargs = {k: v for k, v in self._predict_kwargs.items() if v is not None}
        self._lock = Lock()

    def close(self) -> None:
        try:
            self._engine.close()
        except Exception:
            pass

    def recognize(self, image_bytes: bytes) -> str:
        with self._lock:
            lines = self._recognize_locked(image_bytes)
        return json.dumps({"lines": lines}, ensure_ascii=False)

    def _recognize_locked(self, image_bytes: bytes) -> list[dict[str, Any]]:
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        with tempfile.NamedTemporaryFile(prefix="ocr_vl_", suffix=".png", delete=False) as tmp:
            temp_path = tmp.name
            image.save(tmp, format="PNG")

        try:
            pages = list(self._engine.predict_iter(temp_path, **self._predict_kwargs))
        finally:
            try:
                os.remove(temp_path)
            except OSError:
                pass

        lines: list[dict[str, Any]] = []
        for page in pages:
            lines.extend(self._extract_lines(page))
        return lines

    def _extract_lines(self, page: Any) -> list[dict[str, Any]]:
        for candidate in self._iter_candidate_dicts(page):
            parsed = self._parse_page_dict(candidate)
            if parsed:
                return parsed

        markdown = getattr(page, "markdown", None)
        if isinstance(markdown, dict):
            text = self._sanitize_block_text(markdown.get("markdown_texts"))
            if text and not self._looks_like_markup_or_asset_ref(text):
                return [
                    {
                        "text": text,
                        "box": [0.0, 0.0, 1.0, 1.0],
                        "confidence": 1.0,
                    }
                ]

        return []

    @staticmethod
    def _iter_candidate_dicts(page: Any) -> list[dict[str, Any]]:
        candidates: list[dict[str, Any]] = []
        if isinstance(page, dict):
            candidates.append(page)
        for attr in ("res", "json", "data"):
            value = getattr(page, attr, None)
            if isinstance(value, dict):
                candidates.append(value)
        return candidates

    @staticmethod
    def _normalize_label(value: Any) -> str:
        if value is None:
            return ""
        return str(value).strip().lower()

    @classmethod
    def _is_text_like_block(cls, label: Any) -> bool:
        normalized = cls._normalize_label(label)
        if not normalized:
            return True
        if any(hint in normalized for hint in _NON_TEXT_LABEL_HINTS):
            return False
        if any(hint in normalized for hint in _TEXT_LABEL_HINTS):
            return True
        return True

    @staticmethod
    def _sanitize_block_text(raw_text: Any) -> str:
        text = raw_text.strip() if isinstance(raw_text, str) else str(raw_text or "").strip()
        if not text:
            return ""
        return " ".join(text.replace("\r", "\n").split())

    @staticmethod
    def _looks_like_markup_or_asset_ref(text: str) -> bool:
        candidate = text.strip()
        if not candidate:
            return True
        return any(pattern.search(candidate) for pattern in _MARKUP_OR_ASSET_PATTERNS)

    def _parse_page_dict(self, page: dict[str, Any]) -> list[dict[str, Any]]:
        wrapped = page.get("res")
        if isinstance(wrapped, dict):
            parsed_wrapped = self._parse_page_dict(wrapped)
            if parsed_wrapped:
                return parsed_wrapped

        direct_lines = page.get("lines")
        if isinstance(direct_lines, list):
            parsed_direct = self._parse_line_list(direct_lines)
            if parsed_direct:
                return parsed_direct

        nested = page.get("ocr_res")
        if isinstance(nested, list):
            parsed_nested = self._parse_line_list(nested)
            if parsed_nested:
                return parsed_nested

        parsing_res_list = page.get("parsing_res_list")
        if isinstance(parsing_res_list, list):
            parsed_blocks = self._parse_parsing_res_list(parsing_res_list)
            if parsed_blocks:
                return parsed_blocks

        texts = page.get("rec_texts") or page.get("texts") or []
        scores = page.get("rec_scores") or page.get("scores") or []
        polys = page.get("rec_polys") or page.get("dt_polys") or page.get("polys") or []
        count = min(len(texts), len(scores), len(polys))
        if count <= 0:
            return []

        lines: list[dict[str, Any]] = []
        for index in range(count):
            try:
                box = self._poly_to_ltrbwh(polys[index])
            except Exception:
                continue

            if box[2] <= 0 or box[3] <= 0:
                continue

            score = scores[index]
            lines.append(
                {
                    "text": str(texts[index]) if texts[index] is not None else "",
                    "box": box,
                    "confidence": float(score) if score is not None else 1.0,
                }
            )
        return lines

    def _parse_parsing_res_list(self, items: list[Any]) -> list[dict[str, Any]]:
        lines: list[dict[str, Any]] = []
        labels = Counter[str]()
        rejected_non_text = 0
        rejected_markup = 0
        rejected_empty = 0
        rejected_box = 0

        for item in items:
            if not isinstance(item, dict):
                continue

            label = self._normalize_label(item.get("block_label") or item.get("label") or item.get("type"))
            labels[label or "<none>"] += 1

            if not self._is_text_like_block(label):
                rejected_non_text += 1
                continue

            text = self._sanitize_block_text(item.get("block_content") or item.get("text"))
            if not text:
                rejected_empty += 1
                continue

            if self._looks_like_markup_or_asset_ref(text):
                rejected_markup += 1
                continue

            box: list[float] | None = None
            polygon = item.get("block_polygon_points") or item.get("polygon_points")
            if isinstance(polygon, list) and len(polygon) >= 3:
                try:
                    box = self._poly_to_ltrbwh(polygon)
                except Exception:
                    box = None

            if box is None:
                raw_bbox = item.get("block_bbox") or item.get("bbox") or item.get("box")
                box = self._bbox_to_ltrbwh(raw_bbox)

            if box is None or box[2] <= 0 or box[3] <= 0:
                rejected_box += 1
                continue

            score = item.get("score") or item.get("confidence")
            try:
                confidence = float(score) if score is not None else 1.0
            except (TypeError, ValueError):
                confidence = 1.0

            lines.append(
                {
                    "text": text,
                    "box": box,
                    "confidence": confidence,
                }
            )

        if labels:
            label_summary = ",".join(f"{name}:{count}" for name, count in labels.most_common(6))
            _LOGGER.info(
                "stage=ocr_vl_parser event=layout_filter_stats total=%s accepted=%s "
                "reject_label=%s reject_markup=%s reject_empty=%s reject_box=%s labels=%s",
                sum(labels.values()),
                len(lines),
                rejected_non_text,
                rejected_markup,
                rejected_empty,
                rejected_box,
                label_summary,
            )

        return lines

    @classmethod
    def _parse_line_list(cls, items: list[Any]) -> list[dict[str, Any]]:
        lines: list[dict[str, Any]] = []
        for item in items:
            if not isinstance(item, dict):
                continue

            text = cls._sanitize_block_text(item.get("text"))
            if not text or cls._looks_like_markup_or_asset_ref(text):
                continue

            box = item.get("box") or item.get("bbox")
            if not isinstance(box, list) or len(box) < 4:
                continue

            try:
                left = float(box[0])
                top = float(box[1])
                width = float(box[2])
                height = float(box[3])
            except (TypeError, ValueError):
                continue

            if width <= 0 or height <= 0:
                continue

            confidence = item.get("confidence")
            try:
                confidence_value = float(confidence) if confidence is not None else 1.0
            except (TypeError, ValueError):
                confidence_value = 1.0

            lines.append(
                {
                    "text": text,
                    "box": [left, top, width, height],
                    "confidence": confidence_value,
                }
            )
        return lines

    @staticmethod
    def _bbox_to_ltrbwh(raw_bbox: Any) -> list[float] | None:
        if not isinstance(raw_bbox, list) or len(raw_bbox) < 4:
            return None

        try:
            x1 = float(raw_bbox[0])
            y1 = float(raw_bbox[1])
            x2 = float(raw_bbox[2])
            y2 = float(raw_bbox[3])
        except (TypeError, ValueError):
            return None

        if x2 > x1 and y2 > y1:
            return [x1, y1, x2 - x1, y2 - y1]

        if x2 <= 0 or y2 <= 0:
            return None

        return [x1, y1, x2, y2]

    @staticmethod
    def _poly_to_ltrbwh(poly: Any) -> list[float]:
        xs = [float(point[0]) for point in poly]
        ys = [float(point[1]) for point in poly]
        left = min(xs)
        top = min(ys)
        right = max(xs)
        bottom = max(ys)
        return [left, top, right - left, bottom - top]
