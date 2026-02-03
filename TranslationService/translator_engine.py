import logging
import os
from typing import Iterable, List, Optional

import ctranslate2
from huggingface_hub import snapshot_download
from transformers import AutoTokenizer


class NllbTranslator:
    def __init__(
        self,
        model_id: str,
        model_dir: Optional[str],
        device: str,
        precision: str,
        auto_download: bool,
    ) -> None:
        os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")

        self._model_id = model_id
        self._model_dir = model_dir
        self._device = normalize_device(device)
        self._precision = normalize_precision(self._device, precision)

        model_path = resolve_model_path(model_id, model_dir, auto_download)
        self._model_path = model_path

        self._tokenizer = AutoTokenizer.from_pretrained(
            model_path,
            use_fast=False,
            trust_remote_code=False,
        )
        self._translator = ctranslate2.Translator(
            model_path,
            device=self._device,
            compute_type=self._precision,
        )

    @property
    def model_path(self) -> str:
        return self._model_path

    def translate(self, texts: Iterable[str], source_lang: str, target_lang: str) -> List[str]:
        sentences = [text for text in texts]
        if not sentences:
            return []

        src_lang = (source_lang or "").strip()
        tgt_lang = (target_lang or "").strip()
        if not src_lang or not tgt_lang:
            raise ValueError("source_lang and target_lang are required.")

        if not hasattr(self._tokenizer, "lang_code_to_id"):
            raise ValueError("Tokenizer does not provide lang_code_to_id.")

        lang_code_to_id = self._tokenizer.lang_code_to_id
        if src_lang not in lang_code_to_id or tgt_lang not in lang_code_to_id:
            raise ValueError(f"Unsupported language: {src_lang} -> {tgt_lang}")

        self._tokenizer.src_lang = src_lang
        encoded = self._tokenizer(
            sentences,
            return_tensors=None,
            padding=True,
            truncation=True,
            max_length=512,
        )
        input_ids = encoded["input_ids"]
        tokens = [self._tokenizer.convert_ids_to_tokens(ids) for ids in input_ids]

        tgt_id = lang_code_to_id[tgt_lang]
        tgt_token = self._tokenizer.convert_ids_to_tokens([tgt_id])
        target_prefix = [tgt_token for _ in tokens]

        results = self._translator.translate_batch(
            tokens,
            target_prefix=target_prefix,
        )

        outputs: List[str] = []
        for result in results:
            hypothesis = result.hypotheses[0]
            output_ids = self._tokenizer.convert_tokens_to_ids(hypothesis)
            outputs.append(self._tokenizer.decode(output_ids, skip_special_tokens=True))

        return outputs


def normalize_device(device: str) -> str:
    dev = (device or "").strip().lower()
    if dev in ("gpu", "cuda", "cuda:0", "gpu:0"):
        return "cuda"
    return "cpu"


def normalize_precision(device: str, precision: str) -> str:
    pref = (precision or "").strip().lower()
    if device == "cpu":
        return pref if pref else "int8"
    if pref in ("fp16", "float16", "f16"):
        return "float16"
    if pref in ("int8", "int8_float16", "int8_bfloat16"):
        return pref
    return "float16"


def resolve_model_path(model_id: str, model_dir: Optional[str], auto_download: bool) -> str:
    if model_dir:
        resolved = os.path.abspath(model_dir)
        if not os.path.isdir(resolved):
            raise FileNotFoundError(f"Model directory not found: {resolved}")
        return resolved

    if not auto_download:
        raise ValueError("Model directory is not set and auto_download is disabled.")

    base_dir = os.path.dirname(__file__)
    cache_root = os.path.join(base_dir, "models")
    os.makedirs(cache_root, exist_ok=True)
    logging.info("Downloading model %s into %s", model_id, cache_root)
    return snapshot_download(
        repo_id=model_id,
        local_dir=os.path.join(cache_root, model_id.replace("/", "_")),
        local_dir_use_symlinks=False,
    )
