import argparse
import asyncio
import logging
import os
import subprocess
import sys
import time
from collections import OrderedDict
from concurrent import futures
from threading import Lock

import grpc

from ocr_engine import PaddleOcrEngine


def ensure_proto() -> None:
    if os.path.exists(os.path.join(os.path.dirname(__file__), "ocr_pb2.py")):
        return

    cmd = [
        sys.executable,
        "-m",
        "grpc_tools.protoc",
        "-I",
        os.path.dirname(__file__),
        "--python_out",
        os.path.dirname(__file__),
        "--grpc_python_out",
        os.path.dirname(__file__),
        os.path.join(os.path.dirname(__file__), "ocr.proto"),
    ]
    subprocess.check_call(cmd)


ensure_proto()

import ocr_pb2
import ocr_pb2_grpc


class EnginePool:
    def __init__(
        self,
        default_language: str,
        device: str,
        model_dir: str | None,
        default_det_model: str,
        default_rec_model: str,
        max_engines: int = 2,
        ttl_seconds: int = 1800,
    ):
        self._default_language = default_language
        self._device = device
        self._model_dir = model_dir
        self._default_det_model = default_det_model
        self._default_rec_model = default_rec_model
        # WHY: prevent unbounded memory usage when switching language/model combos.
        self._max_engines = max(1, max_engines)
        self._ttl_seconds = max(60, ttl_seconds)
        self._engines: OrderedDict[tuple[str, str, str], tuple[PaddleOcrEngine, float]] = OrderedDict()
        self._lock = Lock()

    def _evict_expired(self, now: float) -> None:
        if not self._engines:
            return
        expired_keys = [
            key for key, (_, last_used) in self._engines.items() if now - last_used > self._ttl_seconds
        ]
        for key in expired_keys:
            self._engines.pop(key, None)
            logging.info("Evicted PaddleOCR engine (TTL): %s", key)

    def _evict_lru(self) -> None:
        while len(self._engines) > self._max_engines:
            key, _ = self._engines.popitem(last=False)
            logging.info("Evicted PaddleOCR engine (LRU): %s", key)

    def get(self, language: str | None, det_model: str | None) -> PaddleOcrEngine:
        lang = (language or self._default_language or "japan").strip() or "japan"
        model = (det_model or self._default_det_model or "PP-OCRv5_mobile_det").strip() or "PP-OCRv5_mobile_det"
        rec_model = (self._default_rec_model or "PP-OCRv5_server_rec").strip() or "PP-OCRv5_server_rec"
        key = (lang, model, rec_model)
        now = time.monotonic()
        with self._lock:
            self._evict_expired(now)
            if key in self._engines:
                engine, _ = self._engines[key]
                self._engines[key] = (engine, now)
                self._engines.move_to_end(key)
                return engine

            engine = PaddleOcrEngine(
                language=lang,
                device=self._device,
                model_dir=self._model_dir,
                text_detection_model_name=model,
                text_recognition_model_name=rec_model,
            )
            self._engines[key] = (engine, now)
            self._engines.move_to_end(key)
            self._evict_lru()
            return engine


class OcrService(ocr_pb2_grpc.OcrServiceServicer):
    def __init__(self, engines: EnginePool):
        self._engines = engines
        self._ready = True

    def Health(self, request, context):
        return ocr_pb2.HealthResponse(ready=self._ready, message="ready")

    def Recognize(self, request, context):
        if not request.image:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("image is empty")
            return ocr_pb2.OcrResponse()

        try:
            engine = self._engines.get(request.language, request.text_detection_model_name)
            payload = engine.recognize(request.image)
            return ocr_pb2.OcrResponse(json=payload)
        except Exception as exc:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(exc))
            return ocr_pb2.OcrResponse()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=50051)
    parser.add_argument("--lang", default="japan")
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--model", default=None)
    parser.add_argument("--det-model", default="PP-OCRv5_mobile_det")
    parser.add_argument("--rec-model", default="PP-OCRv5_server_rec")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    logging.info("Initializing PaddleOCR engine pool...")
    engine_pool = EnginePool(
        args.lang,
        args.device,
        args.model,
        args.det_model,
        args.rec_model,
        max_engines=2,
        ttl_seconds=1800,
    )
    logging.info("PaddleOCR EnginePool: max=%s ttl=%ss", 2, 1800)

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    ocr_pb2_grpc.add_OcrServiceServicer_to_server(OcrService(engine_pool), server)
    server.add_insecure_port(f"{args.host}:{args.port}")
    server.start()
    logging.info("PaddleOCR gRPC listening on %s:%s", args.host, args.port)
    server.wait_for_termination()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
