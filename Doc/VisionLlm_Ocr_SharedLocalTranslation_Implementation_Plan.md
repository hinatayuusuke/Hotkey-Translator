# VisionLLM OCR Shared Local Translation Implementation Plan

1. **概要（1-3行）**

VisionLLM を新しい OCR エンジンとして追加する。  
VisionLLM 選択時にローカル翻訳が有効なら、既存の純粋翻訳用 `TranslationServiceLlama` ではなく、同じ VisionLLM サービス/モデルへ翻訳も流して VRAM 重複を避ける。  
非 VisionLLM 時の OCR と LlamaCpp 翻訳は現状のままとする。  
Python 実行環境は `TranslationServiceLlama` と分離し、`OcrServiceVisionLlm` を別プロジェクトとして持つ。

2. **ゴール / 非ゴール**

- ゴール
- VisionLLM を `OcrEngineKind` の1項目として追加する。
- VisionLLM OCR の本番パスを `OCR -> grouping -> diff -> translate -> overlay` に自然接続する。
- VisionLLM 選択時かつローカル翻訳有効時だけ、既存 `LlamaGrpcTranslationProvider` の接続先を VisionLLM サービスへ切り替えて同一モデルを共有する。
- VisionLLM は Paddle / PaddleVL とは排他的に運用する。

- 非ゴール
- 既存 `TranslationServiceLlama` を廃止すること。
- VisionLLM と純粋翻訳モデルを同時に自動切替する複雑なフォールバックを入れること。
- 画像直接翻訳を本番既定にすること。

3. **前提・仮定**

- VisionLLM は画像入力から OCR 相当のテキスト抽出を実用精度で返せる。
- VisionLLM は text-only 翻訳も同じモデル/同じ `llama-server` 系プロセスで処理できる。
- VisionLLM OCR は ROI 有無に関係なく動作させるが、レトロゲーム用途では ROI を使った方が性能と品質の両面で有利である。
- VisionLLM 用モデル設定と host 設定は、既存の純粋翻訳用 Llama 設定と分離する。
- VisionLLM を選択したときは、Paddle / PaddleVL Host を同時起動しない前提にする。
- 現在の実装は座標を返さないため、既存 overlay へ流す矩形は line 数に応じた合成矩形を使う。

4. **現状整理**

- OCR 切替は `Services/OcrEngine.cs` で `OcrEngineKind` に応じて分岐している。
- 既存 OCR は `WinRt / Paddle / PaddleVL / NDL` の4系統で、失敗時は WinRT フォールバックがある。
- 翻訳は `TranslationFallbackService` が provider 優先順に実行する。
- ローカル翻訳は `LlamaGrpcTranslationProvider` が `TranslationServiceLlama` の gRPC を使う。
- ROI は `Services/PipelineOrchestrator.cs` の `GetRoiBounds()` で決まり、`EnableRoi=false` ならフレーム全体になる。
- `EnableFixedRoiOverlay` は表示方式であり、OCR 入力範囲の強制とは意味が違う。

5. **提案アーキテクチャ**

- コンポーネント構成
- `VisionLlmGrpcHost`
  - VisionLLM 用の Python/gRPC サーバ起動管理。
- `VisionLlmGrpcOcrProvider`
  - 画像 OCR を呼ぶ provider。
- `OcrEngine`
  - `OcrEngineKind.VisionLlm` を受けて OCR provider を選ぶ。
- `LlamaGrpcTranslationProvider`
  - VisionLLM OCR + shared local translation 有効時だけ、既存 Llama endpoint の代わりに VisionLLM endpoint を使う。

- データフロー / シーケンス
- VisionLLM OCR 選択時:
  1. 現行パイプラインどおり、設定された ROI があれば ROI を、無ければフレーム全体を OCR 入力に使う。
  2. VisionLLM OCR provider が OCR テキストを返す。
  3. provider 側で line 数に応じた合成矩形を作り、grouping/diff は既存処理を使う。
  4. ローカル翻訳有効時は既存 `LlamaGrpcTranslationProvider` が同一 VisionLLM サービスへ text-only 翻訳を送る。
  5. overlay は既存 OCR と同じルートを使う。
- 非 VisionLLM OCR 時:
  - 現状どおり、既存 OCR + 既存翻訳 provider を使う。

- 既存パターンへの整合
- Paddle/NDL と同じく「Host」と「Provider」を分ける。
- 翻訳 provider は既存 `ITranslationProvider` 契約を維持し、新規 provider は増やさない。
- `TranslationFallbackService` 自体の責務は変えず、既存 Llama provider の endpoint 切替だけで shared translation を成立させる。

6. **インターフェース設計**

- AppSettings 追加候補
- `VisionLlmGrpcEndpoint`
- `VisionLlmGrpcHost`
- `VisionLlmGrpcPort`
- `VisionLlmGrpcReadyTimeoutMs`
- `VisionLlmGrpcRestartMax`
- `VisionLlmGrpcRestartWindowSeconds`
- `EnableVisionLlmGrpcHost`
- `EnableVisionLlmExclusiveOcr`
- `VisionLlmHost`
- `VisionLlmPort`
- `VisionLlmContextSize`
- `VisionLlmGpuLayers`
- `VisionLlmThreads`
- `VisionLlmParallel`
- `VisionLlmBatchSize`
- `VisionLlmMaxTokens`
- `VisionLlmSelectedModelFileName`
- `VisionLlmSelectedMmprojFileName`
- `VisionLlmMaxImageSide`
- `EnableVisionLlmSharedLocalTranslation`
- 現行実装では advanced 設定 UI は未追加で、詳細値は `settings.json` から変更する。

- OcrEngineKind 追加
- `VisionLlm = 5`
- COMPAT: 既存 enum 値は変更しない。

- gRPC 契約
- VisionLLM OCR サービスは少なくとも以下を持つ。
- `Recognize(image_bytes, language) -> json{text}`
- `Translate(texts, source_lang, target_lang) -> translations`
- OCR と翻訳を同一サーバ・同一モデルで処理できることを前提にする。
- 既存 `OcrGrpc.proto` / `TranslationGrpc.proto` を再利用する。

- バリデーション
- VisionLLM 選択時にモデルまたは mmproj 未設定なら Host 起動失敗として明示ログを出す。
- `EnableVisionLlmSharedLocalTranslation=true` でも VisionLLM OCR 以外では無視する。
- ROI 未設定でも VisionLLM OCR は実行可能とする。強制停止はしない。
- VisionLLM 選択時は Paddle / PaddleVL Host を起動しない。

7. **実装手順（ステップ分割）**

- Step 1
- `OcrEngineKind.VisionLlm` と `AppSettings` の VisionLLM 設定項目を追加する。
- UI に VisionLLM OCR の選択肢を追加する。

- Step 2
- `VisionLlmGrpcHost` と `VisionLlmGrpcOcrProvider` を追加する。
- `OcrEngine` へ VisionLLM 分岐を追加する。
- `ResourceHostFacade` に VisionLLM Host の起動停止を組み込む。
- VisionLLM 選択時は Paddle / PaddleVL を停止し、排他運用にする。

- Step 3
- VisionLLM 用 Python/gRPC サービスを追加する。
- OCR RPC と text translation RPC を同一プロセス・同一モデルで提供する。
- OCR RPC は text-only OCR を返す。

- Step 4
- `LlamaGrpcTranslationProvider` に VisionLLM endpoint への切替条件を追加する。
- `ResourceHostFacade` で shared local translation 時は標準 Llama host を起動しない。

- Step 5
- `PipelineOrchestrator` では VisionLLM を既存 OCR と同じ ROI 解決フローへ乗せる。
- ROI がある場合はその範囲、無い場合はフレーム全体を OCR 入力とする。
- 返却テキストは既存 OCR と同じ grouping / diff / overlay ルートへ流す。
- 座標未実装の間は provider 側で合成矩形を作る。

- Step 6
- ログと UI 表示を追加する。
- `OCR engine: VisionLLM.`
- `Translation provider active: VisionLLM(shared).`
- `VisionLLM OCR completed.`
- `VisionLLM exclusive OCR mode active.`

8. **非機能要件チェック**

- 性能
- VisionLLM OCR とローカル翻訳を同一モデル・同一プロセスへ寄せることで VRAM 重複を避ける。
- `VisionLlmMaxImageSide` を持ち、送信前縮小を可能にする。
- ROI は任意としつつ、未設定時は処理範囲が広がるため性能低下がありうることを UI/ログで明示する。
- VisionLLM を有効にしたときは他 OCR Host を止め、GPU/CPU メモリ重複を避ける。

- セキュリティ
- 外部公開は不要。既存ローカル gRPC と同じく localhost 限定前提。

- 可観測性
- Host 起動、ready、OCR 実行、translation 実行、shared translation 選択をログへ出す。
- `OCR engine: VisionLLM.` と既存 Llama provider の active ログで経路確認できる。

- 互換性
- 非 VisionLLM OCR 時の挙動は変更しない。
- 既存 `TranslationServiceLlama` は維持する。

- 運用
- VisionLLM OCR 用モデルと純粋翻訳用モデルは別設定として共存させる。
- shared translation は VisionLLM OCR 時だけ使う。

9. **リスクと緩和策**

- Risk: VisionLLM の text translation 品質が純粋翻訳モデルより落ちる。
- Mitigation: `EnableVisionLlmSharedLocalTranslation` を設定で切れるようにし、必要なら既存 LlamaCpp 翻訳へ戻せるようにする。

- Risk: ROI 未設定時に VisionLLM OCR が重く不安定になる。
- Mitigation: 実行は許可しつつ、`VisionLlmMaxImageSide` とログで負荷を抑制・可視化する。

- Risk: OCR と翻訳を同一プロセスへ寄せた結果、同時実行競合が起きる。
- Mitigation: まずは直列処理前提にし、server 側の parallel は 1 を既定にする。

- Risk: Vision モデル設定と純粋翻訳モデル設定を混同しやすい。
- Mitigation: UI と `AppSettings` を分離し、項目名も `VisionLlm*` と `Llama*` で分ける。

- Risk: VisionLLM と Paddle 系 OCR を同時起動すると VRAM/メモリ浪費や状態競合が起きる。
- Mitigation: VisionLLM 選択時は Paddle / PaddleVL を停止し、排他運用を前提にする。

- Risk: 座標未実装のため overlay 矩形が厳密でない。
- Mitigation: 初期実装は text-first を優先し、矩形は合成で暫定対応する。

10. **影響範囲**

- `Models/OcrEngineKind.cs`
- `Models/AppSettings.cs`
- `Services/OcrEngine.cs`
- `Services/Application/ResourceHostFacade.cs`
- `Services/Settings/FeatureSettings/HostFeatureSettings.cs`
- `Services/Settings/FeatureSettings/FeatureSettingsProvider.cs`
- `Services/PipelineOrchestrator.cs`
- `Services/LlamaGrpcTranslationProvider.cs` は既存維持
- `Services/VisionLlmGrpcHost.cs` 新規
- `Services/VisionLlmGrpcOcrProvider.cs` 新規
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- 新規 Python プロジェクト（例: `OcrServiceVisionLlm`）

11. **Definition of Done**

- VisionLLM が OCR エンジン一覧に表示され、設定保存できる。
- VisionLLM OCR で Run したとき、既存 overlay パイプラインまで通る。
- VisionLLM OCR のレスポンスからテキストが既存 OCR と同じ後続処理へ渡る。
- VisionLLM OCR + ローカル翻訳有効 + shared translation 有効時、既存 `LlamaGrpcTranslationProvider` が VisionLLM endpoint を使う。
- このとき純粋翻訳用 `TranslationServiceLlama` を起動しなくても翻訳が成立する。
- VisionLLM 選択時は Paddle / PaddleVL Host が起動しない。
- 非 VisionLLM OCR 時は既存 LlamaCpp 翻訳が従来どおり動く。
- ROI 未設定の VisionLLM Run でも実行できる。
- 主要ログに OCR engine / translation provider / VisionLLM complete が出る。
