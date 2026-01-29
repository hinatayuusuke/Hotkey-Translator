# ocr_engine.py
import io
import json
import os
os.environ["CUDA_VISIBLE_DEVICES"] = "-1"  # GPUを無効化（最強）
from typing import Optional, Any, Literal

import numpy as np
import paddle
from PIL import Image


class PaddleOcrEngine:
    """
    PaddleOCR 3.x / PP-OCRv5 対応（CPU版 / bytes入力）。

    - 入力: 画像bytes（PNG/JPEG等のエンコード済みバイト列）
    - 推論: PaddleOCR.predict(numpy.ndarray) を使用
    - 返却: {"lines":[{"text","box":[l,t,w,h],"confidence"}...]} のJSON文字列

    重要:
    - CPU実行では、基本は mobile_det + mobile_rec を推奨（速度優先）
    - server_* は精度寄りだがCPUだと遅くなりやすい
    """

    def __init__(
        self,
        language: str = "japan",
        device: Literal["cpu"] = "cpu",
        model_dir: Optional[str] = None,
        disable_model_source_check: bool = True,
        use_textline_orientation: bool = True,
        ocr_version: str = "PP-OCRv5",
        profile: Literal["mobile", "server"] = "mobile",
        cpu_threads: int = 4,
    ):
        try:
            from paddleocr import PaddleOCR
        except Exception as exc:
            raise RuntimeError("PaddleOCR is not installed.") from exc

        # モデルホスト疎通チェックが遅い/失敗する環境向け（必要なら False に）
        if disable_model_source_check:
            os.environ.setdefault("DISABLE_MODEL_SOURCE_CHECK", "True")

        # CPU固定
        dev = (device or "").lower().strip()
        if dev != "cpu":
            raise ValueError("このCPU版エンジンは device='cpu' のみ対応です。")
        paddle.device.set_device("cpu")

        # CPUスレッド（前後処理も含めて効くことが多い）
        # ※環境により MKL/OPENBLAS の挙動が違うので、まずは 2〜8 で調整推奨
        t = max(1, int(cpu_threads))
        os.environ.setdefault("OMP_NUM_THREADS", str(t))
        os.environ.setdefault("MKL_NUM_THREADS", str(t))

        # モデル選択（CPUでは基本 mobile 推奨）
        if profile == "server":
            det_name = "PP-OCRv5_server_det"
            rec_name = "PP-OCRv5_server_rec"
        else:
            det_name = "PP-OCRv5_mobile_det"
            rec_name = "PP-OCRv5_mobile_rec"

        kwargs: dict[str, Any] = {
            "lang": language,
            "ocr_version": ocr_version,  # "PP-OCRv5"
            "use_textline_orientation": bool(use_textline_orientation),
            # 明示的にモデルを固定（将来のデフォルト変更に備える）
            "text_detection_model_name": det_name,
            "text_recognition_model_name": rec_name,
        }

        # model_dir を使う場合の注意:
        # PaddleOCR 3.x は det/rec それぞれの *_model_dir を指定できますが、
        # ここでは互換維持のため det_model_dir のみ受けています（必要なら拡張してください）。
        if model_dir is not None:
            kwargs["det_model_dir"] = model_dir

        self._engine = PaddleOCR(**kwargs)

    def recognize(self, image_bytes: bytes) -> str:
        # 念のためCPU固定チェック
        cur = str(paddle.device.get_device())
        if cur != "cpu":
            raise RuntimeError(f"CPU実行が外れました（現在のdevice={cur}）。処理を中断します。")

        # bytes -> RGB ndarray (H, W, 3) uint8
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        img_np = np.array(image)

        # PaddleOCR 3.x の推奨: predict()
        result = self._engine.predict(img_np)
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

        代表的な返り値:
          result = [ { 'rec_texts': [...], 'rec_scores': [...], 'rec_polys': [...], ... } ]

        ここから lines を組み立てる。
        """
        # まず「ページ dict」を取り出す
        page = None
        if isinstance(result, list) and result:
            page = result[0]
        elif isinstance(result, dict):
            page = result

        if not isinstance(page, dict):
            return []

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

            lines.append(
                {
                    "text": str(text) if text is not None else "",
                    "box": box,
                    "confidence": float(score) if score is not None else 1.0,
                }
            )

        return lines
