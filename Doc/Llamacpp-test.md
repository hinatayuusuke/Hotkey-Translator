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


変更ファイル:
- `TranslationServiceLlama/test_llama_vision_ocr.py`

できること:
- `llama-server` をローカル起動
- 画像を `data:image/...;base64,...` に変換
- `/v1/chat/completions` に `image_url` 付きで送信
- 抽出テキストを標準出力
- 必要なら raw JSON を `--json-out`、文字列を `--text-out` に保存
- `--mmproj` も指定可能
- think mode は既定で無効

確認:
- `python -m compileall TranslationServiceLlama\test_llama_vision_ocr.py`
- `uv run test_llama_vision_ocr.py --help`

実行例:
```powershell
cd "G:\Local App\Hotkey-Translator\TranslationServiceLlama"
uv run test_llama_vision_ocr.py --image ".\test.png" --model ".\LlamaCpp\Models\Qwen3.5-9B-Q4_K_M.gguf" --device gpu --mode ocr --mmproj ".\LlamaCpp\Models\<mmproj>.gguf"
  --json-out ".\out\vision_raw.json" `
  --text-out ".\out\vision_text.txt"
  --mode ocr|translate
  --source-lang
  --target-lang
```

`mmproj` が必要なモデルなら:
```powershell
uv run test_llama_vision_ocr.py `
  --image ".\test.png" `
  --model ".\LlamaCpp\Models\<vision-model>.gguf" `
  --mmproj ".\LlamaCpp\Models\<mmproj>.gguf" `
  --device gpu
```

cd "G:\Local App\Hotkey-Translator\OcrServiceVisionLlm"

uv run test_vision_llama_engine.py `
  --image ".\test.png" `
  --llama-server "..\TranslationServiceLlama\LlamaCpp\llama-server.exe" `
  --model "..\TranslationServiceLlama\LlamaCpp\Models\Qwen3.5-9B-Q4_K_M.gguf" `
  --mmproj "..\TranslationServiceLlama\LlamaCpp\Models\mmproj-F16.gguf" `
  --device gpu `
  --warmup 1 `
  --repeat 3 `
  --json-out ".\out\vision_ocr_perf.json" `
  --text-out ".\out\vision_ocr_text.txt"
  
