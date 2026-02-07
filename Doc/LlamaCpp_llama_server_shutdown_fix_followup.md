# LlamaCpp `llama-server.exe` 終了漏れ対策（フォローアップ）

## 背景
先行修正（`.venv` Python 直接起動 + `server.py` の `finally: host.stop()`）後も、環境によっては `llama-server.exe` が残留する報告があった。

## 想定される残留経路
- 停止時点で親プロセス（Python）が既に終了しており、WPF 側が直接保持するプロセスハンドルから子孫を辿れない。
- その結果、`llama-server.exe` が孤立して残る。

## 追加した解消策

### 1) `llama-server` PID の明示追跡
- 変更: `TranslationServiceLlama/llama_engine.py`
- `subprocess.Popen(...)` 直後に `llama-server pid=<pid>` をログ出力。
- 目的: 親プロセス経由で回収できないケースでも、子プロセスを直接特定できるようにする。

### 2) WPF 側での残留プロセス回収フォールバック
- 変更: `Services/LlamaGrpcHost.cs`
- gRPC ホスト出力から `llama-server pid=<pid>` を抽出して保持。
- `Stop()` 時に以下の順で回収を実施:
  1. 通常の `_process.Kill(true)`（親側のプロセスツリー停止）
  2. 追跡済み PID を直接 kill（実行ファイルパス一致を検証）
  3. PID が使えない場合は、固定 `llama-server.exe` パス一致プロセスを列挙して回収
- 目的: 親の状態に依存しない停止経路を追加し、残留を抑止する。

## 安全性
- 直接 kill する前に、`MainModule.FileName` と固定サーバーパスを照合して対象を限定。
- 一致しない PID は kill せずスキップ。

## 検証
- `dotnet build -nologo` 成功。
- `python -m py_compile TranslationServiceLlama/server.py TranslationServiceLlama/llama_engine.py` 成功。

## 期待結果
- WPF 終了後に `llama-server.exe` が残るケースを大幅に低減。
- とくに「親が先に落ちた」経路でも回収可能。
