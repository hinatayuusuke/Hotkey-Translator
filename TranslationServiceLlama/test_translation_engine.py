import argparse
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Optional

import grpc
import httpx

import translation_pb2
import translation_pb2_grpc

REQUIRED_CUDA_DLLS = (
    "cudart64_12.dll",
    "cublas64_12.dll",
    "cublasLt64_12.dll",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Llama gRPC translation smoke test")
    parser.add_argument("--host", default="127.0.0.1", help="gRPC host")
    parser.add_argument("--port", type=int, default=50071, help="gRPC port")
    parser.add_argument(
        "--send-mode",
        choices=["grpc", "llama-json-batch"],
        default="grpc",
        help="Translation request mode: current gRPC behavior or Gemini-like JSON batch over llama HTTP.",
    )
    parser.add_argument(
        "--device",
        choices=["cpu", "gpu"],
        default=None,
        help="Execution device for local test server. Requires local server startup.",
    )
    parser.add_argument(
        "--batch-size",
        type=int,
        default=None,
        help="Batch size for local test server. Requires local server startup.",
    )
    parser.add_argument(
        "--max-tokens",
        type=int,
        default=None,
        help="Max tokens for local test server. Requires local server startup.",
    )
    parser.add_argument(
        "--gpu-layers",
        type=int,
        default=None,
        help="Override gpu layers for local test server startup.",
    )
    parser.add_argument(
        "--auto-start-server",
        action="store_true",
        help="Start TranslationServiceLlama/server.py locally before testing.",
    )
    parser.add_argument("--llama-server", default=r"LlamaCpp\llama-server.exe", help="llama-server path")
    parser.add_argument(
        "--model",
        default=r"LlamaCpp\Models\HY-MT1.5-1.8B-Q8_0.gguf",
        help="GGUF model path",
    )
    parser.add_argument("--llama-host", default="127.0.0.1", help="llama-server HTTP host")
    parser.add_argument("--llama-port", type=int, default=8088, help="llama-server HTTP port")
    parser.add_argument("--http-timeout-sec", type=float, default=120.0, help="HTTP timeout for llama-json-batch mode")
    parser.add_argument(
        "--startup-timeout-sec",
        type=float,
        default=120.0,
        help="Timeout while waiting for local test server to become healthy.",
    )
    parser.add_argument("--source-lang", default="en", help="Source language")
    parser.add_argument("--target-lang", default="ja", help="Target language")
    parser.add_argument(
        "--input",
        default=None,
        help="UTF-8 text file. Blank lines split requests; each non-empty line is one text item.",
    )
    parser.add_argument(
        "--text",
        action="append",
        dest="texts",
        help="Single text item. Can be specified multiple times (sent as one request).",
    )
    parser.add_argument(
        "--continue-on-error",
        action="store_true",
        help="Continue remaining requests even if one request fails.",
    )
    return parser.parse_args()


def resolve_path(base_dir: Path, path: str) -> Path:
    candidate = Path(path)
    if candidate.is_absolute():
        return candidate
    return (base_dir / candidate).resolve()


def should_start_local_server(args: argparse.Namespace) -> bool:
    return args.auto_start_server


def has_local_start_overrides(args: argparse.Namespace) -> bool:
    return (
        args.device is not None
        or args.batch_size is not None
        or args.max_tokens is not None
        or args.gpu_layers is not None
    )


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


def start_local_server(args: argparse.Namespace) -> subprocess.Popen[str]:
    base_dir = Path(__file__).resolve().parent
    server_script = base_dir / "server.py"
    llama_server = resolve_path(base_dir, args.llama_server)
    model_path = resolve_path(base_dir, args.model)
    if not server_script.is_file():
        raise FileNotFoundError(f"server.py not found: {server_script}")
    if not llama_server.is_file():
        raise FileNotFoundError(f"llama-server not found: {llama_server}")
    if not model_path.is_file():
        raise FileNotFoundError(f"model not found: {model_path}")

    batch_size = args.batch_size if args.batch_size is not None else 512
    max_tokens = args.max_tokens if args.max_tokens is not None else 512
    gpu_layers = resolve_gpu_layers(args)
    nvidia_bin_paths = collect_nvidia_dll_bin_paths(base_dir)
    if args.device == "gpu":
        missing_cuda_dlls = find_missing_cuda_dlls(nvidia_bin_paths)
        if missing_cuda_dlls:
            raise RuntimeError(
                "CUDA DLLs are missing under TranslationServiceLlama/.venv. "
                f"Missing: {', '.join(missing_cuda_dlls)}"
            )

    cmd = [
        sys.executable,
        str(server_script),
        "--host",
        args.host,
        "--port",
        str(args.port),
        "--llama-server",
        str(llama_server),
        "--model",
        str(model_path),
        "--llama-host",
        args.llama_host,
        "--llama-port",
        str(args.llama_port),
        "--batch-size",
        str(batch_size),
        "--max-tokens",
        str(max_tokens),
        "--gpu-layers",
        str(gpu_layers),
    ]

    print(
        "Starting local server "
        f"(device={args.device or 'default'}, batch_size={batch_size}, "
        f"max_tokens={max_tokens}, gpu_layers={gpu_layers})"
    )
    env = os.environ.copy()
    if nvidia_bin_paths:
        # WHY: Match app host behavior so llama-server can resolve CUDA runtime DLLs.
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
    start_log_pump(process.stdout, "local-server:stdout")
    start_log_pump(process.stderr, "local-server:stderr")
    return process


def start_log_pump(pipe: Optional[object], label: str) -> None:
    if pipe is None:
        return

    def pump() -> None:
        for line in pipe:
            text = line.strip()
            if text:
                print(f"[{label}] {text}")

    threading.Thread(target=pump, daemon=True).start()


def wait_health(client: translation_pb2_grpc.TranslationServiceStub, timeout_sec: float) -> translation_pb2.HealthResponse:
    deadline = time.monotonic() + max(1.0, timeout_sec)
    last_error: Optional[Exception] = None
    while time.monotonic() < deadline:
        try:
            response = client.Health(translation_pb2.HealthRequest())
            if response.ready:
                return response
        except grpc.RpcError as exc:
            last_error = exc
        time.sleep(0.2)

    if last_error is not None:
        raise RuntimeError(f"Health check timed out: {last_error}") from last_error
    raise RuntimeError("Health check timed out.")


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


def resolve_llama_model_name(models_payload: dict, fallback_model_path: str) -> str:
    data = models_payload.get("data")
    if isinstance(data, list) and data:
        first = data[0]
        if isinstance(first, dict):
            model_id = first.get("id")
            if isinstance(model_id, str) and model_id.strip():
                return model_id.strip()
    return Path(fallback_model_path).name


def extract_json_object(raw_text: str) -> Optional[str]:
    if not raw_text:
        return None

    text = raw_text.strip()
    if text.startswith("```"):
        lines = text.splitlines()
        if len(lines) >= 3:
            text = "\n".join(lines[1:-1]).strip()

    start = text.find("{")
    end = text.rfind("}")
    if start == -1 or end == -1 or end <= start:
        return None
    return text[start:end + 1]


def parse_batch_outputs(raw_text: str, source_texts: list[str]) -> list[str]:
    parsed = extract_json_object(raw_text)
    if not parsed:
        return ["" for _ in source_texts]

    try:
        payload = json.loads(parsed)
    except json.JSONDecodeError:
        return ["" for _ in source_texts]

    translations = payload.get("translations")
    if not isinstance(translations, list):
        return ["" for _ in source_texts]

    outputs = ["" for _ in source_texts]
    if len(translations) == len(source_texts):
        for i, item in enumerate(translations):
            if isinstance(item, dict):
                translated = item.get("translated_text", "")
                if isinstance(translated, str):
                    outputs[i] = translated.strip()
        return outputs

    mapped: dict[str, str] = {}
    for item in translations:
        if not isinstance(item, dict):
            continue
        source = item.get("source_text", "")
        translated = item.get("translated_text", "")
        if isinstance(source, str) and isinstance(translated, str) and source not in mapped:
            mapped[source] = translated.strip()

    return [mapped.get(text, "") for text in source_texts]


def translate_via_llama_json_batch(
    http_client: httpx.Client,
    model_name: str,
    source_lang: str,
    target_lang: str,
    max_tokens: int,
    texts: list[str],
) -> list[str]:
    user_input = json.dumps(texts, ensure_ascii=False)
    system_prompt = (
        f"Role: Game Localization Expert. Translate array from {source_lang} to {target_lang}. "
        "Return JSON only."
    )
    user_prompt = (
        "Rules:\n"
        "1. Keep exact array length and order.\n"
        "2. Keep numbers/units/symbols exactly unless obvious OCR typo.\n"
        "3. Output exactly this JSON schema: "
        '{"translations":[{"source_text":"...","translated_text":"..."}]}.\n'
        f"Input: {user_input}"
    )
    body = {
        "model": model_name,
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ],
        "temperature": 0.2,
        "top_p": 0.6,
        "top_k": 20,
        "repeat_penalty": 1.05,
        "max_tokens": max_tokens,
        "stream": False,
    }

    response = http_client.post("/v1/chat/completions", json=body)
    response.raise_for_status()
    payload = response.json()
    content = (
        payload.get("choices", [{}])[0]
        .get("message", {})
        .get("content", "")
    )
    if not isinstance(content, str):
        content = ""
    return parse_batch_outputs(content, texts)


def stop_local_server(process: subprocess.Popen[str]) -> None:
    if os.name == "nt":
        # WHY: server.py spawns llama-server as a child process; kill the full tree to avoid VRAM leak.
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


def load_requests_from_file(path: str) -> list[list[str]]:
    with open(path, "r", encoding="utf-8") as handle:
        lines = handle.read().splitlines()

    requests: list[list[str]] = []
    current: list[str] = []
    for line in lines:
        stripped = line.strip()
        if not stripped:
            if current:
                requests.append(current)
                current = []
            continue

        current.append(stripped)

    if current:
        requests.append(current)

    return requests


def build_requests(args: argparse.Namespace) -> list[list[str]]:
    if args.input:
        return load_requests_from_file(args.input)

    if args.texts:
        return [[text.strip() for text in args.texts if text and text.strip()]]

    return [[
        "Hello world.",
        "This is a llama grpc smoke test.",
    ]]


def main() -> int:
    args = parse_args()
    if has_local_start_overrides(args) and not args.auto_start_server:
        print(
            "Options --device/--batch-size/--max-tokens/--gpu-layers require --auto-start-server. "
            "Use existing server mode without these options, or add --auto-start-server."
        )
        return 1

    endpoint = f"{args.host}:{args.port}"
    requests = [chunk for chunk in build_requests(args) if chunk]
    if not requests:
        print("No request payloads found.")
        return 1

    local_server: Optional[subprocess.Popen[str]] = None
    grpc_channel: Optional[grpc.Channel] = None
    llama_http_client: Optional[httpx.Client] = None
    try:
        if should_start_local_server(args):
            local_server = start_local_server(args)

        client: Optional[translation_pb2_grpc.TranslationServiceStub] = None
        llama_model_name: Optional[str] = None
        if args.send_mode == "grpc":
            grpc_channel = grpc.insecure_channel(endpoint)
            client = translation_pb2_grpc.TranslationServiceStub(grpc_channel)
            try:
                health = wait_health(client, args.startup_timeout_sec)
            except Exception as exc:
                print(f"Health check failed: {exc}")
                return 2

            print(f"Health: ready={health.ready} message={health.message!r}")
            if not health.ready:
                return 2
        else:
            llama_base_url = f"http://{args.llama_host}:{args.llama_port}"
            try:
                models_payload = wait_llama_http_ready(llama_base_url, args.startup_timeout_sec)
            except Exception as exc:
                print(f"Llama HTTP health check failed: {exc}")
                return 2
            llama_model_name = resolve_llama_model_name(models_payload, args.model)
            llama_http_client = httpx.Client(base_url=llama_base_url, timeout=max(1.0, args.http_timeout_sec))
            print(f"Llama HTTP ready: model={llama_model_name!r} base_url={llama_base_url}")

        request_count = 0
        total_items = 0
        translated_items = 0
        total_elapsed_ms = 0
        all_outputs: list[str] = []

        for idx, texts in enumerate(requests):
            request_count += 1
            total_items += len(texts)
            started = time.monotonic()
            try:
                if args.send_mode == "grpc":
                    if client is None:
                        raise RuntimeError("gRPC client is not initialized.")
                    req = translation_pb2.TranslateRequest(
                        texts=texts,
                        source_lang=args.source_lang,
                        target_lang=args.target_lang,
                    )
                    res = client.Translate(req)
                    outputs = list(res.translations)
                    grpc_latency_ms = res.latency_ms
                else:
                    if llama_http_client is None or llama_model_name is None:
                        raise RuntimeError("Llama HTTP client is not initialized.")
                    max_tokens = args.max_tokens if args.max_tokens is not None else 4096
                    outputs = translate_via_llama_json_batch(
                        llama_http_client,
                        llama_model_name,
                        args.source_lang,
                        args.target_lang,
                        max_tokens,
                        texts,
                    )
                    grpc_latency_ms = -1
            except grpc.RpcError as exc:
                print(f"[request {idx}] RPC failed: code={exc.code()} detail={exc.details()!r}")
                if not args.continue_on_error:
                    return 3
                continue
            except Exception as exc:
                print(f"[request {idx}] Translate failed: {exc}")
                if not args.continue_on_error:
                    return 3
                continue

            elapsed_ms = int((time.monotonic() - started) * 1000)
            total_elapsed_ms += elapsed_ms
            all_outputs.extend(outputs)
            translated_items += sum(1 for t in outputs if t.strip())

            print(
                f"[request {idx}] mode={args.send_mode} texts={len(texts)} outputs={len(outputs)} "
                f"grpc_latency_ms={grpc_latency_ms} client_elapsed_ms={elapsed_ms}"
            )
            for i, src in enumerate(texts):
                out = outputs[i] if i < len(outputs) else ""
                print(f"  [{idx}:{i}] src={src!r}")
                print(f"  [{idx}:{i}] out={out!r}")

        empty_outputs = sum(1 for t in all_outputs if not t.strip())
        print("---")
        print(f"Requests: {request_count}")
        print(f"Input items: {total_items}")
        print(f"Translated items (non-empty): {translated_items}")
        print(f"Empty outputs: {empty_outputs}")
        print(f"Total client elapsed: {total_elapsed_ms} ms")
        print(f"LOGLEVEL={os.getenv('LOGLEVEL', '')!r}")
        return 0
    finally:
        if llama_http_client is not None:
            llama_http_client.close()
        if grpc_channel is not None:
            grpc_channel.close()
        if local_server is not None:
            stop_local_server(local_server)


if __name__ == "__main__":
    raise SystemExit(main())
