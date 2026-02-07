# LlamaCpp `llama-server.exe` 終了漏れ調査と修正

## 問題
WPF アプリ終了後も `llama-server.exe` が残留する場合がある。

## 原因
1. WPF 側 (`Services/LlamaGrpcHost.cs`) が監視・停止していたのは `uv run ...` のプロセスであり、`llama-server.exe` 自体ではない。
2. Python 側 (`TranslationServiceLlama/server.py`) で gRPC サーバー終了時に `host.stop()` を保証していなかった。
3. この組み合わせにより、親プロセス（`uv`）の終了状態次第で `llama-server.exe` が孤立して残る経路があった。

## 修正方針
- プロセス所有を明確化し、WPF 側が長寿命プロセス（Python）を直接管理する。
- Python 側は終了時に必ず `llama-server` を停止する。

## 実装内容
### 1) WPF 側の起動経路を変更
- ファイル: `Services/LlamaGrpcHost.cs`
- 変更:
  - `uv run --project ... python server.py` を廃止。
  - `EnsurePythonRuntimeAsync` 実行後、`.venv` の Python 実行ファイルを直接起動する方式に変更。
  - `ResolvePythonExecutable(projectDir)` を追加し、Windows は `.venv\\Scripts\\python.exe`、非 Windows は `.venv/bin/python` を解決。
- 目的:
  - WPF 側 `_process.Kill(true)` が Python および配下の `llama-server.exe` を確実に停止できるようにする。

### 2) Python 側の終了保証を追加
- ファイル: `TranslationServiceLlama/server.py`
- 変更:
  - `SIGINT` / `SIGTERM` ハンドラを登録し、gRPC サーバー停止を要求。
  - `grpc_server.wait_for_termination()` の `finally` で `host.stop()` を必ず実行。
- 目的:
  - gRPC ホスト終了時に `llama-server` を取りこぼさない。

## 期待される挙動
- WPF アプリ終了時に、Llama gRPC ホスト（Python）とその子プロセス `llama-server.exe` が連動して停止する。

## 検証
- `dotnet build -nologo` 成功（0 error / 0 warning）。
- `python -m py_compile TranslationServiceLlama/server.py TranslationServiceLlama/llama_engine.py` 成功。

## 補足
- 本修正は Llama 経路に限定。
- 既存の `uv sync`（依存同期）は維持し、実行時のみ `.venv` Python 直起動に切り替えている。
