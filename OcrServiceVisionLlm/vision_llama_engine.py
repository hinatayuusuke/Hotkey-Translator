import base64
import hashlib
import io
import json
import logging
import os
import subprocess
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

import httpx
from PIL import Image
from chinese_script_postprocess import ChineseScriptPostProcessor


class VisionLlamaError(RuntimeError):
    pass


class VisionLlamaBusyError(RuntimeError):
    pass


DEFAULT_OCR_PROMPT = (
    "Extract all visible text from this image. Output plain text only. "
    "Do not translate or explain. "
    "For dialogue or subtitle text that belongs to the same text box, merge visual line breaks into one natural sentence. "
    "Keep line breaks only for menus, lists, clearly separate UI items, or distinct text boxes."
)

DEFAULT_TRANSLATE_PROMPT = (
    "Translate the following text list from {source_lang} to {target_lang}. "
    'Return JSON only with this schema: {{"t":["..."]}}. '
    "Preserve order and preserve line breaks inside each item. Do not explain."
)


def build_chat_template_kwargs(disable_thinking: bool) -> dict[str, bool] | None:
    if not disable_thinking:
        return None
    return {"enable_thinking": False}


@dataclass
class VisionLlamaServerConfig:
    llama_server_path: str
    model_path: str
    mmproj_path: str
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
    max_image_side: int
    disable_thinking: bool


@dataclass
class VisionLlamaRequestConfig:
    max_tokens: int
    temperature: float
    top_p: float
    top_k: int
    repeat_penalty: float
    http_timeout_seconds: float
    disable_thinking: bool


@dataclass(frozen=True)
class PreparedImageUpload:
    data_url: str
    input_width: int
    input_height: int
    upload_width: int
    upload_height: int
    upload_bytes: int


class VisionDiagLogger:
    def __init__(self, path: str) -> None:
        self._path = path
        self._lock = threading.Lock()
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)

    def write(self, event: str, **fields: object) -> None:
        parts = [f"ts={datetime.now(timezone.utc).astimezone().isoformat(timespec='milliseconds')}", f"event={event}"]
        for key, value in fields.items():
            parts.append(f"{key}={self._format(value)}")
        line = " ".join(parts)
        with self._lock:
            with open(self._path, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")

    @staticmethod
    def _format(value: object) -> str:
        if value is None:
            return "null"
        if isinstance(value, bool):
            return "1" if value else "0"
        if isinstance(value, float):
            return f"{value:.2f}"
        text = str(value)
        if any(ch.isspace() for ch in text) or "=" in text:
            return json.dumps(text, ensure_ascii=False)
        return text


class LlamaServerHost:
    def __init__(self, config: VisionLlamaServerConfig) -> None:
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
    def model_name(self) -> str:
        return os.path.basename(self._config.model_path)

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
        env = os.environ.copy()
        env["PATH"] = build_process_path(Path(__file__).resolve().parent, env.get("PATH", ""))
        args = [
            config.llama_server_path,
            "-m",
            config.model_path,
            "--mmproj",
            config.mmproj_path,
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

        logging.info("Starting vision llama-server: %s", " ".join(args))
        proc = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            env=env,
        )
        self._process = proc
        logging.info("vision llama-server pid=%d", proc.pid)

        def pump(pipe, label: str) -> None:
            if pipe is None:
                return
            for line in pipe:
                line = line.strip()
                if line:
                    logging.info("[vision-llama][%s] %s", label, line)

        threading.Thread(target=pump, args=(proc.stdout, "stdout"), daemon=True).start()
        threading.Thread(target=pump, args=(proc.stderr, "stderr"), daemon=True).start()

    def _wait_ready(self) -> None:
        deadline = time.time() + max(1.0, self._config.ready_timeout_ms / 1000.0)
        url = f"{self.base_url}/v1/models"
        while time.time() < deadline:
            if not self.is_running:
                raise VisionLlamaError("llama-server exited during startup.")
            try:
                response = httpx.get(url, timeout=2.0)
                if response.status_code == 200:
                    logging.info("vision llama-server ready.")
                    return
            except Exception:
                pass
            time.sleep(0.2)

        raise VisionLlamaError("llama-server did not become ready in time.")

    def _ensure_monitor(self) -> None:
        if self._monitor_thread is not None:
            return
        self._monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self._monitor_thread.start()

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

            logging.warning("vision llama-server exited (code %s).", proc.returncode)
            if not self._can_restart():
                logging.error("vision llama-server restart limit reached.")
                return

            try:
                self._start_process()
                self._wait_ready()
            except Exception as exc:
                logging.error("vision llama-server restart failed: %s", exc)
                time.sleep(0.5)

    def _can_restart(self) -> bool:
        now = time.time()
        window = max(1, self._config.restart_window_seconds)
        self._restart_history = [entry for entry in self._restart_history if now - entry <= window]
        if len(self._restart_history) >= max(1, self._config.restart_max):
            return False
        self._restart_history.append(now)
        return True


class VisionLlamaEngine:
    def __init__(self, host: LlamaServerHost, request: VisionLlamaRequestConfig, max_image_side: int) -> None:
        self._host = host
        self._request = request
        self._max_image_side = max(0, max_image_side)
        self._lock = threading.Lock()
        self._client = httpx.Client(base_url=host.base_url, timeout=request.http_timeout_seconds)
        self._diag_logger: VisionDiagLogger | None = None
        self._chinese_script_post_processor = ChineseScriptPostProcessor()

    def set_diag_log_file(self, path: str) -> None:
        self._diag_logger = VisionDiagLogger(path)

    @property
    def model_name(self) -> str:
        return self._host.model_name

    def health(self) -> tuple[bool, str]:
        if not self._host.is_running:
            return False, "vision llama-server not running"
        return True, "ready"

    def recognize(self, image_bytes: bytes, language: str) -> str:
        if not self._lock.acquire(blocking=False):
            raise VisionLlamaBusyError("Vision OCR busy")
        try:
            request_id = self._build_request_id(image_bytes)
            total_start = time.perf_counter()
            prepare_start = time.perf_counter()
            prepared = prepare_image_for_upload(image_bytes, self._max_image_side)
            prepare_ms = (time.perf_counter() - prepare_start) * 1000.0
            prompt = DEFAULT_OCR_PROMPT
            if language:
                prompt = f"{prompt} The primary OCR language hint is {resolve_language_label(language)}."
            self._write_diag(
                "ocr_begin",
                request_id=request_id,
                image_bytes_in=len(image_bytes),
                image_sha256_8=hashlib.sha256(image_bytes).hexdigest()[:8],
                image_width_in=prepared.input_width,
                image_height_in=prepared.input_height,
                image_width_upload=prepared.upload_width,
                image_height_upload=prepared.upload_height,
                upload_bytes=prepared.upload_bytes,
                max_image_side=self._max_image_side,
                language=language or "",
                max_tokens=self._request.max_tokens,
                temperature=self._request.temperature,
                top_p=self._request.top_p,
                top_k=self._request.top_k,
                repeat_penalty=self._request.repeat_penalty,
                prepare_ms=prepare_ms,
            )
            payload = self._build_payload(
                messages=[
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": prompt},
                            {"type": "image_url", "image_url": {"url": prepared.data_url}},
                        ],
                    }
                ],
                max_tokens=self._request.max_tokens,
            )
            http_start = time.perf_counter()
            response = self._client.post("/v1/chat/completions", json=payload)
            response.raise_for_status()
            http_ms = (time.perf_counter() - http_start) * 1000.0
            message = extract_message_content(response.json())
            text = message.strip()
            total_ms = (time.perf_counter() - total_start) * 1000.0
            self._write_diag(
                "ocr_done",
                request_id=request_id,
                prompt_chars=len(prompt),
                response_chars=len(text),
                response_lines=len([line for line in text.splitlines() if line.strip()]),
                http_ms=http_ms,
                total_ms=total_ms,
            )
            return text
        except Exception as exc:
            self._write_diag("ocr_failed", error=str(exc))
            raise
        finally:
            self._lock.release()

    def translate(self, texts: Iterable[str], source_lang: str, target_lang: str) -> list[str]:
        if not self._lock.acquire(blocking=False):
            raise VisionLlamaBusyError("Vision translation busy")
        try:
            normalized = [text if text is not None else "" for text in texts]
            if not normalized:
                return []

            total_start = time.perf_counter()
            request_json = json.dumps(
                [{"i": index, "s": value} for index, value in enumerate(normalized)],
                ensure_ascii=False,
            )
            request_id = self._build_request_id(request_json.encode("utf-8"))
            self._write_diag(
                "translate_begin",
                request_id=request_id,
                items=len(normalized),
                source_lang=source_lang or "",
                target_lang=target_lang or "",
                request_chars=len(request_json),
                max_tokens=self._request.max_tokens,
            )
            payload = self._build_payload(
                messages=[
                    {
                        "role": "user",
                        "content": (
                            DEFAULT_TRANSLATE_PROMPT.format(
                                source_lang=resolve_language_label(source_lang),
                                target_lang=resolve_language_label(target_lang),
                            )
                            + "\n\n"
                            + request_json
                        ),
                    }
                ],
                max_tokens=self._request.max_tokens,
                response_format={"type": "json_object"},
            )
            http_start = time.perf_counter()
            response = self._client.post("/v1/chat/completions", json=payload)
            response.raise_for_status()
            http_ms = (time.perf_counter() - http_start) * 1000.0
            message = extract_message_content(response.json())
            parsed, rescued = parse_json_object_from_text_resilient(message)
            if parsed is None:
                raise VisionLlamaError("Vision translation response is not valid JSON.")
            translations = parsed.get("t")
            if not isinstance(translations, list):
                translations = parsed.get("translations")
            if not isinstance(translations, list):
                raise VisionLlamaError("Vision translation response is missing translations list.")
            outputs = [str(item) for item in translations[: len(normalized)]]
            outputs, variant, applied_count, error_count = self._chinese_script_post_processor.postprocess_translations(
                outputs,
                target_lang,
            )
            if variant is not None:
                logging.info(
                    "Vision Chinese script postprocess: target=%s mode=%s applied=%d errors=%d",
                    target_lang,
                    variant,
                    applied_count,
                    error_count,
                )
            total_ms = (time.perf_counter() - total_start) * 1000.0
            self._write_diag(
                "translate_done",
                request_id=request_id,
                items=len(outputs),
                response_chars=sum(len(item) for item in outputs),
                http_ms=http_ms,
                total_ms=total_ms,
                parser_rescued=rescued,
                chinese_variant=variant or "",
                chinese_applied=applied_count,
                chinese_errors=error_count,
            )
            return outputs
        except Exception as exc:
            self._write_diag("translate_failed", error=str(exc))
            raise
        finally:
            self._lock.release()

    def _build_payload(self, messages: list[dict], max_tokens: int, response_format: dict | None = None) -> dict:
        payload: dict[str, object] = {
            "model": self._host.model_name,
            "messages": messages,
            "max_tokens": max_tokens,
            "temperature": self._request.temperature,
            "top_p": self._request.top_p,
            "top_k": self._request.top_k,
            "repeat_penalty": self._request.repeat_penalty,
        }
        if self._request.disable_thinking:
            payload["reasoning_budget"] = 0
            kwargs = build_chat_template_kwargs(True)
            if kwargs is not None:
                payload["chat_template_kwargs"] = kwargs
        if response_format is not None:
            payload["response_format"] = response_format
        return payload

    def _write_diag(self, event: str, **fields: object) -> None:
        logger = self._diag_logger
        if logger is None:
            return
        logger.write(event, **fields)

    @staticmethod
    def _build_request_id(seed: bytes) -> str:
        return hashlib.sha256(seed).hexdigest()[:12]


def prepare_image_for_upload(image_bytes: bytes, max_image_side: int) -> PreparedImageUpload:
    if max_image_side <= 0:
        with Image.open(io.BytesIO(image_bytes)) as image:
            width, height = image.size
        return PreparedImageUpload(
            data_url=to_data_url(image_bytes, "image/png"),
            input_width=width,
            input_height=height,
            upload_width=width,
            upload_height=height,
            upload_bytes=len(image_bytes),
        )

    with Image.open(io.BytesIO(image_bytes)) as image:
        converted = image.convert("RGB")
        width, height = converted.size
        longest_side = max(width, height)
        if longest_side > max_image_side:
            scale = max_image_side / float(longest_side)
            resized = converted.resize(
                (max(1, int(round(width * scale))), max(1, int(round(height * scale)))),
                Image.Resampling.LANCZOS,
            )
        else:
            resized = converted

        output = io.BytesIO()
        resized.save(output, format="PNG")
        upload_bytes = output.getvalue()
        return PreparedImageUpload(
            data_url=to_data_url(upload_bytes, "image/png"),
            input_width=width,
            input_height=height,
            upload_width=resized.width,
            upload_height=resized.height,
            upload_bytes=len(upload_bytes),
        )


def to_data_url(data: bytes, mime_type: str) -> str:
    encoded = base64.b64encode(data).decode("ascii")
    return f"data:{mime_type};base64,{encoded}"


def extract_message_content(payload: dict) -> str:
    choices = payload.get("choices")
    if not isinstance(choices, list) or not choices:
        raise VisionLlamaError("No choices returned by llama-server.")

    message = choices[0].get("message")
    if not isinstance(message, dict):
        raise VisionLlamaError("llama-server response is missing message.")

    content = message.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(
            item.get("text", "") for item in content if isinstance(item, dict)
        )
    raise VisionLlamaError("Unsupported llama-server content format.")


def parse_json_object_from_text_resilient(content: str) -> tuple[dict | None, bool]:
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
        repaired = repair_swapped_array_object_closer(normalized_lines)
        add_candidate(repaired, True)

    for candidate, rescued in candidates:
        parsed = parse_json_object_candidate(candidate)
        if parsed is not None:
            return parsed, rescued

    return None, False


def parse_json_object_candidate(candidate: str) -> dict | None:
    value: object = candidate
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
    while len(candidate) >= 2 and candidate.endswith("}}"):
        candidate = candidate[:-1]
    return candidate


def repair_swapped_array_object_closer(text: str) -> str:
    candidate = text.strip()
    # WHY: Vision translation sometimes ends with `..."}]` instead of `..."]}` for a string-array payload.
    if candidate.endswith("}]") and ('"t"' in candidate or '"translations"' in candidate):
        return candidate[:-2] + "]}"
    return candidate


def resolve_language_label(language: str) -> str:
    if not language:
        return "English"
    key = language.strip().lower().replace("_", "-")
    if key.startswith("ja") or key.startswith("jpn"):
        return "Japanese"
    if key.startswith("en") or key.startswith("eng"):
        return "English"
    # WHY: Keep Traditional/Simplified hints explicit so OCR and translation prompts do not collapse both variants to generic Chinese.
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


def build_process_path(base_dir: Path, current_path: str) -> str:
    site_packages = base_dir / ".venv" / "Lib" / "site-packages" / "nvidia"
    bins: list[str] = []
    for package in ("cuda_runtime", "cublas", "nvjitlink", "cudnn"):
        path = site_packages / package / "bin"
        if path.is_dir():
            bins.append(str(path))

    unique = []
    seen = set()
    for path in bins:
        lowered = path.lower()
        if lowered in seen:
            continue
        seen.add(lowered)
        unique.append(path)

    if not unique:
        return current_path

    return ";".join(unique + ([current_path] if current_path else []))
