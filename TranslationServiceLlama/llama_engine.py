import logging
import os
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Iterable, List

import httpx


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


@dataclass
class LlamaRequestConfig:
    max_tokens: int
    temperature: float
    top_p: float
    top_k: int
    repeat_penalty: float
    http_timeout_seconds: float


DEFAULT_SYSTEM_PROMPT = "Translate the following segment into {target}. Output translation only."


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

        logging.info("Starting llama-server: %s", " ".join(args))
        proc = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
        self._process = proc

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
            system_prompt = build_system_prompt(target_lang)
            outputs: list[str] = []
            for text in texts:
                payload = {
                    "model": os.path.basename(self._host._config.model_path),
                    "messages": [
                        {"role": "system", "content": system_prompt},
                        {"role": "user", "content": text},
                    ],
                    "temperature": self._request.temperature,
                    "top_p": self._request.top_p,
                    "top_k": self._request.top_k,
                    "repeat_penalty": self._request.repeat_penalty,
                    "max_tokens": self._request.max_tokens,
                    "stream": False,
                }
                resp = self._client.post("/v1/chat/completions", json=payload)
                resp.raise_for_status()
                data = resp.json()
                output = extract_response_text(data)
                outputs.append(output)
            return outputs
        finally:
            self._lock.release()


def build_system_prompt(target_lang: str) -> str:
    target_label = resolve_language_label(target_lang)
    # WHY: Fixed template avoids UI/user drift while keeping target language alignment.
    return DEFAULT_SYSTEM_PROMPT.format(target=target_label)


def resolve_language_label(language: str) -> str:
    if not language:
        return "English"
    key = language.strip().lower()
    if key.startswith("ja") or key.startswith("jpn"):
        return "Japanese"
    if key.startswith("en") or key.startswith("eng"):
        return "English"
    if key.startswith("zh") or key.startswith("zho"):
        return "Chinese"
    if key.startswith("ko") or key.startswith("kor"):
        return "Korean"
    if key.startswith("ru") or key.startswith("rus"):
        return "Russian"
    if "_" in key:
        return key.split("_")[0]
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
