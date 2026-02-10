# OCR縦書き対応（ReadingUnit導入）実装案

1. **概要（1-3行）**
- 非固定ROIの表示順と翻訳送信単位を `OcrLine` 直結から `ReadingUnit` 単位へ切り替える。
- 縦書き時は `ReadingUnit` を「列順（右→左）・列内（上→下）」で構築し、表示と翻訳の順序を一致させる。
- 固定ROIも内部では同じ `ReadingUnit` を使い、最終描画のみ1ボックス集約にする。
- Llama.cpp の単件入力は既存どおり plain 単文経路を維持し、JSON構造化ガードとの衝突を避ける。

2. **ゴール / 非ゴール**
- ゴール:
- 非固定ROIで縦書き表示順が正しくなる（右列→左列）。
- 翻訳送信が細切れOCR枠ではなく、縦書きの意味単位（ReadingUnit）になる。
- 表示順と翻訳順が同じデータ構造（ReadingUnit）に統一される。
- 非ゴール:
- OCR認識精度そのものの改善。
- Llama.cpp 側プロンプト設計の全面刷新。
- Gemini/DeepL の挙動最適化。

3. **前提・仮定**
- 現行は `PipelineOrchestrator` が `groupedLines` をそのまま非固定ROI表示と翻訳投入に使っている。
- 現行翻訳マッピングは `Dictionary<string, string>`（原文テキストキー）で、同文重複時の対応が弱い。
- Llama.cpp は `len(texts)==1` で plain 単文経路、`len(texts)>1` で batch JSON/schema+grammar ガード経路に入る。

4. **現状整理**
- 固定ROI表示順は `VerticalModeOverride=Vertical` で改善済み。
- 非固定ROIは `groupedLines` の順序と粒度に依存し、縦書き時に左列先頭や細切れ送信が起きる。
- 翻訳の pending/cache は `line.Text` 基準のため、行分割状態に強く依存する。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `ReadingUnit`（新規レコード）
- `ReadingUnitBuilder`（新規サービス）
- `PipelineOrchestrator`（`groupedLines -> readingUnits` 変換利用）
- データフロー:
1. OCR -> `groupedLines`（既存）
2. `ReadingUnitBuilder.Build(groupedLines, settings)`（新規）
3. 非固定ROI表示は `readingUnits` 順で描画
4. 固定ROI表示は `readingUnits` を同順でテキスト連結し、1ボックスに集約して描画
5. 翻訳投入は `readingUnits.Text` 配列
6. 翻訳結果は `UnitId` 基準で保持し、表示に反映
- 既存パターン整合:
- 翻訳プロバイダ I/F（`IReadOnlyList<string> texts`）は維持し、送信元のみ `ReadingUnit` に変更する。

6. **インターフェース設計**
- 新規モデル:
- `Models/ReadingUnit.cs`
- 例: `public sealed record ReadingUnit(int Id, string Text, Rect Rect, int LineCount, double LineHeight, IReadOnlyList<int> SourceIndices);`
- 新規サービス:
- `Services/ReadingUnitBuilder.cs`
- `Build(IReadOnlyList<OcrLine> groupedLines, AppSettings settings): IReadOnlyList<ReadingUnit>`
- 既存改修:
- `PipelineOrchestrator.ResolveTranslationsAsync(...)` を `ReadingUnit` 対応へ変更
- 翻訳保持辞書を `Dictionary<int, string>`（`UnitId -> translated`）中心へ変更
- Llama単件経路ポリシー:
- `readingUnits.Count == 1` の場合、送信配列は1件のまま（既存 plain 単文経路を維持）
- `readingUnits.Count >= 2` の場合、既存 batch 経路（JSON schema/grammar）を利用

7. **実装手順（ステップ分割）**
- Step 1: `ReadingUnit` と `ReadingUnitBuilder` を追加。
- Step 2: `ReadingUnitBuilder` に縦書き構築を実装。
- 列クラスタ化: X中心差 + 幅比 + 最低限の水平オーバーラップガード
- 列内並び: Y昇順
- 列順: `VerticalColumnOrder`（既定 `RightToLeft`）
- 連結: 縦書きは原則スペースなし、横書きは既存スペース連結
- Step 3: 非固定ROI表示を `groupedLines` から `readingUnits` に切替。
- Step 3-1: 固定ROI表示も `readingUnits` を入力にし、最終段のみ1ボックス結合描画へ統一。
- Step 4: 翻訳投入・キャッシュ・前回翻訳参照を `ReadingUnit` 基準に切替。
- pendingは `UnitId` を保持し、同文重複でもユニット単位で復元可能にする。
- Step 5: `OverlayTextMode.Translated` の参照先を `UnitId` 辞書へ切替。
- Step 6: ログ追加（`readingUnits.Count`, `vertical/horizontal`, `pendingCount`）。
- Step 7: ビルド・手動検証。

8. **非機能要件チェック**
- 性能:
- `ReadingUnitBuilder` は既存と同程度の近傍判定コストに収める。
- セキュリティ:
- 外部I/O変更なし（翻訳先API形式は既存と同一）。
- 可観測性:
- `groupedLines -> readingUnits` の件数変化をログに出す。
- 互換性:
- `VerticalModeOverride` が `Horizontal/Auto` の場合は既存横書き優先を維持。
- Llama単件経路は維持し、既存ガード方針を壊さない。

9. **リスクと緩和策**
- Risk: ReadingUnitの過結合で意味単位が崩れる。
- Mitigation: 列分離ガード（X差 + overlap）を導入し、過結合時は段階的に閾値を調整。
- Risk: 同文重複で翻訳マッピングが崩れる。
- Mitigation: テキストキー依存を減らし、`UnitId` で表示反映する。
- Risk: 単件比率の増加でLlama batchガード経路が使われにくくなる。
- Mitigation: 単件はplain前提で許容し、必要に応じて最小2件バッチ化ポリシーを将来検討する。

10. **影響範囲（変更ファイル候補）**
- `Models/ReadingUnit.cs` — 新規
- `Services/ReadingUnitBuilder.cs` — 新規
- `Services/PipelineOrchestrator.cs` — 非固定ROI表示順/翻訳投入を `ReadingUnit` 化
- `Services/OcrLineGrouper.cs` — 必要なら列分離閾値の再利用のみ
- `Doc/Ocr_Vertical_ReadingUnit_Plan.md` — 本計画

11. **Definition of Done（完了条件）**
- [ ] 非固定ROIで縦書き表示順が右列→左列になる
- [ ] 固定ROIでも ReadingUnit 順で連結され、縦書き時の結合表示順が正しい
- [ ] 縦書き翻訳送信件数が細切れOCR枠数より減り、意味単位で送信される
- [ ] `readingUnits.Count==1` でLlama単件plain経路が維持される
- [ ] `readingUnits.Count>=2` でLlama batch JSON/schema+grammar経路が維持される
- [ ] 同文重複ケースで翻訳表示が崩れない（`UnitId` 反映）
- [ ] `dotnet build Hotkey-Translator.sln` が成功する
