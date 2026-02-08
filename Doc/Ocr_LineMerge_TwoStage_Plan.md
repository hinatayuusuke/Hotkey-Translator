# OCR枠結合 2段階化（同一行トークン結合 → 行間結合）実装案

1. **概要（1–3行）**
- 現行の行結合は縦方向中心の判定で、`7.` のような同一行内トークン分離を取りこぼしやすい。
- 結合処理を2段階化し、まず横方向（同一行）を安定化してから縦方向（段落）を結合する。
- 既存の縦方向ロジックは活かしつつ、前段に「同一行トークン結合」を追加する。

2. **ゴール / 非ゴール**
- ゴール: 番号プレフィックス（`7.`）、箇条書き記号（`*`, `-`）と本文が同一行として結合される。
- ゴール: 現在の縦方向結合品質を維持しながら、横分割の取りこぼしを減らす。
- 非ゴール: OCRエンジン自体の再学習・変更。
- 非ゴール: 句読点補正や翻訳品質改善。

3. **前提・仮定**
- 現行の `Services/OcrLineGrouper.cs` は `PassesAlignmentGate + IsMergeableByCost` の1段構成。
- `AppSettings` には Merge 系パラメータが存在するが、現状UIで編集できない。
- 入力は `OcrLine`（Rect, Text, Confidence など）として渡される。

4. **現状整理**
- 現行ロジックは「Y順ソート + 近傍探索 + UnionFind」で行単位を統合。
- 判定が縦ギャップ/高さ差寄りのため、同一行内の横分離（左に短トークン、右に本文）に弱い。
- 典型失敗例: `7.` と見出し本文が別枠のまま残る。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - Stage A: 同一行トークン結合（横方向）
  - Stage B: 既存行結合（縦方向）
- データフロー / シーケンス:
  1. OCR結果 `lines` を入力
  2. **Stage A** で同一行候補を UnionFind で結合
  3. Stage A 出力を **Stage B** に渡し、既存の縦方向結合を適用
  4. 最終 `OcrLine` を返却
- 既存パターンへの整合:
  - UnionFind / ソート / 最終 `string.Join(Environment.NewLine, ...)` 方式を継続
  - Stage B は既存の `PassesAlignmentGate/IsMergeableByCost` を再利用

6. **インターフェース設計**
- 既存メソッド:
  - `MergeLines(IReadOnlyList<OcrLine> lines, AppSettings settings)` は維持
- 新規内部メソッド（案）:
  - `MergeSameRowTokens(...)`（Stage A）
  - `IsSameRowCandidate(Rect a, Rect b, AppSettings settings)`
  - `BuildMergedLineFromGroup(...)`（共通化）
- 設定追加（AppSettings, 初期値案）:
  - `EnableTwoStageLineMerge: bool = true`
  - `RowMergeYCenterToleranceRatio: double = 0.45`
  - `RowMergeHeightRatioMin: double = 0.55`
  - `RowMergeMaxGapRatio: double = 1.25`
  - `RowMergeNeighborCount: int = 24`
- 判定式（Stage A）:
  - Y中心差: `abs(centerY(a)-centerY(b)) <= min(hA,hB)*RowMergeYCenterToleranceRatio`
  - 高さ比: `min(hA,hB)/max(hA,hB) >= RowMergeHeightRatioMin`
  - Xギャップ: `gapX <= min(hA,hB)*RowMergeMaxGapRatio`

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に Stage A 用パラメータを追加（既存互換を壊さない既定値）。
- Step 2: `OcrLineGrouper` を以下順に分離:
  - 2-1. 既存処理を `MergeVerticalLines` として抽出
  - 2-2. Stage A `MergeSameRowTokens` を実装
  - 2-3. `MergeLines` で Stage A → Stage B を直列化
- Step 3: Stage A のしきい値（Y中心差/高さ比/Xギャップ）を再現ケースで調整。
- Step 4: ログ（任意）
  - `Line merge: input=N, stageA=M, stageB=K` を debug/info で出力可能にする。
- Step 5: 設定は `settings.json` 直編集で運用する（UI追加は行わない）。

8. **非機能要件チェック**
- 性能: O(N * neighborCount) を維持。`RowMergeNeighborCount` の上限を設け暴走回避。
- セキュリティ: 外部入力はOCRテキストのみ、既存と同等。
- 可観測性: stage別件数ログで改善効果を確認可能。
- 互換性: `EnableTwoStageLineMerge=false` で旧挙動へ戻せるようにする。

9. **リスクと緩和策**
- Risk: 横結合が強すぎると、本来別列のテキストを誤結合する。
- Mitigation: Y中心差/高さ比/gap比を同時判定し、単一条件での結合を避ける。
- Risk: フォント差の大きい画面で高さ比が合わず結合漏れ。
- Mitigation: `RowMergeHeightRatioMin` を調整可能にし、既定値は中庸に設定。
- Risk: パラメータ過多で運用負荷が増える。
- Mitigation: 固定既定値を基準にし、必要時のみ `settings.json` を調整する。

10. **影響範囲（変更ファイル候補）**
- `Services/OcrLineGrouper.cs` — Stage A追加、Stage B抽出、統合フロー更新
- `Models/AppSettings.cs` — Stage A関連設定追加
- （任意）`MainWindow.xaml.cs` — デバッグログ強化

11. **Definition of Done（完了条件）**
- [ ] `7.` とタイトル本文が同一行として結合される再現ケースで改善を確認
- [ ] 箇条書き `*` 行の先頭記号が本文と分離されにくくなる
- [ ] 既存の縦方向結合（段落結合）に大きな退行がない
- [ ] `EnableTwoStageLineMerge=false` で旧挙動を再現できる
- [ ] `dotnet build Hotkey-Translator.sln` が成功する
