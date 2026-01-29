# ocr_engine.py
import io
import json
import os
from typing import Optional, Any

import numpy as np
import paddle
from PIL import Image


class PaddleOcrEngine:
    """
    PaddleOCR 3.x / PP-OCRv5 対応（Windows + GPU必須 / bytes入力）。
    - 入力: 画像bytes（PNG/JPEG等のエンコード済みバイト列）
    - 推論: PaddleOCR.predict(numpy.ndarray) を使用
    - 返却: {"lines":[{"text","box":[l,t,w,h],"confidence"}...]} のJSON文字列
    """

    def __init__(
        self,
        language: str = "japan",
        device: str = "gpu:0",
        model_dir: Optional[str] = None,
        disable_model_source_check: bool = True,
        use_textline_orientation: bool = True,
        ocr_version: str = "PP-OCRv5",
    ):
        try:
            from paddleocr import PaddleOCR
        except Exception as exc:
            raise RuntimeError("PaddleOCR is not installed.") from exc

        # モデルホスト疎通チェックが遅い/失敗する環境向け（必要なら False に）
        if disable_model_source_check:
            os.environ.setdefault("DISABLE_MODEL_SOURCE_CHECK", "True")

        # CPU禁止（重い処理がCPUに落ちるのを防ぐ）
        dev = (device or "").lower().strip()
        if dev == "cpu":
            raise ValueError("CPU実行は禁止です。GPUを指定してください（例: 'gpu:0'）。")

        if not paddle.device.is_compiled_with_cuda():
            raise RuntimeError("CUDA非対応のPaddleです。paddlepaddle-gpu（CUDA対応）を入れてください。")

        # GPUが見えるか（環境差があるので try で守る）
        try:
            gpu_count = paddle.device.cuda.device_count()
        except Exception:
            gpu_count = 1  # compiled_with_cuda=True なら通常1以上の前提で進める

        if gpu_count <= 0:
            raise RuntimeError("CUDA対応GPUが検出できません（ドライバ/CUDA/PATHを確認）。")

        # デバイス固定
        if dev in ("gpu", "cuda", "gpu:0"):
            dev = "gpu:0"
        paddle.device.set_device(dev)

        # 前処理がCPUで走ることはあるので、過剰並列だけ抑える（ゼロにはできません）
        os.environ.setdefault("OMP_NUM_THREADS", "1")
        os.environ.setdefault("MKL_NUM_THREADS", "1")

        kwargs: dict[str, Any] = {
            "lang": language,
            "ocr_version": ocr_version,  # "PP-OCRv5"
            "use_textline_orientation": bool(use_textline_orientation),
            "text_detection_model_name": "PP-OCRv5_mobile_det",
            "text_recognition_model_name": "PP-OCRv5_server_rec",
        }

        # model_dir を使う場合は PaddleOCR 3.x の仕様に沿ってください。
        # ここでは互換維持のため det_model_dir のみ指定可能にしています。
        if model_dir is not None:
            kwargs["det_model_dir"] = model_dir

        self._engine = PaddleOCR(**kwargs)

    def recognize(self, image_bytes: bytes) -> str:
        # 念のためGPU固定チェック
        cur = str(paddle.device.get_device())
        if not cur.startswith("gpu"):
            raise RuntimeError(f"GPU実行が外れました（現在のdevice={cur}）。処理を中断します。")

        # bytes -> RGB ndarray (H, W, 3) uint8
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        img_np = np.array(image)

        # PaddleOCR 3.x の推奨: predict()
        result = self._engine.predict(img_np)
        #print("=== RAW OCR RESULT START ===")
        #print(repr(result))
        #print("=== RAW OCR RESULT END ===")
        lines = self._parse_v5_predict_result(result)
        return json.dumps({"lines": lines}, ensure_ascii=False)

    @staticmethod
    def _poly_to_ltrbwh(poly) -> list[float]:
        """
        poly: shape (4,2) 相当（list/np.ndarray）
        return: [left, top, width, height]
        """
        xs = [float(pt[0]) for pt in poly]
        ys = [float(pt[1]) for pt in poly]
        left, top, right, bottom = min(xs), min(ys), max(xs), max(ys)
        return [left, top, right - left, bottom - top]

    def _parse_v5_predict_result(self, result: Any) -> list[dict]:
        """
        PaddleOCR 3.x / PP-OCRv5 の predict() 返り値に対応。

        代表的な返り値（今回あなたが貼った形式）:
          result = [ { 'rec_texts': [...], 'rec_scores': [...], 'rec_polys': [...], ... } ]

        ここから lines を組み立てる。
        """
        # まず「ページ dict」を取り出す
        page = None
        if isinstance(result, list) and result:
            # 1枚推論なら先頭がページdictのことが多い
            page = result[0]
        elif isinstance(result, dict):
            page = result

        if not isinstance(page, dict):
            return []

        texts = page.get("rec_texts") or []
        scores = page.get("rec_scores") or []
        polys = page.get("rec_polys") or page.get("dt_polys") or []

        # polys が numpy 配列 / list 混在でも扱えるようにする
        n = min(len(texts), len(scores), len(polys))
        if n <= 0:
            return []

        lines: list[dict] = []
        for i in range(n):
            text = texts[i]
            score = scores[i]
            poly = polys[i]

            # poly が np.ndarray の場合はそのまま iterable
            try:
                box = self._poly_to_ltrbwh(poly)
            except Exception:
                # 想定外の形ならスキップ
                continue

            lines.append(
                {
                    "text": str(text) if text is not None else "",
                    "box": box,
                    "confidence": float(score) if score is not None else 1.0,
                }
            )

        return lines
