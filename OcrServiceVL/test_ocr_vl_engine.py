import argparse
import json
import sys
from pathlib import Path

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
    return parser.parse_args()


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
