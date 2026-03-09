import argparse
import json
import statistics
import sys
import time
from pathlib import Path

from vision_llama_engine import (
    LlamaServerHost,
    VisionLlamaEngine,
    VisionLlamaRequestConfig,
    VisionLlamaServerConfig,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Direct VisionLlamaEngine OCR smoke test")
    parser.add_argument("--image", required=True, help="Input image path")
    parser.add_argument("--llama-server", default=r"..\TranslationServiceLlama\LlamaCpp\llama-server.exe", help="llama-server path")
    parser.add_argument("--model", default=r"..\TranslationServiceLlama\LlamaCpp\Models\Qwen3.5-9B-Q4_K_M.gguf", help="GGUF model path")
    parser.add_argument("--mmproj", default=r"..\TranslationServiceLlama\LlamaCpp\Models\mmproj-F16.gguf", help="mmproj path")
    parser.add_argument("--host", default="127.0.0.1", help="llama-server HTTP host")
    parser.add_argument("--port", type=int, default=8089, help="llama-server HTTP port")
    parser.add_argument("--device", choices=["cpu", "gpu"], default="gpu", help="Execution device")
    parser.add_argument("--gpu-layers", type=int, default=None, help="Override GPU layers")
    parser.add_argument("--ctx-size", type=int, default=4096, help="Context size")
    parser.add_argument("--threads", type=int, default=4, help="CPU threads")
    parser.add_argument("--parallel", type=int, default=1, help="Parallel slots")
    parser.add_argument("--batch-size", type=int, default=512, help="Batch size")
    parser.add_argument("--max-tokens", type=int, default=512, help="Max output tokens")
    parser.add_argument("--max-image-side", type=int, default=1024, help="Resize long side before upload; 0 disables")
    parser.add_argument("--http-timeout-sec", type=float, default=120.0, help="HTTP timeout")
    parser.add_argument("--startup-timeout-sec", type=float, default=120.0, help="Server startup timeout")
    parser.add_argument("--restart-max", type=int, default=3, help="Restart limit")
    parser.add_argument("--restart-window-seconds", type=int, default=30, help="Restart window")
    parser.add_argument("--temperature", type=float, default=0.3, help="Sampling temperature")
    parser.add_argument("--top-p", type=float, default=0.6, help="Top-p")
    parser.add_argument("--top-k", type=int, default=20, help="Top-k")
    parser.add_argument("--repeat-penalty", type=float, default=1.05, help="Repeat penalty")
    parser.add_argument("--language", default="ja", help="OCR language hint")
    parser.add_argument("--warmup", type=int, default=1, help="Warmup OCR runs before timing")
    parser.add_argument("--repeat", type=int, default=3, help="Measured OCR runs")
    parser.add_argument(
        "--disable-thinking",
        dest="disable_thinking",
        action="store_true",
        default=True,
        help="Disable model reasoning/think mode for OCR.",
    )
    parser.add_argument(
        "--enable-thinking",
        dest="disable_thinking",
        action="store_false",
        help="Allow model reasoning/think mode for diagnostics.",
    )
    parser.add_argument("--text-out", default=None, help="Optional path to write the final OCR text")
    parser.add_argument("--json-out", default=None, help="Optional path to write timing/text summary JSON")
    return parser.parse_args()


def resolve_path(base_dir: Path, path: str) -> Path:
    candidate = Path(path)
    if candidate.is_absolute():
        return candidate
    return (base_dir / candidate).resolve()


def resolve_gpu_layers(args: argparse.Namespace) -> int:
    if args.gpu_layers is not None:
        return args.gpu_layers
    if args.device == "cpu":
        return 0
    return 999


def write_text(path: str | None, content: str) -> None:
    if not path:
        return
    Path(path).write_text(content, encoding="utf-8")


def write_json(path: str | None, payload: dict) -> None:
    if not path:
        return
    Path(path).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def main() -> int:
    args = parse_args()
    base_dir = Path(__file__).resolve().parent
    image_path = resolve_path(base_dir, args.image)
    llama_server_path = resolve_path(base_dir, args.llama_server)
    model_path = resolve_path(base_dir, args.model)
    mmproj_path = resolve_path(base_dir, args.mmproj)

    if not image_path.is_file():
        raise FileNotFoundError(f"image not found: {image_path}")
    if not llama_server_path.is_file():
        raise FileNotFoundError(f"llama-server not found: {llama_server_path}")
    if not model_path.is_file():
        raise FileNotFoundError(f"model not found: {model_path}")
    if not mmproj_path.is_file():
        raise FileNotFoundError(f"mmproj not found: {mmproj_path}")

    image_bytes = image_path.read_bytes()
    server_config = VisionLlamaServerConfig(
        llama_server_path=str(llama_server_path),
        model_path=str(model_path),
        mmproj_path=str(mmproj_path),
        host=args.host,
        port=args.port,
        context_size=args.ctx_size,
        gpu_layers=resolve_gpu_layers(args),
        threads=args.threads,
        parallel=args.parallel,
        batch_size=args.batch_size,
        ready_timeout_ms=max(1000, int(args.startup_timeout_sec * 1000)),
        restart_max=args.restart_max,
        restart_window_seconds=args.restart_window_seconds,
        max_image_side=args.max_image_side,
        disable_thinking=args.disable_thinking,
    )
    request_config = VisionLlamaRequestConfig(
        max_tokens=args.max_tokens,
        temperature=args.temperature,
        top_p=args.top_p,
        top_k=args.top_k,
        repeat_penalty=args.repeat_penalty,
        http_timeout_seconds=args.http_timeout_sec,
        disable_thinking=args.disable_thinking,
    )

    host = LlamaServerHost(server_config)
    engine = VisionLlamaEngine(host, request_config, args.max_image_side)

    warmup = max(0, args.warmup)
    repeat = max(1, args.repeat)
    timings_ms: list[float] = []
    final_text = ""
    startup_start = time.perf_counter()

    try:
        host.start()
        startup_ms = (time.perf_counter() - startup_start) * 1000.0
        print(f"startup_ms={startup_ms:.2f}")
        print(f"image_bytes={len(image_bytes)} max_image_side={args.max_image_side} model={model_path.name} mmproj={mmproj_path.name}")

        for index in range(warmup):
            began = time.perf_counter()
            text = engine.recognize(image_bytes, args.language)
            elapsed_ms = (time.perf_counter() - began) * 1000.0
            print(f"warmup[{index + 1}/{warmup}] ocr_ms={elapsed_ms:.2f} chars={len(text)}")

        for index in range(repeat):
            began = time.perf_counter()
            text = engine.recognize(image_bytes, args.language)
            elapsed_ms = (time.perf_counter() - began) * 1000.0
            timings_ms.append(elapsed_ms)
            final_text = text
            print(f"run[{index + 1}/{repeat}] ocr_ms={elapsed_ms:.2f} chars={len(text)}")

        summary = {
            "startup_ms": startup_ms,
            "warmup_count": warmup,
            "repeat_count": repeat,
            "ocr_ms": timings_ms,
            "ocr_ms_avg": statistics.mean(timings_ms),
            "ocr_ms_min": min(timings_ms),
            "ocr_ms_max": max(timings_ms),
            "image_path": str(image_path),
            "image_bytes": len(image_bytes),
            "model_path": str(model_path),
            "mmproj_path": str(mmproj_path),
            "device": args.device,
            "gpu_layers": resolve_gpu_layers(args),
            "max_image_side": args.max_image_side,
            "disable_thinking": args.disable_thinking,
            "text": final_text,
        }

        print("--- OCR Text ---")
        print(final_text)
        print("--- Summary ---")
        print(json.dumps(summary, ensure_ascii=False, indent=2))

        write_text(args.text_out, final_text)
        write_json(args.json_out, summary)
        return 0
    finally:
        host.stop()


if __name__ == "__main__":
    raise SystemExit(main())
