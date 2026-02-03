# Paddle OCR 低信頼行の除去（行単位スキップ）実装案

1. **概要（1–3行）**
- Paddle OCR が返す行単位の confidence を使い、閾値未満の行のみ除去する。
- Paddle OCR 利用時のみ有効な設定として UI/設定を追加する。

2. **ゴール / 非ゴール**
- ゴール: 低精度で「文字ではないもの」を拾った行を翻訳・表示から除外する。
- ゴール: 既存の翻訳パイプラインを崩さずにノイズ除去を追加する。
- 非ゴール: OCR検出精度そのものの改善、検出枠の再推定。
- 非ゴール: WinRT への適用（Paddleのみ）。

3. **前提・仮定**
- Paddle OCR の結果は `lines[]` に `confidence` を含む（行単位のスコア）。
- WinRT には confidence がない。

4. **現状整理**
- Paddle OCR の gRPC 返却は `lines` 配列（text/box/confidence）。
- 現在は confidence を使ったフィルタはない。

5. **提案アーキテクチャ**
- Paddle OCR 結果の行配列を**後段でフィルタ**し、
  `confidence < threshold` の行のみ除去。
- フィルタは Paddle OCR 利用時のみ適用。

6. **インターフェース設計**
- `AppSettings` に設定追加:
  - `EnablePaddleConfidenceFilter` (bool, default: false)
  - `PaddleConfidenceThreshold` (double, default: 0.5〜0.7)
- UI（Paddle OCR 設定パネル）にチェック＋スライダー/数値入力。

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に設定追加、保存/読込対応。
- Step 2: UI に設定項目追加（Paddle OCR セクション）。
- Step 3: Paddle OCR 結果を組み立てる箇所にフィルタ処理を追加。
  - 例: `confidence < threshold` の行を除去。
- Step 4: 0行になった場合は従来通り「OCR結果なし」扱い。

8. **非機能要件チェック**
- 性能: 行フィルタは O(n) で影響軽微。
- 互換性: Paddle利用時のみ適用、WinRTは現状維持。

9. **リスクと緩和策**
- Risk: 閾値を高くしすぎると正しい行も落ちる。
- Mitigation: 初期値は控えめ（0.6前後）に設定、UIで調整可能にする。

10. **影響範囲**
- `Models/AppSettings.cs`
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- Paddle OCR 解析後のパイプライン（Paddleのみ）

11. **Definition of Done**
- Paddle OCR 使用時に、閾値未満の行が翻訳/表示から除去される。
- WinRT の挙動は変わらない。
- 設定変更で ON/OFF と閾値調整ができる。
