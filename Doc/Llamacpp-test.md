`TranslationServiceLlama\test_translation_engine.py` は `TranslationServiceLlama` フォルダで実行します。

基本コマンド:
```powershell
cd "G:\Local App\Hotkey-Translator\TranslationServiceLlama"
uv run test_translation_engine.py
```

既定値:
- `--host 127.0.0.1`
- `--port 50071`
- `--send-mode grpc`
- `--source-lang en`
- `--target-lang ja`

よく使う実行例:

既存の gRPC サーバーに送る
```powershell
uv run test_translation_engine.py --text "Hello world." --text "How are you?"
```

入力ファイルから送る
```powershell
uv run test_translation_engine.py --input ".\input.txt"
```

ローカルサーバーを自動起動して試す
```powershell
uv run test_translation_engine.py --auto-start-server --device cpu --text "Hello world."
```

GPU でローカルサーバー自動起動
```powershell
uv run test_translation_engine.py --auto-start-server --device gpu --text "Hello world."
```

Llama HTTP の JSON batch モードで送る
```powershell
uv run test_translation_engine.py --send-mode llama-json-batch --text "Hello world."
```

主なパラメーター:
- `--host` gRPC ホスト
- `--port` gRPC ポート
- `--send-mode grpc|llama-json-batch`
- `--auto-start-server` `server.py` を自動起動
- `--device cpu|gpu`
- `--batch-size <int>`
- `--max-tokens <int>`
- `--gpu-layers <int>`
- `--llama-server <path>`
- `--model <path>`
- `--llama-host <host>`
- `--llama-port <port>`
- `--http-timeout-sec <sec>`
- `--startup-timeout-sec <sec>`
- `--source-lang <lang>`
- `--target-lang <lang>`
- `--input <utf8 text file>`
- `--text <text>` 複数指定可
- `--continue-on-error`

注意:
- `--device` / `--batch-size` / `--max-tokens` / `--gpu-layers` は `--auto-start-server` とセットでないとエラーになります。