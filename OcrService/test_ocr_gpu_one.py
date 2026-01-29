# test_ocr_gpu_one.py
import sys
import json
from pathlib import Path

import paddle
from ocr_engine import PaddleOcrEngine


def main():
    print("paddle:", paddle.__version__)
    print("compiled_with_cuda:", paddle.device.is_compiled_with_cuda())
    print("paddle device:", paddle.device.get_device())

    # --- 引数で画像ファイル指定 ---
    if len(sys.argv) >= 2:
        img_path = Path(sys.argv[1])
    else:
        img_path = Path("test.png")

    if not img_path.exists():
        raise FileNotFoundError(f"画像が見つかりません: {img_path.resolve()}")

    image_bytes = img_path.read_bytes()
    print("image:", img_path.name, "bytes:", len(image_bytes))

    # GPU必須（CPU禁止）
    engine = PaddleOcrEngine(language="japan", device="gpu:0", model_dir=None, disable_model_source_check=True)
    out = engine.recognize(image_bytes)

    payload = json.loads(out)
    lines = payload.get("lines", [])
    print("lines:", len(lines))
    for i, ln in enumerate(lines):
        print(f"[{i}] text={ln.get('text')!r} conf={ln.get('confidence'):.3f} box={ln.get('box')}")

    print("final paddle device:", paddle.device.get_device())


if __name__ == "__main__":
    main()
