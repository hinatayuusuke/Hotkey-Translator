import argparse
import json
import sys
import traceback
from typing import Any, Iterable, List, Optional

from PIL import Image
import torch
from transformers import AutoModelForCausalLM, AutoProcessor


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Florence-2 OCR bridge")
    parser.add_argument("--image", required=True, help="Path to PNG image")
    parser.add_argument("--model", required=True, help="Model name or HF repo")
    parser.add_argument("--device", default="cuda", help="Device: cuda or cpu")
    parser.add_argument("--model-dir", default=None, help="Optional local model directory")
    return parser.parse_args()


def to_xywh(box: Iterable[float]) -> Optional[List[float]]:
    values = list(box)
    if len(values) < 4:
        return None
    if len(values) >= 8:
        xs = values[0::2]
        ys = values[1::2]
        return [min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys)]

    x1, y1, x2, y2 = values[:4]
    w = x2 - x1
    h = y2 - y1
    if w >= 0 and h >= 0:
        return [x1, y1, w, h]
    return [x1, y1, x2, y2]


def normalize_texts(value: Any) -> List[str]:
    if value is None:
        return []
    if isinstance(value, str):
        return [value]
    if isinstance(value, list):
        return [str(item) for item in value]
    return [str(value)]


def normalize_boxes(value: Any) -> List[List[float]]:
    if value is None:
        return []
    if isinstance(value, list):
        if value and isinstance(value[0], (list, tuple)):
            return [list(item) for item in value]
        if value and isinstance(value[0], (int, float)):
            return [list(value)]
    return []


def extract_lines(parsed: Any) -> List[dict]:
    if parsed is None:
        return []

    if isinstance(parsed, dict):
        texts = normalize_texts(parsed.get("text") or parsed.get("texts") or parsed.get("labels"))
        boxes = normalize_boxes(
            parsed.get("bboxes")
            or parsed.get("boxes")
            or parsed.get("bbox")
            or parsed.get("quad_boxes")
            or parsed.get("quadrilateral_boxes")
        )
        if texts and boxes:
            return build_lines(texts, boxes)

        for value in parsed.values():
            lines = extract_lines(value)
            if lines:
                return lines

    if isinstance(parsed, list):
        if parsed and isinstance(parsed[0], dict):
            lines = []
            for item in parsed:
                text = item.get("text") or item.get("label") or item.get("value")
                box = item.get("box") or item.get("bbox") or item.get("boxes")
                if text is None or box is None:
                    continue
                xywh = to_xywh(box)
                if xywh is None:
                    continue
                lines.append({"text": str(text), "box": xywh, "confidence": 1.0})
            if lines:
                return lines

    return []


def build_lines(texts: List[str], boxes: List[List[float]]) -> List[dict]:
    lines = []
    count = min(len(texts), len(boxes))
    for i in range(count):
        xywh = to_xywh(boxes[i])
        if xywh is None:
            continue
        lines.append({"text": texts[i], "box": xywh, "confidence": 1.0})
    return lines


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    args = parse_args()
    model_path = args.model_dir or args.model

    if not args.device:
        args.device = "cuda"

    # SECURITY: Florence-2 relies on remote code; use a trusted model source.
    dtype = torch.float16 if args.device.startswith("cuda") else torch.float32

    processor = AutoProcessor.from_pretrained(
        model_path,
        trust_remote_code=True,
        force_download=False,
    )

    model = AutoModelForCausalLM.from_pretrained(
        model_path,
        dtype=dtype,  # torch_dtype ではなく dtype（警告回避）
        trust_remote_code=True,
        force_download=False,
        attn_implementation="eager",
    )

    model.to(args.device)
    model.eval()

    image = Image.open(args.image).convert("RGB")
    task_prompt = "<OCR_WITH_REGION>"

    # 1) まず processor が pixel_values を作れる形で呼ぶ
    inputs = processor(text=task_prompt, images=image, return_tensors="pt", padding=True)

    # 2) まとめて device / dtype に乗せる（model card と同じ流儀）
    inputs = inputs.to(args.device, dtype)

    # 3) ここで pixel_values が None になっていないかチェック（原因切り分けが一気に楽になります）
    if inputs.get("pixel_values", None) is None:
        raise RuntimeError(
            "processor returned pixel_values=None. "
            "AutoProcessor が Florence2Processor を正しく読めていない/画像前処理が失敗している可能性があります。"
        )

    # NOTE: Debug logs to diagnose NoneType shape errors during generation.
    print(f"[DEBUG] processor={type(processor).__name__}", file=sys.stderr)
    print(f"[DEBUG] model={type(model).__name__}", file=sys.stderr)
    print(f"[DEBUG] device={args.device} dtype={dtype}", file=sys.stderr)
    print(f"[DEBUG] input keys={list(inputs.keys())}", file=sys.stderr)
    print(f"[DEBUG] input_ids shape={tuple(inputs['input_ids'].shape)} dtype={inputs['input_ids'].dtype}", file=sys.stderr)
    print(f"[DEBUG] attention_mask shape={tuple(inputs['attention_mask'].shape)} dtype={inputs['attention_mask'].dtype}", file=sys.stderr)
    pixel_values = inputs.get("pixel_values")
    print(f"[DEBUG] pixel_values type={type(pixel_values).__name__}", file=sys.stderr)
    if pixel_values is not None:
        print(f"[DEBUG] pixel_values shape={tuple(pixel_values.shape)} dtype={pixel_values.dtype}", file=sys.stderr)
    print(f"[DEBUG] attn_implementation={getattr(model.config, '_attn_implementation', None)}", file=sys.stderr)

    try:
        with torch.no_grad():
            generated_ids = model.generate(
                input_ids=inputs["input_ids"],
                pixel_values=inputs["pixel_values"],
                max_new_tokens=1024,
                num_beams=3,
                do_sample=False,
                # NOTE: Disable cache to avoid None past_key_values in Florence-2 generation.
                use_cache=False,
            )
    except Exception:
        print("[DEBUG] model.generate failed; traceback follows.", file=sys.stderr)
        traceback.print_exc()
        raise

    generated_text = processor.batch_decode(generated_ids, skip_special_tokens=False)[0]
    parsed = processor.post_process_generation(generated_text, task=task_prompt, image_size=image.size)
    lines = extract_lines(parsed)

    print(json.dumps({"lines": lines}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"Florence-2 OCR failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
