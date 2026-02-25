import argparse
import json
from pathlib import Path


def _validate_payload(payload: dict) -> None:
    if not isinstance(payload, dict):
        raise AssertionError("Payload must be a dict.")
    lines = payload.get("lines")
    if not isinstance(lines, list):
        raise AssertionError("Payload.lines must be a list.")

    for idx, line in enumerate(lines):
        if not isinstance(line, dict):
            raise AssertionError(f"lines[{idx}] must be an object.")
        if "text" not in line or not isinstance(line["text"], str):
            raise AssertionError(f"lines[{idx}].text must be a string.")
        box = line.get("box")
        if not isinstance(box, list) or len(box) != 4:
            raise AssertionError(f"lines[{idx}].box must be [x,y,w,h].")
        for j, value in enumerate(box):
            if not isinstance(value, (int, float)):
                raise AssertionError(f"lines[{idx}].box[{j}] must be numeric.")
        conf = line.get("confidence")
        if not isinstance(conf, (int, float)):
            raise AssertionError(f"lines[{idx}].confidence must be numeric.")


def _main() -> int:
    parser = argparse.ArgumentParser(description="Single-image NDLOCR engine test.")
    parser.add_argument("--image", required=True, help="Input image path.")
    parser.add_argument("--model-dir", default=None, help="Model directory (default: OcrServiceNDL/model).")
    parser.add_argument("--config-dir", default=None, help="Config directory (default: OcrServiceNDL/config).")
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--json-out", default=None, help="Optional output JSON file path.")
    parser.add_argument("--expect-min-lines", type=int, default=0, help="Fail if detected lines are below this value.")
    parser.add_argument("--print-limit", type=int, default=20, help="Max lines to print.")
    args = parser.parse_args()

    image_path = Path(args.image)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    from ndl_ocr_engine import NdlOcrLiteEngine

    engine = NdlOcrLiteEngine(
        model_dir=args.model_dir,
        config_dir=args.config_dir,
        device=args.device,
    )

    payload_json = engine.recognize(image_path.read_bytes())
    payload = json.loads(payload_json)
    _validate_payload(payload)

    lines = payload["lines"]
    if len(lines) < max(0, args.expect_min_lines):
        raise AssertionError(f"Detected lines={len(lines)} < expect_min_lines={args.expect_min_lines}")

    print(f"[OK] lines={len(lines)}")
    for i, line in enumerate(lines[: max(0, args.print_limit)]):
        text = line.get("text", "").replace("\n", "\\n")
        box = line.get("box", [0, 0, 0, 0])
        confidence = float(line.get("confidence", 0.0))
        det = float(line.get("detection_confidence", 0.0))
        rec = float(line.get("recognition_confidence", 0.0))
        print(
            f"[{i}] conf={confidence:.4f} det={det:.4f} rec={rec:.4f} "
            f"box=({box[0]:.1f},{box[1]:.1f},{box[2]:.1f},{box[3]:.1f}) text={text}"
        )

    if args.json_out:
        out_path = Path(args.json_out)
    else:
        out_path = image_path.with_suffix(".ndlocr.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[OK] wrote json: {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
