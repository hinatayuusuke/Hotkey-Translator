# VisionLLM Hybrid Global Segment Allocation Plan

1. 概要
- 現行の VisionLLM Hybrid は、完成済み geometry 枠に対してまず 1:1 の greedy 対応を行い、その後で `1 Vision -> many Geometry` split、unmatched の synthetic、既存枠への統合を行っている。
- 本案では geometry 枠の位置と統合結果を固定したまま、VisionLLM line が多い場合でも `many Vision -> 1 Geometry` を最初から扱えるようにし、fallback を減らす。

2. ゴール / 非ゴール
- ゴール:
  - 完成済み geometry 枠の位置を崩さずに、VisionLLM text をより自然に配分する。
  - 既存の `1 Vision -> many Geometry` split を維持したまま、`many Vision -> 1 Geometry` を追加する。
  - synthetic fallback と高 penalty merge を減らす。
- 非ゴール:
  - geometry 枠そのものの再配置・再結合。
  - OCR provider や `OcrLineGrouper` の仕様変更。
  - `Doc\` 内の既存設計書の改訂。

3. 前提・仮定
- geometry 側は `OcrAndGroupStage` で `MergeLines(...)` 済みの完成枠であり、位置と順序はこの時点で信頼する。
- VisionLLM 側は意味単位で line を返す傾向があり、helper OCR の枠数より多くなることがある。
- 既存の `1 Vision -> many Geometry` split は有効なので、廃止ではなく上位の分配アルゴリズムに取り込む。

4. 現状整理
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
  - Vision hybrid は helper OCR を `MergeLines(...)` 後の `groupedGeometryLines` に対して適用している。
- `Services/VisionGeometryHybridAligner.cs`
  - 先に 1:1 greedy matching を行う。
  - その後 `TryBuildSplitOutputsForVision(...)` で `1 Vision -> many Geometry` を試す。
  - unmatched は `BuildSyntheticLineForGroup(...)` か、重なりが強ければ既存枠への merge に流す。
- 問題点:
  - greedy の時点で geometry 枠の割当が固定されるため、後続の Vision line が unmatched になりやすい。
  - `many Vision -> 1 Geometry` は fallback 的に既存枠へ merge されるだけで、最初から最適化されていない。

5. 提案アーキテクチャ
- コンポーネント構成:
  - `OcrAndGroupStage` は現状維持。完成済み geometry 枠と Vision line を `VisionGeometryHybridAligner` へ渡す。
  - `VisionGeometryHybridAligner` に、新しい `global segment allocation` pass を追加する。
- データフロー:
  1. geometry 枠を固定したまま、`Vision[i..k] -> Geometry[j..l]` の区間候補を生成する。
  2. 各候補に対して text similarity / length ratio / reading order / geometry coverage のスコアを計算する。
  3. monotonic な DP で全体の最適な区間対応を選ぶ。
  4. 区間対応の中で:
     - `1 -> 1` は現行と同じ。
     - `1 -> many` は既存 `TrySplitVisionTextAcrossGeometryRun(...)` を流用する。
     - `many -> 1` は新規に Vision line を結合して geometry 1 枠へ割り当てる。
  5. 最後に説明できなかった残りだけを synthetic / merge fallback に回す。
- 既存パターンへの整合:
  - geometry 枠の位置は一切変更しない。
  - 既存 split ロジックは fallback ではなく候補評価の一部として再利用する。

6. インターフェース設計
- `Services/VisionGeometryHybridAligner.cs`
  - `Align(...)` の公開シグネチャは維持する。
  - 内部に以下の helper を追加する。
    - `BuildSegmentCandidates(...)`
    - `ScoreVisionToGeometrySegment(...)`
    - `SolveMonotonicSegmentAlignment(...)`
    - `BuildManyVisionToOneGeometryOutput(...)`
- `HybridOcrAlignmentResult` は既存のままでよい。
- ログは以下を追加する。
  - `stage=vision_geometry_hybrid event=segment_solver_summary visionSegments=... geometrySegments=... assigned11=... assigned1n=... assignedn1=... fallback=...`

7. 実装手順
- Step 1: 区間候補のモデル化
  - `Vision[i..k]` と `Geometry[j..l]` の候補構造を追加する。
  - まずは `1->1`, `1->many`, `many->1` だけを扱う。`many->many` は禁止する。
- Step 2: スコアリング
  - `join(Vision[i..k])` と `join(Geometry[j..l].Text)` の normalized similarity を計算する。
  - length ratio と順序距離を加点/減点に使う。
  - `1->many` 候補では既存 split の score を再利用する。
- Step 3: monotonic solver
  - `dp[i,j]` で Vision 先頭 `i` 行と Geometry 先頭 `j` 枠までの最小コストを解く。
  - geometry の順序も Vision の順序も崩さない。
- Step 4: 出力生成
  - solver の結果から最終 `OcrLine` 群を作る。
  - `many->1` の場合は Vision line を index 順に結合して geometry 枠へ入れる。
  - `1->many` は既存 split 出力を使う。
- Step 5: fallback 縮小
  - solver 後に説明できない Vision line だけを synthetic / merge fallback に回す。
  - 既存 fallback 実装は残すが、適用件数が減ることを期待する。

8. 非機能要件チェック
- 性能:
  - 候補数が増えるので、`many->1` と `1->many` は最大 3 セグメント程度に制限する。
  - DP の状態数を `O(V * G * K)` に抑える。
- セキュリティ:
  - 追加の外部入出力なし。
- 可観測性:
  - 既存 summary に加えて segment solver summary を追加する。
- 互換性:
  - public API は維持し、feature flag なしで内部アルゴリズム置換する。

9. リスクと緩和策
- Risk: `many->1` を強くしすぎると、今の `1->many` split が発動しにくくなる。
- Mitigation: `1->many` 候補評価には既存 split score を使い、同等以上なら優先させる。
- Risk: DP 導入で実装が複雑化し、デバッグしにくくなる。
- Mitigation: まず `1->1`, `1->many`, `many->1` のみ対応し、`many->many` は禁止する。
- Risk: score 設計が悪いと UI 見出しや短文を誤統合する。
- Mitigation: length ratio と順序制約を強めにかけ、短い geometry 見出し枠への `many->1` を減点する。

10. 影響範囲
- `Services/VisionGeometryHybridAligner.cs` — 実装本体。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — ログ項目を追加する場合のみ軽微変更。
- `./.agent/changes.md` — 実装時の記録。

11. Definition of Done
- 完成済み geometry 枠の位置を変更せずに、`many Vision -> 1 Geometry` を最初から扱える。
- 既存の `1 Vision -> many Geometry` split が回帰しない。
- `synthetic` と `merged` の件数が現行より減るケースをログで確認できる。
- `stage=vision_geometry_hybrid event=segment_solver_summary` で割当内訳を追える。
- 実画面で、Vision line が geometry 枠より多いケースでも fallback ではなく自然な枠内配分になる。
