import argparse
import logging
import os
import signal
import subprocess
import sys
import time
from concurrent import futures

import grpc

from llama_engine import (
    LlamaBusyError,
    LlamaRequestConfig,
    LlamaServerConfig,
    LlamaServerError,
    LlamaServerHost,
    LlamaTranslator,
)


def ensure_proto() -> None:
    base_dir = os.path.dirname(__file__)
    proto_path = os.path.join(base_dir, "translation.proto")
    pb2_path = os.path.join(base_dir, "translation_pb2.py")
    pb2_grpc_path = os.path.join(base_dir, "translation_pb2_grpc.py")
    if os.path.exists(pb2_path) and os.path.exists(pb2_grpc_path):
        try:
            proto_mtime = os.path.getmtime(proto_path)
            if os.path.getmtime(pb2_path) >= proto_mtime and os.path.getmtime(pb2_grpc_path) >= proto_mtime:
                return
        except OSError:
            pass

    cmd = [
        sys.executable,
        "-m",
        "grpc_tools.protoc",
        "-I",
        base_dir,
        "--python_out",
        base_dir,
        "--grpc_python_out",
        base_dir,
        proto_path,
    ]
    subprocess.check_call(cmd)


ensure_proto()

import translation_pb2
import translation_pb2_grpc


class TranslationService(translation_pb2_grpc.TranslationServiceServicer):
    def __init__(self, engine: LlamaTranslator):
        self._engine = engine

    def Health(self, request, context):
        ready, message = self._engine.health()
        return translation_pb2.HealthResponse(ready=ready, message=message)

    def Translate(self, request, context):
        texts = list(request.texts)
        if not texts:
            return translation_pb2.TranslateResponse()

        try:
            start = time.monotonic()
            outputs = self._engine.translate(texts, request.source_lang, request.target_lang)
            elapsed = int((time.monotonic() - start) * 1000)
            return translation_pb2.TranslateResponse(
                translations=outputs,
                model=self._engine.model_name,
                latency_ms=elapsed,
            )
        except LlamaBusyError:
            context.set_code(grpc.StatusCode.RESOURCE_EXHAUSTED)
            context.set_details("BUSY")
            return translation_pb2.TranslateResponse()
        except Exception as exc:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(exc))
            return translation_pb2.TranslateResponse()


def resolve_model_path(base_dir: str, path: str) -> str:
    if not path:
        raise ValueError("Model path is required.")
    if os.path.isabs(path):
        resolved = path
    else:
        resolved = os.path.abspath(os.path.join(base_dir, path))
    if not os.path.isfile(resolved):
        raise FileNotFoundError(f"Model file not found: {resolved}")
    return resolved


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=50071)
    parser.add_argument("--llama-server", dest="llama_server", default="llama-server")
    parser.add_argument("--llama-host", default="127.0.0.1")
    parser.add_argument("--llama-port", type=int, default=8088)
    parser.add_argument("--model", required=True)
    parser.add_argument("--ctx-size", type=int, default=4096)
    parser.add_argument("--gpu-layers", type=int, default=999)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--parallel", type=int, default=1)
    parser.add_argument("--batch-size", type=int, default=512)
    parser.add_argument("--max-tokens", type=int, default=512)
    parser.add_argument("--temperature", type=float, default=0.3)
    parser.add_argument("--top-p", type=float, default=0.6)
    parser.add_argument("--top-k", type=int, default=20)
    parser.add_argument("--repeat-penalty", type=float, default=1.05)
    parser.add_argument("--http-timeout", type=float, default=60.0)
    parser.add_argument("--ready-timeout-ms", type=int, default=60000)
    parser.add_argument("--restart-max", type=int, default=3)
    parser.add_argument("--restart-window-seconds", type=int, default=30)
    parser.add_argument(
        "--disable-thinking",
        dest="disable_thinking",
        action="store_true",
        default=True,
        help="Disable model reasoning/think mode for translation-only output.",
    )
    parser.add_argument(
        "--enable-thinking",
        dest="disable_thinking",
        action="store_false",
        help="Allow model reasoning/think mode for diagnostics.",
    )
    args = parser.parse_args()

    logging.basicConfig(
        level=os.getenv("LOGLEVEL", "INFO").upper(),
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    )

    base_dir = os.path.dirname(__file__)
    model_path = resolve_model_path(base_dir, args.model)

    server_config = LlamaServerConfig(
        llama_server_path=args.llama_server,
        model_path=model_path,
        host=args.llama_host,
        port=args.llama_port,
        context_size=args.ctx_size,
        gpu_layers=args.gpu_layers,
        threads=args.threads,
        parallel=args.parallel,
        batch_size=args.batch_size,
        ready_timeout_ms=args.ready_timeout_ms,
        restart_max=args.restart_max,
        restart_window_seconds=args.restart_window_seconds,
        disable_thinking=args.disable_thinking,
    )

    request_config = LlamaRequestConfig(
        max_tokens=args.max_tokens,
        temperature=args.temperature,
        top_p=args.top_p,
        top_k=args.top_k,
        repeat_penalty=args.repeat_penalty,
        http_timeout_seconds=args.http_timeout,
        disable_thinking=args.disable_thinking,
    )

    host = LlamaServerHost(server_config)
    logging.info("Initializing llama-server...")
    try:
        host.start()
    except LlamaServerError as exc:
        logging.error("Failed to start llama-server: %s", exc)
        raise

    engine = LlamaTranslator(host, request_config)
    logging.info("llama-server ready: %s", host.base_url)

    grpc_server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    translation_pb2_grpc.add_TranslationServiceServicer_to_server(TranslationService(engine), grpc_server)
    grpc_server.add_insecure_port(f"{args.host}:{args.port}")
    grpc_server.start()
    logging.info("Translation gRPC listening on %s:%s", args.host, args.port)

    def request_shutdown(signum, _frame):
        logging.info("Signal received (%s); stopping Translation gRPC.", signum)
        grpc_server.stop(grace=2)

    signal.signal(signal.SIGINT, request_shutdown)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, request_shutdown)

    try:
        grpc_server.wait_for_termination()
        return 0
    finally:
        # WHY: Ensure child llama-server is terminated when gRPC host process exits.
        host.stop()


if __name__ == "__main__":
    raise SystemExit(main())
