# test_ocr_gpu_one.py
import json
import argparse
from pathlib import Path
import paddle
from ocr_engine_V4 import PaddleOcrEngine

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("image", nargs="?", default="test.png", help="input image path")
    parser.add_argument("--lang", default="japan", help="OCR language (e.g. japan, en)")
    # vvv 修正: Mobileモデルのデフォルト指定を削除（指定しない方が高精度） vvv
    parser.add_argument("--device", default="gpu:0", help="Paddle device (gpu:0)")
    args = parser.parse_args()

    print("paddle:", paddle.__version__)
    print("compiled_with_cuda:", paddle.device.is_compiled_with_cuda())
    print("paddle device:", paddle.device.get_device())

    img_path = Path(args.image)
    if not img_path.exists():
        # ファイルがない場合は例外ではなくヘルプを表示して終了する場合もありますが、ここではエラーにします
        raise FileNotFoundError(f"画像が見つかりません: {img_path.resolve()}")

    image_bytes = img_path.read_bytes()
    print("image:", img_path.name, "bytes:", len(image_bytes))

    # GPU必須（CPU禁止）
    # vvv 修正: text_detection_model_name の指定を削除 vvv
    engine = PaddleOcrEngine(
        language=args.lang,
        device=args.device,
        model_dir=None,
        disable_model_source_check=True,
        use_angle_cls=True # 明示的にTrue（デフォルトTrueにしたので省略可だが念のため）
    )
    
    out = engine.recognize(image_bytes)

    payload = json.loads(out)
    lines = payload.get("lines", [])
    print("lines:", len(lines))
    for i, ln in enumerate(lines):
        # 読みやすいようにフォーマット調整
        print(f"[{i}] text='{ln.get('text')}' conf={ln.get('confidence'):.3f} box={ln.get('box')}")

    print("final paddle device:", paddle.device.get_device())

if __name__ == "__main__":
    main()