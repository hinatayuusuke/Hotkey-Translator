# test_ocr_cpu_one.py
import os
os.environ["CUDA_VISIBLE_DEVICES"] = "-1"  # GPUを無効化（最強）
import sys
import json
from pathlib import Path

import paddle
from cpu_ocr_engine import PaddleOcrEngine


def main():
    print("paddle:", paddle.__version__)
    print("compiled_with_cuda:", paddle.device.is_compiled_with_cuda())
    print("paddle device (before):", paddle.device.get_device())

    # --- 引数で画像ファイル指定 ---
    if len(sys.argv) >= 2:
        img_path = Path(sys.argv[1])
    else:
        img_path = Path("test.png")

    if not img_path.exists():
        raise FileNotFoundError(f"画像が見つかりません: {img_path.resolve()}")

    image_bytes = img_path.read_bytes()
    print("image:", img_path.name, "bytes:", len(image_bytes))

    # CPU実行
    engine = PaddleOcrEngine(
        language="japan",
        device="cpu",
        profile="mobile",           # 速度優先なら mobile 推奨
        cpu_threads=4,              # 環境に合わせて 2〜8 で調整
        model_dir=None,
        disable_model_source_check=True,
        use_textline_orientation=True,
        ocr_version="PP-OCRv5",
    )

    out = engine.recognize(image_bytes)

    payload = json.loads(out)
    lines = payload.get("lines", [])
    print("lines:", len(lines))
    for i, ln in enumerate(lines):
        conf = float(ln.get("confidence", 0.0))
        print(f"[{i}] text={ln.get('text')!r} conf={conf:.3f} box={ln.get('box')}")

    print("paddle device (after):", paddle.device.get_device())


if __name__ == "__main__":
    main()
