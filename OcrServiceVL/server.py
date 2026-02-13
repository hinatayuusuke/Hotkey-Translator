import argparse
import logging
import os
import subprocess
import sys
import time
from collections import OrderedDict
from concurrent import futures
from threading import Lock

import grpc

from ocr_vl_engine import PaddleOcrVlEngine


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
        device: str,
        pipeline_version: str,
        max_pixels: int | None,
        layout_threshold: float | None,
        max_new_tokens: int | None,
        merge_layout_blocks: bool | None,
        use_ocr_for_image_block: bool | None,
        use_layout_detection: bool | None,
        enable_hpi: bool | None,
        use_tensorrt: bool | None,
        precision: str | None,
        max_engines: int = 1,
        ttl_seconds: int = 1800,
    ):
        self._device = device
        self._pipeline_version = pipeline_version
        self._max_pixels = max_pixels
        self._layout_threshold = layout_threshold
        self._max_new_tokens = max_new_tokens
        self._merge_layout_blocks = merge_layout_blocks
        self._use_ocr_for_image_block = use_ocr_for_image_block
        self._use_layout_detection = use_layout_detection
        self._enable_hpi = enable_hpi
        self._use_tensorrt = use_tensorrt
        self._precision = precision
        # WHY: prevent unbounded memory usage when switching language/model combos.
        self._max_engines = max(1, max_engines)
        self._ttl_seconds = max(60, ttl_seconds)
        self._engines: OrderedDict[tuple[str], tuple[PaddleOcrVlEngine, float]] = OrderedDict()
        self._lock = Lock()

    def _evict_expired(self, now: float) -> None:
        if not self._engines:
            return
        expired_keys = [
            key for key, (_, last_used) in self._engines.items() if now - last_used > self._ttl_seconds
        ]
        for key in expired_keys:
            entry = self._engines.pop(key, None)
            if entry is not None:
                entry[0].close()
            logging.info("Evicted PaddleOCR-VL engine (TTL): %s", key)

    def _evict_lru(self) -> None:
        while len(self._engines) > self._max_engines:
            key, (engine, _) = self._engines.popitem(last=False)
            engine.close()
            logging.info("Evicted PaddleOCR-VL engine (LRU): %s", key)

    def get(self, language: str | None) -> PaddleOcrVlEngine:
        lang = (language or "en").strip() or "en"
        key = (lang,)
        now = time.monotonic()
        with self._lock:
            self._evict_expired(now)
            if key in self._engines:
                engine, _ = self._engines[key]
                self._engines[key] = (engine, now)
                self._engines.move_to_end(key)
                return engine

            engine = PaddleOcrVlEngine(
                device=self._device,
                pipeline_version=self._pipeline_version,
                max_pixels=self._max_pixels,
                layout_threshold=self._layout_threshold,
                max_new_tokens=self._max_new_tokens,
                merge_layout_blocks=self._merge_layout_blocks,
                use_ocr_for_image_block=self._use_ocr_for_image_block,
                use_layout_detection=self._use_layout_detection,
                enable_hpi=self._enable_hpi,
                use_tensorrt=self._use_tensorrt,
                precision=self._precision,
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
            engine = self._engines.get(request.language)
            payload = engine.recognize(request.image)
            return ocr_pb2.OcrResponse(json=payload)
        except Exception as exc:
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(exc))
            return ocr_pb2.OcrResponse()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=50052)
    parser.add_argument("--device", default="gpu:0")
    parser.add_argument(
        "--pipeline-version",
        default="v1.5",
        choices=["v1", "v1.5"],
    )
    parser.add_argument("--max-pixels", type=int, default=None)
    parser.add_argument("--layout-threshold", type=float, default=None)
    parser.add_argument("--max-new-tokens", type=int, default=None)
    parser.add_argument("--merge-layout-blocks", action=argparse.BooleanOptionalAction, default=None)
    parser.add_argument("--use-ocr-for-image-block", action=argparse.BooleanOptionalAction, default=None)
    parser.add_argument("--use-layout-detection", action=argparse.BooleanOptionalAction, default=None)
    parser.add_argument("--enable-hpi", action=argparse.BooleanOptionalAction, default=None)
    parser.add_argument("--use-tensorrt", action=argparse.BooleanOptionalAction, default=None)
    parser.add_argument(
        "--precision",
        choices=["fp32", "fp16"],
        default=None,
    )
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    logging.info("Initializing PaddleOCR-VL engine pool...")
    engine_pool = EnginePool(
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
        max_engines=1,
        ttl_seconds=1800,
    )
    logging.info("PaddleOCR-VL EnginePool: max=%s ttl=%ss", 1, 1800)

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    ocr_pb2_grpc.add_OcrServiceServicer_to_server(OcrService(engine_pool), server)
    server.add_insecure_port(f"{args.host}:{args.port}")
    server.start()
    logging.info("PaddleOCR-VL gRPC listening on %s:%s", args.host, args.port)
    server.wait_for_termination()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
