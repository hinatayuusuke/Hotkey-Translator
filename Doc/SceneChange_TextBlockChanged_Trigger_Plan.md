# SceneChange Text/Block Changed Trigger Plan

1. **概要（1–3行）**
- 自動翻訳（Auto-translate）と自動非表示（Auto-hide）の発火条件を、「画面差分」だけでなく「文字/文字ブロックの意味的変化」まで満たした時に限定する。
- 既存 watcher の pHash 検知は一次ゲートとして残し、二次ゲートとして OCR スナップショット差分を追加する。
- 目的は、背景アニメーションやエフェクト変化での誤発火を抑え、文字が新しく出た/変わった時だけ動作させること。

2. **ゴール / 非ゴール**
### ゴール
- Auto-hide / Auto-translate が「新規テキスト出現」「ブロック追加/削除」「既存ブロック本文変化」の時のみ発火する。
- 既存の実行排他（`_runInProgress`）と pending-drain 仕様を維持する。
- 既存 watcher（`SceneChangeWatchIntervalMs`, `SceneChangeWatchPhashThreshold`）を活かしつつ、誤発火を減らす。
- Stage B で確定に使った OCR 結果を Auto-translate に再利用し、確定後の再OCRを省いて遅延を下げる。

### 非ゴール
- OCRエンジン自体の変更（WinRT/Paddle/VL の品質改善は対象外）。
- 翻訳エンジン選択や翻訳プロンプト仕様の変更。
- Scene watcher を OCR常時実行型に作り替えること。

3. **前提・仮定**
- 現状は `OnAutoHideTick` で主に pHash 差分を見て発火している。
- 自動翻訳は現在「検知ごとにUIスレッドへキュー投入（実行中は pending 集約して後追い実行）」の方針。
- Auto-hide と Auto-translate は排他仕様（旧設定両ON時は Auto-hide 優先）。

4. **現状整理（課題）**
- 背景だけ変わるシーン（カメラ揺れ、エフェクト、UIアニメ）でも pHash 差分が閾値を超えやすい。
- その結果、文字が変わっていないのに Auto-hide / Auto-translate が発火する。
- ユーザー意図は「文字内容または文字ブロックが変わった時だけ動作」であり、現状ロジックとズレる。

5. **提案アーキテクチャ**
### 5.1 二段ゲート構成
- Stage A（既存）: pHash 差分で「候補フレーム」を抽出。
- Stage B（新規）: 候補フレームに対して OCR スナップショット差分を評価。
- 最終発火条件: `StageA == true && StageB == true` かつ `streak >= SceneSemanticRequireConfirmTicks`。

### 5.2 追加コンポーネント
- `Services/SceneTextSnapshotService.cs`（新規）
  - `CaptureSnapshotAsync(...)` : 監視用 OCR スナップショット取得（翻訳なし、overlay更新なし）。
  - NOTE: 取得経路は本番と同一品質の OCR 経路（同じ前処理/同じ結合設定）を使う。
  - `CompareSnapshots(prev, current, settings)` : 意味的変化判定。
- `Models/SceneTextSnapshot.cs`（新規）
  - `Blocks: IReadOnlyList<SceneTextBlock>`
  - `ReadingUnits: IReadOnlyList<ReadingUnit>`（Auto-translate再利用用、翻訳未実行）
  - `CapturedAtUtc: DateTime`
  - `SnapshotSignature: string`（ROI/言語/OCR engine + preprocess settings + writing mode + line-merge関連 の再利用整合性検証用）
- `Models/SceneTextBlock.cs`（新規）
  - `Rect`, `NormalizedText`, `CharCount`

### 5.3 変化判定ルール（初期版）
- 1) 新規ブロック出現: 前回とマッチしない block が追加。
- 2) ブロック消失: 前回 block が今回マッチしない。
- 3) 本文変化: IoUで対応付いた block の `NormalizedText` が変化。
- 対応付け: IoU 最大の 1:1 greedy matching（`IoU >= 0.5` 初期値）。
- ノイズ抑制: `CharCount < 2` の block は判定対象外（初期値）。

### 5.4 既存処理との整合
- Auto-hide: StageB true の時のみ hide 実行。
- Auto-translate: StageB true の時のみ既存 `QueueSceneChangeAutoTranslate` 経路へ投入。
- Auto-translate 実行時は、まず Stage B で取得した `ReadingUnits` を翻訳入力として再利用する。
- 再利用不可条件では従来 `RunOnceAsync` 経路へフォールバックする。
  - `CapturedAtUtc` が古い（初期値: 500ms 超）
  - `SnapshotSignature` が現設定と不一致（ROI/言語/OCR engine + preprocess/writing-mode/line-merge差分）
  - 実行競合で payload が無効化された
- pending-drain は現行仕様を維持し、「Semantic true で投入されたイベント」のみ対象にする。

6. **インターフェース設計**
### 6.1 AppSettings 追加（UI非露出、settings.json運用）
- `EnableSceneChangeSemanticGate: bool = true`
- `SceneSemanticBlockIouThreshold: double = 0.5`
- `SceneSemanticMinChars: int = 2`
- `SceneSemanticRequireConfirmTicks: int = 1`

### 6.2 MainWindow 追加状態
- `_lastSceneTextSnapshot: SceneTextSnapshot?`
- `_semanticCandidateStreak: int`
- `_pendingSceneSemanticPayload: SceneTextSnapshot?`（Auto-translate再利用用）

### 6.3 判定フロー（OnAutoHideTick）
1. 既存 pHash 判定（Stage A）
2. Stage A true の時だけ `CaptureSnapshotAsync` を呼ぶ
3. `CompareSnapshots` で semantic change 判定（Stage B）
4. Stage B true の連続回数を更新（`_semanticCandidateStreak`）
5. `StageB==false` または `streak < SceneSemanticRequireConfirmTicks` -> 発火しない（ログのみ）
6. `streak >= SceneSemanticRequireConfirmTicks` の時のみ Auto-hide or Auto-translate を実行
7. Auto-translate の場合は `pendingSceneSemanticPayload` を優先利用して翻訳を実行
8. payload 不整合時は従来 `RunOnceAsync` にフォールバック
9. 実行後は `streak` をリセットし、スナップショット基準更新（`_lastSceneTextSnapshot = current`）

7. **実装手順（ステップ分割）**
- Step 1: `SceneTextSnapshotService` とモデルを追加（取得・比較ロジック + ReadingUnit 取得）
- Step 2: `MainWindow` watcher に Stage B を組み込み（Auto-hide / Auto-translate 共通）
- Step 3: `SceneSemanticRequireConfirmTicks` を watcher フローへ適用（streak管理と発火条件）
- Step 4: Auto-translate 実行時の payload 再利用導線を追加（再利用不可時は `RunOnceAsync` フォールバック）
- Step 5: `AppSettings` に semantic gate 係数を追加し、clamp/normalize 実装
- Step 6: ログ整備（A通過/B通過/不通過、追加・削除・本文変更件数、payload再利用/フォールバック理由）
- Step 7: スモーク検証（背景変化のみ、文字変化あり、block増減、再利用有無）

8. **非機能要件チェック**
- 性能:
  - OCR負荷を抑えるため、Stage A true の時だけ Stage B を実行。
  - `SceneSemanticRequireConfirmTicks` を 2 にすると誤判定は減るが遅延が増える（運用調整）。
- 可観測性:
  - ログに `visual_trigger`, `semantic_changed`, `added/removed/text_changed`, `payload_reused` を出力。
- 互換性:
  - `EnableSceneChangeSemanticGate=false` で現行挙動へフォールバック可能。

9. **リスクと緩和策**
- Risk: OCR揺れで false positive が出る。
- Mitigation: `MinChars`, `IoU閾値`, `ConfirmTicks` で抑制。

- Risk: 候補フレームが多い場面でOCRコスト増。
- Mitigation: Stage A の既存閾値を維持し、Stage Bは候補時のみ実行。

- Risk: Stage B payload が古く、現在画面とズレた内容を翻訳する可能性。
- Mitigation: `CapturedAtUtc` TTL（初期 500ms）と signature 一致チェックで再利用可否を厳格化する。

- Risk: 初回基準がなく意図せず即発火。
- Mitigation: watcher開始直後は snapshot を基準化するだけで発火しない。

10. **影響範囲（変更候補）**
- `MainWindow.xaml.cs` — watcher発火条件の二段化、snapshot/payload保持、再利用実行、ログ
- `Models/AppSettings.cs` — semantic gate設定追加
- `Services/SceneTextSnapshotService.cs`（新規） — 監視用OCR snapshot取得と比較
- `Models/SceneTextSnapshot.cs`（新規）
- `Models/SceneTextBlock.cs`（新規）
- `Doc/SceneChange_TextBlockChanged_Trigger_Plan.md`（本ドキュメント）

11. **Definition of Done**
- [ ] 背景変化のみ（文字不変）では Auto-hide / Auto-translate が発火しない。
- [ ] 新規テキスト出現時に Auto-hide / Auto-translate が発火する。
- [ ] ブロック追加/削除、本文変化で発火する。
- [ ] pending-drain と競合せず、既存排他仕様を維持できる。
- [ ] Stage B 確定時の OCR payload が Auto-translate へ再利用され、再OCRなし経路で動作する。
- [ ] payload が古い/不整合時は従来 `RunOnceAsync` へフォールバックする。
- [ ] `EnableSceneChangeSemanticGate=false` で現行ロジックへ戻せる。
- [ ] ログで「Stage A通過」「Stage B通過」「不発理由」が追跡できる。
