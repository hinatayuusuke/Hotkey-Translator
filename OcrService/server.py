import argparse
import asyncio
import logging
import os
import subprocess
import sys
from concurrent import futures

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
    def __init__(self, default_language: str, device: str, model_dir: str | None, default_det_model: str):
        self._default_language = default_language
        self._device = device
        self._model_dir = model_dir
        self._default_det_model = default_det_model
        self._engines: dict[tuple[str, str], PaddleOcrEngine] = {}

    def get(self, language: str | None, det_model: str | None) -> PaddleOcrEngine:
        lang = (language or self._default_language or "japan").strip() or "japan"
        model = (det_model or self._default_det_model or "PP-OCRv5_mobile_det").strip() or "PP-OCRv5_mobile_det"
        key = (lang, model)
        if key not in self._engines:
            self._engines[key] = PaddleOcrEngine(
                language=lang,
                device=self._device,
                model_dir=self._model_dir,
                text_detection_model_name=model,
            )
        return self._engines[key]


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
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    logging.info("Initializing PaddleOCR engine pool...")
    engine_pool = EnginePool(args.lang, args.device, args.model, args.det_model)

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    ocr_pb2_grpc.add_OcrServiceServicer_to_server(OcrService(engine_pool), server)
    server.add_insecure_port(f"{args.host}:{args.port}")
    server.start()
    logging.info("PaddleOCR gRPC listening on %s:%s", args.host, args.port)
    server.wait_for_termination()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
