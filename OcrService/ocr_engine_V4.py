# ocr_engine_V4.py
import io
import json
import os
from typing import Optional, Any

import numpy as np
import paddle
from PIL import Image

class PaddleOcrEngine:
    """
    PaddleOCR v3 対応エンジン (GPU版)
    - 初期化: use_gpu引数廃止、use_angle_cls=True (app.py準拠)
    - 推論: predict() メソッドを使用 (ocr_engine.py準拠)
    """

    def __init__(
        self,
        language: str = "japan",
        device: str = "gpu:0",
        model_dir: Optional[str] = None,
        disable_model_source_check: bool = True,
        use_angle_cls: bool = True,   # 方向補正 (app.py準拠)
        ocr_version: str = "PP-OCRv4",
    ):
        try:
            from paddleocr import PaddleOCR
        except Exception as exc:
            raise RuntimeError("PaddleOCR is not installed.") from exc

        if disable_model_source_check:
            os.environ.setdefault("DISABLE_MODEL_SOURCE_CHECK", "True")

        # デバイス設定
        dev = (device or "").lower().strip()
        if dev == "cpu":
            raise ValueError("CPU実行は禁止です。GPUを指定してください。")

        if not paddle.device.is_compiled_with_cuda():
            raise RuntimeError("CUDA非対応のPaddleです。")

        if dev in ("gpu", "cuda", "gpu:0"):
            dev = "gpu:0"
        paddle.device.set_device(dev)

        # パラメータ設定
        kwargs: dict[str, Any] = {
            "lang": language,
            "use_angle_cls": use_angle_cls, # 初期化時に指定することでpredict内で有効化
            "ocr_version": ocr_version,
        }

        if model_dir is not None:
            kwargs["det_model_dir"] = model_dir

        self._engine = PaddleOCR(**kwargs)

    def recognize(self, image_bytes: bytes) -> str:
        cur = str(paddle.device.get_device())
        if not cur.startswith("gpu"):
            raise RuntimeError(f"GPU実行が外れました（現在のdevice={cur}）。")

        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        img_np = np.array(image)

        # vvv 修正: ocr() ではなく predict() を使用 (ocr_engine.py準拠) vvv
        # これにより戻り値がリスト構造ではなく、辞書を含む構造になります
        result = self._engine.predict(img_np)
        
        # 提示されたファイルのロジックを使ってパース
        lines = self._parse_predict_result(result)
        
        return json.dumps({"lines": lines}, ensure_ascii=False)

    @staticmethod
    def _poly_to_ltrbwh(poly) -> list[float]:
        """ポリゴン座標から [left, top, width, height] を計算"""
        xs = [float(pt[0]) for pt in poly]
        ys = [float(pt[1]) for pt in poly]
        left, top, right, bottom = min(xs), min(ys), max(xs), max(ys)
        return [left, top, right - left, bottom - top]

    def _parse_predict_result(self, result: Any) -> list[dict]:
        """
        PaddleOCR の predict() 返り値をパースする (ocr_engine.py準拠)
        result = [ { 'rec_texts': [...], 'rec_scores': [...], 'dt_polys': [...] } ]
        """
        page = None
        if isinstance(result, list) and result:
            page = result[0]
        elif isinstance(result, dict):
            page = result

        if not isinstance(page, dict):
            return []

        # 辞書から各要素を取得
        # バージョンによってキー名が 'rec_polys' だったり 'dt_polys' だったりするため両対応
        texts = page.get("rec_texts") or []
        scores = page.get("rec_scores") or []
        polys = page.get("rec_polys") or page.get("dt_polys") or []

        n = min(len(texts), len(scores), len(polys))
        if n <= 0:
            return []

        lines: list[dict] = []
        for i in range(n):
            text = texts[i]
            score = scores[i]
            poly = polys[i]

            try:
                box = self._poly_to_ltrbwh(poly)
            except Exception:
                continue

            lines.append({
                "text": str(text) if text is not None else "",
                "box": box,
                "confidence": float(score) if score is not None else 1.0,
            })

        return lines