# OCR書字方向判定（双方向試行スコア選択）実装案

1. **概要（1–3行）**
- `VerticalModeOverride=Auto` かつ `SourceLanguage` が `ja/zh` のときのみ、Horizontal/Vertical の両方で `OcrLineGrouper` を実行し、品質スコアが高い結果を採用する。
- 既存の単純判定（近傍 dx/dy 投票）はノイズ源として扱い、主判定・補助判定の両方から除外する。
- 判定器自体を複雑化するより、最終生成物の品質で選ぶ方針で誤判定を減らす。

2. **ゴール / 非ゴール**
- ゴール:
- 書字方向の誤判定（縦書きなのに横扱い/その逆）を減らす。
- 縦書き時の表示順・翻訳送信順を安定化する。
- `Auto` かつ `ja/zh` のケースのみを強化し、他言語の性能影響を最小化する。
- UI から `Auto / Vertical / Horizontal` を明示選択できるようにする。
- 非ゴール:
- OCR認識精度そのものの改善。
- WinRTの CJK 文字間スペース問題の是正（別タスク）。

3. **前提・仮定**
- 現行は `OcrLineGrouper` 内の近傍幾何判定で Horizontal/Vertical を推定しているが、本計画で当該ロジックは完全撤去する。
- 誤判定が発生すると、行結合順・表示順・翻訳送信順が連鎖的に崩れる。
- 書字方向強化の対象は日本語/中国語を優先し、他言語Autoは保守的に Horizontal 固定とする。

4. **現状整理**
- `VerticalModeOverride` が `Horizontal/Vertical` のときは固定モードで動作し、Auto判定は不要。
- Auto時の判定は単一ロジック依存のため、混在レイアウトで不安定になりやすい。
- 既存ログでは「なぜその方向を選んだか」の根拠が追いにくい。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `OcrLineGrouper` に「候補結果を採点して選択する」制御を追加。
- `OcrDirectionScorer`（新規クラスまたは `OcrLineGrouper` 内 private static）を追加し、結果品質を数値化。
- データフロー / シーケンス:
1. Auto + `ja/zh` 判定に該当するかチェック。
2. 該当しない場合（Auto + 非`ja/zh`）は Horizontal 固定とする（旧単純判定は使わない）。
3. 該当する場合は `MergeAsHorizontal(lines)` と `MergeAsVertical(lines)` を両方実行。
4. 各結果に対してスコア算出。
5. 高スコア側を最終 `groupedLines` として採用。
6. 同点/僅差は言語優先を使わず、差分閾値と前回モード維持（ヒステリシス）でタイブレーク。
- 既存パターンへの整合:
- `PipelineOrchestrator` 以降の契約（`MergeLines` が最終 `IReadOnlyList<OcrLine>` を返す）は変更しない。

6. **インターフェース設計**
- 追加候補 API（例）:
- `private static bool ShouldUseBidirectionalScoring(AppSettings settings)`
- 条件: `settings.VerticalModeOverride == VerticalModeOverride.Auto` かつ `SourceLanguage` が `ja` / `zh` で開始。
- `private static double ScoreMergedResult(IReadOnlyList<OcrLine> merged, WritingMode mode)`
- UI 設定項目:
- `VerticalModeOverride` を Settings UI に表示し、選択肢は `Auto / Vertical / Horizontal` の3値固定とする。
- UI 選択値は既存 enum 値へ 1:1 で保存する（`Auto=0`, `Horizontal=1`, `Vertical=2`）。
- 不正値読込時は `Auto` にフォールバックし、ログへ1回だけ警告を出す。
- 採点要素（要件）:
- `SingleCharUnitRatioScore`: 1文字ユニット比率が低いほど高得点。
- `AbnormalSpaceRatioScore`: 不自然空白率（連続空白・CJK間空白など）が低いほど高得点。
- `RectVarianceScore`: 行/列の矩形ばらつき（中心線/サイズ）が小さいほど高得点。
- `BlockAspectScore`: OCR文字ブロックの縦横比（`Rect.Height/Rect.Width`）がモード仮説と整合するほど高得点。
- 正規化ルール（MUST）:
- 各指標は `0.0..1.0` へ正規化し、`1.0` が最良とする。
- 分母が 0 になるケース（1件しかない等）は `0.5`（中立）で扱う。
- 総合スコア計算時の欠損指標は除外せず中立値で計算し、重み再配分はしない。
- `BlockAspectScore` の適用方針:
- 縦仮説では縦長ブロック比率が高いほど加点、横仮説では横長ブロック比率が高いほど加点。
- 極小ブロックやノイズ（面積が小さすぎる枠）は重みを落とす、または集計対象外にする。
- 面積フィルタ初期値（推奨）:
- `rect.Area < max(16, medianArea * 0.15)` のブロックは `BlockAspectScore` 集計対象外とする。
- 総合スコア（例）:
- `Total = w1*SingleChar + w2*Space + w3*Rect + w4*Aspect`（初期重みは 0.35 / 0.25 / 0.25 / 0.15）
- ログ出力（必須）:
- `mode=Horizontal score=...` / `mode=Vertical score=...` / `selected=...` を1行で出す。

7. **実装手順（ステップ分割）**
- Step 0: Settings UI に `VerticalModeOverride` セレクタ（Auto / Vertical / Horizontal）を追加。
- Step 1: `OcrLineGrouper` に実行条件 `ShouldUseBidirectionalScoring` を追加（Auto + `ja/zh` 限定）。
- Step 2: Horizontal/Vertical の両モードで merge を実行する内部経路を追加（Auto + 非`ja/zh` は Horizontal 固定）。
- Step 3: 採点ロジック（4指標）を実装し、総合点で最終結果を選択。
- Step 3-1: `BlockAspectScore` を追加し、`Rect.Height/Rect.Width` の分布を mode 別に評価する。
- Step 4: 同点・僅差時のタイブレークを実装（旧判定・言語優先は使わない）。`abs(scoreH-scoreV) < epsilon` のとき前回モード維持、初回は Horizontal。
- Step 5: 判定根拠ログを追加。
- Step 6: 手動検証（縦書き/横書き/混在）とビルド確認。

8. **非機能要件チェック**
- 性能:
- 追加コストは主に `OcrLineGrouper` 実行の二重化分。全体影響は OCR/翻訳支配のため限定的。
- 実行対象を `Auto + ja/zh` に限定し、他ケースへの負荷を回避する。
- セキュリティ:
- 外部I/O・認証情報の変更なし。
- 可観測性:
- スコア内訳と最終選択をログ化し、誤判定ケースを追跡可能にする。
- 互換性:
- `VerticalModeOverride=Horizontal/Vertical` は現行どおり固定挙動を維持。

9. **リスクと緩和策**
- Risk: スコア閾値が不適切で誤選択が残る。
- Mitigation: 内訳ログを見ながら重みを段階調整し、初期は conservative に運用。
- Risk: 処理時間増加。
- Mitigation: 実行条件を `Auto + ja/zh` に限定し、必要なら行数少時の片側省略を追加。
- Risk: 混在レイアウトでスコアが拮抗。
- Mitigation: 旧判定・言語優先に戻さず、差分閾値・前回モード維持（ヒステリシス）で揺れを抑える。
- Risk: 記号・ルビ・小さいUI文字で縦横比指標が汚染される。
- Mitigation: 面積下限フィルタや重み減衰で `BlockAspectScore` のノイズ影響を抑える。
- Risk: UI 表示名と enum 永続値の不整合で設定意図と実挙動がズレる。
- Mitigation: UI と enum の対応を固定し、未知値は `Auto` へフォールバックして警告ログを出す。

10. **影響範囲**
- `Services/OcrLineGrouper.cs` — 双方向試行・採点・選択ロジックの追加。
- （必要時）`Services/OcrDirectionScorer.cs` — 採点ロジックの分離実装。
- `ViewModels/Settings*` / `Views/Settings*`（実ファイル名は現行実装に合わせる） — `VerticalModeOverride` UI 追加。
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` — 本計画。

11. **Definition of Done**
- [ ] Settings UI で `Auto / Vertical / Horizontal` を選択・保存・再読込できる。
- [ ] `VerticalModeOverride=Auto` かつ `ja/zh` のときのみ双方向試行が有効。
- [ ] `Horizontal/Vertical` 固定モードでは双方向試行が走らない。
- [ ] `VerticalModeOverride=Auto` かつ非`ja/zh` では Horizontal 固定となる（旧単純判定は使わない）。
- [ ] 既存の単純判定（近傍 dx/dy 投票）は主判定・補助判定の両方で使用しない。
- [ ] 1文字ユニット比率・不自然空白率・矩形ばらつき・ブロック縦横比の4指標で採点される。
- [ ] 4指標は `0..1` 正規化・欠損中立値（0.5）で実装される。
- [ ] 判定ログに `scoreH/scoreV/selected` が出力される。
- [ ] 同点/僅差タイブレークはヒステリシスのみで、言語優先を使わない。
- [ ] 代表検証セットで誤判定率が現行より改善する（縦20ケース・横20ケース以上）。
- [ ] `EnableOcrPerfLog` で計測した `groupMs` の増分中央値が +10ms 以内。
- [ ] 既存の `dotnet build Hotkey-Translator.sln` が成功する。
