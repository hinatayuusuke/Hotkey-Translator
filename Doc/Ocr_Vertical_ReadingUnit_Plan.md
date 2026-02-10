# OCR縦書き対応（ReadingUnit導入）実装案

1. **概要（1-3行）**
- 非固定ROIの表示順と翻訳送信単位を `OcrLine` 直結から `ReadingUnit` 単位へ切り替える。
- 横書き/縦書きの両方で `ReadingUnit` を共通利用し、モード差分による順序不一致を防ぐ。
- 縦書き時は `ReadingUnit` を「列順（右→左）・列内（上→下）」で構築し、表示と翻訳の順序を一致させる。
- 固定ROIも内部では同じ `ReadingUnit` を使い、最終描画のみ1ボックス集約にする。
- Llama.cpp の単件入力は既存どおり plain 単文経路を維持し、JSON構造化ガードとの衝突を避ける。

2. **ゴール / 非ゴール**
- ゴール:
- 非固定ROIで縦書き表示順が正しくなる（右列→左列）。
- 横書きでも表示順/翻訳送信/表示反映の単位が `ReadingUnit` で統一される。
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
- `OcrLineGrouper`（順序と結合の正本）
- データフロー:
1. OCR -> `groupedLines`（既存）
2. `OcrLineGrouper` が順序・結合済み `groupedLines` を確定（唯一の正本）
3. `ReadingUnitBuilder.Build(groupedLines, settings)` で `ReadingUnit` 化（再クラスタ/再連結はしない）
4. 横書き/縦書きともに非固定ROI表示は `readingUnits` 順で描画（再ソート禁止）
5. 固定ROI表示は `readingUnits` を同順でテキスト連結し、1ボックスに集約して描画（再ソート禁止）
6. 翻訳投入は `readingUnits.Text` 配列を順序保持で送信
7. 翻訳結果は `UnitId` 基準で保持し、表示に反映
- 既存パターン整合:
- 翻訳プロバイダ I/F（`IReadOnlyList<string> texts`）は維持し、送信元のみ `ReadingUnit` に変更する。

6. **インターフェース設計**
- 新規モデル:
- `Models/ReadingUnit.cs`
- 例: `public sealed record ReadingUnit(int Id, string Text, Rect Rect, int LineCount, double LineHeight, IReadOnlyList<int> SourceIndices);`
- 新規サービス:
- `Services/ReadingUnitBuilder.cs`
- `Build(IReadOnlyList<OcrLine> groupedLines, AppSettings settings): IReadOnlyList<ReadingUnit>`
- 役割制約（MUST）:
- `ReadingUnitBuilder` は `groupedLines` を順序どおり写像するのみ（ID付与・Rect/Text転記）。
- `ReadingUnitBuilder` での再クラスタ化・再連結・再ソートは禁止。
- 判定/連結/順序の責務は `OcrLineGrouper` 側に集約する。
- 判定・連結ルール（横書き）:
- 同じ行候補:
- `|centerY(a)-centerY(b)| <= minHeight * RowMergeYCenterToleranceRatio`
- `minHeight/maxHeight >= RowMergeHeightRatioMin`
- 連結可否（隣接のみ判定）:
- `gapX <= minHeight * RowMergeMaxGapRatio` で連結
- `gapX > minHeight * RowMergeHardBreakRatio` で強制非連結
- 判定・連結ルール（縦書き）:
- 同じ列候補:
- `|centerX(a)-centerX(b)| <= minWidth * VerticalColumnCenterToleranceRatio`
- `minWidth/maxWidth >= VerticalColumnWidthRatioMin`
- 最低限の水平オーバーラップを満たす場合のみ同列候補に採用
- 連結可否（隣接のみ判定）:
- `gapY <= minWidth * VerticalGapRatio` で連結
- `gapY > minWidth * VerticalGapRatio * K2` で強制非連結（`K2` は 1.5 目安）
- しきい値の公開方針:
- `VerticalGapRatio` は `AppSettings` 公開値を利用する。
- `VerticalColumnCenterToleranceRatio` / `VerticalColumnWidthRatioMin` / `K2` は初期段階では `OcrLineGrouper` 内部定数として保持し、検証結果次第で設定公開を判断する。
- 横書き構築ポリシー:
- 原則は1行=1 `ReadingUnit`（既存行結合結果を尊重）
- 必要時のみ既存横書き2-stageの結果をそのまま単位化し、追加の過結合はしない
- 既存改修:
- `PipelineOrchestrator.ResolveTranslationsAsync(...)` を `ReadingUnit` 対応へ変更
- 翻訳保持辞書を `Dictionary<int, string>`（`UnitId -> translated`）中心へ変更
- Llama単件経路ポリシー:
- `readingUnits.Count == 1` の場合、送信配列は1件のまま（既存 plain 単文経路を維持）
- `readingUnits.Count >= 2` の場合、既存 batch 経路（JSON schema/grammar）を利用

7. **実装手順（ステップ分割）**
- Step 1: `ReadingUnit` と `ReadingUnitBuilder` を追加。
- Step 2: `OcrLineGrouper` を結合/順序の正本として確定し、必要な縦書き判定閾値のみ調整。
- Step 2-1: `ReadingUnitBuilder` は `groupedLines` を順序保持で `ReadingUnit` に写像（再判定なし）。
- Step 3: 非固定ROI表示を `groupedLines` から `readingUnits` に切替（横/縦共通、再ソートなし）。
- Step 3-1: 固定ROI表示も `readingUnits` を入力にし、同順で1ボックス結合描画へ統一（再ソートなし）。
- 横書き固定ROIの互換要件:
- `OcrLineGrouper` が横書き時に `Y->X` 順を返すことを前提に、固定ROIでも従来の視覚順を維持する。
- Step 4: 翻訳投入・キャッシュ・前回翻訳参照を `ReadingUnit` 基準に切替（送信順保持）。
- pendingは `UnitId` を保持し、同文重複でもユニット単位で復元可能にする。
- Step 5: `OverlayTextMode.Translated` の参照先を `UnitId` 辞書へ切替。
- Step 6: ログ追加（`readingUnits.Count`, `vertical/horizontal`, `pendingCount`）。
- Step 7: ビルド・手動検証。

8. **非機能要件チェック**
- 性能:
- `ReadingUnitBuilder` は順序保持の写像中心とし、追加コストを `O(n)` 程度に抑える。
- 近傍判定コストの増加は `OcrLineGrouper` 側の閾値調整範囲に限定する。
- セキュリティ:
- 外部I/O変更なし（翻訳先API形式は既存と同一）。
- 可観測性:
- `groupedLines -> readingUnits` の件数変化をログに出す。
- 互換性:
- `VerticalModeOverride` が `Horizontal/Auto` の場合は既存横書き優先を維持。
- 横書きは1行=1 `ReadingUnit` を基本とし、既存の見た目を崩さない。
- Llama単件経路は維持し、既存ガード方針を壊さない。

9. **リスクと緩和策**
- Risk: ReadingUnitの過結合で意味単位が崩れる。
- Mitigation: 判定ロジックを `OcrLineGrouper` に一本化し、閾値調整も同一箇所で実施する。
- Risk: 後段で再ソートすると表示順と翻訳順が再び不一致になる。
- Mitigation: `ReadingUnitBuilder`/描画側で再ソート禁止を明文化し、テストで順序一致を検証する。
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
- [ ] 横書きでも ReadingUnit ベースで表示順/翻訳送信順が一致する
- [ ] 固定ROIでも ReadingUnit 順で連結され、縦書き時の結合表示順が正しい
- [ ] `ReadingUnitBuilder` が `groupedLines` と同順で出力し、再ソートしない
- [ ] 翻訳送信件数の比較は「最終的に翻訳へ送信する内容（送信直前件数）」を基準に行う
- [ ] 同一フレーム比較で `After(ReadingUnit送信件数) <= Before(現行送信件数)` を満たす
- [ ] 縦書き代表ケースで `After < Before` を確認し、細切れ送信が実際に減る
- [ ] `readingUnits.Count==1` でLlama単件plain経路が維持される
- [ ] `readingUnits.Count>=2` でLlama batch JSON/schema+grammar経路が維持される
- [ ] 同文重複ケースで翻訳表示が崩れない（`UnitId` 反映）
- [ ] `dotnet build Hotkey-Translator.sln` が成功する
