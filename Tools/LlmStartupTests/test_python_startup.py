"""Startup regression tests shared by the translation and VisionLLM engines."""
import importlib
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

root = Path(__file__).resolve().parents[2]
vision = "--vision" in sys.argv
if vision:
    sys.argv.remove("--vision")
sys.path.insert(0, str(root / ("OcrServiceVisionLlm" if vision else "TranslationServiceLlama")))
engine = importlib.import_module("vision_llama_engine" if vision else "llama_engine")
error = engine.VisionLlamaError if vision else engine.LlamaServerError


class StartupTests(unittest.TestCase):
    def host(self):
        return engine.LlamaServerHost(SimpleNamespace(host="127.0.0.1", port=1234, ready_timeout_ms=1000))

    def test_child_exit_includes_exit_code(self):
        host = self.host()
        host._process = Mock(returncode=27)
        host._process.poll.return_value = 27
        with self.assertRaisesRegex(error, "exit code: 27"):
            host._wait_ready()

    def test_failed_load_stops_child_and_never_starts_monitor(self):
        host = self.host()
        with patch.object(host, "_start_process"), patch.object(host, "_wait_ready", side_effect=error("broken")), \
                patch.object(host, "stop") as stop, patch.object(host, "_ensure_monitor") as monitor:
            with self.assertRaisesRegex(error, "broken"):
                host.start()
            stop.assert_called_once()
            monitor.assert_not_called()

    def test_process_creation_failure_is_cleaned_up(self):
        host = self.host()
        with patch.object(host, "_start_process", side_effect=OSError("cannot launch")), patch.object(host, "stop") as stop:
            with self.assertRaises(OSError):
                host.start()
            stop.assert_called_once()

    def test_http_timeout_clamped_to_remaining_budget(self):
        host = self.host()
        host._process = Mock()
        host._process.poll.return_value = None
        with patch.object(engine.time, "monotonic", side_effect=[0.0, 0.2, 0.4]), \
                patch.object(engine.httpx, "get", return_value=SimpleNamespace(status_code=200)) as get:
            host._wait_ready()
            self.assertAlmostEqual(get.call_args.kwargs["timeout"], 0.6)

    def test_ready_budget_uses_monotonic_clock(self):
        host = self.host()
        host._process = Mock()
        host._process.poll.return_value = None
        with patch.object(engine.time, "monotonic", side_effect=[0.0, 2.0]):
            with self.assertRaisesRegex(error, "did not become ready"):
                host._wait_ready()

    def test_stop_waits_for_child_exit(self):
        host = self.host()
        host._process = Mock()
        host.stop()
        host._process.kill.assert_called_once()
        host._process.wait.assert_called_once_with(timeout=5.0)


if __name__ == "__main__":
    unittest.main()
