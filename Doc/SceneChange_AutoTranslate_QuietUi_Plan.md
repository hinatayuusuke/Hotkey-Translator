# 自動翻訳時のUI静音化（右下Spinner / OCR No text非表示）実装案

## 1. 概要（1-3行）
- シーン変化による自動翻訳実行時のみ、右下 Loading Spinner と `No text detected` トーストを表示しない。
- 手動実行（F8/F10/ボタン）は現行どおり表示を維持し、デバッグ性を落とさない。
- 実装は「実行トリガー種別を明示するフラグ追加」で局所化する。

## 2. ゴール / 非ゴール
### ゴール
- 自動翻訳監視中の表示ノイズ（頻繁な Spinner / No text）を減らす。
- 手動実行時の可視フィードバックは維持する。
- 既存のOCR/翻訳処理ロジックへの影響を最小化する。

### 非ゴール
- シーン変化判定（pHash閾値、監視間隔）の仕様変更。
- 自動翻訳トリガー制御（pending drain 等）の仕様変更。
- 翻訳プロバイダ選択ロジックの変更。

## 3. 前提・仮定
- 右下 Spinner は `ShowLoadingSpinnerForRun` / `HideLoadingSpinnerForRun` で制御される。
- `No text detected` は `PipelineOrchestrator` で検出時に `ShowToast` される。
- 自動翻訳起点は `QueueSceneChangeAutoTranslate` から `RunOnceAsync(ForceRunOptions.None)` を呼ぶ経路。

## 4. 現状整理
- 手動実行・自動実行の区別が `ForceRunOptions` で表現されていない。
- そのため自動実行でも以下が表示される。
  - 右下 Spinner（実行開始/終了の都度）
  - OCR結果ゼロ時の `No text detected` トースト
- 自動監視では短周期実行が発生しやすく、表示ノイズになりやすい。

## 5. 提案アーキテクチャ
### 5.1 実行コンテキストの明示
- `ForceRunOptions` に追加（後方互換な optional）
  - `bool IsAutoSceneChangeRun = false`
  - `bool SuppressTransientUiFeedback = false`
- 自動トリガー時のみ `IsAutoSceneChangeRun=true`, `SuppressTransientUiFeedback=true` を設定して実行。

### 5.2 表示ポリシー
- 手動実行（`SuppressTransientUiFeedback=false`）
  - Spinner: 表示
  - No text toast: 表示
- 自動シーン変化実行（`SuppressTransientUiFeedback=true`）
  - Spinner: 非表示
  - No text toast: 非表示

### 5.3 既存パターンとの整合
- 実行制御自体（`_runInProgress`, gate, cancel token）は変更しない。
- フィードバック表示層のみを分岐させる。

## 6. インターフェース設計
### 6.1 MainWindow 側
- `RunOnceAsync(ForceRunOptions options)` 内で以下を分岐。
  - `ShowLoadingSpinnerForRun(settings)` 呼び出し
  - `HideLoadingSpinnerForRun()` 呼び出し
- `QueueSceneChangeAutoTranslate` からの呼び出しを以下へ変更。
  - `RunOnceAsync(new ForceRunOptions(..., IsAutoSceneChangeRun: true, SuppressTransientUiFeedback: true))`

### 6.2 PipelineOrchestrator 側
- `No text detected` 分岐（2箇所）で以下条件を追加。
  - `if (!options.SuppressTransientUiFeedback) { ShowToast(...) }`
- `ClearOverlay` など既存の表示内容更新は初期案では維持（挙動変化を最小化）。

## 7. 実装手順（ステップ分割）
1. `ForceRunOptions` に UI静音用フラグを追加（既存呼び出しを壊さないデフォルト値）。
2. `QueueSceneChangeAutoTranslate` の実行引数を自動実行フラグ付きに変更。
3. `MainWindow.RunOnceAsync` で Spinner 表示/非表示をフラグ連動に変更。
4. `PipelineOrchestrator` の `No text detected` トースト表示をフラグ連動で抑制。
5. ログに実行種別を追加し、手動/自動の切り分けを容易化。

## 8. 非機能要件チェック
- 可観測性: ログに `manual/auto-scene` を出し分けて原因追跡可能にする。
- 性能: 条件分岐追加のみで影響軽微。
- 互換性: 手動経路の UI 挙動は維持。
- 運用: ノイズ減により通常運用時の視認性を改善。

## 9. リスクと緩和策
- Risk: 自動実行で進行状況が見えず、動作停止と誤認される可能性。
- Mitigation: ログには自動実行開始/終了を明示し、必要なら設定で再表示できる拡張余地を残す。

- Risk: `No text` 非表示でテキスト消失の原因特定が遅れる可能性。
- Mitigation: ログに `No text detected (suppressed toast)` を残す。

- Risk: 呼び出し箇所のフラグ設定漏れ。
- Mitigation: 自動実行入口（`QueueSceneChangeAutoTranslate`）を単一維持し、そこだけで設定する。

## 10. 影響範囲（変更候補）
- `Services/PipelineOrchestrator.cs` — No textトースト抑制分岐の追加。
- `MainWindow.xaml.cs` — 自動実行時 Spinner 抑制、および自動実行フラグ付き呼び出し。
- `Services/PipelineOrchestrator.cs`（`ForceRunOptions` 定義部） — UI静音フラグ追加。

## 11. Definition of Done
- 自動シーン変化実行時、右下 Spinner が表示されない。
- 自動シーン変化実行時、`No text detected` トーストが表示されない。
- 手動実行時は、Spinner / No text トーストが現行どおり表示される。
- 既存のOCR・翻訳結果更新フローに回帰がない。

## Open Questions
- 中央 BusyOverlay（`OCR running...` / `Translating...`）も自動実行時は静音化するか。
- 将来、UI設定で「自動実行時フィードバックを再有効化」トグルを設けるか。
