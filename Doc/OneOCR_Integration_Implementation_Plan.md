# OneOCR Integration Implementation Plan

## 1. 概要（1–3行）
Snipping Tool 系 OneOCR は、この端末で `Tools/OneOcrExperiment` から実行でき、`text + bbox + confidence` を約 1 秒で取得できることを確認済みである。  
本体統合では、PoC をそのまま都度起動するのではなく、既存の `Paddle / NDLOCR / VisionLLM` と同じ「ローカル長寿命 gRPC ホスト」として組み込む。  
v1 は manual vendor 配置前提・fail fast 前提で、private 実装の不安定さをアプリ全体へ広げないことを優先する。

## 2. ゴール / 非ゴール
### ゴール
- `OcrEngineKind.OneOcr` を追加し、既存 OCR エンジンと同じ切替点から選択できるようにする。
- OneOCR は `uv + Python` の別プロセス gRPC ホストとして常駐させ、毎回 Python を起動しない構成にする。
- 既存 `OcrGrpc.proto` を再利用し、C# 側の OCR 呼び出しパターンを増やしすぎない。
- v1 では現在のアプリが必要とする `line rect + confidence` を安定供給する。

### 非ゴール
- Snipping Tool private ファイルの自動抽出、自動コピー、再配布。
- `Windows App SDK` 化や package identity 対応。
- polygon ベースの全面レイアウト刷新。
- VisionLLM hybrid geometry の即時 OneOCR 対応。
- private DLL 更新追従を完全自動化すること。

## 3. 前提・仮定
- `Tools/OneOcrExperiment` で、`oneocr.dll`、`oneocr.onemodel`、`onnxruntime.dll` の 3 ファイルだけで OCR 実行できることを確認済み。
- 現時点の OneOCR 実験では、明示的な言語指定なしで英字 UI 画像を十分高精度に読めている。
- 既存アプリには `OcrGrpc.proto`、`GrpcHostBase`、`ResourceHostFacade`、`uv.exe` ベースの Python ホスト起動パターンが既にある。
- 現在の `OcrResultModel` / `OcrLine` は polygon を持たず、矩形と confidence を中心に扱う。


## 4. 現状整理
- OCR エンジン切替は `Models/OcrEngineKind.cs` と `Services/OcrEngine.cs` で一元化されている。
- 外部 OCR は `PaddleGrpcOcrProvider`、`PaddleVlGrpcOcrProvider`、`NdlGrpcOcrProvider`、`VisionLlmGrpcOcrProvider` が `IOcrProvider` を実装している。
- 長寿命ホストの起動・停止・再起動は `GrpcHostBase`、`ResourceHostFacade`、`ResourceHostCommandController` にまとまっている。
- `OcrGrpc.proto` は `Health` / `Recognize` / `OcrResponse.json` の単純な契約で、OneOCR もこの面にそのまま乗せられる。
- `OverviewControl.xaml` と `SettingsViewModel.cs` が OCR engine 選択 UI の主な差し込み点である。
- 現行 `OcrEngine` は外部 OCR 失敗時に WinRT へフォールバックする実装を持つため、OneOCR も当面は同じ大域挙動に乗る。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `OcrServiceOneOcr/`
  - `server.py`
  - `oneocr_bridge.py`
  - `pyproject.toml`
  - `uv.lock`
  - `vendor/.gitkeep`
  - `README.md`
- `Services/OneOcrGrpcHost.cs`
  - `uv run --project OcrServiceOneOcr python server.py` でホストを起動する。
- `Services/OneOcrGrpcOcrProvider.cs`
  - gRPC で JSON を受け取り、`OcrResultModel` へ変換する。
- `Models/AppSettings.cs`
  - OneOCR host 設定を保持する。
- `Services/Application/ResourceHostFacade.cs`
  - OneOCR host の起動計画、停止、失敗時 disable を管理する。

### データフロー / シーケンス
1. ユーザーが OCR engine に `OneOCR` を選ぶ。
2. `ResourceHostFacade` が `OneOcrGrpcHost` を必要ホストとして計画する。
3. `OneOcrGrpcHost` が `uv` で `OcrServiceOneOcr/server.py` を起動する。
4. `server.py` は起動時に vendor ファイル存在を検証し、OneOCR pipeline を eager init する。
5. `OcrEngine` は `OneOcrGrpcOcrProvider` 経由で PNG bytes を送る。
6. Python 側は line / word / polygon / confidence を抽出し、JSON で返す。
7. C# 側は v1 では `lines[]` を `OcrLine(Text, Rect, Confidence, ...)` に変換し、既存後段処理へ流す。

### 既存パターンへの整合
- OCR 呼び出し契約は `IOcrProvider` を維持する。
- ホスト管理は `GrpcHostBase` と `ResourceHostFacade` を使い回す。
- `OcrGrpc.proto` は変更しない。
- `uv` 実行ファイルは既存 `Tools\uv\uv.exe` を流用する。

### 直接 CLI 呼び出しを採らない理由
- `probe.py` を OCR のたびに起動すると、Python 起動コストが毎回乗り、現在の hotkey 主体 UX と相性が悪い。
- 既に本体には gRPC host の起動・再起動・ready 判定・ログ基盤があるため、新規方式を増やす合理性が薄い。

## 6. インターフェース設計
### Python 側
- `OcrServiceOneOcr/server.py`
  - `Health` は pipeline 初期化完了後だけ `ready=true` を返す。
  - `Recognize` は `request.language` を互換維持のため受け取るが、v1 では無視する。
  - `text_detection_model_name` / `text_recognition_model_name` も v1 では未使用とする。

### 返却 JSON
- v1 では次を返す。
  - `imageWidth`
  - `imageHeight`
  - `imageAngle`
  - `lines[]`
  - `words[]`
- C# 側 provider はまず `lines[]` を正とする。
  - `line.text`
  - `line.bbox`
  - `line.words[]` の confidence 群
- line confidence は次のどちらかで統一する。
  - 推奨: line 内 word confidence の平均
  - fallback: word が無い line は `1.0f`

### C# 側マッピング
- OneOCR の polygon は v1 では `Rect(left, top, width, height)` に包絡変換する。
  - WHY: 現在の `OcrLine` と overlay 後段は矩形前提であり、ここで polygon を無理にねじ込むと影響範囲が急拡大するため。
- word 単位 geometry は JSON に残すが、v1 の本体処理では未使用とする。

### AppSettings 追加項目（案）
- `EnableOneOcrGrpcHost` : `true`
- `OneOcrGrpcEndpoint` : `"http://127.0.0.1:50054"`
- `OneOcrGrpcHost` : `"127.0.0.1"`
- `OneOcrGrpcPort` : `50054`
- `OneOcrGrpcReadyTimeoutMs` : `60000`
- `OneOcrGrpcRestartMax` : `3`
- `OneOcrGrpcRestartWindowSeconds` : `30`
- `OneOcrVendorDir` : `"OcrServiceOneOcr\\vendor"`

### UI 方針
- `OverviewControl.xaml` の OCR engine ComboBox に `OneOCR (gRPC)` を追加する。
- `SettingsViewModel` / `MainWindowViewModel` に `OneOcr` タグと表示名を追加する。
- v1 の専用 UI は最小にする。
  - エンジン選択
  - 必要なら vendor path 表示
  - 既存「Apply & Restart OCR host」操作を流用
- host / port / timeout の詳細編集 UI は v1 では増やさない。
  - WHY: private 実装の立ち上げを最優先し、設定面の複雑化を避けるため。

## 7. 実装手順（ステップ分割）
### Step 1: Python service 化
- `Tools/OneOcrExperiment` で検証した bridge を `OcrServiceOneOcr` へ昇格する。
- `server.py` を追加し、`OcrGrpc.proto` 契約で `Health` / `Recognize` を提供する。
- `uv sync` で `.venv` を作り、単体で gRPC 応答できる状態にする。

### Step 2: C# provider / host 追加
- `OneOcrGrpcHost.cs` を追加し、`GrpcHostBase` 継承で起動・ready 判定・restart policy を実装する。
- `OneOcrGrpcOcrProvider.cs` を追加し、返却 JSON を `OcrResultModel` へ変換する。
- `OcrEngine.cs` に `OneOCR` provider 分岐を追加する。

### Step 3: 設定・UI 接続
- `OcrEngineKind` に `OneOcr` を追加する。
- `AppSettings`、`SettingsViewModel`、`MainWindowViewModel`、`OverviewControl.xaml` を更新する。
- 必要な settings rule / normalizer を追加し、endpoint / timeout / vendor dir を正規化する。

### Step 4: host orchestration 接続
- `ResourceHostFacade` に `oneocr_grpc` を追加する。
- `ResourceHostCommandController` の OCR host restart で `OneOcr` を扱う。
- bootstrap preview に「Python runtime setup」と「manual vendor placement required」を出せるようにする。

### Step 5: 実機検証
- `Tools\OneOcrExperiment\input\test.png` 相当の画像でアプリから OCR 実行する。
- line rect、text、confidence が overlay まで通ることを確認する。
- OneOCR host 停止・再起動・vendor 欠落時エラーを確認する。

## 8. 非機能要件チェック
### 性能
- per-request 起動は禁止し、長寿命ホストで常駐させる。
- 初回 ready は model load を含むため数秒まで許容、2回目以降の OCR は現在 PoC 水準を維持する。

### セキュリティ / 配布
- private ファイルは Git 管理しない。
- private ファイルの自動ダウンロードや再配布はしない。
- vendor 不足時は明示エラーにし、暗黙フォールバックや隠れたコピー処理は入れない。

### 可観測性
- Python 側ログ:
  - `stage=ocr_grpc host=oneocr event=request_bytes`
  - `stage=ocr_grpc host=oneocr event=response_bytes`
  - `stage=ocr_grpc host=oneocr event=timing`
- C# 側ログ:
  - host start / ready / stop / fail
  - provider request / parse failure

### 互換性
- `OcrGrpc.proto` は維持する。
- `OcrEngineKind` は settings.json に永続化されるため、既存数値値の順序を壊さない。
  - 推奨: `OneOcr = 6` を末尾追加する。

### 運用
- vendor ファイル欠落時は `OneOCR` を自動で有効化しない。
- 既存の `Restart OCR host` 操作で OneOCR も再起動できるようにする。

## 9. リスクと緩和策
- Risk: private DLL 契約が Snipping Tool 更新で変わる。
- Mitigation: OneOCR は専用 service フォルダに隔離し、bridge を薄く保つ。

- Risk: app 本体は line rect しか持たず、word polygon の価値を活かし切れない。
- Mitigation: v1 は line rect 統合で止め、word/polygon は JSON に残して将来拡張へ回す。

- Risk: vendor ファイル手動配置が分かりにくい。
- Mitigation: `README` と bootstrap preview で必要ファイル名と配置先を明示する。

- Risk: OneOCR host 失敗時に user が原因を掴みにくい。
- Mitigation: vendor 欠落、DLL export 欠落、ready timeout を区別したエラーメッセージを出す。

- Risk: CPU 実行なので重い画像で OCR 中に UI 応答性へ影響が出る。
- Mitigation: 本体 UI スレッドでは待たず、既存 async 経路と host 別プロセスを維持する。

## 10. 影響範囲
- Python 側
  - `OcrServiceOneOcr/` 新規
- C# 側
  - `Models/OcrEngineKind.cs`
  - `Models/AppSettings.cs`
  - `Services/OcrEngine.cs`
  - `Services/OneOcrGrpcHost.cs` 新規
  - `Services/OneOcrGrpcOcrProvider.cs` 新規
  - `Services/Application/ResourceHostFacade.cs`
  - `Services/Application/ResourceHostCommandController.cs`
  - `ViewModels/SettingsViewModel.cs`
  - `ViewModels/MainWindowViewModel.cs`
  - `UI/OverviewControl.xaml`
  - `UI/OcrEnginesControl.xaml`
- Doc
  - OneOCR setup / vendor 配置手順

## 11. Definition of Done
- `OneOcr` を OCR engine として UI から選択できる。
- `OcrServiceOneOcr` が `uv` で起動し、`Health` / `Recognize` に応答する。
- vendor ファイルが揃っていれば、アプリから OneOCR 実行で line text / rect / confidence が返る。
- vendor 欠落時は明示エラーになり、原因がログと UI の両方で分かる。
- `Restart OCR host` で OneOCR を再起動できる。
- `settings.json` の既存ユーザーに破壊的影響がない。

## 12. 推奨判断
- 採用する統合方式: `long-lived local gRPC host`
- 採用しない統合方式: `probe.py を毎回起動する direct CLI OCR`
- v1 の扱い: `実験的だが常用可能なローカル OCR オプション`
