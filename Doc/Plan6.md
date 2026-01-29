# Plan6: オーバレイ枠とフォントの自動フィット

## Goal
- グルーピング済みOCR枠に合わせて表示枠・フォントサイズを自動調整する
- 大きい文字が小さい枠のまま表示される問題を解消する
- 翻訳文が長くなっても破綻しないフィット挙動を実現する

## 方針（推奨）
- OCR由来の行数・行高を基準にフォントサイズを決める
- 必要時のみ測定して縮小する（翻訳文が長い場合にのみ調整）
- MaxWidthだけでなく高さも制御し、枠に対して収まるようにする
- 失敗時は既存の固定フォントサイズにフォールバックする

## 変更点（設計）

### 1) OCR行数・行高の保持
- グルーピング結果に行数と行高を持たせる
  - 行数: グルーピングした元行の数
  - 行高: 元行の高さの中央値（外れ値で膨らむのを抑える）
- 翻訳結果の改行数ではなく、OCR行数を使う
  - 翻訳で改行が消える/増えるため

### 2) モデルの拡張
- `OverlayItem` に `LineCount` と `LineHeight` を追加（候補）
  - 例: `OverlayItem(string Text, Rect Rect, int LineCount, double LineHeight)`
- `OverlayPresenter` のDPI変換で `LineHeight` もスケールする

### 3) パイプライン
- グルーピング後に `LineCount` と `LineHeight` を確定
- `PipelineOrchestrator` で `OverlayItem` 作成時に埋める

### 4) OverlayWindow の自動フィット
- 余白を考慮した利用可能領域を計算
  - `availableWidth = rect.Width - paddingX * 2`
  - `availableHeight = rect.Height - paddingY * 2`
- 基準フォントサイズを算出
  - `baseSize = Clamp(LineHeight * Scale, Min, Max)`
  - `LineHeight` が不正な場合は `rect.Height / LineCount` にフォールバック
- 計測してはみ出す場合のみ縮小
  - `FormattedText` または `TextBlock.Measure` を使い二分探索で縮める
  - 反復回数は固定（例: 6-8回）で性能を安定させる
- 枠サイズも反映
  - `Border.Width/Height` を `rect.Width/rect.Height` に設定
  - 幅・高さが0以下なら既存挙動にフォールバック

### 5) 設定（任意）
- 自動フィットのON/OFFと制限値を追加
  - `OverlayAutoFit` (bool)
  - `OverlayFontMin` / `OverlayFontMax`
  - `OverlayFontScale`
- OFF時は現行の固定フォントサイズを維持

## リスクと対策
- リスク: 計測処理で描画コストが増える
  - 対策: 反復回数を小さく固定し、枠が小さい場合は計測をスキップ
- リスク: OCR枠の空白が大きいとフォントが過大になる
  - 対策: 行高中央値を採用し、さらに縮小計測で上限を抑える

## テスト観点
- 大きい文字/小さい文字で枠とフォントが適切に追従する
- 1行/複数行の両方で自然に収まる
- 翻訳が長くなった場合でも枠内に収まる
- DPIが異なるモニタでレイアウトが破綻しない

## 実装順（推奨）
1) `OverlayItem` 拡張（LineCount/LineHeight）
2) グルーピングで行数・行高の算出
3) Pipeline -> OverlayItem への受け渡し
4) OverlayWindow の自動フィット実装
5) 設定（任意）追加とUI反映