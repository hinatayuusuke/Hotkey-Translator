import argparse
import logging
import os
import subprocess
import sys
import time
from concurrent import futures

import grpc

from translator_engine import NllbTranslator


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
    def __init__(self, engine: NllbTranslator):
        self._engine = engine
        self._ready = True

    def Health(self, request, context):
        return translation_pb2.HealthResponse(ready=self._ready, message="ready")

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
                model=os.path.basename(self._engine.model_path),
                latency_ms=elapsed,
            )
        except Exception as exc:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(exc))
            return translation_pb2.TranslateResponse()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=50061)
    parser.add_argument("--model-id", default="entai2965/nllb-200-distilled-600M-ctranslate2")
    parser.add_argument("--model-dir", default=None)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--precision", default="")
    parser.add_argument("--auto-download", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    logging.info("Initializing NLLB200 translator...")
    engine = NllbTranslator(
        model_id=args.model_id,
        model_dir=args.model_dir,
        device=args.device,
        precision=args.precision,
        auto_download=bool(args.auto_download),
    )
    logging.info("NLLB200 model ready: %s", engine.model_path)

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    translation_pb2_grpc.add_TranslationServiceServicer_to_server(TranslationService(engine), server)
    server.add_insecure_port(f"{args.host}:{args.port}")
    server.start()
    logging.info("Translation gRPC listening on %s:%s", args.host, args.port)
    server.wait_for_termination()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
