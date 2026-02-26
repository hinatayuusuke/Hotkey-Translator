# SceneChange Quiet Window 後 Fresh OCR 実装案

## 1. 概要（1-3行）
`AutoTranslate` で `Stage B` を通過した後、Quiet Window 経過時に **必ず新規OCR** を実行して翻訳する。  
`Stage B` は翻訳入力生成をやめ、シーン変化の判定専用に軽量化する。  
これにより「Quiet Window前の古いOCR結果で翻訳される」問題を解消する。

## 2. ゴール / 非ゴール
### ゴール
- Quiet Window 経過後の翻訳は、常に最新フレームのOCR結果を使う。
- `Stage B` は pass/reject 判定専用にし、生成コストを下げる。
- 既存の `Stage A pass -> 即時非表示` の挙動は維持する。

### 非ゴール
- `Stage A` 判定アルゴリズムの変更。
- OCRエンジンや翻訳プロバイダの入れ替え。
- Overlay復帰ポリシー（手動表示/翻訳成功時表示/手動OCR実行時表示）の変更。

## 3. 前提・仮定
- 現行では `Stage B` 取得の `SceneTextSnapshot` が pending payload として保持され、Quiet Window後に再利用される。
- そのため Quiet Window中に画面内容が変化しても、翻訳入力が古くなる可能性がある。
- Quiet Window の主目的は「実行タイミングを遅らせること」であり、「古いOCR結果を保持して使うこと」ではない。

## 4. 現状整理
- `SceneChangeController` は `QueueSceneChangeAutoTranslate(..., semanticPayload)` で payload を pending 保持できる。
- `TryDrainPendingAutoTranslate()` は pending payload を `RunOnceAsync` に渡す。
- `MainWindowRunCoordinator` は `AutoSceneChange` 時に payload が有効なら `RunWithReadingUnitsAsync` を使い、OCRを再実行しない経路を取る。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `SceneChangeController`
  - `Stage B` の責務を「判定のみ」に限定。
  - translate queue 時は `semanticPayload = null` を強制。
- `MainWindowRunCoordinator`
  - `AutoSceneChange` でも payload が無ければ `RunOnceAsync`（新規OCR）を実行。
- `SceneTextSnapshotService`
  - 比較判定用スナップショットを維持（必要なら軽量DTO化を段階導入）。

### データフロー / シーケンス（新）
1. `Stage A pass`。
2. Overlay を即時非表示。
3. `AutoTranslate` のみ `Stage B` 判定を実行。
4. `Stage B reject` なら翻訳せず `Stage A` 再開。
5. `Stage B pass` なら quiet pending をセット（payloadは保持しない）。
6. Quiet Window 経過後に `RunOnceAsync(..., semanticPayload: null)` を実行。
7. 翻訳直前OCRは必ず最新フレームで再取得される。

### 既存パターンへの整合
- 非表示制御は既存 `Stage A` 経路を流用。
- 翻訳起動は既存 run coordinator を流用し、payload再利用のみ抑止。

## 6. インターフェース設計
### 変更点（論理I/F）
- `HandleSceneChangeWithSemanticGateAsync(...)` は `bool`（pass/fail）のみ返却し、翻訳用payload供給責務を持たない。
- `QueueSceneChangeAutoTranslate(int diff, int threshold, SceneTextSnapshot? semanticPayload)`
  - `AutoSceneChange` + Quiet Window経路では `semanticPayload` を常に `null` とする。
- `TryDrainPendingAutoTranslate()`
  - drained run は `semanticPayload: null` で `_runOnceAsync` を呼ぶ。

### 状態管理
- `_sceneChangeAutoTranslatePendingPayload` は段階的に廃止可能。
- まずは `AutoSceneChange` 経路で payload をセットしない運用に変更し、次段でフィールド削除を行う。

### エラー/競合
- `Stage B` 例外時は翻訳未実行、`Stage A` 再開。
- Quiet Window中の多重イベントは既存 pending coalescing を継続。

## 7. 実装手順（ステップ分割）
### Step 1: Stage B の payload供給を停止
- `SceneChangeController` で `Stage B pass` 時の `semanticPayload` 保持をやめる。
- `QueueSceneChangeAutoTranslate` 呼び出しは `semanticPayload: null` を渡す。

### Step 2: Quiet Window drained run を Fresh OCR 固定化
- `TryDrainPendingAutoTranslate()` で `_runOnceAsync(AutoSceneChangeRunOptions, null)` を使用。
- 既存ログに `fresh_ocr=true` を追加し挙動を可視化。

### Step 3: RunCoordinator の payload再利用ガードを明確化
- `RunTrigger.AutoSceneChange` かつ quiet drain 起動時は payload再利用をスキップ。
- 互換のため手動経路や将来拡張は維持。

### Step 4: Stage B 軽量化（段階導入）
- 比較に必要な最小情報（Rect/NormalizedText/CharCount）中心に処理を見直す。
- まず「機能維持のまま余剰データ生成を削減」し、比較ロジックは変えない。

### Step 5: クリーニング
- 未使用になった pending payload フィールドと consume API を削除。
- 参照箇所（`MainWindow.xaml.cs`, `MainWindowRunCoordinator`）を整理。

## 8. 非機能要件チェック
- 性能: Stage B軽量化で監視コストを下げ、最終OCRは1回に統一。
- 可観測性: `stage_b_pass`, `quiet_pending`, `quiet_ready`, `translate_triggered`, `fresh_ocr=true` をログ化。
- 互換性: 表示/復帰ポリシーは維持。翻訳入力の鮮度のみ変更。
- 運用: Quiet Windowの値を調整しやすい（鮮度と遅延のトレードオフが明確）。

## 9. リスクと緩和策
- Risk: Quiet Window後に再OCRするため、翻訳開始が遅く感じる。
- Mitigation: Stage Aで即時非表示済みのため体感影響を抑え、ログで待機時間を可視化する。

- Risk: Stage Bを軽量化し過ぎると誤判定が増える。
- Mitigation: 最初は比較ルール（IoU/閾値）を維持し、データ生成のみ削減する段階導入にする。

## 10. 影響範囲（変更ファイル候補）
- `Services/Application/SceneChangeController.cs`
- `Services/Application/MainWindowRunCoordinator.cs`
- `MainWindow.xaml.cs`（payload受け渡し経路の整理時）
- `Services/SceneTextSnapshotService.cs`（軽量化段階で必要時）
- `Doc/SceneChange_QuietWindow_FreshOcr_Implementation_Plan.md`

## 11. Definition of Done
- [ ] Quiet Window経過後の auto-translate が常に新規OCR経路（`RunOnceAsync`）で実行される。
- [ ] `Stage B` 通過前のOCR結果が翻訳入力として再利用されない。
- [ ] `Stage B reject/timeout/error/cancel` 後に `Stage A` が再開される。
- [ ] 既存の復帰条件（手動表示/翻訳成功時表示/手動OCR実行時表示）が維持される。
- [ ] ログで `fresh_ocr=true` を確認できる。
