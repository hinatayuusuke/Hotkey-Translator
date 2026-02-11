# Overlay小枠可読性ブースト 実装案

1. **概要（1–3行）**
- 目的は「小さすぎるOCR枠の文字が読めない」問題を、Overlay描画時の枠/フォント補正で改善すること。
- 判定は面積ではなく、短辺と行高ベースの `effectiveTextPx` を主指標にする。
- OCR認識結果や翻訳送信テキストは変更せず、表示レイヤのみで完結させる。

2. **ゴール / 非ゴール**
- ゴール:
- 小枠（特に横長/縦長1行）で文字が潰れるケースを減らす。
- UIは最小公開（ON/OFF + 感度）に絞り、運用負荷を下げる。
- 既存の翻訳品質・翻訳順序・OCR差分判定に影響を与えない。
- 非ゴール:
- OCR検出座標そのものの補正。
- 認識精度（OCRモデル品質）の改善。
- 複雑な自動レイアウト（衝突回避の最適化）を初回実装で完成させること。

3. **前提・仮定**
- 現行は `OverlayWindow.UpdateItems` で OCR枠 `OverlayItem.Rect` をそのまま表示し、フォントだけを枠内フィットしている。
- 小枠では枠寸法自体が小さいため、ベースフォントを上げても可読性が不足しやすい。
- 面積だけだと「横長/縦長で実質文字厚みが小さい枠」を取り逃す。

4. **現状整理**
- `MainWindow` には Overlayの基本フォント/不透明度UIはあるが、小枠専用補正UIはない。
- `OverlayWindow.ResolveFontSize` は availableWidth/Height 前提のフィッティングで、枠拡大ロジックはない。
- 固定ROI Overlayでは全テキストを1つの枠に集約するため、小枠補正の効果が出にくい。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `OverlayWindow` に「表示用補正ステップ」を追加（OCRデータ本体は不変）。
- `AppSettings` に小枠補正設定を追加し、`MainWindow` の Settings > OCR (Advanced) から操作可能にする。
- データフロー / シーケンス:
1. `PipelineOrchestrator` が既存通り `OverlayItem` を生成
2. `OverlayWindow.UpdateItems` で各 item に対して `ComputeSmallBoxBoost()` 実行
3. 補正後の rect/font を使って TextBlock/Border を描画
- 既存パターンへの整合:
- 既存のフォント安定化（量子化・ヒステリシス）は維持。
- 既存 OCR/翻訳パイプラインは非変更。

6. **インターフェース設計**
- 追加設定（UI公開）:
- `EnableSmallBoxReadabilityBoost: bool = false`
- `SmallTextThresholdPx: double = 22`
- 追加設定（内部固定・非UI）:
- `SmallBoxMaxScale: double = 1.6`
- `SmallBoxFontScaleWeight: double = 0.7`  （0=枠のみ拡大, 1=枠倍率をフォントに完全追従）
- `SmallBoxSlenderAspectThreshold: double = 3.0`
- `SmallBoxSlenderThresholdBoost: double = 1.2`
- NOTE: 上記4項目はUI非公開だが `settings.json` で調整可能にする（上級者向け）。
- NOTE: 読み込み時に範囲外値はクランプする（例: scale/weight/threshold の下限・上限）。
- 判定アルゴリズム（MUST）:
- `shortSide = min(rect.Width, rect.Height)`
- `effectiveTextPx = (lineHeight > 0) ? lineHeight : shortSide / max(1, lineCount)`
- `dynamicThreshold = SmallTextThresholdPx`
- `lineCount == 1 && (max(width,height) / max(1,shortSide)) >= SmallBoxSlenderAspectThreshold` のとき `dynamicThreshold *= SmallBoxSlenderThresholdBoost`
- `effectiveTextPx < dynamicThreshold` のとき小枠補正を適用
- 補正アルゴリズム（MUST）:
- `scaleRaw = dynamicThreshold / max(1, effectiveTextPx)`
- `boxScale = clamp(scaleRaw, 1.0, SmallBoxMaxScale)`
- 枠は中心固定で拡大し、画面外/ROI外にはみ出す分はクリップ
- `fontScale = 1.0 + (boxScale - 1.0) * SmallBoxFontScaleWeight`
- `targetFont = clamp(baseFont * fontScale, MinFontSize, MaxFontSize)`
- 適用範囲（初回）:
- 非固定ROI Overlay（`EnableFixedRoiOverlay=false`）のみ適用
- 固定ROIは既存挙動維持（1枠集約のため小枠補正の意味が薄い）

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に設定項目を追加。
- Step 2: `MainWindow.xaml` / `MainWindow.xaml.cs` にUI（`EnableSmallBoxReadabilityBoost` + `SmallTextThresholdPx` のみ）を追加し保存連携。
- Step 2.5: UI非公開パラメータ（`SmallBoxMaxScale` など4項目）は `settings.json` から読み込み、範囲検証して適用。
- Step 3: `OverlayWindow.ApplyStyle` に設定受け渡しを追加。
- Step 4: `OverlayWindow.UpdateItems` に `ComputeSmallBoxBoost()` を導入し、補正後rect/fontで描画。
- Step 5: 非固定ROIでの手動検証（横長1行・縦長1行・通常複数行）。
- Step 6: ログ（デバッグレベル）で `effectiveTextPx`, `boxScale`, `fontScale` を出せるようにする（必要時のみ）。

8. **非機能要件チェック**
- 性能:
- 1 item あたり定数時間計算のみ。描画負荷増は軽微。
- セキュリティ:
- 外部I/Oなし。
- 可観測性:
- 補正ヒット率と平均倍率をログで観測可能にする。
- 互換性:
- デフォルトOFFで既存挙動を維持。
- OCR結果/翻訳送信データは不変。

9. **リスクと緩和策**
- Risk: 枠拡大で隣接枠と重なり、読みにくくなる。
- Mitigation: `SmallBoxMaxScale` の上限を保守的にし、初期値を1.6程度に抑える。
- Risk: 過補正でレイアウトが不安定になる。
- Mitigation: フォント追従は内部固定の `SmallBoxFontScaleWeight` で抑制し、既存フォント安定化を維持。
- Risk: `settings.json` に不正値が入ると挙動が破綻する。
- Mitigation: 設定ロード時にクランプ + 異常値ログを実装する。
- Risk: 固定ROIで期待と違う挙動になる。
- Mitigation: 初回は非固定ROI限定にして作用範囲を明確化する。

10. **影響範囲（変更ファイル候補）**
- `Models/AppSettings.cs` — 小枠可読性ブースト設定追加。
- `MainWindow.xaml` — 設定UI追加。
- `MainWindow.xaml.cs` — 設定の読込/保存/表示更新。
- `UI/OverlayWindow.xaml.cs` — 小枠判定・枠/フォント補正ロジック追加。
- `Doc/Overlay_SmallBox_Readability_Boost_Plan.md` — 本実装案。

11. **Definition of Done**
- [ ] デフォルトOFF時に既存表示と同一。
- [ ] ON時、`effectiveTextPx` が小さい枠だけ拡大される。
- [ ] 面積が中程度でも横長/縦長1行枠が適切に補正対象になる。
- [ ] 翻訳送信内容は補正前後で不変。
- [ ] 固定ROIでは挙動が変わらない（初回仕様どおり）。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
