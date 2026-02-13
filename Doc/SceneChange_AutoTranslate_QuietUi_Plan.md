# 自動翻訳時のUI静音化（右下Spinner / OCR No text非表示）実装案

## 1. 概要（1-3行）
- シーン変化の自動翻訳実行時のみ、右下 Loading Spinner と `No text detected` トーストを抑制する。
- 手動実行（F8/F10/ボタン）は現行どおり可視フィードバックを維持する。
- 中央 BusyOverlay（`OCR running...` / `Translating...`）は静音化せず現行維持とする。

## 2. ゴール / 非ゴール
### ゴール
- 自動翻訳監視中の表示ノイズ（頻繁な Spinner / No text）を減らす。
- 手動実行時のデバッグ性を維持する。
- 実装を最小変更で局所化する。

### 非ゴール
- シーン変化判定（pHash閾値、監視間隔、cooldown/pending drain）の仕様変更。
- 中央 BusyOverlay の表示制御変更（これは現行維持）。
- 翻訳プロバイダ選択ロジックの変更。

## 3. 前提・仮定
- 右下 Spinner は `MainWindow.RunOnceAsync` の `ShowLoadingSpinnerForRun` / `HideLoadingSpinnerForRun` で制御される。
- `No text detected` は `PipelineOrchestrator` 内の2箇所で `ShowToast` される。
- 自動翻訳実行には2経路ある。
  - 即時実行: `QueueSceneChangeAutoTranslate` → `RunOnceAsync(...)`
  - 保留実行: `TryDrainPendingSceneChangeAutoTranslate` → `RunOnceAsync(...)`

## 4. 現状整理
- `ForceRunOptions` には「実行トリガー種別」や「一時UI抑制」の情報がない。
- そのため自動翻訳でも手動と同じ扱いで Spinner / No text toast が表示される。
- さらに pending drain 経路も同じ `ForceRunOptions.None` のため、抑制漏れが起きやすい。

## 5. 提案アーキテクチャ
### 5.1 実行コンテキストの明示
- `ForceRunOptions` に以下を追加（後方互換デフォルト付き）。
  - `RunTrigger Trigger = RunTrigger.Manual`
  - `bool SuppressTransientUiFeedback = false`
- `RunTrigger` は少なくとも以下を定義する。
  - `Manual`
  - `AutoSceneChange`
- 役割分担:
  - `Trigger`: ログ・観測の分類に使う。
  - `SuppressTransientUiFeedback`: Spinner/Toast抑制の表示制御に使う。

### 5.2 表示ポリシー（固定）
- 手動実行（`SuppressTransientUiFeedback=false`）
  - 右下 Spinner: 表示
  - No text toast: 表示
  - 中央 BusyOverlay: 表示（現行どおり）
- 自動シーン変化実行（`SuppressTransientUiFeedback=true`）
  - 右下 Spinner: 非表示
  - No text toast: 非表示
  - 中央 BusyOverlay: 表示（現行どおり）

### 5.3 既存パターンとの整合
- 実行制御（`_runInProgress`, gate, cancel token）は変更しない。
- 変更は表示フィードバック分岐とログ分類に限定する。

## 6. インターフェース設計
### 6.1 MainWindow 側
- `RunOnceAsync(ForceRunOptions options)` で Spinner 呼び出しを分岐。
  - `if (!options.SuppressTransientUiFeedback) ShowLoadingSpinnerForRun(settings);`
  - `if (!options.SuppressTransientUiFeedback) HideLoadingSpinnerForRun();`
- 自動翻訳の2経路を同じ options で統一。
  - `QueueSceneChangeAutoTranslate(...)` の `RunOnceAsync(...)`
  - `TryDrainPendingSceneChangeAutoTranslate(...)` の `RunOnceAsync(...)`
  - 共通で `new ForceRunOptions(..., Trigger: RunTrigger.AutoSceneChange, SuppressTransientUiFeedback: true)`

### 6.2 PipelineOrchestrator 側
- `No text detected` の2分岐に抑制条件を追加。
  - `if (!options.SuppressTransientUiFeedback) { _overlayPresenter.ShowToast(...); }`
  - `else { _logger.Info("No text detected (toast suppressed)."); }`
- `ClearOverlay` は現行どおり維持する（オーバレイ残像を避けるため）。

### 6.3 ログ方針（固定）
- `PipelineOrchestrator.RunOnceAsync` 開始ログに `trigger=<...>` を必ず含める。
- 自動実行でトースト抑制した場合は suppression ログを必ず1行出す。
- これにより UI非表示でも「実行されたが text 0 件」を追跡可能にする。

## 7. 実装手順（ステップ分割）
1. `ForceRunOptions` と `RunTrigger` を追加（既存呼び出しが壊れないデフォルト付き）。
2. `MainWindow` の自動翻訳2経路（即時/Drain）で同一の auto-scene options を使う。
3. `MainWindow.RunOnceAsync` の Spinner 表示/非表示を `SuppressTransientUiFeedback` 連動にする。
4. `PipelineOrchestrator` の `No text detected` 2箇所を suppression 対応する。
5. `PipelineOrchestrator` 実行ログに `trigger` を追加して切り分け可能にする。

## 8. 非機能要件チェック
- 可観測性: `trigger` と suppression ログで手動/自動の切り分けを可能にする。
- 性能: 条件分岐とログ追加のみで影響は軽微。
- 互換性: 手動実行の UI 挙動は維持。
- 運用: 対象ウィンドウ側ノイズを減らしつつ、アプリ側Busy表示は維持する。

## 9. リスクと緩和策
- Risk: 自動実行で右下UIが消えるため、停止と誤認される可能性。
- Mitigation: ログに `trigger=AutoSceneChange` と suppression ログを残す。

- Risk: 自動実行の2経路で options が不統一になると抑制漏れが発生する。
- Mitigation: auto-scene 用 options を共通化し、2経路で同じインスタンス/生成関数を使う。

- Risk: toast抑制で原因調査が難しくなる。
- Mitigation: `No text detected (toast suppressed)` を必ず記録する。

## 10. 影響範囲（変更候補）
- `Services/PipelineOrchestrator.cs`
  - `ForceRunOptions` / `RunTrigger` 定義追加
  - Run開始ログに `trigger` 追加
  - No text toast 抑制分岐（2箇所）
- `MainWindow.xaml.cs`
  - `RunOnceAsync` の Spinner 抑制分岐
  - `QueueSceneChangeAutoTranslate` の `RunOnceAsync` 呼び出し更新
  - `TryDrainPendingSceneChangeAutoTranslate` の `RunOnceAsync` 呼び出し更新

## 11. Definition of Done
- 自動シーン変化実行（即時/Drainの両経路）で右下 Spinner が表示されない。
- 自動シーン変化実行（即時/Drainの両経路）で `No text detected` トーストが表示されない。
- 手動実行では Spinner / No text トーストが現行どおり表示される。
- 中央 BusyOverlay は手動/自動とも現行どおり表示される。
- ログで `trigger` と suppression 状態が追跡できる。
