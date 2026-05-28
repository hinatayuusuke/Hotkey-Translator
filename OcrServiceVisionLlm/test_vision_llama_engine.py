import argparse
import json
import statistics
import sys
import time
from pathlib import Path
from typing import Any

import httpx
from vision_llama_engine import (
    LlamaServerHost,
    VisionLlamaEngine,
    VisionLlamaRequestConfig,
    VisionLlamaServerConfig,
    VisionLlamaError,
    extract_message_content,
    parse_json_object_from_text_resilient,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Direct VisionLlamaEngine OCR smoke test")
    parser.add_argument(
        "--mode",
        choices=["ocr", "translate", "ocr-translate"],
        default="ocr",
        help="Run OCR only, translate only, or OCR-then-translate path",
    )
    parser.add_argument("--image", help="Input image path")
    parser.add_argument("--llama-server", default=r"..\TranslationServiceLlama\LlamaCpp\llama-server.exe", help="llama-server path")
    parser.add_argument("--model", default=r"..\TranslationServiceLlama\LlamaCpp\Models\Qwen3.5-4B-Q4_K_M.gguf", help="GGUF model path")
    parser.add_argument("--mmproj", default=r"..\TranslationServiceLlama\LlamaCpp\Models\mmproj-Qwen3.5-4B-BF16.gguf", help="mmproj path")
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
    parser.add_argument("--warmup", type=int, default=0, help="Warmup OCR runs before timing")
    parser.add_argument("--repeat", type=int, default=1, help="Measured OCR runs")
    parser.add_argument(
        "--enable-mtp",
        action="store_true",
        help="Enable llama.cpp draft-mtp speculative decoding. Requires an MTP-capable GGUF model.",
    )
    parser.add_argument("--mtp-draft-tokens", type=int, default=3, help="Maximum draft tokens for MTP speculative decoding.")
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
    parser.add_argument("--text", action="append", dest="texts", help="Translation item. Can be specified multiple times.")
    parser.add_argument("--source-lang", default="ja", help="Translation source language")
    parser.add_argument("--target-lang", default="en", help="Translation target language")
    parser.add_argument("--raw-response-out", default=None, help="Optional path to write the last raw HTTP response body")
    parser.add_argument("--raw-request-out", default=None, help="Optional path to write the last HTTP JSON request body")
    parser.add_argument(
        "--show-request-json",
        action="store_true",
        help="Print the last HTTP request JSON. Disabled by default because image data URLs are noisy.",
    )
    parser.add_argument(
        "--show-response-json",
        action="store_true",
        help="Print the last HTTP response body.",
    )
    parser.add_argument(
        "--show-grpc-json",
        action="store_true",
        help="Print the gRPC-style OCR JSON payload ({\"text\": ...}) that the app would receive.",
    )
    parser.add_argument(
        "--show-translation-parse",
        action="store_true",
        help="Print translation parser diagnostics from the last raw HTTP response.",
    )
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
    output_path = Path(path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(content, encoding="utf-8")


def write_json(path: str | None, payload: dict) -> None:
    if not path:
        return
    output_path = Path(path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


class HttpTraceCapture:
    def __init__(self) -> None:
        self.last_request_json: Any | None = None
        self.last_response_text: str = ""
        self.last_status_code: int | None = None


def install_http_trace(client: httpx.Client, capture: HttpTraceCapture):
    original_post = httpx.Client.post

    def traced_post(self, *args, **kwargs):
        response = original_post(self, *args, **kwargs)
        if self is client:
            capture.last_request_json = kwargs.get("json")
            capture.last_status_code = response.status_code
            capture.last_response_text = response.text
        return response

    httpx.Client.post = traced_post

    def restore() -> None:
        httpx.Client.post = original_post

    return restore


def dump_http_trace(
    capture: HttpTraceCapture,
    raw_request_out: str | None,
    raw_response_out: str | None,
    show_request_json: bool,
    show_response_json: bool,
) -> None:
    if capture.last_request_json is not None and show_request_json:
        request_text = json.dumps(capture.last_request_json, ensure_ascii=False, indent=2)
        print("--- Last HTTP Request JSON ---")
        print(request_text)
        write_text(raw_request_out, request_text)
    elif capture.last_request_json is not None and raw_request_out:
        request_text = json.dumps(capture.last_request_json, ensure_ascii=False, indent=2)
        write_text(raw_request_out, request_text)

    if capture.last_response_text and show_response_json:
        print("--- Last HTTP Response Body ---")
        print(capture.last_response_text)
        write_text(raw_response_out, capture.last_response_text)
    elif capture.last_response_text and raw_response_out:
        write_text(raw_response_out, capture.last_response_text)


def analyze_translation_response(capture: HttpTraceCapture) -> dict[str, Any]:
    analysis: dict[str, Any] = {
        "status": "not_available",
        "raw_response_chars": len(capture.last_response_text or ""),
    }
    if not capture.last_response_text:
        return analysis

    try:
        payload = json.loads(capture.last_response_text)
    except json.JSONDecodeError as exc:
        analysis.update(
            {
                "status": "outer_json_invalid",
                "error": str(exc),
            }
        )
        return analysis

    try:
        content = extract_message_content(payload)
    except VisionLlamaError as exc:
        analysis.update(
            {
                "status": "message_extract_failed",
                "error": str(exc),
            }
        )
        return analysis

    parsed, rescued = parse_json_object_from_text_resilient(content)
    analysis.update(
        {
            "status": "ok" if parsed is not None else "inner_json_invalid",
            "content_chars": len(content),
            "content_preview": content[:240],
            "parser_rescued": rescued,
        }
    )
    if parsed is None:
        return analysis

    translations = parsed.get("t")
    if not isinstance(translations, list):
        translations = parsed.get("translations")
    if not isinstance(translations, list):
        analysis.update(
            {
                "status": "translations_missing",
                "parsed_keys": sorted(parsed.keys()),
            }
        )
        return analysis

    analysis.update(
        {
            "translation_item_count": len(translations),
            "parsed_keys": sorted(parsed.keys()),
        }
    )
    return analysis


def build_grpc_ocr_json(text: str) -> str:
    return json.dumps({"text": text}, ensure_ascii=False, indent=2)


def print_app_final_ocr(text: str, show_grpc_json: bool) -> None:
    print("--- App Final OCR Text ---")
    print(text)
    if show_grpc_json:
        print("--- App gRPC OCR JSON ---")
        print(build_grpc_ocr_json(text))

    lines = [line for line in text.splitlines() if line.strip()]
    print(f"--- App Final OCR Lines ({len(lines)}) ---")
    for index, line in enumerate(lines):
        print(f"[{index}] {line}")


def main() -> int:
    args = parse_args()
    base_dir = Path(__file__).resolve().parent
    image_path = resolve_path(base_dir, args.image) if args.image else None
    llama_server_path = resolve_path(base_dir, args.llama_server)
    model_path = resolve_path(base_dir, args.model)
    mmproj_path = resolve_path(base_dir, args.mmproj)

    if args.mode != "translate":
        if image_path is None or not image_path.is_file():
            raise FileNotFoundError(f"image not found: {image_path}")
    if not llama_server_path.is_file():
        raise FileNotFoundError(f"llama-server not found: {llama_server_path}")
    if not model_path.is_file():
        raise FileNotFoundError(f"model not found: {model_path}")
    if not mmproj_path.is_file():
        raise FileNotFoundError(f"mmproj not found: {mmproj_path}")

    image_bytes = image_path.read_bytes() if image_path is not None else b""
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
        enable_mtp=args.enable_mtp,
        mtp_draft_tokens=args.mtp_draft_tokens,
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
    trace_capture = HttpTraceCapture()
    restore_http_trace = install_http_trace(engine._client, trace_capture)

    warmup = max(0, args.warmup)
    repeat = max(1, args.repeat)
    timings_ms: list[float] = []
    final_text = ""
    final_translations: list[str] = []
    startup_start = time.perf_counter()

    try:
        host.start()
        startup_ms = (time.perf_counter() - startup_start) * 1000.0
        print(f"startup_ms={startup_ms:.2f}")
        print(f"image_bytes={len(image_bytes)} max_image_side={args.max_image_side} model={model_path.name} mmproj={mmproj_path.name}")

        if args.mode == "translate":
            texts = [text for text in (args.texts or []) if text]
            if not texts:
                print("Translate mode requires at least one --text.")
                return 2

        try:
            if args.mode == "ocr":
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
            elif args.mode == "translate":
                for index in range(warmup):
                    began = time.perf_counter()
                    translations = engine.translate(texts, args.source_lang, args.target_lang)
                    elapsed_ms = (time.perf_counter() - began) * 1000.0
                    print(f"warmup[{index + 1}/{warmup}] translate_ms={elapsed_ms:.2f} items={len(translations)}")

                for index in range(repeat):
                    began = time.perf_counter()
                    translations = engine.translate(texts, args.source_lang, args.target_lang)
                    elapsed_ms = (time.perf_counter() - began) * 1000.0
                    timings_ms.append(elapsed_ms)
                    final_translations = translations
                    print(f"run[{index + 1}/{repeat}] translate_ms={elapsed_ms:.2f} items={len(translations)}")
            else:
                for index in range(warmup):
                    began = time.perf_counter()
                    text = engine.recognize(image_bytes, args.language)
                    # WHY: Match the app's current VisionLLM path where OCR text is forwarded as-is.
                    translations = engine.translate([text], args.source_lang, args.target_lang)
                    elapsed_ms = (time.perf_counter() - began) * 1000.0
                    print(
                        f"warmup[{index + 1}/{warmup}] ocr_translate_ms={elapsed_ms:.2f} "
                        f"ocr_chars={len(text)} items={len(translations)}"
                    )

                for index in range(repeat):
                    began = time.perf_counter()
                    text = engine.recognize(image_bytes, args.language)
                    translations = engine.translate([text], args.source_lang, args.target_lang)
                    elapsed_ms = (time.perf_counter() - began) * 1000.0
                    timings_ms.append(elapsed_ms)
                    final_text = text
                    final_translations = translations
                    print(
                        f"run[{index + 1}/{repeat}] ocr_translate_ms={elapsed_ms:.2f} "
                        f"ocr_chars={len(text)} items={len(translations)}"
                    )
        except Exception as exc:
            print(f"ERROR: {exc}")
            dump_http_trace(
                trace_capture,
                args.raw_request_out,
                args.raw_response_out,
                args.show_request_json,
                args.show_response_json,
            )
            return 1

        summary = {
            "mode": args.mode,
            "startup_ms": startup_ms,
            "warmup_count": warmup,
            "repeat_count": repeat,
            "run_ms": timings_ms,
            "run_ms_avg": statistics.mean(timings_ms),
            "run_ms_min": min(timings_ms),
            "run_ms_max": max(timings_ms),
            "image_path": str(image_path) if image_path is not None else "",
            "image_bytes": len(image_bytes),
            "model_path": str(model_path),
            "mmproj_path": str(mmproj_path),
            "device": args.device,
            "gpu_layers": resolve_gpu_layers(args),
            "max_image_side": args.max_image_side,
            "disable_thinking": args.disable_thinking,
            "enable_mtp": args.enable_mtp,
            "mtp_draft_tokens": args.mtp_draft_tokens,
            "language": args.language,
            "source_lang": args.source_lang,
            "target_lang": args.target_lang,
            "text": final_text,
            "translations": final_translations,
            "last_status_code": trace_capture.last_status_code,
        }
        translation_parse = analyze_translation_response(trace_capture) if args.mode != "ocr" else None
        if translation_parse is not None:
            summary["translation_parse"] = translation_parse

        if args.mode == "ocr":
            print_app_final_ocr(final_text, args.show_grpc_json)
        else:
            if final_text:
                print_app_final_ocr(final_text, args.show_grpc_json)
            print("--- Translations ---")
            for idx, item in enumerate(final_translations):
                print(f"[{idx}] {item}")
            if args.show_translation_parse:
                print("--- Translation Parse Analysis ---")
                print(json.dumps(translation_parse, ensure_ascii=False, indent=2))
        print("--- Summary ---")
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        dump_http_trace(
            trace_capture,
            args.raw_request_out,
            args.raw_response_out,
            args.show_request_json,
            args.show_response_json,
        )

        if args.mode == "ocr":
            write_text(args.text_out, final_text)
        else:
            write_text(args.text_out, "\n".join(final_translations))
        write_json(args.json_out, summary)
        return 0
    finally:
        restore_http_trace()
        host.stop()


if __name__ == "__main__":
    raise SystemExit(main())
