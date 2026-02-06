# Llama.cpp (HY-MT1.5-1.8B-Q4_K_M.gguf) 翻訳エンジン実装案

1. **概要（1–3行）**
- Llama.cpp の `llama-server` を翻訳エンジンとして採用し、既存の翻訳 gRPC サーバから起動・監視する。
- WPF 側は gRPC を通じて翻訳要求を送る（HTTP は gRPC 側が内部で利用）。
- NLLB/CTranslate2 との同時常駐は避け、Llama 有効時は排他で運用する。

2. **ゴール / 非ゴール**
- ゴール: HY-MT1.5-1.8B-Q4_K_M.gguf を GPU(CUDA12) で推論し、翻訳結果を返す。
- ゴール: gRPC サーバが `llama-server` を起動・監視し、停止時は再起動する。
- ゴール: 設定で Llama 翻訳の ON/OFF とパラメータを制御できる。
- 非ゴール: 既存翻訳（NLLB/DeepL/Gemini）のフォールバック実装。
- 非ゴール: モデルの自動ダウンロード。

3. **前提・仮定**
- GPU は CUDA12 対応ドライバが導入済み。
- `llama-server` は CUDA12 ビルド済みバイナリを利用。
- Llama 用の Python 環境は CTranslate2 と分離する。
- 翻訳要求は基本的に逐次（並列は少数）で運用する。
- 1.8B Q4 の品質は「NLLBより自然」に寄る想定だが、分野差はあり得る。

4. **現状整理**
- 翻訳は `TranslationService` の gRPC と、WPF 側の `ITranslationProvider` が連携。
- CTranslate2 gRPC サーバは `CTranslate2GrpcHost` で起動・監視されている。

5. **提案アーキテクチャ**
- WPF → gRPC → Llama.cpp HTTP の 2段構成。
- gRPC サーバは Llama.cpp の起動・監視と HTTP リクエストを担当（新規 `TranslationServiceLlama`）。
- Llama 有効時は CTranslate2 を停止して VRAM 競合を避ける。
- gRPC サーバは CTranslate2 と分離した Python 環境で稼働させる（依存衝突回避）。

6. **インターフェース設計**
- gRPC は既存 `Translate` API を継続利用。
- Llama 用の設定を `AppSettings` に追加。
- `llama-server` への HTTP リクエストは OpenAI 互換エンドポイントを優先利用。

### 6.1 gRPC → HTTP マッピング案（OpenAI互換）
- gRPC 側は `POST /v1/chat/completions` を使用（翻訳指示を system/user で明確化）。
- 送信例（最小）:
  - `model`: `"HY-MT1.5-1.8B-Q4_K_M.gguf"`（識別用の文字列で可）
  - `messages`: `[{role:"system",content:"Translate to Japanese. Output translation only."},{role:"user",content:"<input>"}]`
  - `temperature`, `top_p`, `top_k`, `repeat_penalty`, `max_tokens`
- 応答から `choices[0].message.content` を取り出して翻訳結果とする（`stream=false` 固定）。
- NOTE: 既存 gRPC 形式を維持し、HTTP は内部実装に閉じる。

### 6.2 設定項目（案）
- `EnableLlamaCppTranslation` (bool)
- `LlamaCppServerPath` (string): `llama-server` 実行ファイル
- `LlamaCppModelPath` (string): `.gguf` のパス（相対は `TranslationServiceLlama/` から解決）
- `LlamaCppHost` / `LlamaCppPort`
- `LlamaCppContextSize` (int)
- `LlamaCppGpuLayers` (int)
- `LlamaCppThreads` (int)
- `LlamaCppParallel` (int)
- `LlamaCppBatchSize` (int)
- `LlamaCppMaxTokens` (int)
- `LlamaCppTemperature` (double)
- `LlamaCppTopP` (double)
- `LlamaCppTopK` (int)
- `LlamaCppRepeatPenalty` (double)
- `LlamaCppSystemPrompt` (string)

### 6.3 ディレクトリ構成（分離環境案）
- `TranslationServiceLlama/`（新規）: Llama 用 gRPC サーバー
- `TranslationService/`（既存）: CTranslate2 用 gRPC サーバー
- `Models/` または `TranslationServiceLlama/models/` に `.gguf` を配置
- `TranslationServiceLlama/pyproject.toml` は Llama 用依存のみ（`httpx`, `grpcio`, `grpcio-tools` など）

7. **実装手順（ステップ分割）**
- Step 1: `TranslationServiceLlama/` を作成し、Llama 用 gRPC サーバを追加。
- Step 2: gRPC サーバから `llama-server` を起動・監視する管理層を追加。
- Step 3: HTTP クライアントで OpenAI 互換 API を呼び出す翻訳ロジックを実装。
- Step 4: WPF に Llama 翻訳設定とプロバイダを追加。
- Step 5: 競合防止（Llama 有効時は CTranslate2 を無効化）。
- Step 6: ログ・ヘルスチェック・タイムアウト調整。

8. **非機能要件チェック**
- 性能: `--parallel 1` を基本に、必要なら `--threads` と `--n-gpu-layers` を調整。
- 競合: 翻訳は single-flight で実行し、実行中の追加リクエストは即時エラー（BUSY）で返す。
- 可観測性: 起動/再起動/翻訳時間をログ出力。
- 互換性: 既存設定のデフォルト動作は維持。
- 運用: HTTP サーバが落ちた場合は自動再起動。

9. **リスクと緩和策**
- Risk: CUDA DLL/ドライバ不一致で起動失敗。
- Mitigation: 起動時に明確なダイアログとログ、設定OFF。
- Risk: 1.8B の品質限界。
- Mitigation: 将来モデル差し替え可能な設定設計。

10. **影響範囲**
- `TranslationService/`（llama-server 起動・HTTP クライアント・ヘルスチェック）
- `Services/`（Llama 用 gRPC ホスト・翻訳プロバイダ）
- `Models/AppSettings.cs`（Llama 設定追加）
- `MainWindow.xaml/.cs`（UI 設定項目・排他制御）
- `Doc/`（利用手順）

11. **Definition of Done**
- Llama 翻訳を UI で ON/OFF できる。
- gRPC サーバから `llama-server` が起動・監視される。
- 翻訳要求が HTTP 経由で処理される。
- CTranslate2 と同時常駐しない。

---

## 参考: 起動パラメータ案（GPU CUDA12 前提）
- `llama-server -m <model.gguf> --host 127.0.0.1 --port 8088 --ctx-size 4096 --n-gpu-layers 999 --threads 4 --parallel 1 --batch-size 512`
- `--temp 0.3-0.7 --top-k 20 --top-p 0.6 --repeat-penalty 1.05`

## 参考: 翻訳プロンプト案
- `Translate the following segment into Japanese, without additional explanation.`
- 出力は翻訳文のみ、改行は保持。
