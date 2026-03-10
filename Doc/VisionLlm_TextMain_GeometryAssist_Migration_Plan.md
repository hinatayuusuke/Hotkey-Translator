# VisionLLM Text-Main + Geometry-Assist Migration Plan

## 1. 概要
- 現実装の `VisionLLM + geometry helper を OcrEngine 内で直接合成する方式` から、`geometry 側の merge 後に hybrid 判定する方式` へ段階移行する。
- 目的は、VisionLLM の「語意で結合しやすい」特性を無理にプロンプトで矯正せず、既存 OCR pipeline の grouping 後粒度に合わせて安定に統合すること。

## 2. ゴール / 非ゴール
### ゴール
- 現実装の hybrid 経路を壊さずに、判定位置を `groupedGeometryLines` 基準へ移す。
- hybrid 専用 prompt 分岐を撤去し、VisionLLM OCR は通常 prompt に一本化する。
- helper host の起動条件と resident 条件を維持しつつ、hybrid の text/bbox 対応精度を上げる。

### 非ゴール
- この移行段階で UI 露出を増やさない。
- この移行段階で scene change / cache / overlay 全体設計を見直さない。
- この移行段階で multi-match や文字単位座標復元を実装しない。

## 3. 前提・仮定
- 現実装では `Services/OcrEngine.cs` が VisionLLM OCR 結果と geometry OCR 結果を直接アラインしている。
- geometry 側の grouping は `Services/Orchestration/Stages/OcrAndGroupStage.cs` に既に存在する。
- VisionLLM はプロンプトを調整しても、意味単位で 1 行化する傾向を完全には抑えにくい。
- したがって、geometry 側を VisionLLM に寄せるのではなく、VisionLLM を既存 grouped line 粒度へ対応付ける方が持続的である。

## 4. 現状整理
### 現実装
1. `OcrEngine` が `VisionLLM OCR` を実行する。
2. hybrid 有効時は `WinRT / NDL / Paddle` の geometry helper OCR を追加実行する。
3. `VisionGeometryHybridAligner` が raw line 同士を greedy matching する。
4. `OcrAndGroupStage` 側では、VisionLLM を coarse block と見なして追加 merge を抑止している。

### 問題点
- VisionLLM は raw line 粒度で安定しないため、raw geometry line と比較すると mismatch が増えやすい。
- hybrid 用 prompt 分岐を持つと、通常 OCR と hybrid OCR で Vision 出力特性が分かれ、保守コストが増える。
- 判定場所が `OcrEngine` に寄っており、既存 grouping ロジックを十分に再利用できていない。

## 5. 移行後アーキテクチャ
### 5.1 基本方針
- `OcrEngine` は OCR 実行担当に戻す。
- geometry helper も `OcrEngine` 経由で取得するが、hybrid 合成は `OcrAndGroupStage` 以降へ移す。
- geometry 側は `MergeLines(...)` 後の `groupedGeometryLines` を hybrid 判定入力に使う。
- VisionLLM は通常 OCR prompt に一本化し、hybrid 専用 prompt 分岐は削除する。

### 5.2 データフロー
1. VisionLLM OCR を実行し、`visionRawLines` を得る。
2. geometry helper OCR を実行し、`geometryRawLines` を得る。
3. geometry 側だけ既存 `_lineGrouper.MergeLines(...)` を通し、`groupedGeometryLines` を得る。
4. `VisionGeometryHybridAligner` は `visionRawLines` と `groupedGeometryLines` を対応付ける。
5. `hybridLines(text=visionText, bbox=groupedGeometryBbox)` を最終 grouped line として downstream へ渡す。

## 6. 実装ステップ
### Step 1: 現行 hybrid 実装を feature flag で温存
- `EnableVisionGeometryHybridOcr` は維持する。
- 追加で内部一時 flag を持つか、実装中だけ `OcrEngine` 側合成と `OcrAndGroupStage` 側合成を切り替えられるようにする。
- 目的は段階移行中の比較とロールバックを容易にすること。

### Step 2: OcrEngine の責務を分離
- `OcrEngine` から `VisionGeometryHybridAligner.Align(...)` 呼び出しを外す。
- 代わりに、必要なら
  - `RecognizeAsync(...)` は VisionLLM の text-only 結果を返す
  - `RecognizeVisionGeometryAsync(...)` のような helper 取得手段だけを残す
- `VisionLLM OCR failed -> geometry-only fallback` の責務も stage 側へ移せるなら寄せる。

### Step 3: OcrAndGroupStage に hybrid 合成を移す
- `OcrAndGroupStage` で
  - Vision result
  - geometry helper result
  - grouped geometry lines
  を揃える。
- grouped geometry lines と Vision text を `VisionGeometryHybridAligner` へ渡す。
- ここで最終 grouped lines を確定する。

### Step 4: aligner を grouped geometry 前提に調整
- `VisionGeometryHybridAligner` の入力を raw geometry line 前提から grouped geometry line 前提へ更新する。
- score の重みを見直し、reading order / length / text similarity を grouped line 粒度に最適化する。
- synthetic fallback は既存方針を維持する。

### Step 5: hybrid 専用 prompt 分岐を削除
- `OcrServiceVisionLlm/vision_llama_engine.py` の `DEFAULT_HYBRID_OCR_PROMPT` を削除する。
- `preserve_visual_lines` フラグを proto / server / client から撤去する。
- VisionLLM OCR prompt は通常経路に一本化する。

### Step 6: ログと診断の更新
- hybrid summary ログは
  - `geometryRawLineCount`
  - `groupedGeometryLineCount`
  - `visionLineCount`
  - `matchedCount`
  - `syntheticCount`
  を出すように更新する。
- これで「merge 後へ移した結果、本当に mismatch が減ったか」を確認できるようにする。

## 7. ロールバック方針
- 問題が出た場合は、まず `EnableVisionGeometryHybridOcr=false` で現行 VisionLLM text-only 経路へ戻せる状態を維持する。
- 実装途中の比較が必要なら、旧 `OcrEngine` 合成経路を短期で残し、内部 flag で切り替える。
- ただし最終的には二重経路を残さず、`merge 後判定` に一本化する。

## 8. リスクと緩和策
- Risk: `OcrEngine` と `OcrAndGroupStage` の責務移動で、一時的に flow が複雑になる。
- Mitigation: Step ごとに小さく移し、`OcrEngine` 側合成を最後に削除する。

- Risk: grouped geometry line でも VisionLLM より粒度が細かい / 粗いケースが残る。
- Mitigation: 初期実装では greedy matching を維持し、必要なら後で multi-match を追加する。

- Risk: prompt 分岐削除で現行テストコードとの差分確認がしにくくなる。
- Mitigation: 削除前に `test_vision_llama_engine.py` でログを残し、以後は grouped line ベースで評価軸を切り替える。

## 9. 影響範囲
- `Services/OcrEngine.cs`
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
- `Services/VisionGeometryHybridAligner.cs`
- `Services/VisionLlmGrpcOcrProvider.cs`
- `OcrServiceVisionLlm/vision_llama_engine.py`
- `OcrServiceVisionLlm/server.py`
- `OcrServiceVisionLlm/ocr.proto`
- `Protos/OcrGrpc.proto`

## 10. Definition of Done
- hybrid 判定が `groupedGeometryLines` 基準で動く。
- VisionLLM hybrid 専用 prompt 分岐が削除されている。
- `EnableVisionGeometryHybridOcr=false` で従来の VisionLLM text-only 経路へ戻せる。
- `WinRT / NDL / Paddle` の helper で、現行 raw-line 判定より `matchedCount` が改善するか同等で、`syntheticCount` が減る。
- host 排他 / helper 例外の現仕様が維持される。
