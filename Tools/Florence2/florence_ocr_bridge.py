import argparse
import json
import sys
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
    inputs = processor(text=task_prompt, images=image, return_tensors="pt")
    for key, value in inputs.items():
        if torch.is_tensor(value):
            inputs[key] = value.to(args.device)

    with torch.no_grad():
        generated_ids = model.generate(**inputs, max_new_tokens=1024)

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
