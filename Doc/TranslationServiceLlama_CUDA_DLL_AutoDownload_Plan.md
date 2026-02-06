# TranslationServiceLlama CUDA DLL Auto-Download Plan

## 1. 概要（1-3行）
`TranslationServiceLlama` の起動時に `uv sync` を使って CUDA 12 ランタイム DLL（`cudart64_12.dll` / `cublas64_12.dll` / `cublasLt64_12.dll`）を自動取得し、`llama-server.exe` 起動プロセスへ DLL 探索パスを注入する。
これにより、ユーザーが Windows のグローバル PATH を手動設定しなくても GPU 実行可能な状態を作る。

## 2. ゴール / 非ゴール
### ゴール
- `pyproject.toml` 依存だけで CUDA DLL を自動取得できるようにする。
- `llama-server.exe` 起動時に不足 DLL エラー（`0xC0000135`）を回避できるようにする。
- 失敗時に「不足が Python 依存か、同梱バイナリ不足か」を切り分けできるログを残す。

### 非ゴール
- `llama-server.exe` / `llama.dll` / `ggml*.dll` / `mtmd.dll` を Python 依存で自動取得すること。
- CUDA ドライバーの自動インストール。
- CPU/GPU 自動フォールバックの実装。

## 3. 前提・仮定
- OS は Windows。
- `TranslationServiceLlama\LlamaCpp\` に `llama-server.exe` と関連 DLL を配置する運用。
- `uv` が利用可能。
- NVIDIA ドライバーはユーザー環境で事前に導入済み。
- `mtmd.dll` は llama.cpp ビルド成果物として別途同梱する（`pip` では供給されない）。

## 4. 現状整理
- 現在の構成でも `pyproject.toml` に CUDA 依存を追加すれば DLL ファイル自体は `.venv\Lib\site-packages\nvidia\...\bin` に配置可能。
- ただし `llama-server.exe` は Python の import 解決を使わないため、PATH 注入なしでは DLL を見つけられない。
- 既存検証では `llama-server.exe` の不足依存として `mtmd.dll` が確認されている。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `TranslationServiceLlama/pyproject.toml`
- `TranslationServiceLlama` のランチャー（gRPC サービス起動コード）
- `TranslationServiceLlama/LlamaCpp/*`（ネイティブ実行ファイル群）

### データフロー / シーケンス
1. gRPC サービス起動前に `uv sync --project TranslationServiceLlama` を実行（初回または更新時）。
2. `.venv\Lib\site-packages\nvidia\{cuda_runtime,cublas,nvjitlink,cudnn}\bin` を列挙。
3. `ProcessStartInfo.Environment["PATH"]` に 2 のパスを前置して `llama-server.exe` を起動。
4. 起動失敗時は stderr / exit code をログ化し、UI 側へ明示的にエラー通知。

### 既存パターンへの整合
- 既存の `CTranslate2` 側で行っている「仮想環境 + DLL パス注入」の運用と同じ責務分離を採用する。
- Llama 側でも「依存取得」と「起動環境構築」をランチャー層に集約する。

## 6. インターフェース設計
### pyproject 依存
`TranslationServiceLlama/pyproject.toml` に以下を追加/維持:
- `nvidia-cuda-runtime-cu12; sys_platform == "win32"`
- `nvidia-cublas-cu12; sys_platform == "win32"`
- `nvidia-cudnn-cu12; sys_platform == "win32"`
- `nvidia-nvjitlink-cu12; sys_platform == "win32"`

### 起動前検証 API（内部）
- `EnsurePythonRuntimeAsync()`
- `CollectNvidiaDllBinPaths()`
- `ValidateLlamaNativeFiles()`
- `StartLlamaServerAsync(envPathPrefix)`

### バリデーション
- 必須ファイル確認: `llama-server.exe`, `llama.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-cuda.dll`, `mtmd.dll`
- 必須 CUDA DLL 確認: `cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`
- 欠落時は実行前にエラー化（実行してからのクラッシュ待ちをしない）。

## 7. 実装手順（ステップ分割）
1. `pyproject.toml` の依存を最終化し、`uv sync` 実行パスを統一。
2. ランチャーに `uv sync` 実行（初回/更新時）を実装。
3. `.venv` 配下の NVIDIA `bin` を収集して PATH 前置ロジックを実装。
4. `LlamaCpp` 同梱ファイル検証（`mtmd.dll` 含む）を実装。
5. 起動失敗時のログ・ダイアログメッセージを統一。
6. 手動検証ケース（正常/依存欠落/ドライバー不整合）を実施。

## 8. 非機能要件チェック
- 性能: `uv sync` は初回のみ重く、通常起動はキャッシュヒット前提。
- セキュリティ: ダウンロード元は `uv` 管理下（PyPI）に限定。
- 可観測性: 起動ログに「収集した DLL パス」「不足 DLL 名」「exit code」を残す。
- 互換性: Windows + CUDA12 系を明示サポート。
- 運用: 依存更新時は `uv lock` 更新を伴う。

## 9. リスクと緩和策
- Risk: `mtmd.dll` 未同梱で起動不能。
- Mitigation: 起動前検証で欠落を即時検出し、同梱不足メッセージを表示。

- Risk: `uv sync` 失敗（ネットワーク/ミラー障害）。
- Mitigation: リトライ回数とタイムアウトを設定し、失敗原因を UI とログに明示。

- Risk: CUDA DLL 版数不整合。
- Mitigation: サポート版（CUDA12 系）をドキュメントに固定し、起動時に DLL バージョンを記録。

## 10. 影響範囲（変更ファイル候補・移行・ドキュメント更新）
- `TranslationServiceLlama/pyproject.toml` — CUDA 依存定義。
- `TranslationServiceLlama` サービス起動コード — `uv sync` 実行、PATH 注入、事前検証。
- `Models/AppSettings.cs` / 設定 UI（必要時） — `LlamaServerPath` 運用注意の反映。
- `Doc/*` — セットアップ手順とトラブルシュート追記。

## 11. Definition of Done
- `uv sync` 後、`.venv` の NVIDIA `bin` から CUDA DLL が検出できる。
- PATH 注入付きで `llama-server.exe --help` が成功する。
- `mtmd.dll` 欠落時に事前検証で明示エラーが出る。
- UI から Llama エンジン起動時に、失敗理由がユーザーに判別可能な文言で表示される。
- ドキュメント（本ファイル）に沿って再現手順が第三者環境で再現できる。
