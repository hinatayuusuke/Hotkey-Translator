# EnableSceneChangeAutoHide: ウォッチャのみ方式 実装案

## 1. 概要（1–3行）
既存の `SceneChangeEvaluator.Evaluate` を削除し、自動非表示判定をタイマー型ウォッチャ（`AutoHideWatcher`）のみで行う。  
OCRパスから画像比較を外すことで、OCR前の追加遅延を根本的に解消する。

## 2. ゴール / 非ゴール
- ゴール: OCR実行パスからシーン変化評価を除去し、体感遅延を削減する。
- 非ゴール: 新しい判定アルゴリズムの導入、UIデザイン刷新、OCR精度の改善。

## 3. 前提・仮定
- 自動非表示の判定は「一定間隔の監視」で十分とする。
- 監視間隔（`SceneChangeWatchIntervalMs`）は既存設定を流用する。
- 既存ログや設定名は維持し、破壊的変更は避ける。

## 4. 現状整理
- `PipelineOrchestrator.RunOnceAsync` で `SceneChangeEvaluator.Evaluate` を毎回実行し、複数の `OverlayItem` に対して `Crop + pHash` を繰り返す。
- `MainWindow` には `AutoHideWatcher` があり、別スレッドで `Capture + pHash` を定期実行している。
- 遅延の主因は `SceneChangeEvaluator` の多点評価にある。

## 5. 提案アーキテクチャ
### コンポーネント構成
- OCRパイプライン: `SceneChangeEvaluator` を使用しない。
- 監視パス: `AutoHideWatcher` のみで変化検知を実行。

### データフロー / シーケンス
1) OCR実行時は従来通りキャプチャ→OCR→翻訳→オーバーレイ更新。  
2) オーバーレイ表示中のみ、一定間隔でウォッチャがROIハッシュを取得し、差分閾値を超えたら自動非表示。

### 既存パターンへの整合
- 既存設定 `EnableSceneChangeAutoHide` をそのまま使用。
- `SceneChangeWatchIntervalMs` / `SceneChangeWatchPhashThreshold` を引き続き利用。

## 6. インターフェース設計
- 公開API: 変更なし。
- 削除対象: `PipelineOrchestrator` 内の `SceneChangeEvaluator` 呼び出し。
- ログ: 既存 `Overlay auto-hidden (watcher diff X)` を維持。

## 7. 実装手順（ステップ分割）
- Step 1: `PipelineOrchestrator` から `SceneChangeEvaluator` フィールド/生成/呼び出しを削除。
- Step 2: `EnableSceneChangeAutoHide` が有効時でも、OCRパスでは何も評価しないように調整。
- Step 3: `AutoHideWatcher` の動作確認（閾値/間隔の設定が効くか）。

## 8. 非機能要件チェック
- 性能: OCRパスから重い処理を除外し、遅延を削減。
- セキュリティ: 影響なし。
- 可観測性: 既存ログを維持。
- 互換性: 設定名や挙動は維持されるが、反応はタイマー間隔依存に。

## 9. リスクと緩和策
- リスク: 変化検知の反応が遅れる（タイマー間隔依存）。
- 緩和策: `SceneChangeWatchIntervalMs` の推奨値をドキュメントで提示（例: 300–800ms）。

## 10. 代替案（最低1つ）と採用理由
- 代替案: ROI全体1回pHash判定。
- 採用理由: 既存ウォッチャがあり、最小変更で遅延削減が可能。

## 11. 影響範囲
- 変更ファイル候補: `Services/PipelineOrchestrator.cs`, `Services/SceneChangeEvaluator.cs`（不要なら未使用化）。
- ドキュメント更新: 本ドキュメントのみ。

## 12. Definition of Done
- [ ] `EnableSceneChangeAutoHide` 有効時にOCRパスで `SceneChangeEvaluator` が実行されない。
- [ ] `AutoHideWatcher` による自動非表示が従来通り機能する。
- [ ] OCR実行時の追加遅延が大幅に低減する。
