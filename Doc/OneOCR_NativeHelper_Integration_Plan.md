# OneOCR Native Helper Integration Plan

## 1. 概要（1–3行）
Snipping Tool 系 OneOCR は private DLL 依存なので、本体 WPF プロセスへ直接 P/Invoke せず、小さな native helper process として隔離する。  
本体とは軽量 IPC で接続し、helper が `oneocr.dll` をロードして OCR を実行し、JSON で `text + rect + confidence` を返す。  
v1 は「単一ローカル helper 常駐 + fail fast + manual vendor 配置」を前提にし、Python/uv や gRPC は使わない。OneOCR は既に行としてまとまった結果を返す前提で扱い、他 OCR 向けの重い後段統合ロジックへ寄せすぎない。

## 2. ゴール / 非ゴール
### ゴール
- `OcrEngineKind.OneOcr` を追加し、既存 OCR エンジンと同じ UI から選択できるようにする。
- `oneocr.dll` は別プロセスの native helper だけが読む構成にし、本体クラッシュ半径を最小化する。
- 既存アプリが必要とする `line text + rect + confidence` を返し、overlay と翻訳パイプラインへ接続する。
- Python/uv と gRPC を増やさず、OneOCR 専用の最小ランタイムに留める。

### 非ゴール
- Snipping Tool private ファイルの自動抽出、自動更新、再配布。
- polygon を使った overlay 全面刷新。
- VisionLLM geometry hybrid への即時対応。
- Windows App SDK / package identity ベースの公開 API 化。
- 複数クライアント同時接続や汎用 OCR サーバ化。

## 3. 前提・仮定
- `Tools/OneOcrExperiment` で、この端末の `oneocr.dll`、`oneocr.onemodel`、`onnxruntime.dll` が実際に動くことを確認済み。
- `test.png` 実行では約 `1044.933 ms` で `20 lines / 38 words` を取得でき、line / word geometry も返っている。
- `oneocr.dll` の exported function 形状は、少なくとも現行 Snipping Tool バージョンでは Python / C++ 両方の先行実装と整合する。
- 本体には既に別プロセス起動・ログ捕捉・Named Pipe 利用の実績がある。
  - `Services/FlorenceOcrProvider.cs`
  - `Services/Hook/GraphicsHookClientService.cs`
  - `Native/HookHost/main.cpp`
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` では、`PaddleVllm` と `VisionLlm` は追加 line merge をスキップしており、OneOCR も同じ扱いへ寄せるのが自然である。

## 4. 現状整理
- OCR エンジン切替は `Models/OcrEngineKind.cs` と `Services/OcrEngine.cs` が一元管理している。
- 既存外部 OCR は gRPC host ベースだが、OneOCR は private DLL 1つを叩く単機能エンジンなので、同じ重い面を持ち込む必然は薄い。
- `OcrResultModel` / `OcrLine` は現在 polygon を保持せず、矩形と confidence 中心である。
- `OcrAndGroupStage` は現在 `PaddleVllm` と `VisionLlm` で `_lineGrouper.MergeLines(...)` を通していない。
- OneOCR の実測結果も line 単位が十分まとまっており、Paddle 向け merge tuning や token regrouping を前提にしない方が整合的である。
- `SettingsViewModel.cs`、`MainWindowViewModel.cs`、`OverviewControl.xaml` が OCR engine UI の主な差し込み点である。
- `GrpcHostBase` は使えるが、OneOCR helper では gRPC 自体が不要なので、起動監視の考え方だけ参考にして専用ライフサイクルへ寄せる方が自然である。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `Native/OneOcrHelper/` 新規
  - `OneOcrHelper.vcxproj`
  - `main.cpp`
  - `OneOcrBridge.h/.cpp`
  - `JsonProtocol.h/.cpp`
  - `README.md`
  - `vendor/.gitkeep`
- `Services/OneOcrProcessHost.cs` 新規
  - helper 起動、ready 待機、停止、プロセス監視を担当する。
- `Services/OneOcrProcessOcrProvider.cs` 新規
  - bitmap を PNG にし、helper へ送り、JSON を `OcrResultModel` に変換する。
- `Services/OneOcrProtocolClient.cs` 新規
  - helper との IPC を薄く包む。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
  - OneOCR は `PaddleVllm` / `VisionLlm` と同様に追加 merge をスキップする。
- `Models/AppSettings.cs`
  - OneOCR helper の最小設定だけを保持する。

### helper を native にする理由
- `oneocr.dll` を直接叩く側は C++ の方が先行実装知見があり、型の齟齬を詰めやすい。
- private DLL が異常終了しても、WPF 本体ではなく helper だけを落とせる。
- Python/uv や gRPC を追加しないので、配布と起動コストを減らせる。

### P/Invoke 直結を採らない理由
- private DLL 呼び出し失敗時の影響が本体へ直撃する。
- DLL 検索パス、ハンドル解放、メモリ破損の切り分けが WPF からだと重い。
- 今回は「精度が高い private OCR を試験常用する」が目的であり、「最速の in-process 実装」を取る局面ではない。

## 6. IPC 設計
### 推奨方式
- `Named Pipe + length-prefixed JSON`

### 採用理由
- 既存 repo に Named Pipe 実績があり、Windows 専用アプリとして自然。
- stdin/stdout の行境界やログ混在を避けやすい。
- helper 常駐時に request / response を複数回やり取りしやすい。
- 将来 cancellation や debug command を足す余地がある。

### 非採用
- `gRPC`
  - OneOCR 単体には重い。
- `stdin/stdout JSON`
  - v1 でも可能だが、ログ混在や framing の制約が増える。
- `shared memory`
  - 実装コストが過剰。

### Named Pipe 名
- 既定: `hotkey_translator_oneocr`
- 実際には PID suffix を持たせる。
  - 例: `hotkey_translator_oneocr_12345`
  - WHY: 多重起動やクラッシュ後再接続時の衝突を避けるため。

### request 形式
```json
{
  "id": "req-0001",
  "type": "recognize",
  "imageFormat": "png",
  "imageBytesBase64": "<base64>",
  "maxLineCount": 1000
}
```

### response 形式
```json
{
  "id": "req-0001",
  "ok": true,
  "durationMs": 0.0,
  "imageWidth": 0,
  "imageHeight": 0,
  "imageAngle": 0.0,
  "lines": [
    {
      "text": "",
      "bbox": [0.0, 0.0, 0.0, 0.0],
      "polygon": [[0.0, 0.0], [0.0, 0.0], [0.0, 0.0], [0.0, 0.0]],
      "words": [
        {
          "text": "",
          "confidence": 0.0,
          "bbox": [0.0, 0.0, 0.0, 0.0],
          "polygon": [[0.0, 0.0], [0.0, 0.0], [0.0, 0.0], [0.0, 0.0]]
        }
      ]
    }
  ],
  "error": null
}
```

### 制御メッセージ
- `ping`
- `shutdown`
- `version`

### ready 判定
- helper は起動後、vendor 検証と pipeline 初期化に成功した時だけ ready packet を返す。
- 例:
```json
{
  "type": "ready",
  "ok": true,
  "version": "1",
  "message": "OneOCR helper ready"
}
```

## 7. helper 側設計
### 起動フロー
1. `vendor/` の `oneocr.dll`、`oneocr.onemodel`、`onnxruntime.dll` 存在確認。
2. `SetDllDirectoryW` または同等の DLL 検索パス設定。
3. `CreateOcrInitOptions`、`CreateOcrPipeline`、`CreateOcrProcessOptions` を実行。
4. Named Pipe server を開く。
5. ready packet を返す。
6. request loop に入る。

### OCR 実行
- 画像 bytes は helper 側で decode する。
- decode は WIC か OpenCV のどちらかに統一する。
  - 推奨: WIC
  - WHY: OpenCV 依存を減らし、helper 配布物を軽く保つため。
- OneOCR へ渡す画素形式は BGRA に固定する。
- 結果は line / word / polygon / confidence を JSON に変換する。

### C++ ブリッジ設計
- `OneOcrBridge`
  - `Initialize(vendorDir)`
  - `Recognize(const ImageBuffer&, int maxLineCount)`
  - `Shutdown()`
- exported function 定義は `b1tg/win11-oneocr` ベースで固定する。
- helper 内部で使うデータ構造は生ポインタのまま広げず、RAII で閉じる。

### エラー方針
- vendor 欠落: 起動失敗
- DLL export 欠落: 起動失敗
- model key mismatch: 起動失敗
- request decode failure: per-request error response
- OCR 実行失敗: per-request error response

## 8. C# 側設計
### `OneOcrProcessHost`
- helper exe の起動・停止・再起動を担当する。
- `ProcessStartInfo` で helper を起動する。
- stdout / stderr をログへ流す。
- ready packet を timeout 付きで待つ。
- helper 異常終了時は running state を落とす。

### `OneOcrProtocolClient`
- Named Pipe 接続と request/response の framing を担当する。
- 1 request ごとに `id` を振る。
- v1 は単一同時実行に制限する。
  - WHY: 現在の OCR 実行経路は 1 本で十分であり、同時実行を先に入れると排他と cancellation が複雑化するため。

### `OneOcrProcessOcrProvider`
- bitmap を PNG bytes に変換する。
- `recognize` request を helper へ送る。
- response JSON を `OcrResultModel` に変換する。
- line confidence は line 内 word confidence 平均を採用する。
- polygon は v1 では包絡矩形へ落とす。
- helper が返した line はそのまま「完成済み line」とみなし、provider 側で token regrouping はしない。

### `OcrEngine.cs`
- `OcrEngineKind.OneOcr` 分岐を追加する。
- OneOCR failure 時の扱いは v1 では既存外部 OCR と同じく WinRT fallback を維持する。
  - ただしログには `OneOCR failed; falling back to WinRT.` を明示する。

### `OcrAndGroupStage.cs`
- `effectiveEngineKind is OcrEngineKind.PaddleVllm or OcrEngineKind.VisionLlm` の分岐に `OneOcr` を追加する。
- OneOCR は `filteredLines` をそのまま採用する。
  - WHY: OneOCR は既に語を行へまとめた geometry を返し、ここで共通 merge を再適用すると過剰結合や誤改行の原因になりやすいため。
- Paddle 系の `EnablePaddleConfidenceFilter` や merge tuning は OneOCR に適用しない。

## 9. AppSettings / UI 設計
### AppSettings 追加項目（案）
- `EnableOneOcrHelper` : `true`
- `OneOcrPipeName` : `"hotkey_translator_oneocr"`
- `OneOcrReadyTimeoutMs` : `30000`
- `OneOcrRestartMax` : `3`
- `OneOcrRestartWindowSeconds` : `30`
- `OneOcrVendorDir` : `"Native\\OneOcrHelper\\vendor"`
- `OneOcrHelperPath` : `"Native\\OneOcrHelper\\bin\\x64\\Release\\OneOcrHelper.exe"`

### UI 方針
- OCR engine ComboBox に `OneOCR (native helper)` を追加する。
- v1 の専用設定 UI は極小にする。
  - エンジン選択
  - 必要なら vendor path / helper path 表示
  - `Restart OCR host` ではなく `Restart OneOCR helper` 相当の軽い操作
- pipe 名や細かい timeout 編集 UI は v1 では出さない。
  - WHY: helper は OneOCR 専用で、既存 OCR host 群のような汎用サーバ設定を露出する意味が薄い。

## 10. 実装手順（ステップ分割）
### Step 1: native helper PoC 昇格
- `Tools/OneOcrExperiment` の bridge を C++ helper へ移植する。
- `Native/OneOcrHelper` を作成する。
- 単体 exe で `test.png` を読んで JSON を標準出力できるところまで持っていく。

### Step 2: Named Pipe 常駐化
- helper を request loop 化する。
- ready / recognize / shutdown の JSON プロトコルを実装する。
- 本体なしで疎通テストできる最小クライアントを用意する。

### Step 3: C# host / provider 追加
- `OneOcrProcessHost.cs`
- `OneOcrProtocolClient.cs`
- `OneOcrProcessOcrProvider.cs`
- `OcrEngine.cs` に OneOCR 分岐追加
- `OcrAndGroupStage.cs` に OneOCR merge skip 分岐追加

### Step 4: 設定と UI 接続
- `OcrEngineKind.OneOcr = 6` を追加する。
- `AppSettings`、`SettingsViewModel`、`MainWindowViewModel`、`OverviewControl.xaml`、必要なら `UI/OcrEnginesControl.xaml` を更新する。
- helper path / vendor path / timeout の設定正規化を追加する。

### Step 5: ライフサイクル統合
- `ResourceHostFacade` の重い gRPC 前提へ無理に統合せず、軽量な `OneOcrProcessSupervisor` を第一候補にする。
- `Restart OCR host` に便乗するより、OneOCR helper 専用の再起動経路を追加する。
- vendor 欠落時の UI エラーを追加する。

### Step 6: 実機検証
- `Tools\OneOcrExperiment\input\test.png` 相当画像で本体から OCR 実行する。
- overlay 位置、認識テキスト、confidence、再起動、異常終了復帰を確認する。

## 11. 非機能要件チェック
### 性能
- helper は常駐し、model load を 1 回に抑える。
- IPC はローカル Named Pipe のみで、gRPC より軽くする。
- PNG encode/decode は残るが、ネットワークスタックよりは単純になる。
- OCR 後の追加 line merge をスキップし、OneOCR 本来の行分割を維持する。

### 可観測性
- helper 側:
  - `stage=oneocr_helper event=start`
  - `stage=oneocr_helper event=ready`
  - `stage=oneocr_helper event=request`
  - `stage=oneocr_helper event=timing`
  - `stage=oneocr_helper event=error`
- C# 側:
  - `stage=oneocr_client event=connect/send/receive/restart/fallback`

### 互換性
- `OcrEngineKind` は settings 永続化対象なので数値値を崩さない。
- `OcrResultModel` は v1 では変更しない。
- polygon を追加したくなっても v1 は後方互換を壊さず矩形へ落とす。
- `EnableLineMerge` など既存 global 設定は残るが、OneOCR 実行時は merge skip を優先する。

### 運用
- helper が死んだら次回 OCR 要求時に 1 回だけ自動再起動を試す。
- restart storm を防ぐため、再起動回数制限を入れる。

## 12. リスクと緩和策
- Risk: helper の native 実装コストは Python より高い。
- Mitigation: `Tools/OneOcrExperiment` と `b1tg/win11-oneocr` の知見を移植し、OneOCR 部分を薄い bridge に限定する。

- Risk: helper と本体の IPC バグで OCR が詰まる。
- Mitigation: protocol を単純な request/response に限定し、v1 は単一同時実行のみとする。

- Risk: OpenCV などの追加ネイティブ依存が膨らむ。
- Mitigation: 画像 decode は WIC を第一候補にし、依存 DLL を増やさない。

- Risk: 既存 merge ロジックを通すと OneOCR の行が過剰結合される。
- Mitigation: OneOCR は `OcrAndGroupStage` で merge skip エンジンとして扱う。

- Risk: private DLL 更新で helper が突然動かなくなる。
- Mitigation: 起動時 ready 失敗を明示化し、OneOCR を disable して WinRT fallback へ戻せるようにする。

- Risk: 一部画像で line confidence の解釈が不安定。
- Mitigation: v1 は word confidence 平均で統一し、後で threshold 利用する場合は別途チューニングする。

## 13. 影響範囲
- `Native/OneOcrHelper/` 新規
- `Models/OcrEngineKind.cs`
- `Models/AppSettings.cs`
- `Services/OcrEngine.cs`
- `Services/OneOcrProcessHost.cs` 新規
- `Services/OneOcrProtocolClient.cs` 新規
- `Services/OneOcrProcessOcrProvider.cs` 新規
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
- `Services/Application/ResourceHostCommandController.cs` または OneOCR 専用 command hook
- `Services/Application/ResourceHostFacade.cs` ではなく OneOCR 専用 supervisor を第一候補
- `ViewModels/SettingsViewModel.cs`
- `ViewModels/MainWindowViewModel.cs`
- `UI/OverviewControl.xaml`
- `UI/OcrEnginesControl.xaml`
- setup / vendor 配置ドキュメント

## 14. Definition of Done
- `OneOCR (native helper)` を UI から選択できる。
- helper exe が起動し、ready を返す。
- 本体から `test.png` 相当の画像で line text / rect / confidence を取得できる。
- OneOCR 実行時に `OcrAndGroupStage` の追加 merge を通さず、helper の line 分割がそのまま overlay へ届く。
- helper 異常終了時に本体は落ちず、明示ログが残る。
- vendor 欠落時は原因が分かるエラーになる。
- Python/uv や gRPC に依存せず OneOCR を動かせる。

## 15. 推奨判断
- 推奨: `native helper process + Named Pipe`
