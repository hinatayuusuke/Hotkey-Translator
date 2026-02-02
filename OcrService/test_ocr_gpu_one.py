# test_ocr_gpu_one.py
import json
import argparse
from pathlib import Path

import paddle
from ocr_engine import PaddleOcrEngine


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("image", nargs="?", default="test.png", help="input image path")
    parser.add_argument("--lang", default="japan", help="OCR language (e.g. japan, en)")
    parser.add_argument("--det-model", default="PP-OCRv5_mobile_det", help="text detection model name")
    parser.add_argument("--rec-model", default="PP-OCRv5_server_rec", help="text recognition model name")
    parser.add_argument("--device", default="gpu:0", help="Paddle device (gpu:0)")
    args = parser.parse_args()

    print("paddle:", paddle.__version__)
    print("compiled_with_cuda:", paddle.device.is_compiled_with_cuda())
    print("paddle device:", paddle.device.get_device())

    img_path = Path(args.image)

    if not img_path.exists():
        raise FileNotFoundError(f"画像が見つかりません: {img_path.resolve()}")

    image_bytes = img_path.read_bytes()
    print("image:", img_path.name, "bytes:", len(image_bytes))

    # GPU必須（CPU禁止）
    engine = PaddleOcrEngine(
        language=args.lang,
        device=args.device,
        model_dir=None,
        disable_model_source_check=True,
        text_detection_model_name=args.det_model,
        text_recognition_model_name=args.rec_model,
    )
    out = engine.recognize(image_bytes)

    payload = json.loads(out)
    lines = payload.get("lines", [])
    print("lines:", len(lines))
    for i, ln in enumerate(lines):
        print(f"[{i}] text={ln.get('text')!r} conf={ln.get('confidence'):.3f} box={ln.get('box')}")

    print("final paddle device:", paddle.device.get_device())


if __name__ == "__main__":
    main()
