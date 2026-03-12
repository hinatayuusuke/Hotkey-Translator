# VisionLLM Hybrid Line-to-Geometry Classification Plan

1. 概要
- 現行の VisionLLM Hybrid は、geometry 枠に対してまず `1 Vision line -> 1 geometry` の greedy 対応を行い、その後で `1 Vision -> many Geometry` split や synthetic fallback を行う。
- 本案では発想を切り替え、VisionLLM の各 line を「どの geometry 枠に属するべきか」という分類問題として扱う。
- geometry 枠の位置は固定したまま、`many Vision -> 1 Geometry` を自然に扱えるようにする。

2. ゴール / 非ゴール
- ゴール:
  - geometry 枠を再配置・再結合せずに、VisionLLM line を適切な geometry 枠へ配分する。
  - `2枠 + 4行` のように Vision line 数が geometry 枠数より多いケースで、synthetic fallback を減らす。
  - 実装を full DP より軽く保ち、既存コードへ段階導入しやすくする。
- 非ゴール:
  - geometry 枠の union / split / 再配置。
  - OCR provider や `OcrLineGrouper` の仕様変更。
  - `many->many` の一般解。

3. 前提・仮定
- geometry 側は helper OCR を `MergeLines(...)` 後の完成枠として扱い、枠位置は信頼する。
- VisionLLM 側は意味単位で line を返すため、geometry 枠数より多い line を返すことがある。
- geometry OCR の text は不完全でも、各枠がどのテキスト島を表しているかのヒントにはなる。

4. 現状整理
- 現行 hybrid の課題:
  - greedy 1:1 が先に走るため、後続 Vision line の行き先がなくなりやすい。
  - `many Vision -> 1 Geometry` は、最終的に synthetic merge として吸収されるだけで、最初から最適化されていない。
- 本案の基本アイデア:
  - VisionLLM の line ごとに「geometry 枠 A / B / C ... のどこへ入るべきか」を評価する。
  - 各 line を独立に完全決定するのではなく、隣接 line の連続性も少し考慮する。

5. 提案アーキテクチャ
- コンポーネント構成:
  - `OcrAndGroupStage` は現状維持。
  - `VisionGeometryHybridAligner` に `line-to-geometry classification` pass を追加する。
- データフロー:
  1. geometry 枠ごとに normalized text を作る。
  2. Vision line ごとに normalized text を作る。
  3. 各 `Vision line -> geometry rect` の組み合わせに対してスコアを計算する。
  4. そのスコア列を使い、Vision line 群を geometry 枠群へ monotonic に分類する。
  5. 同じ geometry 枠へ分類された Vision line は、その枠内 text として `Environment.NewLine` で結合する。
  6. どの枠にも十分なスコアで割り当てられない Vision line だけを synthetic fallback に回す。
- 既存パターンへの整合:
  - geometry 枠位置は固定。
  - 既存の `1 Vision -> many Geometry` split は削除しない。
  - 初期実装では classification pass の後に、未解決ケースだけ既存 split を試す。

6. スコアリング設計
- 各 `Vision line -> geometry rect` のスコア候補:
  - text similarity
    - normalized text の edit similarity
    - contains / partial match 補正
  - length ratio
    - line 長と geometry OCR text 長の比率
  - order prior
    - Vision line index と geometry rect index の順序整合
  - continuity bonus
    - 直前 line と同じ geometry 枠へ入る場合に加点
  - switch penalty
    - `A -> B -> A` のような頻繁な切替に減点
- 補足:
  - score は text similarity 単独にしない。
  - 短文や OCR 崩れで similarity が弱い line を continuity で救う。

7. インターフェース設計
- `Services/VisionGeometryHybridAligner.cs`
  - `Align(...)` の公開シグネチャは維持。
  - 内部 helper 候補:
    - `BuildLineToGeometryScores(...)`
    - `ScoreLineAgainstGeometry(...)`
    - `SolveMonotonicLineAssignment(...)`
    - `BuildManyVisionToOneGeometryOutputs(...)`
- 出力:
  - `many Vision -> 1 Geometry` の場合
    - `Text`: `Environment.NewLine` で結合
    - `Rect`: geometry 枠そのまま
    - `LineCount`: 結合した Vision line 数
    - `LineHeight`: geometry rect 高さベースで再計算
  - NOTE: `ReadingUnitBuilder` は `OcrLine.LineCount` / `LineHeight` をそのまま下流へ渡すため、ここを省略すると `OverlayStage` 側で改行が潰れる。

8. 実装手順
- Step 1: score helper の抽出
  - `Vision line` と `geometry rect` の組み合わせスコアを返す helper を追加する。
  - まずは text similarity / length ratio / order prior だけでよい。
- Step 2: monotonic classification
  - Vision line 群を geometry rect 群へ順序を崩さず割り当てる軽量 solver を入れる。
  - 各 line は「同じ枠へ留まる」または「次の枠へ進む」だけを許す。
- Step 3: output build
  - 同じ geometry 枠へ割り当てられた Vision line を `Environment.NewLine` で結合し、`OcrLine` を作る。
- Step 4: fallback
  - 十分なスコアで分類できない line だけ synthetic fallback へ流す。
- Step 5: existing split との共存
  - 既存の `1 Vision -> many Geometry` split は分類 pass の後に、未解決 Vision line と未使用 geometry 枠へだけ適用する。
  - WHY: 先に line classification で `many Vision -> 1 Geometry` を片付け、その後に既存 split で `1 Vision -> many Geometry` を補完する方が、現在の greedy 実装と干渉しにくい。

9. 非機能要件チェック
- 性能:
  - `Vision line count * geometry rect count` の score 計算で済むようにする。
  - full segment DP より軽量に保つ。
- 可観測性:
  - `stage=vision_geometry_hybrid event=line_classifier_summary`
  - 例:
    - `visionLines=4 geometryRects=2 assigned11=1 assignedN1=3 postSplit=0 synthetic=0 merged=0 switched=1`
- 互換性:
  - public API 変更なし。
  - 初期段階では feature flag なしでもよいが、必要なら internal flag 化できる構造にする。

10. リスクと緩和策
- Risk: text similarity が弱い短文 line を誤分類する。
- Mitigation: continuity bonus と switch penalty を入れ、独立分類にしない。
- Risk: geometry OCR text が崩れすぎて分類根拠が弱い。
- Mitigation: low-score line は無理に分類せず fallback へ回す。
- Risk: 同じ geometry 枠へ line を集約した結果、overlay が overflow しやすくなる。
- Mitigation: `Text` は `Environment.NewLine` を保持し、`LineCount` / `LineHeight` を正しく設定したうえで、最終的な改行制御は Overlay 側へ委譲する。

11. 影響範囲
- `Services/VisionGeometryHybridAligner.cs` — 実装本体。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — summary log を増やす場合のみ軽微変更。
- `./.agent/changes.md` — 実装時の変更記録。

12. Definition of Done
- geometry 枠位置を変えずに、`many Vision -> 1 Geometry` を自然に扱える。
- `2枠 + 4行` のようなケースで、line が geometry 枠へ分類され、synthetic fallback が減る。
- `LineCount` と改行が保持され、overlay 表示で行構造が潰れない。
- `stage=vision_geometry_hybrid event=line_classifier_summary` で `assigned11 / assignedN1 / postSplit / synthetic / merged` を確認できる。
