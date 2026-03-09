import base64
import io
import json
import logging
import os
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import httpx
from PIL import Image


class VisionLlamaError(RuntimeError):
    pass


class VisionLlamaBusyError(RuntimeError):
    pass


DEFAULT_OCR_PROMPT = (
    "Extract all visible text from this image. Output plain text only. "
    "Preserve line breaks. Do not translate."
)

DEFAULT_TRANSLATE_PROMPT = (
    "Translate the following text list from {source_lang} to {target_lang}. "
    'Return JSON only with this schema: {{"translations":["..."]}}. '
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
            data_url = prepare_image_for_upload(image_bytes, self._max_image_side)
            prompt = DEFAULT_OCR_PROMPT
            if language:
                prompt = f"{DEFAULT_OCR_PROMPT} The primary OCR language hint is {language}."
            payload = self._build_payload(
                messages=[
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": prompt},
                            {"type": "image_url", "image_url": {"url": data_url}},
                        ],
                    }
                ],
                max_tokens=self._request.max_tokens,
            )
            response = self._client.post("/v1/chat/completions", json=payload)
            response.raise_for_status()
            message = extract_message_content(response.json())
            return message.strip()
        finally:
            self._lock.release()

    def translate(self, texts: Iterable[str], source_lang: str, target_lang: str) -> list[str]:
        if not self._lock.acquire(blocking=False):
            raise VisionLlamaBusyError("Vision translation busy")
        try:
            normalized = [text if text is not None else "" for text in texts]
            if not normalized:
                return []

            request_json = json.dumps(
                [{"i": index, "text": value} for index, value in enumerate(normalized)],
                ensure_ascii=False,
            )
            payload = self._build_payload(
                messages=[
                    {
                        "role": "user",
                        "content": (
                            DEFAULT_TRANSLATE_PROMPT.format(
                                source_lang=source_lang or "auto",
                                target_lang=target_lang or "en",
                            )
                            + "\n\n"
                            + request_json
                        ),
                    }
                ],
                max_tokens=self._request.max_tokens,
                response_format={"type": "json_object"},
            )
            response = self._client.post("/v1/chat/completions", json=payload)
            response.raise_for_status()
            message = extract_message_content(response.json())
            parsed = json.loads(message)
            translations = parsed.get("translations")
            if not isinstance(translations, list):
                raise VisionLlamaError("Vision translation response is missing translations list.")
            return [str(item) for item in translations[: len(normalized)]]
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


def prepare_image_for_upload(image_bytes: bytes, max_image_side: int) -> str:
    if max_image_side <= 0:
        return to_data_url(image_bytes, "image/png")

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
        return to_data_url(output.getvalue(), "image/png")


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
