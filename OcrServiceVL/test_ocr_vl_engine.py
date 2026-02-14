import argparse
import io
import json
import os
import sys
import tempfile
from pathlib import Path
from typing import Any

from PIL import Image
from ocr_vl_engine import PaddleOcrVlEngine


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Test runner for OcrServiceVL/ocr_vl_engine.py."
    )
    parser.add_argument(
        "input_image",
        type=Path,
        help="Path to the input image file.",
    )
    parser.add_argument(
        "--device",
        default="gpu:0",
        help="Inference device (e.g. cpu, gpu:0).",
    )
    parser.add_argument(
        "--pipeline-version",
        default="v1.5",
        choices=["v1", "v1.5"],
        help="PaddleOCR-VL pipeline version.",
    )
    parser.add_argument(
        "--max-pixels",
        type=int,
        default=None,
        help="Maximum pixels used by VL preprocessing.",
    )
    parser.add_argument(
        "--layout-threshold",
        type=float,
        default=None,
        help="Score threshold for layout detection.",
    )
    parser.add_argument(
        "--max-new-tokens",
        type=int,
        default=None,
        help="Maximum number of new tokens for VL generation.",
    )
    parser.add_argument(
        "--merge-layout-blocks",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Enable or disable merging layout blocks.",
    )
    parser.add_argument(
        "--use-ocr-for-image-block",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Enable or disable OCR for image blocks.",
    )
    parser.add_argument(
        "--use-layout-detection",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Enable or disable layout detection.",
    )
    parser.add_argument(
        "--enable-hpi",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Enable or disable high-performance inference mode.",
    )
    parser.add_argument(
        "--use-tensorrt",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Enable or disable TensorRT subgraph acceleration.",
    )
    parser.add_argument(
        "--precision",
        choices=["fp32", "fp16"],
        default=None,
        help="TensorRT precision mode.",
    )
    parser.add_argument(
        "--output-json",
        type=Path,
        default=None,
        help="Optional path to save recognize() JSON output.",
    )
    parser.add_argument(
        "--pretty",
        action="store_true",
        help="Pretty-print output JSON.",
    )
    parser.add_argument(
        "--traceback",
        action="store_true",
        help="Print traceback on failure.",
    )
    parser.add_argument(
        "--dump-raw",
        action="store_true",
        help="Dump raw PaddleOCR-VL page outputs before parser normalization.",
    )
    parser.add_argument(
        "--raw-output-json",
        type=Path,
        default=None,
        help="Optional path to save dumped raw PaddleOCR-VL output JSON.",
    )
    parser.add_argument(
        "--raw-pretty",
        action="store_true",
        help="Pretty-print raw PaddleOCR-VL output JSON.",
    )
    return parser.parse_args()


def _to_jsonable(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, Path):
        return str(value)
    if isinstance(value, dict):
        return {str(k): _to_jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_to_jsonable(v) for v in value]
    return str(value)


def _extract_raw_page(page: Any) -> dict[str, Any]:
    raw: dict[str, Any] = {"type": str(type(page))}
    if isinstance(page, dict):
        raw["page"] = _to_jsonable(page)
    for attr in ("json", "res", "markdown", "data"):
        value = getattr(page, attr, None)
        if value is not None:
            raw[attr] = _to_jsonable(value)
    return raw


def _predict_pages(engine: PaddleOcrVlEngine, image_bytes: bytes) -> list[Any]:
    image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    with tempfile.NamedTemporaryFile(prefix="ocr_vl_test_", suffix=".png", delete=False) as tmp:
        temp_path = tmp.name
        image.save(tmp, format="PNG")

    try:
        return list(engine._engine.predict_iter(temp_path, **engine._predict_kwargs))
    finally:
        try:
            os.remove(temp_path)
        except OSError:
            pass


def main() -> int:
    args = parse_args()
    image_path = args.input_image.expanduser().resolve()
    if not image_path.is_file():
        print(f"Input image not found: {image_path}", file=sys.stderr)
        return 1

    image_bytes = image_path.read_bytes()
    engine = None
    try:
        engine = PaddleOcrVlEngine(
            device=args.device,
            pipeline_version=args.pipeline_version,
            max_pixels=args.max_pixels,
            layout_threshold=args.layout_threshold,
            max_new_tokens=args.max_new_tokens,
            merge_layout_blocks=args.merge_layout_blocks,
            use_ocr_for_image_block=args.use_ocr_for_image_block,
            use_layout_detection=args.use_layout_detection,
            enable_hpi=args.enable_hpi,
            use_tensorrt=args.use_tensorrt,
            precision=args.precision,
        )
        if args.dump_raw:
            pages = _predict_pages(engine, image_bytes)
            raw_dump = {"pages": [_extract_raw_page(page) for page in pages]}
            lines = []
            for page in pages:
                lines.extend(engine._extract_lines(page))
            result = {"lines": lines}

            raw_dumped = json.dumps(
                raw_dump,
                ensure_ascii=False,
                indent=2 if args.raw_pretty else None,
            )
            print(raw_dumped)
            if args.raw_output_json is not None:
                raw_out = args.raw_output_json.expanduser().resolve()
                raw_out.parent.mkdir(parents=True, exist_ok=True)
                raw_out.write_text(raw_dumped, encoding="utf-8")
                print(f"saved_raw_json={raw_out}")
        else:
            result_json = engine.recognize(image_bytes)
            result = json.loads(result_json)

        lines = result.get("lines", [])
        print(f"recognized_lines={len(lines)}")

        dumped = json.dumps(
            result,
            ensure_ascii=False,
            indent=2 if args.pretty else None,
        )
        print(dumped)

        if args.output_json is not None:
            out = args.output_json.expanduser().resolve()
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_text(dumped, encoding="utf-8")
            print(f"saved_json={out}")
        return 0
    except Exception as exc:
        print(f"test_failed: {exc}", file=sys.stderr)
        if args.traceback:
            import traceback

            traceback.print_exc()
        return 1
    finally:
        # WHY: PaddleOCR-VL retains GPU/engine resources until close() is called.
        if engine is not None:
            engine.close()


if __name__ == "__main__":
    raise SystemExit(main())
