# OCR枠結合のエンジン別補正係数 実装案

1. **概要（1–3行）**
- WinRT/Paddle で枠特性が異なるため、結合閾値を「共通ベース + エンジン別補正係数」で運用する。
- `Doc/WinRT-settings.json` をベース値、`Doc/Paddle-settings.json` をターゲット値として係数を導出する。
- 初期実装は横結合関連を優先し、縦結合は同値のため係数 `1.0` で開始する。

2. **ゴール / 非ゴール**
- ゴール:
- 1つの設定体系を維持したまま、WinRT/Paddle で実効閾値を分けられる。
- 既存 `settings.json` 互換を壊さずに移行できる。
- 調整時に「どの実効値で動いたか」をログで追跡できる。
- 非ゴール:
- すべての閾値をエンジンごとに二重管理すること。
- UIに大量のエンジン別閾値項目を露出すること。
- いきなり最適化を全項目へ適用すること。

3. **前提・仮定**
- `Doc/WinRT-settings.json` と `Doc/Paddle-settings.json` は検証済みの基準値。
- 現行の主要差分は横結合側に集中している。
- `OcrLineGrouper` は `AppSettings` の閾値を直接参照しているため、入口で実効値を一度解決する構造へ寄せるのが安全。

4. **現状整理**
- `OcrLineGrouper` は全エンジンで同一閾値を使用。
- 実測比較（Doc設定差分）:
- `MergeOverlapRatioThreshold`: WinRT=0.30, Paddle=0.16
- `MergeVerticalWeight`: WinRT=0.70, Paddle=0.90
- `MergeThresholdRatio`: WinRT=0.90, Paddle=0.70
- `RowMergeYCenterToleranceRatio`: WinRT=0.45, Paddle=0.55
- `RowMergeHeightRatioMin`: WinRT=0.55, Paddle=0.45
- `RowMergeMaxGapRatio`: WinRT=1.40, Paddle=1.10
- `RowMergeHardBreakRatio`: WinRT=1.80, Paddle=1.40
- 縦系（`VerticalGapRatio`, `VerticalColumnMerge*`）は同値。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `AppSettings` に「ベース値（既存）」 + 「エンジン別係数（新規）」を追加。
- `OcrLineGrouper` の先頭で `EffectiveLineMergeProfile` を構築し、以降は実効値を参照。
- データフロー / シーケンス:
1. OCRエンジン種別（WinRt / Paddle）を取得。
2. ベース値にエンジン係数を掛けて実効値を計算。
3. 実効値を clamp で安全範囲に丸める。
4. 結合ロジックは実効値を使用。
5. 1回/実行で実効値ログを出力。
- 既存パターンへの整合:
- 係数のデフォルトは `WinRT=1.0`, `Paddle=1.0` とし、有効化前は現行挙動と一致。

6. **インターフェース設計**
- 新規設定（案）:
- `EnableEngineScaledLineMergeProfile: bool = false`
- `PaddleMergeOverlapScale: double = 0.533`   (`0.16/0.30`)
- `PaddleMergeVerticalWeightScale: double = 1.286` (`0.90/0.70`)
- `PaddleMergeThresholdScale: double = 0.778` (`0.70/0.90`)
- `PaddleRowMergeYCenterToleranceScale: double = 1.222` (`0.55/0.45`)
- `PaddleRowMergeHeightRatioMinScale: double = 0.818` (`0.45/0.55`)
- `PaddleRowMergeMaxGapScale: double = 0.786` (`1.10/1.40`)
- `PaddleRowMergeHardBreakScale: double = 0.778` (`1.40/1.80`)
- `PaddleVerticalGapScale: double = 1.0`
- `PaddleVerticalColumnMergeOverlapScale: double = 1.0`
- `PaddleVerticalColumnMergeWeightScale: double = 1.0`
- `PaddleVerticalColumnMergeThresholdScale: double = 1.0`
- `PaddleVerticalColumnMergeHardBreakScale: double = 1.0`
- 実効値計算:
- `effective = base * scale`
- 整数項目（`*NeighborCount`）は今回スケール対象外（ベース値使用）。
- clamp（例）:
- overlap系: `0.05..0.95`
- ratio系: `0.1..5.0`
- weight系: `0.0..5.0`

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` にエンジン別係数と有効化フラグを追加（既定値は互換重視）。
- Step 2: `OcrLineGrouper` に `EffectiveLineMergeProfile` を追加し、参照箇所を置換。
- Step 3: OCRエンジン種別の受け渡しを `OcrLineGrouper` へ追加（必要最小限）。
- Step 4: 実効値ログ出力を追加（エンジン種別 + 実効閾値）。
- Step 5: `EnableEngineScaledLineMergeProfile=false/true` の比較検証を実施。

8. **非機能要件チェック**
- 性能:
- 実効値計算は軽量（1回の乗算+clamp）で影響は無視できる。
- セキュリティ:
- 追加の外部I/Oなし。
- 可観測性:
- 実効閾値ログにより、現場チューニングの再現性を確保。
- 互換性:
- 係数機能OFF時は現行と同一挙動。

9. **リスクと緩和策**
- Risk: 係数が強すぎると誤結合/未結合が増える可能性。
- Mitigation: `EnableEngineScaledLineMergeProfile` を初期OFFにし、段階的有効化 + clamp で暴れを抑える。
- Risk: 係数項目が増えて運用が複雑化する可能性。
- Mitigation: 初期は横結合7項目のみ実効調整対象として運用し、縦系は1.0固定で開始。
- Risk: エンジン判定の連携漏れ。
- Mitigation: ログにエンジン名と実効値を必ず出して確認可能にする。

10. **影響範囲**（変更ファイル候補・移行・ドキュメント更新）
- `Models/AppSettings.cs` — エンジン別係数と有効化フラグを追加。
- `Services/OcrLineGrouper.cs` — 実効値プロファイル導入と参照置換。
- `Services/PipelineOrchestrator.cs` または `Services/OcrEngine.cs` — エンジン種別の連携（必要に応じて）。
- `Doc/Ocr_LineMerge_Settings_Guide.md` — 実効値計算ルールと運用手順を追記。

11. **Definition of Done**
- [ ] 係数機能OFFで現行挙動と一致する。
- [ ] 係数機能ONで WinRT/Paddle の実効閾値が分岐する。
- [ ] 実効閾値ログで、ベース値・係数・計算結果が確認できる。
- [ ] `Doc/WinRT-settings.json` と `Doc/Paddle-settings.json` の差分方向に挙動が寄る。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。

