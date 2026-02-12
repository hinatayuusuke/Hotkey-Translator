import argparse
import os
import sys
from pathlib import Path

_DLL_DIR_HANDLES = []


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


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run OCR on an image with PaddleOCR-VL."
    )
    parser.add_argument(
        "input_image",
        type=Path,
        help="Path to the input image file.",
    )
    parser.add_argument(
        "--device",
        default="cpu",
        help="Inference device (e.g. cpu, gpu:0). Default: cpu",
    )
    parser.add_argument(
        "--pipeline-version",
        default="v1.5",
        choices=["v1", "v1.5"],
        help="PaddleOCR-VL pipeline version. Default: v1.5",
    )
    parser.add_argument(
        "--save-dir",
        type=Path,
        default=None,
        help="Directory to save JSON/Markdown outputs.",
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
        "--max-new-tokens",
        type=int,
        default=None,
        help="Maximum number of new tokens for VL generation.",
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
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    input_path = args.input_image.expanduser().resolve()

    if not input_path.is_file():
        print(f"Input image not found: {input_path}", file=sys.stderr)
        return 1

    # Skip external model source probing to reduce startup delay.
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    _bootstrap_windows_cuda_dll_dirs()
    from paddleocr import PaddleOCRVL

    try:
        init_kwargs = {
            "pipeline_version": args.pipeline_version,
            "device": args.device,
            "format_block_content": True,
            "use_queues": False,
            "enable_hpi": args.enable_hpi,
            "use_tensorrt": args.use_tensorrt,
            "precision": args.precision,
        }
        # NOTE: Omit None-valued args so PaddleOCRVL keeps its internal defaults.
        init_kwargs = {k: v for k, v in init_kwargs.items() if v is not None}
        ocr = PaddleOCRVL(**init_kwargs)
    except Exception as exc:
        print("Failed to initialize PaddleOCR-VL.", file=sys.stderr)
        print(str(exc), file=sys.stderr)
        print(
            "Hint: install runtime deps with: uv pip install paddlepaddle \"paddlex[ocr]==3.4.1\"",
            file=sys.stderr,
        )
        return 1

    # NOTE: Omit None-valued args so PaddleOCRVL keeps its internal defaults.
    predict_kwargs = {
        "merge_layout_blocks": args.merge_layout_blocks,
        "use_ocr_for_image_block": args.use_ocr_for_image_block,
        "use_layout_detection": args.use_layout_detection,
        "max_new_tokens": args.max_new_tokens,
        "max_pixels": args.max_pixels,
        "layout_threshold": args.layout_threshold,
    }
    predict_kwargs = {k: v for k, v in predict_kwargs.items() if v is not None}

    try:
        for page_idx, res in enumerate(
            ocr.predict_iter(str(input_path), **predict_kwargs)
        ):
            print(f"\n=== Page {page_idx} ===")

            markdown_text = res.markdown.get("markdown_texts", "").strip()
            if markdown_text:
                print(markdown_text)
            else:
                res.print()

            if args.save_dir is not None:
                args.save_dir.mkdir(parents=True, exist_ok=True)
                res.save_to_json(str(args.save_dir))
                res.save_to_markdown(str(args.save_dir))
    except Exception as exc:
        print("OCR inference failed.", file=sys.stderr)
        print(str(exc), file=sys.stderr)
        return 1
    finally:
        ocr.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
