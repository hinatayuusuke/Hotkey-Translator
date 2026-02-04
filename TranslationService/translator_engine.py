import logging
import os
import re
import subprocess
import warnings
from typing import Iterable, List, Optional


def _ensure_optional_ct2_dirs() -> None:
    if os.name != "nt":
        return

    try:
        import importlib.util

        spec = importlib.util.find_spec("ctranslate2")
        if spec is None or spec.origin is None:
            return
        package_dir = os.path.dirname(spec.origin)
    except Exception:
        return
    rocm_core = os.path.abspath(os.path.join(package_dir, "..", "_rocm_sdk_core", "bin"))
    rocm_custom = os.path.abspath(os.path.join(package_dir, "..", "_rocm_sdk_libraries_custom", "bin"))
    os.makedirs(rocm_core, exist_ok=True)
    os.makedirs(rocm_custom, exist_ok=True)

_ensure_optional_ct2_dirs()
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
        # WHY: Windows without symlink support spams warnings; caching still works without symlinks.
        os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

        self._model_id = model_id
        self._model_dir = model_dir
        self._device = normalize_device(device)
        self._precision = normalize_precision(self._device, precision)
        self._hf_token = resolve_hf_token()
        if self._device == "cuda":
            self._precision = resolve_gpu_precision(self._precision)

        model_path = resolve_model_path(model_id, model_dir, auto_download, self._hf_token)
        self._model_path = model_path

        self._tokenizer = load_nllb_tokenizer(model_path, self._hf_token)
        self._translator = build_translator(model_path, self._device, self._precision)

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

        if hasattr(self._tokenizer, "src_lang"):
            self._tokenizer.src_lang = src_lang
        if hasattr(self._tokenizer, "tgt_lang"):
            self._tokenizer.tgt_lang = tgt_lang

        max_tokens = 512
        chunked_texts, boundaries = split_texts_by_token_budget(
            sentences,
            self._tokenizer,
            max_tokens,
        )
        if not chunked_texts:
            return ["" for _ in sentences]

        encoded = self._tokenizer(
            chunked_texts,
            return_tensors=None,
            padding=True,
            truncation=True,
            max_length=max_tokens,
        )
        input_ids = encoded["input_ids"]
        tokens = [self._tokenizer.convert_ids_to_tokens(ids) for ids in input_ids]

        target_prefix = [[tgt_lang] for _ in tokens]

        results = self._translator.translate_batch(
            tokens,
            target_prefix=target_prefix,
        )

        chunk_outputs: List[str] = []
        for result in results:
            hypothesis = result.hypotheses[0]
            if hypothesis and hypothesis[0] == tgt_lang:
                hypothesis = hypothesis[1:]
            output_ids = self._tokenizer.convert_tokens_to_ids(hypothesis)
            chunk_outputs.append(self._tokenizer.decode(output_ids, skip_special_tokens=True))

        outputs: List[str] = []
        for start, end in boundaries:
            outputs.append("".join(chunk_outputs[start:end]).strip())

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


def split_texts_by_token_budget(
    texts: Iterable[str],
    tokenizer,
    max_tokens: int,
) -> tuple[list[str], list[tuple[int, int]]]:
    chunked: list[str] = []
    boundaries: list[tuple[int, int]] = []
    for text in texts:
        start = len(chunked)
        chunks = split_text_by_token_budget(text, tokenizer, max_tokens)
        chunked.extend(chunks)
        boundaries.append((start, len(chunked)))
    return chunked, boundaries


def split_text_by_token_budget(text: str, tokenizer, max_tokens: int) -> list[str]:
    raw = text.strip()
    if not raw:
        return []
    segments = _split_by_delimiters(raw)
    logging.debug("Split segments (%d): %s", len(segments), segments)
    normalized: list[str] = []
    for segment in segments:
        if _token_count(segment, tokenizer) <= max_tokens:
            normalized.append(segment)
            continue
        normalized.extend(_split_long_segment(segment, tokenizer, max_tokens))

    chunks: list[str] = []
    current = ""
    for segment in normalized:
        candidate = current + segment if current else segment
        if _token_count(candidate, tokenizer) <= max_tokens:
            current = candidate
            continue

        if current:
            chunks.append(current)
        current = segment

    if current:
        chunks.append(current)
    logging.debug("Final chunks (%d): %s", len(chunks), chunks)
    return chunks


def _split_by_delimiters(text: str) -> list[str]:
    segments: list[str] = []
    current = ""
    i = 0
    length = len(text)
    hard_delims = set("。！？!?．，、；;:：")
    quote_candidates = {'"', "'", "“", "”", "‘", "’", "(", "["}

    while i < length:
        ch = text[i]
        if ch == "\n":
            j = i
            while j < length and text[j] == "\n":
                j += 1
            current += text[i:j]
            segments.append(current)
            current = ""
            i = j
            continue

        if ch in hard_delims or ch == ".":
            current += ch
            j = i + 1
            if j < length and text[j].isspace():
                while j < length and text[j].isspace():
                    j += 1
                segments.append(current)
                current = ""
                i = j
                continue
            i += 1
            continue

        current += ch
        i += 1

    if current:
        segments.append(current)
    return segments


def _split_long_segment(text: str, tokenizer, max_tokens: int) -> list[str]:
    words = re.split(r"(\s+)", text)
    chunks: list[str] = []
    current = ""
    for part in words:
        if not part:
            continue
        candidate = current + part if current else part
        if _token_count(candidate, tokenizer) <= max_tokens:
            current = candidate
            continue
        if current:
            chunks.append(current)
            current = ""
        if _token_count(part, tokenizer) <= max_tokens:
            current = part
            continue
        # WHY: Very long tokens need a hard split to honor the max token budget.
        chunks.extend(_split_by_char_budget(part, tokenizer, max_tokens))

    if current:
        chunks.append(current)
    return chunks


def _split_by_char_budget(text: str, tokenizer, max_tokens: int) -> list[str]:
    chunks: list[str] = []
    current = ""
    for ch in text:
        candidate = current + ch
        if _token_count(candidate, tokenizer) <= max_tokens:
            current = candidate
            continue
        if current:
            chunks.append(current)
        current = ch
    if current:
        chunks.append(current)
    return chunks


def _token_count(text: str, tokenizer) -> int:
    encoded = tokenizer(
        text,
        return_tensors=None,
        add_special_tokens=True,
        padding=False,
        truncation=False,
    )
    return len(encoded["input_ids"])


def resolve_gpu_precision(preferred: str) -> str:
    pref = (preferred or "").strip().lower()
    if pref.startswith("int8"):
        return pref
    if pref not in ("float16", "float32", "float"):
        pref = "float16"
    compute_cap = get_cuda_compute_capability()
    if compute_cap is not None and compute_cap < 7.0:
        # WHY: GPUs without tensor cores often fail or perform poorly with FP16.
        return "float32"
    return "float16" if pref == "float16" else "float32"


def build_translator(model_path: str, device: str, compute_type: str) -> ctranslate2.Translator:
    try:
        return ctranslate2.Translator(
            model_path,
            device=device,
            compute_type=compute_type,
        )
    except ValueError as exc:
        if device == "cuda" and compute_type == "float16":
            logging.warning("FP16 not supported; falling back to float32. (%s)", exc)
            return ctranslate2.Translator(
                model_path,
                device=device,
                compute_type="float32",
            )
        raise


def get_cuda_compute_capability() -> Optional[float]:
    try:
        result = subprocess.run(
            ["nvidia-smi", "--query-gpu=compute_cap", "--format=csv,noheader"],
            capture_output=True,
            text=True,
            timeout=2,
            check=False,
        )
    except Exception:
        return None
    if result.returncode != 0:
        return None
    output = (result.stdout or "").strip().splitlines()
    if not output:
        return None
    try:
        return float(output[0].strip())
    except ValueError:
        return None


def load_nllb_tokenizer(model_path: str, hf_token: Optional[str]):
    tokenizer = _load_tokenizer(model_path, hf_token, local_files_only=False)
    if hasattr(tokenizer, "lang_code_to_id"):
        return tokenizer

    try:
        fallback = _load_tokenizer(
            "facebook/nllb-200-distilled-600M",
            hf_token,
            local_files_only=True,
        )
        if hasattr(fallback, "lang_code_to_id"):
            return fallback
    except Exception:
        logging.warning("Base NLLB tokenizer not found locally; keeping model tokenizer.")

    return tokenizer


def _load_tokenizer(model_path: str, hf_token: Optional[str], local_files_only: bool):
    tokenizer_kwargs = {
        "use_fast": False,
        "trust_remote_code": False,
        "local_files_only": local_files_only,
    }
    if hf_token:
        tokenizer_kwargs["token"] = hf_token
    try:
        from transformers.models.nllb.tokenization_nllb import NllbTokenizer

        return NllbTokenizer.from_pretrained(model_path, **tokenizer_kwargs)
    except Exception:
        pass

    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", message=".*incorrect regex pattern.*")
        tokenizer = AutoTokenizer.from_pretrained(model_path, **tokenizer_kwargs)
    _apply_mistral_regex_fix(tokenizer, model_path, local_files_only)
    return tokenizer


def _apply_mistral_regex_fix(tokenizer, model_path: str, local_files_only: bool) -> None:
    try:
        from transformers.tokenization_utils_tokenizers import TokenizersBackend
    except Exception:
        return
    if not hasattr(tokenizer, "backend_tokenizer"):
        return
    if not os.path.isdir(model_path):
        return
    patch = getattr(TokenizersBackend, "_patch_mistral_regex", None)
    if not patch:
        return
    try:
        patched = patch(
            tokenizer.backend_tokenizer,
            model_path,
            local_files_only=local_files_only,
            is_local=True,
            init_kwargs={"fix_mistral_regex": True},
            fix_mistral_regex=True,
        )
        tokenizer.backend_tokenizer = patched
        setattr(tokenizer, "fix_mistral_regex", True)
    except Exception:
        return


def resolve_hf_token() -> Optional[str]:
    for key in ("HF_TOKEN", "HUGGINGFACE_HUB_TOKEN", "HUGGING_FACE_HUB_TOKEN"):
        value = os.getenv(key)
        if value:
            return value
    return None


def resolve_model_path(
    model_id: str,
    model_dir: Optional[str],
    auto_download: bool,
    hf_token: Optional[str],
) -> str:
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
    local_dir = os.path.join(cache_root, model_id.replace("/", "_"))
    if os.path.isdir(local_dir) and os.listdir(local_dir):
        return local_dir

    logging.info("Downloading model %s into %s", model_id, cache_root)
    return snapshot_download(
        repo_id=model_id,
        local_dir=local_dir,
        token=hf_token,
    )
