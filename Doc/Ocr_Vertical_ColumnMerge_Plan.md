# OCR縦書き 列間横結合（Vertical Column Merge）実装案

1. **概要（1–3行）**
- 縦書き判定後の列（column）を対象に、横書きB-stageを転置した「列間横結合」を追加する。
- 実装は `OcrLineGrouper` 内で完結させ、`ReadingUnit`/翻訳送信フローとの契約を維持する。
- 過結合を防ぐため、重なりゲート + コスト閾値 + ハードブレークの3段ガードを採用する。

2. **ゴール / 非ゴール**
- ゴール:
- 縦書きで「本来は同じ文脈の隣接列」が分断されるケースを減らす。
- 非固定ROI/固定ROIとも、表示順と翻訳送信順の一貫性を維持する。
- `ReadingUnit` の責務分離（Grouper正本）を壊さない。
- 非ゴール:
- OCR認識精度そのものの改善。
- `ReadingUnitBuilder` 側での再クラスタ・再結合。
- 縦書き混在判定ロジック（H/V選択）の全面刷新。

3. **前提・仮定**
- 現行は縦書きで「同一列判定 + 列内Y隣接結合」まではあるが、列と列の横結合はない。
- 翻訳投入単位は `ReadingUnitBuilder` が `groupedLines` を1:1写像しており、Grouper変更はそのまま下流に伝播する。
- 変更検知は `SourceIndices` 依存のため、結合は Grouper 内で完結させるのが安全。

4. **現状整理**
- `MergeVerticalLinesTwoStage` は列クラスタ生成後、列内結合のみ実施して終了。
- `VerticalColumnOrder` は列の並び順（右→左/左→右）を決めるのみで、列間結合は行わない。
- `PipelineOrchestrator` は `groupedLines -> ReadingUnit(1:1)` 前提で翻訳送信している。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `OcrLineGrouper` に縦書き向け Stage-B（列間横結合）を追加。
- `ReadingUnitBuilder` / `PipelineOrchestrator` は変更しない（契約維持）。
- データフロー / シーケンス:
1. 縦書きモード確定（既存）
2. Stage-A: 同一列クラスタ化 + 列内Y隣接結合（既存）
3. Stage-B(new): Stage-A後の `OcrLine` を `ColumnUnit`（1列セグメント）として扱い、列ユニット同士をX方向近傍で再結合判定
4. 最終 `groupedLines` を `ReadingUnitBuilder` が1:1で単位化
5. 翻訳送信・Overlayは既存フローで反映
- 既存パターン整合:
- 横書きB-stageの思想（ゲート→コスト→UnionFind）を縦書きに転置して再利用する。
- 適用優先順位（MUST）:
- `EnableVerticalColumnMerge` が `true` で、かつ最終モードが Vertical のときのみ Stage-B を適用する。
- `VerticalModeOverride=Vertical` は常に適用対象。
- `VerticalModeOverride=Auto` は `scoreV` 算出時に「Stage-A + Stage-B適用後」のVertical結果を使って比較する。

6. **インターフェース設計**
- 追加設定（初期案）:
- `EnableVerticalColumnMerge: bool = false`（初期はOFF）
- `VerticalColumnMergeNeighborCount: int = 8`
- `VerticalColumnMergeOverlapRatioThreshold: double = 0.20`
- `VerticalColumnMergeWeight: double = 0.5`
- `VerticalColumnMergeThresholdRatio: double = 0.9`
- `VerticalColumnMergeHardBreakRatio: double = 1.8`
- 判定式（列間結合）:
- 近傍候補: X距離が近い列のみ（neighborCount制限）
- ゲート: 縦方向オーバーラップ比 `overlapHeight / minHeight >= threshold`
- コスト: `cost = weight * horizontalGap + |widthA - widthB|`
- 閾値: `cost <= minWidth * thresholdRatio`
- ハードブレーク: `horizontalGap > minWidth * hardBreakRatio` なら強制非結合
- `ColumnUnit` 定義（MUST）:
- Stage-B の入力単位は Stage-A 後 `OcrLine` とし、`Rect`/`Text`/`LineCount` をそのまま継承する。
- Stage-B で結合した `ColumnUnit` は `BuildMergedLineFromGroup(..., separator=string.Empty, forceLineCountOne=true, sortByXThenY=false)` 相当で再生成する。
- テキスト連結:
- 縦書き列間結合後のテキストは `string.Empty` 連結を維持（縦書き前提）。

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に列間横結合の設定項目を追加（初期OFF、UI露出なし）。
- Step 2: `MergeVerticalLinesTwoStage` の Stage-A 出力を `ColumnUnit`（Stage-A後 `OcrLine`）として扱う。
- Step 3: Stage-B（列間ゲート・コスト・ハードブレーク + UnionFind結合）を実装。
- Step 4: Auto判定時は「Stage-B適用後 Vertical 結果」で `scoreV` を計算するように統一。
- Step 5: `VerticalColumnOrder` を維持した最終並びを保証。
- Step 6: ログ追加（列数 before/after、結合件数、適用可否）。
- Step 7: 手動検証（固定ROI/非固定ROI、縦書き代表ケース）とビルド確認。

8. **非機能要件チェック**
- 性能:
- 追加処理は縦書き経路かつ列ユニット対象なので、全体影響は限定的。
- `neighborCount` で計算量を制限する。
- セキュリティ:
- 外部I/Oなし。
- 可観測性:
- `columns before/after`, `mergedPairs`, `skippedByHardBreak` をログ出力。
- 互換性:
- 初期OFFで既存挙動を維持し、段階展開可能。
- `ReadingUnitBuilder` と翻訳I/F契約は不変更。
- UI露出なしで導入し、設定は `settings.json` のみで管理する。

9. **リスクと緩和策**
- Risk: 近接する別セリフ列を誤って横結合する。
- Mitigation: オーバーラップゲート + ハードブレーク + 初期OFF運用で過結合を抑える。
- Risk: ユニット数減少で翻訳送信件数が変わり、体感が変わる。
- Mitigation: `before/after` 件数をログ化し、必要なら閾値を保守的に戻す。
- Risk: 読み順が乱れて表示順と翻訳順がずれる。
- Mitigation: 最終順序は `VerticalColumnOrder` を一貫適用し、後段再ソートを禁止する。

10. **影響範囲**
- `Models/AppSettings.cs` — 列間横結合の設定項目追加。
- `Services/OcrLineGrouper.cs` — 縦書き Stage-B（列間横結合）追加。
- `Doc/Ocr_Vertical_ColumnMerge_Plan.md` — 本計画。

11. **Definition of Done**
- [ ] `EnableVerticalColumnMerge=false` で既存挙動と同一。
- [ ] `EnableVerticalColumnMerge=true` で縦書き列間横結合が有効になる。
- [ ] Stage-B 入力が Stage-A後 `OcrLine`（ColumnUnit）で統一される。
- [ ] Auto判定時、`scoreV` は Stage-B適用後 Vertical 結果で算出される。
- [ ] `ReadingUnit`/翻訳送信経路でクラッシュ・マッピング崩れが発生しない。
- [ ] 非固定ROI/固定ROIともに表示順と翻訳送信順が一致する。
- [ ] 代表縦書きケースで分断列が減る（`afterUnitCount <= beforeUnitCount`）。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
