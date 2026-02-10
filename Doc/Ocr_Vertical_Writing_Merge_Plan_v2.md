# OCR縦書き対応（縦書き判定込み）実装案

1. **概要（1-3行）**
- ロールバック後の現状では、OCR行の並びが常に `Y -> X`（上から、同Yなら左から）で確定している。
- そのため、縦書き（日本語/中国語）で「右列 -> 左列」の順序が崩れ、オーバレイ表示と翻訳投入順の両方が不自然になる。
- 書字方向判定（Horizontal / Vertical / Unknown）を先に行い、結合・整列を方向別に分岐する。

2. **ゴール / 非ゴール**
- ゴール:
- 縦書き時に、非固定ROIでも「列: 右 -> 左、列内: 上 -> 下」で並ぶこと。
- オーバレイ順と翻訳順を同じ正しい順序で揃えること（`groupedLines` を正しく生成する）。
- 判定が不確実なときは安全側で横書きへフォールバックすること。
- 非ゴール:
- OCRエンジン（WinRT/Paddle）自体の認識品質改善。
- 縦書き向け翻訳プロンプト最適化や句読点整形の高度化。

3. **前提・仮定**
- 現行パイプラインでは、`OcrLineGrouper.MergeLines(...)` の出力 `groupedLines` が以下に共通利用される。
- オーバレイ表示（非固定ROI）
- 翻訳投入順
- よって、`OcrLineGrouper` の並び順制御を修正すれば、表示と翻訳の順序は同時に改善できる。
- 縦書き自動判定は当面 `ja` / `zh*` のみ対象にし、他言語は横書き固定とする。

4. **現状整理（ロールバック後）**
- `Services/OcrLineGrouper.cs` の整列が `OrderBy(Rect.Y).ThenBy(Rect.X)` 固定。
- 2-stage merge の Stage A は横書き前提（同一行クラスタ）で、縦書きケースに不利。
- `Services/PipelineOrchestrator.cs` は非固定ROIで `groupedLines` をそのまま表示に使用。
- 翻訳も同じ `groupedLines` 順でバッチ化されるため、順序崩れがそのまま翻訳結果に伝播する。

5. **提案アーキテクチャ**
- コンポーネント構成:
- Step A: 書字方向判定（Horizontal / Vertical / Unknown）
- Step B-H: 横書き結合（既存パスを維持）
- Step B-V: 縦書き結合（同一列 -> 列間）
- データフロー:
1. OCR lines を入力。
2. 言語ゲート（`ja` / `zh*`）を通る場合のみ方向判定を有効化。
3. `Vertical` 判定なら縦書きパス、`Horizontal` / `Unknown` は横書きパスへ。
4. `groupedLines` を生成し、既存パイプラインへ返す。
- 既存パターン整合:
- `MergeLines(IReadOnlyList<OcrLine>, AppSettings)` の公開シグネチャは維持。
- `PipelineOrchestrator` 側の呼び出し構造は変更しない。

6. **インターフェース設計**
- 既存メソッド（維持）:
- `MergeLines(IReadOnlyList<OcrLine> lines, AppSettings settings)`
- 追加する内部メソッド案:
- `ShouldEnableVerticalDetectionForLanguage(string sourceLanguage)`
- `ResolveWritingMode(IReadOnlyList<OcrLine> lines, AppSettings settings)`  
  返り値: `Horizontal | Vertical | Unknown`
- `MergeVerticalLinesTwoStage(IReadOnlyList<OcrLine> lines, AppSettings settings)`
- `IsSameColumnCandidate(Rect a, Rect b, AppSettings settings)`  
  判定軸: X中心差 + 幅比
- `ShouldMergeVerticalAdjacent(Rect top, Rect bottom, AppSettings settings)`  
  判定軸: Yギャップ + 強制ブレーク
- `OrderVerticalColumns(IEnumerable<OcrLine> lines, AppSettings settings)`  
  既定: `RightToLeft`
- 設定追加（最小）:
- `EnableVerticalMerge: bool`
- `VerticalModeAutoDetect: bool`
- `VerticalModeOverride: Auto | Horizontal | Vertical`
- `VerticalColumnOrder: RightToLeft | LeftToRight`
- `VerticalGapRatio: double`

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に縦書き用最小設定を追加。
- Step 2: `OcrLineGrouper.MergeLines(...)` 冒頭で `writingMode` を決定。
- Step 3: 分岐ロジックを追加。
- `Horizontal` -> 既存横書き2-stage
- `Vertical` -> 新規縦書き2-stage
- `Unknown` -> 既存横書き2-stage（安全側）
- Step 4: 縦書き2-stageを実装。
- Stage A（同一列トークン結合）:
- 同一列判定: `X中心差 <= minWidth * ratio` かつ `幅比 >= ratioMin`
- Y昇順で隣接判定のみを行い、`Yギャップ <= minWidth * K` で結合
- `Yギャップ > minWidth * K2` は強制ブレーク
- Stage B（列間順序）:
- 列の代表Xで並べる
- 日本語/中国語は既定 `RightToLeft`
- Step 5: テキスト連結ポリシー。
- 横書き: スペース連結（現行準拠）
- 縦書き: 原則スペースなし
- Step 6: ログ追加（デバッグ用）。
- `writingMode`, `inputCount`, `mergedCount`, `columnCount`
- Step 7: ビルド確認。
- `dotnet build Hotkey-Translator.sln`

8. **非機能要件チェック**
- 性能:
- 既存の近傍探索ベースを踏襲し、計算量増加を限定する。
- セキュリティ:
- OCR後処理のみで、新規外部I/Oや機密データ経路は増やさない。
- 可観測性:
- 判定モードと件数ログを追加して誤判定時の切り分けを可能にする。
- 互換性:
- `Unknown -> Horizontal` フォールバックで既存挙動を優先。
- `ja` / `zh*` 以外は横書き固定で回帰リスクを抑える。

9. **リスクと緩和策**
- Risk: 縦書き誤判定で読み順が悪化する。
- Mitigation: `VerticalModeAutoDetect` + `Unknown` フォールバック + `VerticalModeOverride` を用意する。
- Risk: 列を跨ぐ誤結合が発生する。
- Mitigation: 隣接のみ判定、`Yギャップ` 強制ブレーク（K2）を必須化する。
- Risk: 設定が増えて運用が複雑化する。
- Mitigation: 初期は最小5設定のみ導入し、UI公開は段階的に行う。

10. **影響範囲（変更ファイル候補）**
- `Services/OcrLineGrouper.cs` — 書字方向判定、縦書き分岐、縦書き2-stage実装
- `Models/AppSettings.cs` — 縦書き関連設定の追加
- `Services/PipelineOrchestrator.cs` — 原則変更なし（ログ補強のみ任意）
- `Doc/Ocr_Vertical_Writing_Merge_Plan_v2.md` — 本計画の新規作成

11. **Definition of Done（完了条件）**
- [ ] 非固定ROIで縦書き表示が「右列 -> 左列、列内は上 -> 下」になる
- [ ] 翻訳投入順がオーバレイ表示順と一致する
- [ ] `ja` / `zh*` 以外で縦書き判定が走らず、横書き固定になる
- [ ] 判定不確実時に `Unknown -> Horizontal` へフォールバックする
- [ ] 横書きケースで既存表示順・翻訳順に回帰がない
- [ ] `dotnet build Hotkey-Translator.sln` が成功する
