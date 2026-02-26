from __future__ import annotations

import logging
from typing import Literal, Sequence

try:
    from opencc import OpenCC
except Exception:  # pragma: no cover - import failure is handled at runtime
    OpenCC = None  # type: ignore[assignment]


_ChineseVariant = Literal["hant", "tw", "hk"]
_VARIANT_TO_CONFIG: dict[_ChineseVariant, str] = {
    "hant": "s2t",
    "tw": "s2twp",
    "hk": "s2hk",
}


def resolve_target_variant(target_lang: str) -> _ChineseVariant | None:
    if not target_lang:
        return None

    key = target_lang.strip().lower().replace("_", "-")
    if key.startswith("zh-hk"):
        return "hk"
    if key.startswith("zh-tw"):
        return "tw"
    if key.startswith("zh-hant"):
        return "hant"
    return None


class ChineseScriptPostProcessor:
    def __init__(self) -> None:
        self._logger = logging.getLogger(__name__)
        self._converters: dict[_ChineseVariant, OpenCC] = {}
        self._import_warning_logged = False

    def postprocess_translation(self, text: str, target_lang: str) -> tuple[str, _ChineseVariant | None, bool, bool]:
        outputs, variant, applied_count, error_count = self.postprocess_translations([text], target_lang)
        return outputs[0] if outputs else text, variant, applied_count > 0, error_count > 0

    def postprocess_translations(
        self,
        texts: Sequence[str],
        target_lang: str,
    ) -> tuple[list[str], _ChineseVariant | None, int, int]:
        variant = resolve_target_variant(target_lang)
        if variant is None:
            return list(texts), None, 0, 0

        converter = self._get_converter(variant)
        if converter is None:
            return list(texts), variant, 0, 0

        outputs: list[str] = []
        applied_count = 0
        error_count = 0

        for text in texts:
            if not text:
                outputs.append(text)
                continue

            try:
                converted = converter.convert(text)
            except Exception as exc:
                # WHY: Script conversion must never break translation flow; keep original text on failure.
                self._logger.warning("Chinese script conversion failed (variant=%s): %s", variant, exc)
                outputs.append(text)
                error_count += 1
                continue

            if not converted:
                outputs.append(text)
                continue

            if converted != text:
                applied_count += 1
            outputs.append(converted)

        return outputs, variant, applied_count, error_count

    def _get_converter(self, variant: _ChineseVariant) -> OpenCC | None:
        if OpenCC is None:
            if not self._import_warning_logged:
                self._logger.warning(
                    "OpenCC python library is unavailable; Chinese script postprocess is disabled. "
                    "Install opencc-python-reimplemented in TranslationServiceLlama environment."
                )
                self._import_warning_logged = True
            return None

        converter = self._converters.get(variant)
        if converter is not None:
            return converter

        config = _VARIANT_TO_CONFIG[variant]
        try:
            converter = OpenCC(config)
        except Exception as exc:
            self._logger.warning("OpenCC initialization failed (config=%s): %s", config, exc)
            return None

        self._converters[variant] = converter
        return converter
