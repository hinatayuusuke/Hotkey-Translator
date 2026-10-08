# LLM startup regression checks

Run from the repository root:

```powershell
dotnet run --project Tools/LlmStartupTests/LlmStartupTests.csproj
TranslationServiceLlama/.venv/Scripts/python.exe Tools/LlmStartupTests/test_python_startup.py
OcrServiceVisionLlm/.venv/Scripts/python.exe Tools/LlmStartupTests/test_python_startup.py --vision
```

The C# runner checks startup exit detection, per-RPC and overall deadlines, an unresponsive TCP peer with a real gRPC client, failure notification order, cancellation, gate ownership, shutdown, other OCR host orchestration, download timeouts, file locking, checksum validation, and process cleanup. HTTP downloads use injected handlers and generated temporary assets. No model downloads or application settings writes are performed.

The Python tests verify the translation and VisionLLM engines' startup cleanup, exit codes, readiness clock, HTTP request budget, and child shutdown with mocks.

To additionally check the installed Python runtimes and real llama-server/model files:

```powershell
dotnet run --project Tools/LlmStartupTests/LlmStartupTests.csproj -- --native
```

This starts each Python gRPC server on temporary loopback ports, loads the existing Hy-MT2-1.8B and Qwen3.5-4B/mmproj models on CPU, checks the connection phase and Health, and verifies immediate failure with a generated invalid GGUF file. Each owned process tree is stopped before the next case. The repository's model files and runtime configuration are preserved.

The native checks require the existing `.venv` environments, llama.cpp runtime, and model files. They do not cover visual inspection of the WPF dialogs or real Paddle/PaddleVL/NDL model startup.
