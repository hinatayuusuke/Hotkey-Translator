import argparse
import base64
import json
import mimetypes
import os
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Optional

import httpx

REQUIRED_CUDA_DLLS = (
    "cudart64_12.dll",
    "cublas64_12.dll",
    "cublasLt64_12.dll",
)

DEFAULT_PROMPT = "Extract all visible text from this image. Output plain text only. Preserve line breaks. Do not translate."
DEFAULT_TRANSLATE_PROMPT = (
    "Translate all visible text in this image from {source_lang} to {target_lang}. "
    "Output translated text only. Preserve line breaks where possible. Do not describe the image."
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Vision OCR/translation smoke test via llama-server OpenAI-compatible API")
    parser.add_argument("--image", required=True, help="Input image path")
    parser.add_argument("--llama-server", default=r"LlamaCpp\llama-server.exe", help="llama-server path")
    parser.add_argument("--model", required=True, help="GGUF model path")
    parser.add_argument("--mmproj", default=None, help="Optional multimodal projector path")
    parser.add_argument("--host", default="127.0.0.1", help="llama-server HTTP host")
    parser.add_argument("--port", type=int, default=8088, help="llama-server HTTP port")
    parser.add_argument("--device", choices=["cpu", "gpu"], default="gpu", help="Execution device for local server")
    parser.add_argument("--gpu-layers", type=int, default=None, help="Override GPU layers")
    parser.add_argument("--ctx-size", type=int, default=4096, help="Context size")
    parser.add_argument("--threads", type=int, default=4, help="CPU threads")
    parser.add_argument("--parallel", type=int, default=1, help="Parallel slots")
    parser.add_argument("--batch-size", type=int, default=512, help="Batch size")
    parser.add_argument("--max-tokens", type=int, default=512, help="Max output tokens")
    parser.add_argument("--http-timeout-sec", type=float, default=120.0, help="HTTP timeout")
    parser.add_argument("--startup-timeout-sec", type=float, default=120.0, help="Server startup timeout")
    parser.add_argument("--mode", choices=["ocr", "translate"], default="ocr", help="Vision task mode")
    parser.add_argument("--source-lang", default="ja", help="Source language for translate mode")
    parser.add_argument("--target-lang", default="en", help="Target language for translate mode")
    parser.add_argument("--prompt", default=None, help="Optional prompt override")
    parser.add_argument(
        "--disable-thinking",
        dest="disable_thinking",
        action="store_true",
        default=True,
        help="Disable model reasoning/think mode for OCR output.",
    )
    parser.add_argument(
        "--enable-thinking",
        dest="disable_thinking",
        action="store_false",
        help="Allow model reasoning/think mode for diagnostics.",
    )
    parser.add_argument("--json-out", default=None, help="Optional path to write raw JSON response")
    parser.add_argument("--text-out", default=None, help="Optional path to write extracted text")
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


def collect_nvidia_dll_bin_paths(base_dir: Path) -> list[Path]:
    site_packages = base_dir / ".venv" / "Lib" / "site-packages" / "nvidia"
    bins: list[Path] = []
    for package in ("cuda_runtime", "cublas", "nvjitlink", "cudnn"):
        candidate = site_packages / package / "bin"
        if candidate.is_dir():
            bins.append(candidate)
    return bins


def find_missing_cuda_dlls(bin_paths: list[Path]) -> list[str]:
    missing: list[str] = []
    for dll_name in REQUIRED_CUDA_DLLS:
        if not any((path / dll_name).is_file() for path in bin_paths):
            missing.append(dll_name)
    return missing


def start_log_pump(pipe: Optional[object], label: str) -> None:
    if pipe is None:
        return

    def pump() -> None:
        for line in pipe:
            text = line.strip()
            if text:
                print(f"[{label}] {text}")

    threading.Thread(target=pump, daemon=True).start()


def wait_llama_http_ready(base_url: str, timeout_sec: float) -> dict:
    deadline = time.monotonic() + max(1.0, timeout_sec)
    last_error: Optional[Exception] = None
    with httpx.Client(base_url=base_url, timeout=5.0) as client:
        while time.monotonic() < deadline:
            try:
                response = client.get("/v1/models")
                if response.status_code == 200:
                    data = response.json()
                    if isinstance(data, dict):
                        return data
            except Exception as exc:
                last_error = exc
            time.sleep(0.2)

    if last_error is not None:
        raise RuntimeError(f"Llama HTTP readiness timed out: {last_error}") from last_error
    raise RuntimeError("Llama HTTP readiness timed out.")


def stop_local_server(process: subprocess.Popen[str]) -> None:
    if os.name == "nt":
        # WHY: The helper process owns llama-server; kill the tree to avoid VRAM leaks after a failed test.
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        return

    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def start_local_server(args: argparse.Namespace) -> subprocess.Popen[str]:
    base_dir = Path(__file__).resolve().parent
    llama_server = resolve_path(base_dir, args.llama_server)
    model_path = resolve_path(base_dir, args.model)
    mmproj_path = resolve_path(base_dir, args.mmproj) if args.mmproj else None
    if not llama_server.is_file():
        raise FileNotFoundError(f"llama-server not found: {llama_server}")
    if not model_path.is_file():
        raise FileNotFoundError(f"model not found: {model_path}")
    if mmproj_path is not None and not mmproj_path.is_file():
        raise FileNotFoundError(f"mmproj not found: {mmproj_path}")

    nvidia_bin_paths = collect_nvidia_dll_bin_paths(base_dir)
    if args.device == "gpu":
        missing_cuda_dlls = find_missing_cuda_dlls(nvidia_bin_paths)
        if missing_cuda_dlls:
            raise RuntimeError(
                "CUDA DLLs are missing under TranslationServiceLlama/.venv. "
                f"Missing: {', '.join(missing_cuda_dlls)}"
            )

    cmd = [
        str(llama_server),
        "-m",
        str(model_path),
        "--host",
        args.host,
        "--port",
        str(args.port),
        "--ctx-size",
        str(args.ctx_size),
        "--n-gpu-layers",
        str(resolve_gpu_layers(args)),
        "--threads",
        str(args.threads),
        "--parallel",
        str(args.parallel),
        "--batch-size",
        str(args.batch_size),
    ]
    if mmproj_path is not None:
        cmd.extend(["--mmproj", str(mmproj_path)])
    if args.disable_thinking:
        cmd.extend(
            [
                "--reasoning-budget",
                "0",
                "--reasoning-format",
                "none",
                "--chat-template-kwargs",
                json.dumps({"enable_thinking": False}, ensure_ascii=True, separators=(",", ":")),
            ]
        )

    print(
        "Starting local llama-server "
        f"(device={args.device}, gpu_layers={resolve_gpu_layers(args)}, batch_size={args.batch_size})"
    )
    env = os.environ.copy()
    if nvidia_bin_paths:
        bins = ";".join(str(path) for path in nvidia_bin_paths)
        env["PATH"] = f"{bins};{env.get('PATH', '')}"
        print(f"Injected CUDA DLL PATH entries: {len(nvidia_bin_paths)}")

    process = subprocess.Popen(
        cmd,
        cwd=str(base_dir),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        env=env,
    )
    start_log_pump(process.stdout, "llama-vision:stdout")
    start_log_pump(process.stderr, "llama-vision:stderr")
    return process


def guess_mime_type(image_path: Path) -> str:
    guessed, _ = mimetypes.guess_type(str(image_path))
    if guessed:
        return guessed
    return "image/png"


def build_data_url(image_path: Path) -> str:
    mime_type = guess_mime_type(image_path)
    raw = image_path.read_bytes()
    encoded = base64.b64encode(raw).decode("ascii")
    return f"data:{mime_type};base64,{encoded}"


def extract_message_content(payload: dict) -> str:
    choices = payload.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    message = choices[0].get("message", {})
    if not isinstance(message, dict):
        return ""
    content = message.get("content", "")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: list[str] = []
        for item in content:
            if isinstance(item, dict):
                text = item.get("text")
                if isinstance(text, str) and text.strip():
                    parts.append(text.strip())
        return "\n".join(parts)
    return ""


def build_user_prompt(args: argparse.Namespace) -> str:
    if args.prompt:
        return args.prompt
    if args.mode == "translate":
        return DEFAULT_TRANSLATE_PROMPT.format(source_lang=args.source_lang, target_lang=args.target_lang)
    return DEFAULT_PROMPT


def run_vision_chat(args: argparse.Namespace) -> tuple[dict, str, int]:
    base_dir = Path(__file__).resolve().parent
    image_path = resolve_path(base_dir, args.image)
    model_path = resolve_path(base_dir, args.model)
    if not image_path.is_file():
        raise FileNotFoundError(f"image not found: {image_path}")
    if not model_path.is_file():
        raise FileNotFoundError(f"model not found: {model_path}")

    base_url = f"http://{args.host}:{args.port}"
    wait_llama_http_ready(base_url, args.startup_timeout_sec)
    model_name = model_path.name
    data_url = build_data_url(image_path)
    user_prompt = build_user_prompt(args)
    body = {
        "model": model_name,
        "messages": [
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": user_prompt},
                    {"type": "image_url", "image_url": {"url": data_url}},
                ],
            }
        ],
        "temperature": 0.0,
        "top_p": 0.1,
        "top_k": 20,
        "repeat_penalty": 1.0,
        "max_tokens": args.max_tokens,
        "stream": False,
    }
    if args.disable_thinking:
        body["reasoning_budget"] = 0
        body["reasoning_format"] = "none"
        body["chat_template_kwargs"] = {"enable_thinking": False}

    started = time.monotonic()
    with httpx.Client(base_url=base_url, timeout=max(1.0, args.http_timeout_sec)) as client:
        response = client.post("/v1/chat/completions", json=body)
        response.raise_for_status()
        payload = response.json()
    elapsed_ms = int((time.monotonic() - started) * 1000)
    return payload, extract_message_content(payload), elapsed_ms


def write_text(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def main() -> int:
    args = parse_args()
    local_server: Optional[subprocess.Popen[str]] = None
    try:
        local_server = start_local_server(args)
        payload, text, elapsed_ms = run_vision_chat(args)
        print(f"{args.mode} completed in {elapsed_ms} ms")
        print("--- Model Output ---")
        print(text)

        if args.json_out:
            Path(args.json_out).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
            print(f"[OK] wrote json: {args.json_out}")
        if args.text_out:
            write_text(args.text_out, text)
            print(f"[OK] wrote text: {args.text_out}")
        return 0
    finally:
        if local_server is not None:
            stop_local_server(local_server)


if __name__ == "__main__":
    raise SystemExit(main())
