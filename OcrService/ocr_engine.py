# ocr_engine.py
import io
import json
import os
from typing import Optional, Any

import numpy as np
from PIL import Image, ImageOps


def _add_cuda_dll_paths() -> None:
    if os.name != "nt":
        return

    candidates: list[str] = []
    try:
        import importlib.util

        spec = importlib.util.find_spec("nvidia")
        if spec and spec.submodule_search_locations:
            base_dir = list(spec.submodule_search_locations)[0]
            for sub in ("cublas", "cuda_runtime", "cudnn", "nvjitlink"):
                candidates.append(os.path.join(base_dir, sub, "bin"))
    except Exception:
        return

    if not candidates:
        return

    seen: set[str] = set()
    path_entries: list[str] = []
    for path in candidates:
        if not path or path in seen or not os.path.isdir(path):
            continue
        seen.add(path)
        path_entries.append(path)
        try:
            # WHY: Ensure CUDA DLLs inside the venv are discoverable by Windows loader.
            os.add_dll_directory(path)
        except (AttributeError, OSError):
            pass

    if path_entries:
        existing = os.environ.get("PATH", "")
        os.environ["PATH"] = os.pathsep.join(path_entries + [existing] if existing else path_entries)


_add_cuda_dll_paths()
import paddle


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
        text_detection_model_name: str = "PP-OCRv5_mobile_det",
        text_recognition_model_name: str = "PP-OCRv5_server_rec",
        text_det_thresh: float = 0.5,
        text_det_box_thresh: float = 0.68,
        text_det_unclip_ratio: float = 1.3,
        text_rec_score_thresh: float = 0.58,
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

        source_key = (language or "").strip().lower().replace("_", "-")
        if source_key.startswith("ja"):
            lang_for_engine = "japan"
        elif source_key in ("chinese-cht", "zh-tw", "zh-hk", "zh-mo", "zh-hant") or source_key.startswith("zh-hant-"):
            lang_for_engine = "chinese_cht"
        elif source_key == "ch" or source_key.startswith("zh"):
            lang_for_engine = "ch"
        elif source_key in ("cyrillic", "ru") or source_key.startswith("ru-"):
            lang_for_engine = "cyrillic"
        else:
            lang_for_engine = "en"
        rec_model_name = (text_recognition_model_name or "PP-OCRv5_server_rec").strip() or "PP-OCRv5_server_rec"

        kwargs: dict[str, Any] = {
            "lang": lang_for_engine,
            "ocr_version": ocr_version,  # "PP-OCRv5"
            "use_textline_orientation": bool(use_textline_orientation),
            "text_detection_model_name": text_detection_model_name,
            "text_recognition_model_name": rec_model_name,
            # NOTE: v3系の推奨パラメータ名。旧 det_db_* は非推奨。
            # WHY: Raise thresholds slightly by default to reduce low-confidence boxes that tend to drift visually.
            "text_det_thresh": text_det_thresh,
            "text_det_box_thresh": text_det_box_thresh,
            "text_det_unclip_ratio": text_det_unclip_ratio,
            "text_rec_score_thresh": text_rec_score_thresh,
            "use_doc_orientation_classify": True,
            "use_doc_unwarping": True,
            "use_textline_orientation": True,

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
        original_width, original_height = image.size
        padding_px = 20
        if padding_px > 0:
            padding_color = self._estimate_padding_color(image)
            image = ImageOps.expand(image, border=padding_px, fill=padding_color)
        img_np = np.array(image)

        # PaddleOCR 3.x の推奨: predict()
        result = self._engine.predict(img_np)
        #print("=== RAW OCR RESULT START ===")
        #print(repr(result))
        #print("=== RAW OCR RESULT END ===")
        lines = self._parse_v5_predict_result(result)
        # NOTE: Temporarily disable unpadding to verify whether OCR output is already in original-image coordinates.
        if padding_px > 0 and lines:
            lines = self._restore_boxes_after_padding(lines, padding_px, original_width, original_height)
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

    @staticmethod
    def _restore_boxes_after_padding(
        lines: list[dict],
        padding_px: int,
        original_width: int,
        original_height: int,
    ) -> list[dict]:
        restored: list[dict] = []
        for line in lines:
            box = line.get("box")
            if not isinstance(box, list) or len(box) < 4:
                continue

            left = float(box[0]) - padding_px
            top = float(box[1]) - padding_px
            right = float(box[0] + box[2]) - padding_px
            bottom = float(box[1] + box[3]) - padding_px

            # WHY: Clamp all edges in original-image coordinates, then recompute width/height
            # to avoid oversized boxes near image boundaries after unpadding.
            left = min(max(0.0, left), float(original_width))
            top = min(max(0.0, top), float(original_height))
            right = min(max(0.0, right), float(original_width))
            bottom = min(max(0.0, bottom), float(original_height))
            width = max(0.0, right - left)
            height = max(0.0, bottom - top)
            if width <= 0.0 or height <= 0.0:
                continue

            line["box"] = [left, top, width, height]
            restored.append(line)

        return restored

    @staticmethod
    def _estimate_padding_color(image: Image.Image) -> tuple[int, int, int]:
        arr = np.asarray(image)
        if arr.ndim != 3 or arr.shape[2] < 3:
            return (0, 0, 0)
        height, width = arr.shape[0], arr.shape[1]
        edge = max(1, min(4, width // 2, height // 2))
        top = arr[:edge, :, :3]
        bottom = arr[height - edge : height, :, :3]
        left = arr[:, :edge, :3]
        right = arr[:, width - edge : width, :3]
        samples = np.concatenate(
            [
                top.reshape(-1, 3),
                bottom.reshape(-1, 3),
                left.reshape(-1, 3),
                right.reshape(-1, 3),
            ],
            axis=0,
        )
        if samples.size == 0:
            return (0, 0, 0)
        mean = samples.mean(axis=0)
        return (int(mean[0]), int(mean[1]), int(mean[2]))
