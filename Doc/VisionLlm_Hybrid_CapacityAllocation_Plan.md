# VisionLLM Hybrid Capacity Allocation Plan

1. 概要
- 現行の VisionLLM Hybrid は、完成済み geometry 枠に対して 1:1 greedy 対応を先に行い、余った Vision line を synthetic または既存枠への統合で救済している。
- 本案では `many-to-many` の複雑化を避け、Vision line が geometry 枠より多い場合だけ、完成済み geometry 枠の容量スコアを評価し、helper OCR テキスト類似度も補助スコアとして使って複数 line を適切に配分する。

2. ゴール / 非ゴール
- ゴール:
  - geometry 枠の位置・サイズを変更せずに、Vision line が多いケースの fallback を減らす。
  - 既存の `1 Vision -> many Geometry` split を壊さず、`many Vision -> one Geometry` を容量ベースで自然に割り当てる。
  - synthetic と高 penalty merge の発生件数を減らす。
- 非ゴール:
  - geometry 枠の再配置や再結合。
  - `many-to-many` の汎用最適化。
  - OCR provider / `OcrLineGrouper` の仕様変更。

3. 前提・仮定
- `OcrAndGroupStage` で helper OCR はすでに `MergeLines(...)` 済みであり、`geometryLines` は完成済み枠である。
- VisionLLM は意味単位で line を返す傾向があり、helper OCR の枠数より line 数が多いことがある。
- 既存の `1 Vision -> many Geometry` split は有効なので維持する。

4. 現状整理
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
  - Hybrid は helper OCR の grouped lines を `VisionGeometryHybridAligner` へ渡している。
- `Services/VisionGeometryHybridAligner.cs`
  - 1:1 greedy matching
  - `TryBuildSplitOutputsForVision(...)` による `1 Vision -> many Geometry`
  - unmatched Vision group の synthetic / merge fallback
- 問題点:
  - `Vision line > geometry box` の時、先に 1:1 が確定するため、後続 line が synthetic へ落ちやすい。
  - `many Vision -> one Geometry` は最初から設計されておらず、fallback merge に依存している。

5. 提案アーキテクチャ
- コンポーネント構成:
  - `OcrAndGroupStage` は変更しない。
  - `VisionGeometryHybridAligner` に `capacity allocation` pass を追加する。
- データフロー:
  1. 既存どおり 1:1 greedy matching を行う。
  2. 既存どおり `1 Vision -> many Geometry` split を試す。
  3. それでも unmatched な Vision line が残り、かつ geometry 枠の残余容量がある場合、容量スコアと helper OCR テキスト類似度に基づいて `many Vision -> one Geometry` を行う。
  4. それでも説明できないものだけ synthetic / merge fallback に回す。
- 既存パターンへの整合:
  - 現在の strong path は維持し、`Vision > geometry` の不足側だけ新しい割当を差し込む。

6. 容量スコア設計
- 各 geometry 枠に `capacityScore` を持たせる。
- 候補要素:
  - `areaScore`: 枠面積の相対比
  - `widthScore`: 横書き本文なら幅が広いほど複数 line を受け持ちやすい
  - `heightScore`: 枠高さ / lineHeight から想定収容行数を推定
  - `textLengthScore`: helper OCR text の長さ。元から本文量が多い枠は複数 line を持ちやすい
  - `headingPenalty`: 幅が狭く中央寄せっぽい見出し枠は減点
- 初期の簡易式:
  - `capacityScore = areaScore * 0.35 + widthScore * 0.25 + textLengthScore * 0.25 + heightScore * 0.15 - headingPenalty`
- まずはハードコードでよい。
- NOTE: `capacityScore` は枠の収容力だけを表す。どの Vision line 群をどの枠へ入れるかは別の `allocationScore` で決める。

7. 分配ルール
- 対象条件:
  - `unmatchedVisionCount > 0`
  - `geometryCount > 0`
  - 既存 split 対応では救えなかった場合
- 分配単位:
  - `many Vision -> one Geometry` のみ
  - geometry 枠は完成位置を維持し、text だけ複数 line を受け持つ
- 具体ルール:
  1. geometry 枠を reading order のまま並べる
  2. 各枠に `capacityScore` から `targetLineQuota` を計算する
  3. unmatched Vision lines は、quota が残っている geometry 枠の中から `allocationScore` 最大の枠へ割り当てる
  4. `allocationScore` は `capacityScore` に加えて helper OCR text と Vision text 群の類似度を含める
  5. `targetLineQuota` が 2 以上の枠だけ複数 line を許可する
  6. 結合 text は Vision index 順で `Environment.NewLine` 連結する
- 初期の簡易式:
  - `allocationScore = capacityScore * 0.45 + textSimilarityScore * 0.45 + orderScore * 0.10`
- NOTE: helper OCR text は誤認識もあるため、`textSimilarityScore` は補助信号として使い、容量や順序を上書きしない。
- ガード:
  - 見出しっぽい枠には quota を 1 以下に clamp
  - helper OCR text が極端に短い枠には複数 line を入れない
  - 枠高さに対して推定行数が過大なら quota を削る

8. インターフェース設計
- `Services/VisionGeometryHybridAligner.cs`
  - `Align(...)` の public interface は維持
  - 追加 helper:
    - `ComputeGeometryCapacityScores(...)`
    - `ComputeTargetLineQuotas(...)`
    - `ComputeAllocationScore(...)`
    - `TryAllocateUnmatchedVisionLinesByCapacity(...)`
    - `IsHeadingLikeGeometryLine(...)`
- ログ:
  - `stage=vision_geometry_hybrid event=capacity_allocation_summary geometryCount=... unmatchedVisionCount=... allocatedVisionCount=... quotaExpandedGeometryCount=... fallbackVisionCount=...`

9. 実装手順
- Step 1: geometry 容量スコアの計算
  - 面積、幅、高さ、text length、見出し減点を計算する helper を追加
- Step 2: quota 算出
  - unmatched Vision line 数に応じて geometry 枠ごとの `targetLineQuota` を計算する
- Step 3: allocation score 算出
  - unmatched Vision line 群と geometry 枠の helper OCR text 類似度を計算する
  - `capacityScore + textSimilarityScore + orderScore` の合成で割当先候補を評価する
- Step 4: capacity allocation pass
  - 既存 1:1 / split 後の unmatched Vision line を、quota が残る geometry 枠へ `allocationScore` 最大の順で割り当てる
- Step 5: 出力統合
  - quota で複数 line を受け持つ geometry 枠は Vision line を改行連結して text を更新する
  - geometry 枠の Rect はそのまま
- Step 6: fallback 縮小
  - quota 配分後に余った Vision line だけ existing synthetic / merge fallback に回す

10. 非機能要件チェック
- 性能:
  - DP を使わず、quota 算出と線形走査で済むため現行に近いコストで実装できる
- セキュリティ:
  - 追加の外部 I/O なし
- 可観測性:
  - capacity allocation summary をログに追加する
- 互換性:
  - public interface は維持
  - geometry 枠の位置は維持

11. リスクと緩和策
- Risk: 容量スコアだけだと誤配分し、短い UI 枠へ過剰に line が入る
- Mitigation: headingPenalty と quota clamp を強めに入れる
- Risk: helper OCR text が誤認識していると、text similarity が誤誘導する
- Mitigation: `textSimilarityScore` は補助信号に留め、capacity と order の重みを同等以上に維持する
- Risk: helper OCR text が貧弱な場合、textLengthScore / similarity が効かない
- Mitigation: width/area を主、textLength と similarity を補助にする
- Risk: reading order のみで配ると一部レイアウトで外す
- Mitigation: 初期実装では `allocationScore` に orderScore を少量足し、必要なら後で局所 similarity 補正を強める

12. 影響範囲
- `Services/VisionGeometryHybridAligner.cs` — 実装本体
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — ログ補助が必要な場合のみ軽微変更
- `./.agent/changes.md` — 実装時の記録

13. Definition of Done
- `Vision line > geometry box` のケースで、synthetic に落ちる前に複数 Vision line が geometry 枠へ自然に配分される
- 既存の `1 Vision -> many Geometry` split が回帰しない
- geometry 枠の位置とサイズは変わらない
- `stage=vision_geometry_hybrid event=capacity_allocation_summary` で配分結果を追える
- 実画面で、synthetic fallback と forced merge の件数が減る
