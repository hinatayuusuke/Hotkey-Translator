# Overlay Adaptive Background Color Plan

## 1. 概要（1–3行）
- オーバレイの半透明背景色を、文字近傍の画面色に合わせる `PerBox` 自動推定機能を追加する。
- デフォルトは現行どおり半透明黒を維持し、自動推定は任意ONで使う。
- 既存 `OverlayBackgroundOpacity` はそのまま有効とし、推定色にも同じ透明度を適用する。
- 文字色は背景色の輝度から `白/黒` を自動選択し、補色アルゴリズムは採用しない。

## 2. ゴール / 非ゴール
### ゴール
- UIから自動背景色の ON/OFF を切り替えられる。
- ON時は `PerBox` のみで背景色を推定し、最終透明度は既存 `OverlayBackgroundOpacity` で制御する。
- 背景色に応じて文字色を白/黒で自動切替し、最低限の可読性を確保する。
- 既存パイプライン（OCR/翻訳/表示順）を壊さずに導入する。

### 非ゴール
- `PerFrame`（ROI全体代表色）の実装。
- 文字色の補色計算や多色最適化（今回は白/黒のみ）。
- OCR認識結果や翻訳ロジックの変更。

## 3. 前提・仮定
- 現在の背景色は `OverlayWindow.ApplyStyle()` で `_background` として保持される。
- 透明度は `OverlayBackgroundOpacity` が最終決定する（既存仕様）。
- 推定に必要な画素情報は `PipelineOrchestrator` が持つ `roiBitmap` から取得できる。
- `OverlayItem.Rect` は画面座標、`roiBitmap` は ROI ローカル座標であり、サンプリング時に座標変換が必要。

## 4. 現状整理
- `UI/OverlayWindow.xaml.cs` は全 `OverlayItem` に同一背景ブラシ `_background` を適用している。
- `Services/OverlayPresenter.cs` は `OverlayItem` の座標変換と描画呼び出しを担う。
- `Services/PipelineOrchestrator.cs` は `roiBitmap` と `OverlayItem` 生成の両方を持つため、背景色推定の投入点として最適。

## 5. 提案アーキテクチャ
### 5.1 コンポーネント構成
- 追加: `Services/OverlayBackgroundSampler.cs`
  - `EstimatePerBoxColor(...)`
- 拡張: `Models/OverlayItem.cs`
  - `BackgroundColor`（任意、未指定時は既定背景）
  - `ForegroundColor`（任意、未指定時は既定前景）
- 拡張: `PipelineOrchestrator`
  - `roiBitmap` + 読み単位情報から `PerBox` 背景色を計算し、背景色から白/黒文字色を決定して `OverlayItem` に付与
- 拡張: `OverlayWindow`
  - `OverlayItem.BackgroundColor` / `OverlayItem.ForegroundColor` があればそれを優先し、背景には最後に `OverlayBackgroundOpacity` を適用

### 5.2 データフロー / シーケンス
1. OCR後、`PipelineOrchestrator` で `OverlayItem` 生成前に背景色推定（ON時のみ）。
2. 各 `OverlayItem.Rect` ごとに `PerBox` 背景色を算出。
3. 背景色の相対輝度で文字色を白/黒に決定（初期値: `L <= 0.45 -> 白`, `L >= 0.55 -> 黒`, 間は前回値維持）。
4. `OverlayWindow` 側で最終的に `OverlayBackgroundOpacity` を反映して描画。

### 5.3 既存パターン整合
- 表示は既存 `OverlayPresenter.Update()` を維持し、描画データ（`OverlayItem`）を拡張するだけに留める。
- 既存の固定ROI/非固定ROI分岐は維持し、色付けのみ追加。
- 自動推定OFF時は既存背景（半透明黒）をそのまま使う。

## 6. インターフェース設計
### 6.1 AppSettings 追加
- `EnableAdaptiveOverlayBgColor: bool = false`

### 6.2 UI（Mainタブ > Overlay）
- `Match overlay bg to scene color`（CheckBox）
- 既存 `Overlay opacity` は変更しない（同じスライダーを推定色にも適用）。
- 配置は `Main` タブの `Overlay` セクションに置き、`Overlay opacity` と同じ場所で運用する。

### 6.3 バリデーション
- 推定失敗時は既存 `OverlayBackground` にフォールバック。
- 文字色決定不能時は既存 `OverlayForeground` にフォールバック。
- `PerBox` サンプリング時は `OverlayItem.Rect (screen)` を `roiBitmap` 座標へ変換してから外周リングを評価する。
- 変換後矩形/リングは `roiBitmap` 範囲へ clamp し、負座標や範囲外アクセスを禁止する。

## 7. 実装手順（ステップ分割）
### Step 1: PerBox 実装
- `OverlayBackgroundSampler` を追加し、`OverlayItem.Rect` 外周リング（枠外数px）の代表色（中央値推奨）を算出。
- サンプリング前に `screen -> roi local` 座標変換を行う（`roiScreen` 原点差分を使用）。
- 変換後のリング領域を `roiBitmap` に clamp し、サンプル不能時は既存背景にフォールバック。
- 背景色から白/黒文字色を決定し、`OverlayItem.BackgroundColor` / `ForegroundColor` に設定。
- 白黒判定は固定ヒステリシス閾値（`0.45/0.55`）で開始し、初期実装では UI 露出しない。
- `OverlayWindow` で `OverlayItem.BackgroundColor` / `ForegroundColor` を使って描画。
- 透明度は必ず `OverlayBackgroundOpacity` を後段で適用。

### Step 2: UI/設定保存
- `Main` タブ `Overlay` セクションに新規設定を追加し、`SaveSettingsAsync` / `ApplySettingsToUi` に反映。
- 既定値は OFF（現行半透明黒維持）。

### Step 3: スモーク確認
- ON/OFF切替時に即反映されること。
- 推定ON時に枠ごと背景色が変わること。
- 透明度スライダー変更が推定色にも同様に効くこと。

## 8. 非機能要件チェック
- 性能:
  - `PerBox` は枠数比例で増加するため、サンプル点数を上限化する。
- 可観測性:
  - `EnableLogging` 時のみ、フォールバック回数をログ出力。
- 互換性:
  - 新設定OFF時は既存表示と同一。

## 9. リスクと緩和策
- Risk: PerBoxでちらつきが増える。
- Mitigation: 色更新に平滑化（EMA）と最小更新閾値を導入する。

- Risk: 背景輝度の境界付近で白/黒文字色が頻繁に切り替わる。
- Mitigation: 白黒切替にヒステリシス（閾値2段）を導入する。

- Risk: 座標変換ミスで誤領域をサンプルし、背景色が不安定になる。
- Mitigation: `screen -> roi local` 変換と clamp を必須手順として固定し、範囲外時は既存背景へフォールバック。

- Risk: 計算コスト増でOverlay更新遅延。
- Mitigation: サンプル数上限と早期打切りを実装する。

## 10. 影響範囲（変更ファイル候補）
- `Models/AppSettings.cs` — 新規設定項目追加
- `Models/OverlayItem.cs` — 任意背景色/前景色プロパティ追加
- `Services/PipelineOrchestrator.cs` — `PerBox` 背景色推定の呼び出し追加
- `Services/OverlayPresenter.cs` — 必要に応じて描画データ受け渡し調整
- `UI/OverlayWindow.xaml.cs` — アイテム単位背景色/前景色の描画反映 + opacity適用
- `MainWindow.xaml` / `MainWindow.xaml.cs` — Mainタブ Overlay セクションへの UI追加と保存・読込処理
- `Services/OverlayBackgroundSampler.cs`（新規） — 色推定ロジック

## 11. Definition of Done
- [ ] 新UI設定（ON/OFF）が保存・再読込できる。
- [ ] 推定ON時に `PerBox` で枠ごとの背景色推定が動作し、失敗時フォールバックが機能する。
- [ ] 背景色に応じて文字色が白/黒で自動切替され、補色は使用しない。
- [ ] `PerBox` のサンプリングで `screen -> roi local` 変換と clamp が実装され、範囲外アクセスが発生しない。
- [ ] 新設定OFF時は既存表示（半透明黒）から変化しない。
- [ ] ログ有効時にフォールバック情報を確認できる。
