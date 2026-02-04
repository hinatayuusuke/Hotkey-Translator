import argparse
import logging
import os
import time

from translator_engine import NllbTranslator


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="NLLB200 translation engine smoke test")
    parser.add_argument(
        "--model-id",
        default="entai2965/nllb-200-distilled-600M-ctranslate2",
        help="Hugging Face model id",
    )
    parser.add_argument("--model-dir", default=None, help="Local model directory")
    parser.add_argument("--device", default="cpu", choices=["cpu", "gpu"], help="Device for CTranslate2")
    parser.add_argument("--precision", default="", help="Override precision (optional)")
    parser.add_argument("--auto-download", action="store_true", help="Download model if missing")
    parser.add_argument("--source-lang", default="eng_Latn", help="NLLB source language code")
    parser.add_argument("--target-lang", default="jpn_Jpan", help="NLLB target language code")
    parser.add_argument("--input", default=None, help="Text file to translate (blank lines separate entries)")
    parser.add_argument(
        "--text",
        action="append",
        dest="texts",
        help="Text to translate (can be specified multiple times)",
    )
    return parser.parse_args()


def load_texts_from_file(path: str) -> list[str]:
    with open(path, "r", encoding="utf-8") as handle:
        lines = handle.read().splitlines()

    blocks: list[str] = []
    current: list[str] = []
    for line in lines:
        if line.strip() == "":
            if current:
                blocks.append(" ".join(current).strip())
                current = []
            continue

        current.append(line.strip())

    if current:
        blocks.append(" ".join(current).strip())

    return [block for block in blocks if block]


def main() -> int:
    args = parse_args()
    logging.basicConfig(
        level=os.getenv("LOGLEVEL", "INFO").upper(),
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    )
    if args.input:
        texts = load_texts_from_file(args.input)
    else:
        texts = args.texts or [
            "Hello world!",
            "This is a batch translation test.",
        ]

    translator = NllbTranslator(
        model_id=args.model_id,
        model_dir=args.model_dir,
        device=args.device,
        precision=args.precision,
        auto_download=args.auto_download,
    )

    start = time.monotonic()
    outputs = translator.translate(texts, args.source_lang, args.target_lang)
    elapsed_ms = int((time.monotonic() - start) * 1000)

    print(f"Translated {len(texts)} items in {elapsed_ms} ms.")
    for i, (src, out) in enumerate(zip(texts, outputs)):
        print(f"[{i}] src={src!r}")
        print(f"[{i}] out={out!r}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
