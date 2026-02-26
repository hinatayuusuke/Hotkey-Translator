# SceneChange 自動非表示/自動翻訳 分離判定 実装案

## 1. 概要（1-3行）
`Stage A` を共通の一次変化検知として一本化し、`AutoHide` は `Stage A` 通過で即時非表示する。  
`AutoTranslate` は `Stage A` 通過時点で一旦非表示したうえで、裏で `Stage B` 判定を継続し、`Stage B` 通過時のみ翻訳を実行する。  
UI反応速度と誤翻訳抑制の両立を目的とする。

## 2. ゴール / 非ゴール
### ゴール
- 自動非表示と自動翻訳の `Stage A` 判定ルートを共通化する。
- `AutoTranslate` でも `Stage A` 通過時に即時非表示する。
- `AutoTranslate` は `Stage B` 通過時のみ翻訳実行する。
- `Stage B` 否決時は翻訳を実行しない。

### 非ゴール
- `Stage A` / `Stage B` のアルゴリズム自体を全面改修すること。
- OCR/翻訳エンジンの切替ロジック変更。
- Overlay描画実装（WPF/Hook）の大規模変更。

## 3. 前提・仮定
- 既存の SceneChange 処理に `Stage A` / `Stage B` 相当の判定フローが存在する。
- 現在は自動非表示と自動翻訳で分岐条件が重複または混在している。
- `Stage A` は低コスト・高感度、`Stage B` は高精度ゲートとして運用可能。
- 非表示制御と翻訳実行を別イベントとして扱える。

## 4. 現状整理
- 自動非表示は即時性が重要、誤検知コストは比較的低い。
- 自動翻訳は誤検知コストが高く、追加の厳格判定が必要。
- 判定ルートを共通化しないと閾値調整やデバッグが複雑化する。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `SceneChangeController`（既存）
  - `EvaluateStageA(...)` を共通一次ゲート化
  - `EvaluateStageB(...)` を翻訳専用二次ゲートとして維持
- `AutoHideFlow`（論理）
  - `Stage A pass -> HideNow`
- `AutoTranslateFlow`（論理）
  - `Stage A pass -> HideNow -> Continue Stage B (background) -> if pass then RunTranslate`

### データフロー / シーケンス
1. フレーム監視イベント受信。
2. 共通 `Stage A` 判定を実行。
3. `Stage A` 不通過: 何もしない。
4. `Stage A` 通過:
   - `AutoHide` 有効なら即時非表示。
   - `AutoTranslate` 有効でも即時非表示。
5. 非表示後は `Stage A` 判定ループを停止する。
6. `AutoTranslate` 有効時のみ `Stage B` 判定を非同期/裏処理で継続。
7. `Stage B` 通過時のみ翻訳ジョブを実行。
8. `Stage B` 否決時は翻訳を実行しない。
9. `Stage B reject/timeout/error/cancel` の終了後は、Overlay状態を変えずに `Stage A` 判定のみ再開する。

### 既存パターンへの整合
- 非表示制御は既存 Overlay 制御APIを再利用。
- 翻訳実行は既存 Pipeline 実行経路を再利用。
- 新規判定ロジックは増やさず、分岐責務のみ整理する。

## 6. インターフェース設計
### 追加/整理する論理I/F（案）
- `bool EvaluateStageA(SceneInput input, out StageAMeta meta)`
- `Task<StageBResult> EvaluateStageBAsync(SceneInput input, StageAMeta meta, CancellationToken ct)`
- `void RequestOverlayHide(HideReason reason)`
- `Task RequestAutoTranslateAsync(TranslateReason reason, CancellationToken ct)`

### 状態管理
- `PendingStageB` フラグ（多重起動防止）
- `StageBInFlightId`（古い判定結果の破棄）
- `StageAActive`（非表示中は停止）
- `OverlayRestorePolicy`（既存復帰条件: 手動表示 / 翻訳成功時表示 / 手動OCR実行時表示）

### エラー/競合処理
- `Stage B` 実行中に新しい `Stage A pass` が来たら古い `Stage B` をキャンセル。
- `Stage B` 例外時は翻訳実行せずログのみ。
- 非表示命令は冪等であることを保証。
- `Stage B reject/timeout/error/cancel` では `StageAActive=true` に戻し、監視ループを再開する。

## 7. 実装手順（ステップ分割）
### Step 1: Stage A 共通化
- 自動非表示/自動翻訳の入口を共通 `Stage A` 判定へ集約。
- 既存分岐内の重複条件を削除。

### Step 2: Stage A 通過時の即時非表示統一
- `AutoTranslate` 経路にも `HideNow` を追加。
- 非表示トリガに理由コード（`stage_a_pass`）を付与。

### Step 3: AutoTranslate の Stage B 継続判定
- `Stage B` をバックグラウンド継続へ移行。
- 多重判定防止・キャンセル制御を追加。

### Step 4: Stage B 通過時のみ翻訳実行
- `Stage B` pass で既存翻訳ジョブをキック。
- fail/timeout/cancel は翻訳未実行で終了。
- `Stage B` 終了（pass/reject/timeout/error/cancel）時に `Stage A` 再開判定を共通化する。

### Step 5: ログ/可観測性整備
- `stage_a_pass/hide_issued/stage_b_start/stage_b_pass/stage_b_reject/translate_triggered` を記録。
- 1イベントあたりの相関IDを付けて追跡可能にする。

## 8. 非機能要件チェック
- 性能: `Stage A` は軽量維持、`Stage B` は必要時のみ起動。
- セキュリティ: 外部I/Oの追加なし。
- 可観測性: A/B各段階の結果・遅延をログで追跡可能にする。
- 互換性: 翻訳実行条件を `Stage B pass` に限定し、既存誤発火耐性を維持。
- 運用: 設定で `AutoHide` / `AutoTranslate` を個別に ON/OFF 継続。

## 9. リスクと緩和策
- Risk: pHash閾値が低すぎると意図しない非表示が増える。
- Mitigation: チラつきガードは追加せず、pHash感度調整で運用チューニングする。

- Risk: `Stage B` 判定遅延で翻訳開始が遅く感じる。
- Mitigation: `Stage A` で先に非表示し体感遅延を隠蔽、`Stage B` のタイムアウトを明示。

- Risk: 非同期競合で古い `Stage B` 結果が新フレームに適用される。
- Mitigation: 相関ID/キャンセルで古い結果を破棄。

- Risk: `Stage B` 異常終了時に `Stage A` が停止したままだと自動翻訳監視が止まる。
- Mitigation: `Stage B reject/timeout/error/cancel` 後に `Stage A` のみ再開する。

## 10. 影響範囲（変更ファイル候補）
- `Services/Application/SceneChangeController.cs`
- `Services/Application/MainWindowRunCoordinator.cs`（必要時）
- `Models/` 配下の状態モデル（必要時）
- `Doc/SceneChange_AStageShared_BStageTranslate_Implementation_Plan.md`（本書）

## 11. Definition of Done
- [ ] 自動非表示と自動翻訳が同じ `Stage A` 判定ルートを使用する。
- [ ] 自動翻訳でも `Stage A pass` で即時非表示される。
- [ ] 自動翻訳は `Stage B pass` 時のみ翻訳を実行する。
- [ ] `Stage B reject` 時は翻訳が実行されない。
- [ ] `Stage B reject/timeout/error/cancel` 後に `Stage A` 判定が再開される。
- [ ] `Stage B reject` 時に自動再表示しない（既存復帰条件のみで復帰する）。
- [ ] ログで A/B 判定と翻訳実行の因果関係を追跡できる。
