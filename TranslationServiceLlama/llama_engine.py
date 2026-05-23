import json
import logging
import math
import os
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Any, Iterable, List

import httpx
from chinese_script_postprocess import ChineseScriptPostProcessor


class LlamaServerError(RuntimeError):
    pass


class LlamaBusyError(RuntimeError):
    pass


@dataclass
class LlamaServerConfig:
    llama_server_path: str
    model_path: str
    host: str
    port: int
    context_size: int
    gpu_layers: int
    threads: int
    parallel: int
    batch_size: int
    ready_timeout_ms: int
    restart_max: int
    restart_window_seconds: int
    disable_thinking: bool


@dataclass
class LlamaRequestConfig:
    max_tokens: int
    temperature: float
    top_p: float
    top_k: int
    repeat_penalty: float
    http_timeout_seconds: float
    disable_thinking: bool


DEFAULT_SYSTEM_PROMPT = "Translate the following segment into {target}. Output translation only."
STRUCTURE_SPLIT_MAX_ITEM_CHARS = 400
STRUCTURE_SPLIT_TOTAL_CHARS = 800


def build_chat_template_kwargs(disable_thinking: bool) -> dict[str, bool] | None:
    if not disable_thinking:
        return None
    # WHY: Qwen-family templates may emit internal reasoning unless the template flag is disabled explicitly.
    return {"enable_thinking": False}


class LlamaServerHost:
    def __init__(self, config: LlamaServerConfig) -> None:
        self._config = config
        self._lock = threading.Lock()
        self._process: subprocess.Popen | None = None
        self._stopping = False
        self._restart_history: list[float] = []
        self._monitor_thread: threading.Thread | None = None

    @property
    def base_url(self) -> str:
        return f"http://{self._config.host}:{self._config.port}"

    @property
    def is_running(self) -> bool:
        proc = self._process
        return proc is not None and proc.poll() is None

    def start(self) -> None:
        with self._lock:
            if self.is_running:
                return
            self._stopping = False

        self._start_process()
        self._wait_ready()
        self._ensure_monitor()

    def stop(self) -> None:
        with self._lock:
            self._stopping = True
            proc = self._process

        if proc is None:
            return

        try:
            proc.kill()
        except Exception:
            pass

    def _start_process(self) -> None:
        config = self._config
        args = [
            config.llama_server_path,
            "-m",
            config.model_path,
            "--host",
            config.host,
            "--port",
            str(config.port),
            "--ctx-size",
            str(config.context_size),
            "--n-gpu-layers",
            str(config.gpu_layers),
            "--threads",
            str(config.threads),
            "--parallel",
            str(config.parallel),
            "--batch-size",
            str(config.batch_size),
        ]
        if config.disable_thinking:
            # WHY: Startup-side disable keeps the server in terse translation mode even before the first request arrives.
            args.extend(
                [
                    "--reasoning-budget",
                    "0",
                    "--reasoning-format",
                    "none",
                    "--chat-template-kwargs",
                    json.dumps(build_chat_template_kwargs(True), ensure_ascii=True, separators=(",", ":")),
                ]
            )

        logging.info("Starting llama-server: %s", " ".join(args))
        proc = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        self._process = proc
        logging.info("llama-server pid=%d", proc.pid)

        def pump(pipe, label: str) -> None:
            if pipe is None:
                return
            for line in pipe:
                line = line.strip()
                if line:
                    logging.info("[llama-server][%s] %s", label, line)

        threading.Thread(target=pump, args=(proc.stdout, "stdout"), daemon=True).start()
        threading.Thread(target=pump, args=(proc.stderr, "stderr"), daemon=True).start()

    def _wait_ready(self) -> None:
        deadline = time.time() + max(1.0, self._config.ready_timeout_ms / 1000.0)
        url = f"{self.base_url}/v1/models"
        while time.time() < deadline:
            if not self.is_running:
                raise LlamaServerError("llama-server exited during startup.")
            try:
                resp = httpx.get(url, timeout=2.0)
                if resp.status_code == 200:
                    logging.info("llama-server ready.")
                    return
            except Exception:
                pass
            time.sleep(0.2)

        raise LlamaServerError("llama-server did not become ready in time.")

    def _ensure_monitor(self) -> None:
        if self._monitor_thread is not None:
            return
        thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self._monitor_thread = thread
        thread.start()

    def _monitor_loop(self) -> None:
        while True:
            proc = self._process
            if proc is None:
                time.sleep(0.5)
                continue

            proc.wait()
            with self._lock:
                if self._stopping:
                    return

            logging.warning("llama-server exited (code %s).", proc.returncode)
            if not self._can_restart():
                logging.error("llama-server restart limit reached.")
                return

            try:
                self._start_process()
                self._wait_ready()
            except Exception as exc:
                logging.error("llama-server restart failed: %s", exc)
                time.sleep(0.5)

    def _can_restart(self) -> bool:
        now = time.time()
        window = max(1, self._config.restart_window_seconds)
        self._restart_history = [t for t in self._restart_history if now - t <= window]
        if len(self._restart_history) >= max(1, self._config.restart_max):
            return False
        self._restart_history.append(now)
        return True


class LlamaTranslator:
    def __init__(self, host: LlamaServerHost, request: LlamaRequestConfig) -> None:
        self._host = host
        self._request = request
        self._lock = threading.Lock()
        self._chinese_script_post_processor = ChineseScriptPostProcessor()
        self._client = httpx.Client(
            base_url=host.base_url,
            timeout=request.http_timeout_seconds,
        )

    @property
    def model_name(self) -> str:
        return os.path.basename(self._host._config.model_path)

    def health(self) -> tuple[bool, str]:
        if not self._host.is_running:
            return False, "llama-server not running"
        return True, "ready"

    def translate(self, texts: Iterable[str], source_lang: str, target_lang: str) -> List[str]:
        if not self._lock.acquire(blocking=False):
            raise LlamaBusyError("Translator busy")
        try:
            sentences = [text if text is not None else "" for text in texts]
            if not sentences:
                return []

            stats = {"http_calls": 0, "splits": 0}
            outputs = self._translate_with_adaptive_split(sentences, source_lang, target_lang, depth=0, stats=stats)
            outputs, variant, applied_count, error_count = self._chinese_script_post_processor.postprocess_translations(
                outputs,
                target_lang,
            )
            if variant is not None:
                logging.info(
                    "Chinese script postprocess: target=%s mode=%s applied=%d errors=%d",
                    target_lang,
                    variant,
                    applied_count,
                    error_count,
                )
            logging.info(
                "Llama batch translation done: items=%d http_calls=%d splits=%d",
                len(sentences),
                stats["http_calls"],
                stats["splits"],
            )
            return outputs
        finally:
            self._lock.release()

    def _translate_with_adaptive_split(
        self,
        texts: List[str],
        source_lang: str,
        target_lang: str,
        depth: int,
        stats: dict[str, int],
    ) -> List[str]:
        if not texts:
            return []

        split_reason = self._resolve_split_reason(texts, source_lang, target_lang)
        if split_reason and len(texts) > 1:
            stats["splits"] += 1
            logging.info(
                "Split batch before request: items=%d depth=%d reason=%s",
                len(texts),
                depth,
                split_reason,
            )
            left, right = split_texts_evenly(texts)
            return (
                self._translate_with_adaptive_split(left, source_lang, target_lang, depth + 1, stats)
                + self._translate_with_adaptive_split(right, source_lang, target_lang, depth + 1, stats)
            )

        try:
            return self._translate_batch_once(texts, source_lang, target_lang, stats)
        except Exception as exc:
            if len(texts) <= 1:
                # WHY: Preserve positional contract even when a single request fails.
                logging.warning(
                    "Single-item translation failed; returning empty output: depth=%d reason=%s",
                    depth,
                    exc,
                )
                return [""]

            stats["splits"] += 1
            logging.warning(
                "split_fallback: items=%d depth=%d reason=%s",
                len(texts),
                depth,
                exc,
            )
            left, right = split_texts_evenly(texts)
            return (
                self._translate_with_adaptive_split(left, source_lang, target_lang, depth + 1, stats)
                + self._translate_with_adaptive_split(right, source_lang, target_lang, depth + 1, stats)
            )

    def _translate_batch_once(
        self,
        texts: List[str],
        source_lang: str,
        target_lang: str,
        stats: dict[str, int],
    ) -> List[str]:
        if len(texts) == 1:
            return self._translate_single_plain(texts[0], target_lang, stats)

        system_prompt = build_batch_system_prompt(source_lang, target_lang)
        user_prompt = build_batch_user_prompt(texts)
        base_payload = {
            "model": os.path.basename(self._host._config.model_path),
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "temperature": self._request.temperature,
            "top_p": self._request.top_p,
            "top_k": self._request.top_k,
            "repeat_penalty": self._request.repeat_penalty,
            "max_tokens": self._request.max_tokens,
            "stream": False,
        }
        if self._request.disable_thinking:
            base_payload["reasoning_budget"] = 0
            base_payload["reasoning_format"] = "none"
            base_payload["chat_template_kwargs"] = build_chat_template_kwargs(True)
        schema_error: Exception | None = None
        schema_payload = {
            **base_payload,
            # WHY: Keep the first attempt strict/short to reduce breakage with small models.
            "response_format": build_short_json_schema_response_format(),
        }
        try:
            content = self._post_chat_completion(schema_payload, stats)
            parsed, rescued = parse_batch_translation_content_resilient(content, len(texts))
            if parsed is not None:
                if rescued:
                    logging.info("parser_rescue_success: stage=schema items=%d", len(texts))
                logging.info("schema_success: items=%d rescued=%s", len(texts), rescued)
                return parsed
            schema_error = ValueError("schema response is not valid JSON")
        except Exception as exc:
            schema_error = exc
            logging.warning("schema request failed; retry with grammar: items=%d reason=%s", len(texts), exc)

        grammar_payload = {
            **base_payload,
            "grammar": build_short_json_grammar(),
        }
        content = self._post_chat_completion(grammar_payload, stats)
        parsed, rescued = parse_batch_translation_content_resilient(content, len(texts))
        if parsed is not None:
            if rescued:
                logging.info("parser_rescue_success: stage=grammar items=%d", len(texts))
            logging.info("grammar_fallback_success: items=%d rescued=%s", len(texts), rescued)
            return parsed

        raise ValueError(f"batch response is not valid JSON after grammar fallback: {schema_error}")

    def _translate_single_plain(self, text: str, target_lang: str, stats: dict[str, int]) -> List[str]:
        payload = {
            "model": os.path.basename(self._host._config.model_path),
            "messages": [
                {"role": "system", "content": build_system_prompt(target_lang)},
                {"role": "user", "content": text},
            ],
            "temperature": self._request.temperature,
            "top_p": self._request.top_p,
            "top_k": self._request.top_k,
            "repeat_penalty": self._request.repeat_penalty,
            "max_tokens": self._request.max_tokens,
            "stream": False,
        }
        if self._request.disable_thinking:
            payload["reasoning_budget"] = 0
            payload["reasoning_format"] = "none"
            payload["chat_template_kwargs"] = build_chat_template_kwargs(True)
        content = self._post_chat_completion(payload, stats)
        logging.info("plain_single_success: chars=%d", len(content))
        return [content]

    def _post_chat_completion(self, payload: dict[str, Any], stats: dict[str, int]) -> str:
        stats["http_calls"] += 1
        response = self._client.post("/v1/chat/completions", json=payload)
        response.raise_for_status()
        data = response.json()
        return extract_response_text(data)

    def _resolve_split_reason(self, texts: List[str], source_lang: str, target_lang: str) -> str | None:
        if len(texts) <= 1:
            return None

        max_item_chars = max(len(text) for text in texts)
        total_chars = sum(len(text) for text in texts)
        if max_item_chars >= STRUCTURE_SPLIT_MAX_ITEM_CHARS or total_chars >= STRUCTURE_SPLIT_TOTAL_CHARS:
            # WHY: Long multi-item JSON prompts make small local models more likely to break the response schema.
            return (
                f"long_multi_block(max_item_chars={max_item_chars}, total_chars={total_chars}, "
                f"thresholds={STRUCTURE_SPLIT_MAX_ITEM_CHARS}/{STRUCTURE_SPLIT_TOTAL_CHARS})"
            )

        system_prompt = build_batch_system_prompt(source_lang, target_lang)
        user_prompt = build_batch_user_prompt(texts)
        estimated_prompt_tokens = estimate_token_count(system_prompt) + estimate_token_count(user_prompt)

        input_budget = resolve_input_token_budget(self._host._config.context_size, self._request.max_tokens)
        if estimated_prompt_tokens > input_budget:
            return f"estimated_prompt_tokens({estimated_prompt_tokens}) > input_budget({input_budget})"

        batch_budget = max(128, self._host._config.batch_size)
        if estimated_prompt_tokens > batch_budget:
            return f"estimated_prompt_tokens({estimated_prompt_tokens}) > batch_size({batch_budget})"

        return None


def build_system_prompt(target_lang: str) -> str:
    target_label = resolve_language_label(target_lang)
    # WHY: Fixed template avoids UI/user drift while keeping target language alignment.
    return DEFAULT_SYSTEM_PROMPT.format(target=target_label)


def build_batch_system_prompt(source_lang: str, target_lang: str) -> str:
    source_label = resolve_language_label(source_lang)
    target_label = resolve_language_label(target_lang)
    return (
        "You are a translation function. "
        f"Translate from {source_label} to {target_label}. "
        'Output MUST be JSON with schema {"t":[{"i":0,"x":"..."}]}. '
        "Use only keys t, i, x. "
        "Keep array length and order exactly. "
        "Keep numbers/units/symbols unless an obvious OCR typo."
    )


def build_batch_user_prompt(texts: List[str]) -> str:
    indexed_texts = [{"i": i, "s": text} for i, text in enumerate(texts)]
    input_json = json.dumps({"items": indexed_texts}, ensure_ascii=False, separators=(",", ":"))
    return (
        "Translate each items[i].s to target language.\n"
        "Return one-line JSON only. Do not translate keys.\n"
        f"Input JSON: {input_json}"
    )


def build_batch_json_schema_response_format() -> dict[str, Any]:
    # COMPAT: Keep old function name because callers/tests may still import this symbol.
    return build_short_json_schema_response_format()


def build_short_json_schema_response_format() -> dict[str, Any]:
    return {
        "type": "json_schema",
        "json_schema": {
            "name": "translations_short_v1",
            "strict": True,
            "schema": {
                "type": "object",
                "properties": {
                    "t": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "i": {"type": "integer"},
                                "x": {"type": "string"},
                            },
                            "required": ["i", "x"],
                            "additionalProperties": False,
                        },
                    },
                },
                "required": ["t"],
                "additionalProperties": False,
            },
        },
    }


def build_short_json_grammar() -> str:
    # WHY: Keep grammar minimal so generation remains feasible for small models.
    return (
        'root ::= ws obj ws\n'
        'obj ::= "{" ws "\\"t\\"" ws ":" ws "[" ws items? ws "]" ws "}"\n'
        "items ::= item (ws \",\" ws item)*\n"
        'item ::= "{" ws "\\"i\\"" ws ":" ws int ws "," ws "\\"x\\"" ws ":" ws str ws "}"\n'
        "int ::= \"-\"? digit digit*\n"
        'str ::= "\\"" char* "\\""\n'
        'char ::= [^"\\\\\\x00-\\x1F] | "\\\\" (["\\\\/bfnrt] | "u" hex hex hex hex)\n'
        "digit ::= [0-9]\n"
        "hex ::= [0-9a-fA-F]\n"
        "ws ::= [ \\t\\n\\r]*\n"
    )


def resolve_input_token_budget(context_size: int, max_tokens: int) -> int:
    context_limit = max(512, context_size)
    output_budget = max(64, max_tokens)
    reserve = max(64, min(512, output_budget // 2))
    # WHY: Keep headroom for chat wrapper and sampling artifacts to reduce overflow retries.
    return max(128, context_limit - output_budget - reserve)


def estimate_token_count(text: str) -> int:
    if not text:
        return 0
    # NOTE: llama.cpp tokenizer is not available here; use a conservative char-based estimate.
    return max(1, math.ceil(len(text) / 3.5))


def split_texts_evenly(texts: List[str]) -> tuple[List[str], List[str]]:
    if len(texts) <= 1:
        return texts, []
    pivot = max(1, len(texts) // 2)
    return texts[:pivot], texts[pivot:]


def parse_batch_translation_content(content: str, expected_count: int) -> List[str] | None:
    parsed, _ = parse_batch_translation_content_resilient(content, expected_count)
    return parsed


def parse_batch_translation_content_resilient(content: str, expected_count: int) -> tuple[List[str] | None, bool]:
    payload, rescued = parse_json_object_from_text_resilient(content)
    if payload is None:
        return None, False

    translations = payload.get("t")
    if not isinstance(translations, list):
        translations = payload.get("translations")
    if not isinstance(translations, list):
        return None, rescued

    outputs = ["" for _ in range(expected_count)]
    index_hits = 0
    for item in translations:
        if not isinstance(item, dict):
            continue
        index = item.get("i")
        if not isinstance(index, int):
            index = item.get("index")

        translated_text = item.get("x")
        if not isinstance(translated_text, str):
            translated_text = item.get("translated_text")

        if not isinstance(index, int) or not isinstance(translated_text, str):
            continue
        if index < 0 or index >= expected_count:
            continue
        outputs[index] = translated_text.strip()
        index_hits += 1

    if index_hits > 0:
        return outputs, rescued

    if len(translations) != expected_count:
        return None, rescued

    for i, item in enumerate(translations):
        if not isinstance(item, dict):
            return None, rescued

        translated_text = item.get("x")
        if not isinstance(translated_text, str):
            translated_text = item.get("translated_text")
        if not isinstance(translated_text, str):
            return None, rescued
        outputs[i] = translated_text.strip()

    return outputs, rescued


def parse_json_object_from_text(content: str) -> dict[str, Any] | None:
    payload, _ = parse_json_object_from_text_resilient(content)
    return payload


def parse_json_object_from_text_resilient(content: str) -> tuple[dict[str, Any] | None, bool]:
    if not content:
        return None, False

    cleaned = strip_markdown_code_fence(content.strip())
    if not cleaned:
        return None, False

    candidates: list[tuple[str, bool]] = []
    seen: set[str] = set()

    def add_candidate(text: str, rescued: bool) -> None:
        candidate = text.strip()
        if not candidate or candidate in seen:
            return
        seen.add(candidate)
        candidates.append((candidate, rescued))

    add_candidate(cleaned, False)

    bounded = extract_first_balanced_json_object(cleaned)
    if bounded:
        add_candidate(bounded, True)

    start = cleaned.find("{")
    end = cleaned.rfind("}")
    if start >= 0 and end > start:
        add_candidate(cleaned[start : end + 1], True)

    for candidate, rescued in list(candidates):
        normalized_lines = candidate.replace("\r\n", "\n").replace("\r", "\n")
        add_candidate(normalized_lines, rescued or normalized_lines != candidate)
        escaped_line_breaks = normalized_lines.replace("\\r\\n", "\\n").replace("\\r", "\\n")
        add_candidate(escaped_line_breaks, rescued or escaped_line_breaks != candidate)
        trimmed = trim_trailing_extra_closing_braces(normalized_lines)
        add_candidate(trimmed, rescued or trimmed != candidate)

    for candidate, rescued in candidates:
        parsed = parse_json_object_candidate(candidate)
        if parsed is not None:
            return parsed, rescued

    return None, False


def parse_json_object_candidate(candidate: str) -> dict[str, Any] | None:
    value: Any = candidate
    for _ in range(3):
        if not isinstance(value, str):
            return None
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            return None
        if isinstance(value, dict):
            return value
        if isinstance(value, str):
            value = value.strip()
            continue
        return None
    return None


def strip_markdown_code_fence(text: str) -> str:
    if not text.startswith("```"):
        return text
    lines = text.splitlines()
    if len(lines) >= 3:
        return "\n".join(lines[1:-1]).strip()
    return text


def extract_first_balanced_json_object(text: str) -> str | None:
    start = text.find("{")
    if start < 0:
        return None

    depth = 0
    in_string = False
    escape = False
    for idx in range(start, len(text)):
        ch = text[idx]
        if in_string:
            if escape:
                escape = False
                continue
            if ch == "\\":
                escape = True
                continue
            if ch == '"':
                in_string = False
            continue

        if ch == '"':
            in_string = True
            continue
        if ch == "{":
            depth += 1
            continue
        if ch == "}":
            depth -= 1
            if depth == 0:
                return text[start : idx + 1]
    return None


def trim_trailing_extra_closing_braces(text: str) -> str:
    candidate = text.strip()
    while candidate.endswith("}") and candidate.count("}") > candidate.count("{"):
        candidate = candidate[:-1].rstrip()
    return candidate


def resolve_language_label(language: str) -> str:
    if not language:
        return "English"
    key = language.strip().lower().replace("_", "-")
    if key.startswith("ja") or key.startswith("jpn"):
        return "Japanese"
    if key.startswith("en") or key.startswith("eng"):
        return "English"
    # WHY: Normalize Chinese variants first; lower() was making old zh-Hans/zh-Hant checks unreachable.
    if key.startswith("zh-hant") or key.startswith("zh-tw") or key.startswith("zh-hk"):
        return "Traditional Chinese"
    if key.startswith("zh-hans") or key.startswith("zh-cn"):
        return "Simplified Chinese"
    if key.startswith("zh") or key.startswith("zho") or key.startswith("chi"):
        return "Chinese"
    if key.startswith("ko") or key.startswith("kor"):
        return "Korean"
    if key.startswith("ru") or key.startswith("rus"):
        return "Russian"
    if "-" in key:
        return key.split("-")[0]
    return language


def extract_response_text(data: dict) -> str:
    try:
        choices = data.get("choices") or []
        if not choices:
            return ""
        message = choices[0].get("message") or {}
        content = message.get("content")
        if content is None:
            return ""
        return str(content).strip()
    except Exception:
        return ""
