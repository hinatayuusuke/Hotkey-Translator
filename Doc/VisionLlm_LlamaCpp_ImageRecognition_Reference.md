# VisionLLM + llama.cpp 画像認識実装リファレンス

## 1. 概要
- 現在の VisionLLM OCR は、WPF/C# から `llama-server.exe` を直接叩かず、`OcrServiceVisionLlm` の Python gRPC ホストを 1 枚噛ませる構成になっている。
- Python 側は `llama.cpp` の OpenAI 互換 HTTP API を使い、画像を `data:image/png;base64,...` として `/v1/chat/completions` に送る。
- OCR 返却値は現状 `text` のみで、座標は返していない。C# 側で改行ごとに全幅の疑似 bbox を作り、既存 overlay パイプラインへ流している。
- VisionLLM OCR 中に `EnableVisionLlmSharedLocalTranslation=true` なら、翻訳も同じ VisionLLM gRPC サービスへ寄せ、別の `TranslationServiceLlama` を起動しない。

## 2. 関連ファイル
| ファイル | 役割 |
| --- | --- |
| `Services/VisionLlmGrpcHost.cs` | VisionLLM 用 Python gRPC ホストの起動管理、`uv sync`、モデル自動配置 |
| `Services/VisionLlmGrpcOcrProvider.cs` | C# OCR provider。Bitmap を PNG bytes にして gRPC へ送る |
| `Services/LlamaGrpcTranslationProvider.cs` | VisionLLM OCR 中は翻訳先 endpoint を VisionLLM gRPC に切り替える |
| `Services/Application/ResourceHostFacade.cs` | VisionLLM host を必要ホストへ組み込み、shared translation 時に通常 Llama host を抑止する |
| `OcrServiceVisionLlm/server.py` | gRPC サーバ本体。OCR/翻訳の 2 サービスを公開する |
| `OcrServiceVisionLlm/vision_llama_engine.py` | `llama-server` 起動、HTTP payload 構築、OCR/翻訳の本体ロジック |
| `OcrServiceVisionLlm/model_manifest.json` | 既定 model / mmproj の自動ダウンロード定義 |
| `OcrServiceVisionLlm/test_vision_llama_engine.py` | Python 単体の OCR/翻訳スモークテスト |

## 3. 全体アーキテクチャ
```text
WPF UI / AppSettings
  -> ResourceHostFacade
    -> VisionLlmGrpcHost
      -> uv run python OcrServiceVisionLlm/server.py
        -> LlamaServerHost
          -> llama-server.exe (OpenAI互換HTTP)
            -> /v1/chat/completions

C# OCR call
  -> VisionLlmGrpcOcrProvider
    -> gRPC OcrService.Recognize
      -> VisionLlamaEngine.recognize
        -> llama-server HTTP
        -> {"text":"..."} を返す
      -> C# が synthetic bbox を生成
```

## 4. 起動シーケンス
### 4.1 C# ホスト準備
`VisionLlmGrpcHost.OnBeforeStartAsync(...)` では、起動前に 2 つの準備だけを行う。

1. `EnsurePythonRuntimeAsync(...)`
   - `Tools\uv\uv.exe sync --project OcrServiceVisionLlm` を必要時だけ実行する。
   - 実行判定は `pyproject.toml`、`uv.lock`、`model_manifest.json` の SHA256 から作った fingerprint で行う。
   - `.venv` と `.uv-sync.state` があり fingerprint 一致なら `uv sync` をスキップする。
2. `EnsureVisionLlmAssetsAsync(...)`
   - 選択中 model / mmproj が manifest 既定値と一致する場合だけ、自動ダウンロードを使う。
   - 既定以外のファイル名を選んだ場合は、ローカルに存在することをそのまま要求する。

### 4.2 Python gRPC ホスト起動
`VisionLlmGrpcHost.StartProcessCoreAsync(...)` は `uv run --project ... python server.py` で Python 側を起動し、以下の値を引き渡す。

- gRPC listen: `--host`, `--port`
- llama.cpp HTTP listen: `--llama-host`, `--llama-port`
- model paths: `--model`, `--mmproj`
- llama.cpp runtime: `--ctx-size`, `--gpu-layers`, `--threads`, `--parallel`, `--batch-size`
- generation: `--max-tokens`, `--temperature`, `--top-p`, `--top-k`, `--repeat-penalty`
- image preprocessing: `--max-image-side`
- diagnostics: `--diag-log-file`, `--disable-thinking`

### 4.3 Python 側で llama-server を起動
`server.py` の `main()` は `VisionLlamaServerConfig` と `VisionLlamaRequestConfig` を構築し、`LlamaServerHost.start()` を呼ぶ。

`LlamaServerHost._start_process()` が実際に組み立てるコマンドは概ね次の形。

```text
llama-server.exe
  -m <model.gguf>
  --mmproj <mmproj.gguf>
  --host 127.0.0.1
  --port 8089
  --ctx-size ...
  --n-gpu-layers ...
  --threads ...
  --parallel ...
  --batch-size ...
  --reasoning-budget 0
  --reasoning-format none
  --chat-template-kwargs {"enable_thinking":false}
```

thinking 無効化は C# と Python の両方で明示している。将来類似実装を作るときも、vision モデルが余計な chain-of-thought を返すなら同じ抑制を入れた方が扱いやすい。

### 4.4 ready 判定
- Python 側: `LlamaServerHost._wait_ready()` が `http://<llama-host>:<llama-port>/v1/models` をポーリングする。
- C# 側: `VisionLlmGrpcHost.WaitForReadyCoreAsync(...)` が gRPC `Health` をポーリングする。
- つまり ready は 2 段で見ている。
  - `llama-server` 自体が起動済みか
  - その上に載る gRPC ホストが応答可能か

この分離のおかげで、`llama-server` 起動失敗と gRPC 側の初期化失敗を切り分けやすい。

## 5. OCR リクエストの流れ
### 5.1 C# -> gRPC
`VisionLlmGrpcOcrProvider.RecognizeAsync(...)` は以下を行う。

1. `Bitmap` を PNG にシリアライズ
2. `OcrRequest.Image` に bytes を格納
3. `OcrRequest.Language` に `settings.SourceLanguage` を hint として渡す
4. gRPC `Recognize` を呼ぶ
5. 戻り JSON の `text` をパース
6. 改行ごとに synthetic bbox を作って `OcrResultModel` に変換

gRPC の戻り値は実質これだけである。

```json
{"text":"1行目\n2行目\n3行目"}
```

### 5.2 gRPC -> VisionLlamaEngine
`server.py` の `OcrService.Recognize(...)` は bytes をそのまま `VisionLlamaEngine.recognize(...)` に渡し、成功時に `{"text": ...}` を JSON 化して返す。

- busy 時: `RESOURCE_EXHAUSTED` + `"BUSY"`
- 例外時: `INTERNAL`

`VisionLlamaEngine` は内部 lock を non-blocking 取得しているため、OCR と翻訳は同時実行せず直列化される。これが現在の「VRAM を無駄に増やさず、安全に 1 本ずつ流す」前提になっている。

## 6. llama.cpp への画像送信方法
### 6.1 画像前処理
`prepare_image_for_upload(...)` の仕様はかなり重要。

- 入力 bytes を Pillow で開く
- RGB に変換
- 長辺が `max_image_side` を超える場合だけ縮小
- PNG で再エンコード
- `data:image/png;base64,...` に変換

この処理で、入力画像サイズの揺れを吸収しつつ、OpenAI 互換 API が受け取れる形式へ揃えている。

### 6.2 OCR payload
`VisionLlamaEngine.recognize(...)` が llama-server に投げる payload の要点は以下。

```json
{
  "model": "<gguf model name>",
  "messages": [
    {
      "role": "user",
      "content": [
        {"type": "text", "text": "<OCR prompt>"},
        {"type": "image_url", "image_url": {"url": "data:image/png;base64,..."}}
      ]
    }
  ],
  "max_tokens": 512,
  "temperature": 0.3,
  "top_p": 0.6,
  "top_k": 20,
  "repeat_penalty": 1.05,
  "reasoning_budget": 0,
  "chat_template_kwargs": {"enable_thinking": false}
}
```

OCR prompt の要点:
- visible text を全部抽出する
- plain text only
- 翻訳や説明をしない
- 同じ text box の dialogue / subtitle は自然文になるよう visual line break をまとめる
- menu/list/別 text box だけ改行を残す

つまり「画面上の物理行そのまま」ではなく、「読みやすい意味単位」を少し優先する prompt である。これが後段の synthetic bbox と少しズレる理由でもある。

### 6.3 レスポンス取り出し
`extract_message_content(...)` は `choices[0].message.content` から本文を取り出す。

- `content` が string の場合はそのまま返す
- `content` が array の場合は `text` を連結する

OpenAI 互換 API 実装差分に多少耐えるため、この 2 系統に対応している。

## 7. OCR 結果をアプリに戻す形
現状の VisionLLM OCR は bbox を持たないため、`VisionLlmGrpcOcrProvider.BuildSyntheticLines(...)` で次のルールを使う。

- テキストを改行で分割
- 行数で画像高さを均等割り
- 各行に `Rect(0, y, width, lineHeight)` を割り当て
- confidence は固定 `1.0f`

これは品質の高い座標復元ではなく、「既存 grouping / overlay ルートを壊さずに text-first 統合する」ための暫定策である。  
将来、類似実装で bbox を返せるなら、この synthetic line 生成は置き換えてよい。

## 8. shared local translation の実装
VisionLLM 実装の特徴は、OCR 用ホストを翻訳にも再利用している点にある。

### 8.1 endpoint 切替条件
`LlamaGrpcTranslationProvider.ResolveEndpoint(...)` は、以下の条件をすべて満たすと通常の Llama endpoint ではなく VisionLLM gRPC endpoint を返す。

- `settings.OcrEngine == OcrEngineKind.VisionLlm`
- `settings.EnableVisionLlmGrpcHost`
- `settings.EnableVisionLlmSharedLocalTranslation`

### 8.2 通常 Llama host の抑止
`ResourceHostFacade.BuildRequiredHosts(...)` では、`UsesVisionLocalTranslation(settings)` が true のとき通常の `Llama.cpp` host を required hosts に入れない。

つまり shared translation 有効時の実行形はこうなる。

```text
VisionLLM OCR host 1本
  - OCR gRPC
  - Translation gRPC
  - 内部では同じ llama-server / 同じ model
```

VRAM の二重消費を避けたいときに有効なパターンなので、類似実装でも再利用価値が高い。

### 8.3 翻訳 payload
`VisionLlamaEngine.translate(...)` は入力文字列リストを次の JSON 文字列にして prompt に埋め込む。

```json
[{"i":0,"s":"TEXT_0"},{"i":1,"s":"TEXT_1"}]
```

返却は `{"t":["...","..."]}` を期待し、`response_format={"type":"json_object"}` を指定している。  
それでもモデルが壊れた JSON を返すことがあるため、`parse_json_object_from_text_resilient(...)` で以下の補修を試す。

- markdown code fence 除去
- 最初の balanced JSON object 抽出
- 余分な閉じ括弧の切り捨て
- 配列/オブジェクト終端の入れ違い修正
- 文字列 quote 崩れの軽微補修

OCR より translation の方が「モデル出力を JSON として信用しすぎない」実装になっている。

## 9. 現在の既定値
2026-04-09 時点の主要既定値は以下。

- gRPC port: `50074`
- llama.cpp HTTP port: `8089`
- model: `Qwen3.5-4B-Q4_K_M.gguf`
- mmproj: `mmproj-Qwen3.5-4B-BF16.gguf`
- `VisionLlmMaxImageSide`: `768`
- `EnableVisionLlmSharedLocalTranslation`: `true`
- `EnableVisionGeometryHybridOcr`: `false`

依存ランタイム:
- Python: `>=3.10,<3.13`
- `uv` 管理の `.venv`
- `grpcio`, `httpx`, `pillow`, `opencc-python-reimplemented`

## 10. 類似実装時に真似するとよい点
### 10.1 最小構成
類似実装を最小で再現するなら、この 4 点が本体になる。

1. C# 側の host manager
2. Python gRPC facade
3. llama-server process host
4. image bytes -> OpenAI 互換 chat completions payload 変換

### 10.2 再利用価値が高い設計判断
- OCR 呼び出しと `llama.cpp` 直接制御を分離し、アプリ本体からは gRPC だけ見せる
- `uv sync` の fingerprint を持ち、起動ごとの環境再構築を避ける
- model 自動配置は manifest 一致時だけ許可し、非既定ファイルは fail fast にする
- thinking 無効化を既定にし、vision OCR の出力を plain text / JSON へ寄せる
- OCR と翻訳を同一 host にまとめ、重い vision model を 2 本持たない

### 10.3 そのまま流用しない方がよい点
- synthetic bbox は暫定策なので、座標を返せるモデル/実装なら最初から structured output を検討した方がよい
- `VisionLlamaEngine` の単一 lock は安全寄りで、throughput は高くない
- OCR prompt が「意味単位へ寄せる」方向なので、行位置復元を厳密にしたい用途には不向き

## 11. 追加実装時のチェックリスト
### bbox を返したい場合
- gRPC OCR response を `{"text": ...}` だけでなく `lines[]` へ拡張する
- C# 側 `BuildSyntheticLines(...)` を置き換える
- 既存 `OcrLine` への変換で confidence / bbox height / order を埋める

### 別モデルへ差し替えたい場合
- `model_manifest.json` の既定ファイル名と SHA256 を更新する
- mmproj の実ファイル名とローカル alias 名を分けるか確認する
- prompt と `response_format` の相性を単体テストで確認する
- think mode 有効時の出力崩れを確認する

### 並列性を上げたい場合
- Python 側の単一 lock を見直す
- `--parallel` を増やすだけで安全になるとは限らない
- OCR と翻訳で別 queue を持つか、host を分けるかを先に決める

## 12. 動作確認の入口
Python 単体で確認するなら `OcrServiceVisionLlm/test_vision_llama_engine.py` が一番早い。

例:

```powershell
cd "G:\Local App\Hotkey-Translator\OcrServiceVisionLlm"
uv run test_vision_llama_engine.py `
  --mode ocr `
  --image ".\test.png" `
  --llama-server "..\TranslationServiceLlama\LlamaCpp\llama-server.exe" `
  --model "..\TranslationServiceLlama\LlamaCpp\Models\Qwen3.5-4B-Q4_K_M.gguf" `
  --mmproj "..\TranslationServiceLlama\LlamaCpp\Models\mmproj-Qwen3.5-4B-BF16.gguf" `
  --device gpu `
  --json-out ".\out\vision_ocr_perf.json" `
  --text-out ".\out\vision_ocr_text.txt"
```

翻訳経路まで見るなら:

```powershell
uv run test_vision_llama_engine.py `
  --mode translate `
  --text "1行目のテスト" `
  --text "2行目のテスト" `
  --source-lang ja `
  --target-lang en `
  --raw-request-out ".\out\vision_translate_request.json" `
  --raw-response-out ".\out\vision_translate_response.txt"
```

## 13. まとめ
- 実装の本質は「Vision model を直接 UI から叩かない」「Python gRPC facade で llama.cpp を包む」「OCR/翻訳を同一 host に共有する」の 3 点にある。
- 画像認識そのものは OpenAI 互換 chat completions + `image_url(data URL)` で成立している。
- 現在の弱点は geometry 不足であり、将来の改善余地は structured OCR output と bbox 復元に集中している。
