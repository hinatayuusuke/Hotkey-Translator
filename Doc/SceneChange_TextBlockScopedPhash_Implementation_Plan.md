# Scene Change: 文字ブロック限定 pHash 実装案

## 1. 概要（1–3行）
シーン変化判定の Stage A を、ROI 全体 pHash から「OCR 文字ブロック限定 pHash」へ拡張する。  
非文字領域の変化ノイズ（エフェクト/カメラ揺れ/UI）を抑え、翻訳対象テキストの変化検出を優先する。  
初期は既存挙動を壊さないため、ブロック判定を優先しつつ ROI 全体判定をフォールバックとして残す。

## 2. ゴール / 非ゴール
### ゴール
- Stage A 判定を「文字ブロック重み付き pHash 差分」で評価できるようにする。
- 複数ブロックに対応し、面積重み付きで最終 diff を算出する。
- ブロックなし/取得失敗時は従来 ROI 全体 pHash にフォールバックする。

### 非ゴール
- SceneTextSnapshotService の OCR ロジック全面改修。
- Stage B（semantic gate）アルゴリズムの置換。
- 初期段階での複雑な時系列学習（履歴モデル）導入。

## 3. 前提・仮定
- 現在の監視実装は `SceneChangeController` の Stage A（ROI pHash）→ Stage B（Semantic gate）で構成。
- Stage A のしきい値は `SceneChangeWatchPhashThreshold` を利用。
- Stage B は `EnableSceneChangeSemanticGate` が true の時に実行。
- Auto-hide / Auto-translate は排他運用。

## 4. 現状整理
- Stage A は ROI 全体を Crop して pHash 比較しているため、非文字ノイズに反応しやすい。
- Stage B は OCR スナップショット比較で精度は高いが、初回ベースライン化の影響で 1 回目は動作しないケースがある。
- `SceneChangeThreshold` は現行 watcher 経路で実質未使用。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `SceneChangeController`（更新）
  - Stage A 用に「文字ブロック限定 pHash 評価」関数を追加。
- `SceneTextSnapshotService`（軽微更新）
  - 直近スナップショットの block rect を Stage A に提供可能にする（既存 `SceneTextSnapshot.Blocks` を再利用）。

### データフロー / シーケンス
1. watcher tick でフレームを取得。  
2. Stage A で次を試行:
   - 直近の文字ブロック矩形群を取得。
   - 各矩形（微拡張あり）で current/previous を切り出して pHash 比較。
   - 面積重み付き平均 diff を算出。
3. ブロックが 0 件または評価不可なら、従来 ROI 全体 pHash にフォールバック。
4. しきい値以上なら Stage B / Action 判定へ進む。

### 既存パターンへの整合
- 既存の `SceneChangeWatchPhashThreshold` を再利用（初期は互換重視）。
- `EnableSceneChangeSemanticGate` や quiet-window の挙動はそのまま。

## 6. インターフェース設計
### 追加内部API（案）
- `SceneChangeController.EvaluateBlockScopedVisualDiff(...) -> (bool canEvaluate, int diff, int threshold)`
- `SceneChangeController.TryComputeBlockScopedHashDelta(...)`

### パラメータ（初期ハードコード案）
- `BlockPaddingPx = 3`
- `MinBlockSizePx = 8`
- `MinCoveredAreaRatio = 0.02`（有効ブロック総面積が ROI 比で小さすぎる時はフォールバック）

### エラー/フォールバック
- ブロックなし、矩形不正、切り出し失敗、面積不足時:
  - `event=stage_a_fallback reason=<...>` をログ出力
  - ROI 全体 pHash に自動フォールバック

## 7. 実装手順（ステップ分割）
### Step 1: Stage A ブロック限定評価の最小実装
- `SceneChangeController` に block-scoped diff 算出関数を追加。
- 比較元ブロックは「直近 snapshot の blocks」を使用。
- ブロックごと pHash 差分を面積重み付きで集約。

### Step 2: フォールバックと診断ログ
- ブロック評価不可時に ROI 全体判定へフォールバック。
- ログ追加:
  - `stage=scene_change event=stage_a_blocks`（blocks, coveredAreaRatio, diff）
  - `stage=scene_change event=stage_a_fallback reason=...`

### Step 3: しきい値運用の安全化
- 初期は既存 `SceneChangeWatchPhashThreshold` をそのまま適用。
- 必要なら後続で `SceneChangeWatchBlockPhashThreshold` を追加（互換既定は null=既存値）。

### Step 4: 回帰確認
- Auto-hide と Auto-translate で誤発火率/見逃し率を比較。
- quiet-window 有効時の pending/drain フロー回帰確認。

## 8. 非機能要件チェック
- 性能: ROI 全体 1 回から「ブロック数回」へ増えるため、最大ブロック数上限を設ける（例: 24）。
- 可観測性: diff 内訳・フォールバック理由をログ化。
- 互換性: フォールバック保持で既存設定を壊さない。
- 運用: しきい値調整時はログを根拠に段階変更。

## 9. リスクと緩和策
- Risk: OCR block が古く、実際の文字位置ズレで誤判定。
- Mitigation: block padding を入れ、covered area が低い時は ROI 判定へフォールバック。

- Risk: ブロック数が多い画面で tick 負荷が上がる。
- Mitigation: ブロック上限・最小面積フィルタ・早期打ち切りを導入。

- Risk: 小ブロックのみで diff が過敏になる。
- Mitigation: MinBlockSize/MinCoveredAreaRatio で安定化。

## 10. 影響範囲（変更ファイル候補）
- `Services/Application/SceneChangeController.cs` — Stage A の block-scoped 判定追加（主変更）
- `Services/SceneTextSnapshotService.cs` — block 提供経路の補助（必要なら）
- `Models/AppSettings.cs` — 将来の block 専用しきい値追加時のみ
- `Services/Settings/Rules/SceneSemanticSettingsRule.cs` — 新設定追加時のみ
- `Doc/SceneChange_TextBlockScopedPhash_Implementation_Plan.md` — 本ドキュメント

## 11. Definition of Done
- [ ] Stage A が文字ブロック限定 pHash 判定を実行できる。
- [ ] ブロック評価不可時に ROI 全体 pHash へ確実にフォールバックする。
- [ ] ログで block 判定とフォールバック理由が確認できる。
- [ ] Auto-hide / Auto-translate の既存フロー（semantic gate, quiet-window, pending/drain）が壊れない。
- [ ] 実画面で非文字ノイズ由来の誤発火が減ったことを確認できる。
