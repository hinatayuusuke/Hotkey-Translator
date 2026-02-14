# Refactoring_Layer2_Orchestration_Plan

1. **概要（1–3行）**
- 本計画は、層2（Orchestration）として `PipelineOrchestrator` と `SceneTextSnapshotService` に集中している実行分岐を段階責務へ分離するための実装案である。
- 目的は、OCR→差分→翻訳→Overlay の正常系/フォールバック系を明示化し、機能追加時の回帰リスクを下げること。
- 層1で整備した Application コントローラ群を前提に、挙動互換を維持した段階移行で実施する。

2. **ゴール / 非ゴール**
### ゴール
- `PipelineOrchestrator` の主要フローを「ステージ単位（Capture, OCR, Group, Diff, Translate, Overlay）」へ分割し、各ステージの入出力を固定する。
- `SceneTextSnapshotService` の Stage B 用 OCRパスと比較判定を、共通ステージ/共通DTOで再利用できる構造に寄せる。
- 正常系・スキップ系（pHash unchanged / black frame / unchanged diff / translation skip）の分岐を状態遷移として可視化する。

### 非ゴール
- OCRアルゴリズム・翻訳品質そのものの改善。
- Capture provider ポリシー抽象化（層3で実施）。
- gRPC host 管理共通化（層4で実施）。
- `settings.json` スキーマの再設計（層5で実施）。

3. **前提・仮定**
- 層1で `MainWindow` の責務分離が進み、Orchestration 層は `RunCoordinator` 経由で呼ばれる前提が成立している。
- `PipelineOrchestrator` は現状、`RunOnceAsync` 内に多数の早期return分岐を持ち、変更時の影響追跡が難しい。
- `SceneTextSnapshotService` も OCR preprocess→group→reading units→semantic compare を独自に持ち、重複が発生している。

4. **現状整理**
- `PipelineOrchestrator`
- 課題: 単一メソッドに分岐が集中（black frame, pHash skip, OCR diff skip, translation cache skip, force options）。
- 課題: `_last*` 状態（overlay, translation, hash, snapshot）の更新タイミングが暗黙的。
- 課題: perf logging が実フローと混在し、メインロジックの可読性を落としている。

- `SceneTextSnapshotService`
- 課題: Stage B 用の OCR/line merge/build block が Pipeline 側と似た責務を重複実装。
- 課題: 比較判定の入力整形（char count, IoU threshold, signature）が拡張時の差分源になりやすい。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `PipelineExecutionContext`（新規, immutable寄り）
- 役割: 1回の実行で共有する `settings`, `force options`, `frame/roi`, `intermediate result`, `last state` 参照。

- `PipelineStageResult`（新規）
- 役割: 次ステージへ渡す共通結果（continue/skip reason/overlay action）。

- `IPipelineStage`（新規）
- 役割: `Task<PipelineStageResult> ExecuteAsync(PipelineExecutionContext, CancellationToken)`。

- 推奨ステージ（初期）
1. `CaptureStage`
2. `RoiAndHashGateStage`
3. `OcrAndGroupStage`
4. `DiffStage`
5. `TranslateStage`
6. `OverlayStage`

- `SceneSemanticPipeline`（新規 or `SceneTextSnapshotService` 内部抽出）
- 役割: Stage B の OCR/Group/Block構築を共通ステージで再利用。

### 5.2 データフロー / シーケンス
1. `RunCoordinator` から `PipelineOrchestrator.RunOnceAsync` 呼び出し。
2. `PipelineOrchestrator` は `PipelineExecutionContext` を生成。
3. 各 `IPipelineStage` を順に実行。
4. スキップ条件は `PipelineStageResult` で統一し、overlay action（ShowLast/Clear/Update）を明示。
5. 実行終了時に only-once で `_last*` state を更新。

### 5.3 既存パターンへの整合
- 既存 `PipelineOrchestrator` は facade として残し、内部実装を段階的に `PipelineStage` 群へ委譲する。
- 既存ログ文言は大きく変えず、stage key を追加（例: `stage=ocr_group`, `stage=translate`）。

6. **インターフェース設計**
### 6.1 主要 DTO / I/F（案）
- `PipelineExecutionContext`
- 入力: `AppSettings`, `ForceRunOptions`, cancellation, previous state snapshot。
- 可変領域: frame, roi, ocr lines, reading units, changed unit ids, translations, overlay items。

- `PipelineStageResult`
- `bool Continue`
- `PipelineStopReason? StopReason`（BlackFrame, RoiOutOfBounds, UnchangedHash, UnchangedDiff など）
- `OverlayAction`（ShowLast/Clear/Update/None）

- `ISceneSemanticComparator`
- `SceneTextSnapshotComparison Compare(SceneTextSnapshot previous, SceneTextSnapshot current, AppSettings settings)`

### 6.2 エラー・バリデーション
- ステージ例外は orchestrator で一元処理し、現行と同じ user-visible 動作（log + overlay fallback）を維持。
- clamp/normalize は settings 層責務のため、Orchestration 側では参照のみ（重複 normalize 禁止）。

7. **実装手順（ステップ分割）**
- Step 1: 実行コンテキスト導入
- `PipelineExecutionContext` と `PipelineStageResult` を追加し、既存メソッドを壊さずに中間データ受け皿を作る。

- Step 2: 早期return分岐の状態化
- black frame / ROI invalid / pHash unchanged / OCR diff unchanged の return を `PipelineStopReason` に変換。

- Step 3: OCR〜Group〜Diff をステージ抽出
- `OcrAndGroupStage`, `DiffStage` を追加し、`RunOnceAsync` から委譲。

- Step 4: Translate / Overlay をステージ抽出
- translation cache / fallback provider / overlay mode 適用を `TranslateStage`, `OverlayStage` へ移す。

- Step 5: SceneTextSnapshotService の共通化
- Stage B の OCR/Group/Block 構築を共通 stage/helper に統合。

- Step 6: Perf logging の分離
- timing収集を `PipelinePerfProbe` へ分離し、メインロジックから stopwatch ノイズを削減。

- Step 7: 旧経路削除
- `PipelineOrchestrator` の旧分岐直書きを段階削除し、stage 実行に一本化。

8. **非機能要件チェック**
- 性能
- 実行時間ベースライン（capture/ocr/group/diff/overlay/total）を維持。劣化許容は +5% 以内。

- 可観測性
- stage 単位ログを追加し、停止理由をログで機械判定可能にする。

- 互換性
- `ForceRunOptions` と既存 hotkey シナリオ（F8/F10/Shift+F10/F11）の挙動互換を維持。

- 運用
- 旧経路 fallback を一時残置し、段階リリース中に新旧差分を比較できる状態を維持。

9. **リスクと緩和策**
- Risk: ステージ分割時に `_last*` state 更新順序が変わり、overlay の見え方が回帰する。
- Mitigation: state 更新を `CommitLastState` 相当の単一ポイントに集約し、回帰テストを追加する。

- Risk: SceneTextSnapshot 側との共通化で責務が逆流する。
- Mitigation: 共通化は「OCR/Group/Block 構築」のみに限定し、scene semantic 比較ロジックは独立維持。

- Risk: 分割で class 数が増え、追跡コストが上がる。
- Mitigation: ステージ責務を6個前後に固定し、1ステージ1責務ルールを徹底する。

10. **影響範囲**
- 新規候補
- `Services/Orchestration/PipelineExecutionContext.cs`
- `Services/Orchestration/PipelineStageResult.cs`
- `Services/Orchestration/Stages/*`
- `Services/Orchestration/PipelinePerfProbe.cs`
- `Services/Orchestration/SceneSemanticPipeline.cs`

- 既存更新
- `Services/PipelineOrchestrator.cs`
- `Services/SceneTextSnapshotService.cs`
- 必要に応じて `Services/Application/MainWindowRunCoordinator.cs`（呼び出し口調整のみ）

11. **Definition of Done**
- [ ] `PipelineOrchestrator` の主要分岐が stage 実行モデルに置換されている。
- [ ] 停止理由（black/roi/hash/diff/translation skip）が `PipelineStopReason` で追跡可能。
- [ ] `SceneTextSnapshotService` と Pipeline の重複処理が共通化されている。
- [ ] 既存主要シナリオ（手動Run/force run/scene auto-translate/overlay text toggle）が互換動作する。
- [ ] `dotnet build` / `dotnet run` が成功し、perfログで主要指標が許容範囲内である。
- [ ] ロールバック手順（旧経路へ戻す手順）が明記されている。
