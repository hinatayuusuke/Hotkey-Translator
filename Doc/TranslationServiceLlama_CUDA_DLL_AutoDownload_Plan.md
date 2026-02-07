# TranslationServiceLlama CUDA DLL + Model Auto-Download Plan

## 1. 概要（1-3行）
`TranslationServiceLlama` の起動時に `uv sync` で CUDA 12 ランタイム DLL を自動取得し、翻訳モデル `qwen3-1_7b-instruct-q4_k_m.gguf` を自動ダウンロードする。  
`llama-server.exe` は `TranslationServiceLlama\LlamaCpp\llama-server.exe` に固定し、モデル保存先は `TranslationServiceLlama\LlamaCpp\Models` に固定して一貫運用する。  
`llama-server.exe` 実行時は DLL 探索パスを注入し、手動セットアップを最小化する。

## 2. ゴール / 非ゴール
### ゴール
- `pyproject.toml` 依存だけで CUDA DLL を自動取得できるようにする。
- `qwen3-1_7b-instruct-q4_k_m.gguf` を `TranslationServiceLlama\LlamaCpp\Models` へ自動取得できるようにする。
- `llama-server.exe` 起動時に不足 DLL エラー（`0xC0000135`）を回避できるようにする。
- モデル未配置時に自動取得し、起動失敗を減らす。
- `LlamaServerPath` / `LlamaModelPath` をユーザー編集不可にし、固定パスで運用する。
- 失敗時に「Python依存不足 / ネイティブ不足 / モデル取得失敗」を切り分けできるログを残す。

### 非ゴール
- `llama-server.exe` / `llama.dll` / `ggml*.dll` / `mtmd.dll` を Python 依存で自動取得すること。
- CUDA ドライバーの自動インストール。
- CPU/GPU 自動フォールバックの実装。
- `LlamaServerPath` / `LlamaModelPath` のユーザー編集機能を維持すること。

## 3. 前提・仮定
- OS は Windows。
- `TranslationServiceLlama\LlamaCpp\` に `llama-server.exe` と関連 DLL を配置する運用。
- モデル格納ディレクトリは `TranslationServiceLlama\LlamaCpp\Models` を利用する。
- 対象モデルは `qwen3-1_7b-instruct-q4_k_m.gguf` を採用する。
- `llama-server.exe` の固定パスは `TranslationServiceLlama\LlamaCpp\llama-server.exe`。
- モデルの固定パスは `TranslationServiceLlama\LlamaCpp\Models\qwen3-1_7b-instruct-q4_k_m.gguf`。
- `uv` が利用可能。
- NVIDIA ドライバーはユーザー環境で事前に導入済み。

## 4. 現状整理
- `pyproject.toml` に CUDA 依存を追加すれば DLL は `.venv\Lib\site-packages\nvidia\...\bin` に配置可能。
- `llama-server.exe` は Python import 解決を使わないため、PATH 注入なしでは DLL を見つけられない。
- モデル（GGUF）は手動配置前提で、未配置時は起動失敗しやすい。
- 現在は `LlamaServerPath` / `LlamaModelPath` が設定値で変更可能なため、配布想定と実行実体がずれる可能性がある。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `TranslationServiceLlama/pyproject.toml`
- `TranslationServiceLlama` のランチャー（gRPC サービス起動コード）
- `TranslationServiceLlama/LlamaCpp/*`（ネイティブ実行ファイル群）
- `TranslationServiceLlama/LlamaCpp/Models/*`（自動取得モデル保管先）
- `TranslationServiceLlama/model_manifest.json`（新規; 取得元URL・revision・sha256 を固定）

### モデル配布元の推奨（固定方針）
- モデル取得元は Hugging Face の単一配布元に固定し、**revision（コミット）固定 URL** を使う。
- 取得URLはコード直書きではなく `model_manifest.json` で管理する。
- マニフェストに以下を保持する:
  - `filename`: `qwen3-1_7b-instruct-q4_k_m.gguf`
  - `download_url`: 固定URL（revision付き）
  - `sha256`: 期待ハッシュ
  - `size_bytes`: 期待サイズ（任意だが推奨）

### データフロー / シーケンス
1. gRPC サービス起動前に排他ロックを取得（同時起動時の重複DL防止）。
2. `uv sync --project TranslationServiceLlama` を「初回または lock/manifest 変更時のみ」実行。
3. `TranslationServiceLlama\LlamaCpp\Models` を作成し、`qwen3-1_7b-instruct-q4_k_m.gguf` の存在を確認。
4. 不足時はマニフェスト URL から `.tmp` で受信し、SHA256 検証後に rename。
5. `.venv\Lib\site-packages\nvidia\{cuda_runtime,cublas,nvjitlink,cudnn}\bin` を列挙。
6. `ProcessStartInfo.Environment["PATH"]` に 5 のパスを前置して `llama-server.exe` を起動。
7. 起動時に `llama-server.exe` / モデルは固定パス（`TranslationServiceLlama\LlamaCpp\llama-server.exe` / `TranslationServiceLlama\LlamaCpp\Models\qwen3-1_7b-instruct-q4_k_m.gguf`）のみを使用する。
8. 起動失敗時は stderr / exit code / モデル検証結果 / DLL検証結果をログ化し、UIへ通知。

### 既存パターンへの整合
- `CTranslate2` 側と同じく「依存取得・起動環境構築」をホスト層に集約。
- モデル取得も同層に寄せ、UI層に配布処理を漏らさない。

## 6. インターフェース設計
### pyproject 依存
`TranslationServiceLlama/pyproject.toml` に以下を追加/維持:
- `nvidia-cuda-runtime-cu12; sys_platform == "win32"`
- `nvidia-cublas-cu12; sys_platform == "win32"`
- `nvidia-cudnn-cu12; sys_platform == "win32"`
- `nvidia-nvjitlink-cu12; sys_platform == "win32"`

### 起動前検証 API（内部）
- `EnsurePythonRuntimeAsync()`
- `EnsureLlamaModelAsync()`（`qwen3-1_7b-instruct-q4_k_m.gguf` の存在確認 + DL + SHA256検証）
- `CollectNvidiaDllBinPaths()`
- `ValidateLlamaNativeFiles()`
- `ResolveFixedLlamaPaths()`（`llama-server.exe` / モデルの固定パス解決）
- `StartLlamaServerAsync(envPathPrefix)`

### バリデーション
- 必須ファイル: `llama-server.exe`, `llama.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-cuda.dll`, `mtmd.dll`
- モデル: `TranslationServiceLlama\LlamaCpp\Models\qwen3-1_7b-instruct-q4_k_m.gguf`
  - サイズ > 0
  - SHA256 一致（必須）
- 必須 CUDA DLL: `cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`
- 欠落時は実行前にエラー化（クラッシュ待ちしない）。

## 7. 実装手順（ステップ分割）
1. `model_manifest.json` を追加し、URL/revision/sha256 を固定。
2. モデル保存先 `TranslationServiceLlama\LlamaCpp\Models` の作成と存在確認を実装。
3. `qwen3-1_7b-instruct-q4_k_m.gguf` の自動DL（`.tmp` + rename + SHA256検証）を実装。
4. `LlamaServerPath` / `LlamaModelPath` を固定パス運用へ変更（設定読み取りを廃止）。
5. `uv sync` を初回・更新時のみ実行するガードを実装。
6. `.venv` の NVIDIA `bin` 収集と PATH 前置を実装。
7. ネイティブ必須ファイル検証（`mtmd.dll` 含む）を実装。
8. 起動失敗時のログ・ダイアログ文言を統一。
9. 手動検証（正常/依存欠落/モデル欠落/ハッシュ不一致/ドライバー不整合）を実施。

## 8. 非機能要件チェック
- 性能: 初回は `uv sync` + モデルDLで重い。2回目以降は差分なしならスキップ。
- セキュリティ: モデル配布元を固定し、SHA256 で改ざん検知。
- 可観測性: ログに「モデルURL(revision)」「SHA検証結果」「DLLパス」「exit code」を残す。
- 互換性: Windows + CUDA12 系を明示サポート。
- 運用: モデル更新時は `model_manifest.json` 更新を必須化。

## 9. リスクと緩和策
- Risk: `uv sync` 失敗（ネットワーク/ミラー障害）。
- Mitigation: リトライ回数・タイムアウト設定、失敗原因を UI/ログに明示。

- Risk: モデルDL中断で不完全ファイルが残る。
- Mitigation: `.tmp` 受信 + 完了時 rename。起動時に `.tmp` を無視/削除。

- Risk: モデル配布元変更でURL無効化。
- Mitigation: `model_manifest.json` の revision 固定更新運用とフォールバックURL（任意）を準備。

- Risk: CUDA DLL 版数不整合。
- Mitigation: CUDA12 系に固定し、起動時に DLL バージョン記録。

- Risk: 既存ユーザー設定に残っている `LlamaServerPath` / `LlamaModelPath` が無視され、混乱する。
- Mitigation: リリースノートとUI文言で「固定パス化」を明示し、設定項目をUIから削除する。

## 10. 影響範囲（変更ファイル候補・移行・ドキュメント更新）
- `TranslationServiceLlama/pyproject.toml` — CUDA 依存定義。
- `TranslationServiceLlama/model_manifest.json` — モデル取得先/ハッシュ定義（新規）。
- `TranslationServiceLlama` サービス起動コード — 固定パス解決、モデル取得、`uv sync`、PATH 注入、事前検証。
- `Models/AppSettings.cs` — `LlamaServerPath` / `LlamaModelPath` を削除または廃止注記。
- `MainWindow.xaml` / `MainWindow.xaml.cs` — `LlamaServerPath` / `LlamaModelPath` 入力UIを削除。
- `Doc/*` — セットアップ手順とトラブルシュート追記。

## 11. Definition of Done
- `uv sync` 後、`.venv` の NVIDIA `bin` から CUDA DLL が検出できる。
- 初回起動で `qwen3-1_7b-instruct-q4_k_m.gguf` が `TranslationServiceLlama\LlamaCpp\Models` に自動配置される。
- 配置モデルの SHA256 が `model_manifest.json` と一致する。
- PATH 注入後に `llama-server` が起動し、`/v1/models` が `200` を返す。
- 翻訳APIで最小1ケース（英→日など）が成功し、空文字でない結果が返る。
- `mtmd.dll` 欠落時に事前検証で明示エラーが出る。
- UI から Llama エンジン起動時に、失敗理由が判別可能な文言で表示される。
