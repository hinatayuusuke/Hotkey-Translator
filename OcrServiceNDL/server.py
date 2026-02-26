from __future__ import annotations

import argparse
import logging
import os
import subprocess
import sys
import time
from collections import OrderedDict
from concurrent import futures
from pathlib import Path
from threading import Lock

import grpc

from ndl_grpc_adapter import NdlGrpcAdapter

DEFAULT_BIND_HOST = "127.0.0.1"
DEFAULT_PORT = 50053
DEFAULT_MAX_MESSAGE_BYTES = 32 * 1024 * 1024


def ensure_proto() -> None:
    base_dir = os.path.dirname(__file__)
    proto_path = os.path.join(base_dir, "ocr.proto")
    pb2_path = os.path.join(base_dir, "ocr_pb2.py")
    pb2_grpc_path = os.path.join(base_dir, "ocr_pb2_grpc.py")
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

import ocr_pb2
import ocr_pb2_grpc


class EnginePool:
    def __init__(
        self,
        *,
        model_dir: str | None,
        config_dir: str | None,
        device: str,
        det_score_threshold: float,
        det_conf_threshold: float,
        det_iou_threshold: float,
        max_engines: int = 1,
        ttl_seconds: int = 1800,
    ) -> None:
        self._model_dir = model_dir
        self._config_dir = config_dir
        self._device = device
        self._det_score_threshold = float(det_score_threshold)
        self._det_conf_threshold = float(det_conf_threshold)
        self._det_iou_threshold = float(det_iou_threshold)
        # WHY: Keep memory bounded if future request variants create additional engine keys.
        self._max_engines = max(1, int(max_engines))
        self._ttl_seconds = max(60, int(ttl_seconds))
        self._engines: OrderedDict[tuple[str], tuple[NdlGrpcAdapter, float]] = OrderedDict()
        self._lock = Lock()

    def _evict_expired(self, now: float) -> None:
        if not self._engines:
            return
        expired_keys = [key for key, (_, last_used) in self._engines.items() if now - last_used > self._ttl_seconds]
        for key in expired_keys:
            entry = self._engines.pop(key, None)
            if entry is not None:
                entry[0].close()
            logging.info("Evicted NDLOCR engine (TTL): %s", key)

    def _evict_lru(self) -> None:
        while len(self._engines) > self._max_engines:
            key, (engine, _) = self._engines.popitem(last=False)
            engine.close()
            logging.info("Evicted NDLOCR engine (LRU): %s", key)

    def get(self) -> NdlGrpcAdapter:
        key = ("default",)
        now = time.monotonic()
        with self._lock:
            self._evict_expired(now)
            if key in self._engines:
                engine, _ = self._engines[key]
                self._engines[key] = (engine, now)
                self._engines.move_to_end(key)
                return engine

            engine = NdlGrpcAdapter(
                model_dir=self._model_dir,
                config_dir=self._config_dir,
                device=self._device,
                det_score_threshold=self._det_score_threshold,
                det_conf_threshold=self._det_conf_threshold,
                det_iou_threshold=self._det_iou_threshold,
            )
            self._engines[key] = (engine, now)
            self._engines.move_to_end(key)
            self._evict_lru()
            return engine


class OcrService(ocr_pb2_grpc.OcrServiceServicer):
    def __init__(self, engines: EnginePool) -> None:
        self._engines = engines
        self._ready = True

    def Health(self, request, context):  # noqa: N802 (grpc method naming)
        return ocr_pb2.HealthResponse(ready=self._ready, message="ready")

    def Recognize(self, request, context):  # noqa: N802 (grpc method naming)
        if not request.image:
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details("image is empty")
            return ocr_pb2.OcrResponse()

        start = time.perf_counter()
        try:
            logging.info("stage=ocr_grpc host=ndl event=request_bytes bytes=%s", len(request.image))
            engine = self._engines.get()
            payload = engine.recognize(request.image)
            elapsed_ms = (time.perf_counter() - start) * 1000.0
            logging.info("stage=ocr_grpc host=ndl event=response_bytes bytes=%s", len(payload.encode("utf-8")))
            logging.info("stage=ocr_grpc host=ndl event=timing elapsedMs=%.2f", elapsed_ms)
            return ocr_pb2.OcrResponse(json=payload)
        except Exception as exc:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(exc))
            return ocr_pb2.OcrResponse()


def main() -> int:
    base_dir = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--model-dir", default=str(base_dir / "model"))
    parser.add_argument("--config-dir", default=str(base_dir / "config"))
    parser.add_argument("--det-score-threshold", type=float, default=0.2)
    parser.add_argument("--det-conf-threshold", type=float, default=0.25)
    parser.add_argument("--det-iou-threshold", type=float, default=0.2)
    parser.add_argument("--max-message-bytes", type=int, default=DEFAULT_MAX_MESSAGE_BYTES)
    parser.add_argument("--pool-max-engines", type=int, default=1)
    parser.add_argument("--pool-ttl-seconds", type=int, default=1800)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    logging.info("Initializing NDLOCR engine pool...")
    engine_pool = EnginePool(
        model_dir=args.model_dir,
        config_dir=args.config_dir,
        device=args.device,
        det_score_threshold=args.det_score_threshold,
        det_conf_threshold=args.det_conf_threshold,
        det_iou_threshold=args.det_iou_threshold,
        max_engines=args.pool_max_engines,
        ttl_seconds=args.pool_ttl_seconds,
    )
    logging.info("NDLOCR EnginePool: max=%s ttl=%ss", max(1, args.pool_max_engines), max(60, args.pool_ttl_seconds))

    # WHY: Health.ready should mean Recognize is executable right now.
    engine_pool.get()
    logging.info("NDLOCR eager init completed.")

    max_message_bytes = max(1, args.max_message_bytes)
    logging.info("NDLOCR gRPC max_message_bytes=%s", max_message_bytes)
    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=4),
        options=[
            ("grpc.max_receive_message_length", max_message_bytes),
            ("grpc.max_send_message_length", max_message_bytes),
        ],
    )
    ocr_pb2_grpc.add_OcrServiceServicer_to_server(OcrService(engine_pool), server)
    server.add_insecure_port(f"{DEFAULT_BIND_HOST}:{args.port}")
    server.start()
    logging.info("NDLOCR gRPC listening on %s:%s", DEFAULT_BIND_HOST, args.port)
    server.wait_for_termination()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
