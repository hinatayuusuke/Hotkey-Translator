import logging
import os
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

        model_path = resolve_model_path(model_id, model_dir, auto_download, self._hf_token)
        self._model_path = model_path

        self._tokenizer = load_nllb_tokenizer(model_path, self._hf_token)
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

        if hasattr(self._tokenizer, "src_lang"):
            self._tokenizer.src_lang = src_lang
        if hasattr(self._tokenizer, "tgt_lang"):
            self._tokenizer.tgt_lang = tgt_lang

        encoded = self._tokenizer(
            sentences,
            return_tensors=None,
            padding=True,
            truncation=True,
            max_length=512,
        )
        input_ids = encoded["input_ids"]
        tokens = [self._tokenizer.convert_ids_to_tokens(ids) for ids in input_ids]

        target_prefix = [[tgt_lang] for _ in tokens]

        results = self._translator.translate_batch(
            tokens,
            target_prefix=target_prefix,
        )

        outputs: List[str] = []
        for result in results:
            hypothesis = result.hypotheses[0]
            if hypothesis and hypothesis[0] == tgt_lang:
                hypothesis = hypothesis[1:]
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


def load_nllb_tokenizer(model_path: str, hf_token: Optional[str]):
    return _load_tokenizer(model_path, hf_token)


def _load_tokenizer(model_path: str, hf_token: Optional[str]):
    tokenizer_kwargs = {
        "use_fast": False,
        "trust_remote_code": False,
    }
    if hf_token:
        tokenizer_kwargs["token"] = hf_token
    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", message=".*incorrect regex pattern.*")
        tokenizer = AutoTokenizer.from_pretrained(model_path, **tokenizer_kwargs)
    _apply_mistral_regex_fix(tokenizer)
    return tokenizer


def _apply_mistral_regex_fix(tokenizer) -> None:
    try:
        from transformers.tokenization_utils_tokenizers import TokenizersBackend
    except Exception:
        return
    if not hasattr(tokenizer, "_tokenizer"):
        return
    patch = getattr(TokenizersBackend, "_patch_mistral_regex", None)
    if not patch:
        return
    try:
        tokenizer._tokenizer = patch(tokenizer._tokenizer, fix_mistral_regex=True)
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
