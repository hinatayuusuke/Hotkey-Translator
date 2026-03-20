from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw

from oneocr_bridge import DEFAULT_MAX_LINE_COUNT, OneOcrBridge, OneOcrError


SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_VENDOR_DIR = SCRIPT_DIR / "vendor"


def build_argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run Snipping Tool OneOCR against a single image.")
    parser.add_argument("--image", required=True, help="Input image path (PNG/JPG).")
    parser.add_argument("--out-json", help="Optional output path for JSON result.")
    parser.add_argument("--dump-overlay", help="Optional output path for a bounding-box overlay image.")
    parser.add_argument(
        "--vendor-dir",
        default=str(DEFAULT_VENDOR_DIR),
        help="Directory containing oneocr.dll, oneocr.onemodel, and onnxruntime.dll.",
    )
    parser.add_argument(
        "--max-line-count",
        type=int,
        default=DEFAULT_MAX_LINE_COUNT,
        help="Maximum line count passed to OcrProcessOptionsSetMaxRecognitionLineCount.",
    )
    parser.add_argument("--pretty", action="store_true", help="Pretty-print JSON output.")
    return parser


def render_overlay(source_image: Path, destination_image: Path, result: dict) -> None:
    with Image.open(source_image) as image:
        image = image.convert("RGBA")
        draw = ImageDraw.Draw(image)

        for line in result["lines"]:
            polygon = [tuple(point) for point in line["polygon"]]
            draw.polygon(polygon, outline=(255, 128, 0, 255), width=2)

        for word in result["words"]:
            polygon = [tuple(point) for point in word["polygon"]]
            draw.polygon(polygon, outline=(0, 200, 255, 255), width=1)

        destination_image.parent.mkdir(parents=True, exist_ok=True)
        image.save(destination_image)


def main() -> int:
    parser = build_argument_parser()
    args = parser.parse_args()

    image_path = Path(args.image).expanduser().resolve()
    if not image_path.exists():
        parser.error(f"Input image does not exist: {image_path}")

    try:
        started_at = time.perf_counter()
        with OneOcrBridge(args.vendor_dir, max_line_count=args.max_line_count) as bridge:
            ocr_result = bridge.recognize_image(image_path)
        duration_ms = round((time.perf_counter() - started_at) * 1000, 3)
    except OneOcrError as exc:
        print(f"OneOCR failed: {exc}", file=sys.stderr)
        return 1

    payload = {
        "engine": "OneOCR",
        "durationMs": duration_ms,
        "imagePath": str(image_path),
        **ocr_result.to_dict(),
    }

    json_text = json.dumps(payload, ensure_ascii=False, indent=2 if args.pretty else None)

    if args.out_json:
        output_path = Path(args.out_json).expanduser().resolve()
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(json_text + ("\n" if args.pretty else ""), encoding="utf-8")
    else:
        print(json_text)

    if args.dump_overlay:
        render_overlay(image_path, Path(args.dump_overlay).expanduser().resolve(), payload)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
