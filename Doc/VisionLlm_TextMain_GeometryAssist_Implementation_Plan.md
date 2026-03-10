# VisionLLM Text-Main + Geometry-Assist Implementation Plan

## 1. 概要
- VisionLLM を文字認識の正本とし、WinRT / NDL / Paddle 系 OCR の bbox と補助テキストを使って表示位置を推定する。
- 最終的な表示文字列・翻訳入力は VisionLLM を優先し、他 OCR は geometry とマッチング補助に限定する。

## 2. ゴール / 非ゴール
### ゴール
- VisionLLM の高い文字認識精度をそのまま表示・翻訳へ反映する。
- 他 OCR の bbox を使って、VisionLLM の文字列を既存オーバーレイ経路に載せる。
- 他 OCR で読めないが bbox はある、あるいは VisionLLM だけが読める、というケースでも破綻しにくい統合経路を作る。

### 非ゴール
- 初期段階で完全な文字単位座標復元は行わない。
- 初期段階で全 OCR エンジンへ一斉対応しない。
- 初期段階で scene change 判定や cache key の全面再設計は行わない。

## 3. 前提・仮定
- VisionLLM の文字認識精度は他 OCR より高く、最終表示文字として優先する価値がある。
- 他 OCR の bbox は VisionLLM より安定しており、表示位置の近似に使える。
- 他 OCR の text は誤認識を含むため、最終表示には使わず、bbox 割当の参考情報としてのみ使う。
- 初期段階では reading order ベースの行単位アラインで十分な改善が見込める。
- 設定は初期段階では `settings.json` のみへ追加し、UI 露出は後回しにする。

## 4. 現状整理
- VisionLLM は現状 text-only OCR として統合されており、`VisionLlmGrpcOcrProvider` は合成の全幅行 bbox を作っている。
- WinRT / NDL / Paddle は `OcrLine(text, bbox, confidence, ...)` を返し、既存 overlay 経路へそのまま載る。
- そのため現状の VisionLLM overlay は「全文字は良いが geometry が粗い」、他 OCR は「geometry はあるが text が弱い」という分離状態になっている。

## 5. 提案アーキテクチャ
### 5.1 基本方針
- Base OCR を 2 系統に分ける。
- Text source: VisionLLM
- Geometry source: WinRT / NDL / Paddle のいずれか
- `OcrEngine` 自体は引き続き 1 つだけ選ぶ。初期段階では `OcrEngine == VisionLlm` のときだけ hybrid を有効にする。
- geometry source は「主 OCR の切替」ではなく、「VisionLLM の bbox 補助 host」として扱う。

### 5.2 データフロー
1. 同じ Bitmap に対して geometry OCR を実行する。
2. 同じ Bitmap に対して VisionLLM OCR を実行する。
3. geometry OCR から `geometryLines(text, bbox, confidence, order)` を得る。
4. VisionLLM OCR から `visionLines(text, order)` を得る。
5. 両者を行単位でアラインし、`visionText -> geometryBbox` の対応表を作る。
6. 最終出力 `hybridLines(text=visionText, bbox=geometryBbox)` を組み立てる。
7. 対応不能な Vision 行は synthetic bbox fallback を使う。

### 5.3 既存パターンとの整合
- 既存 pipeline は `OCR -> grouping -> diff -> translate -> overlay` なので、追加位置は OCR 直後が自然。
- 実装位置は `OcrEngine` 内ではなく、OCR 後に両結果を受けて統合する専用 stage / service がよい。

## 6. インターフェース設計
### 6.1 新規モデル
- `HybridOcrLineCandidate`
  - `Text`
  - `Rect Bbox`
  - `double MatchScore`
  - `string SourceTextKind` (`vision`, `geometry`, `synthetic`)
- `HybridOcrAlignmentResult`
  - `IReadOnlyList<OcrLine> Lines`
  - `int GeometryLineCount`
  - `int VisionLineCount`
  - `int MatchedCount`
  - `int SyntheticFallbackCount`

### 6.2 新規サービス候補
- `VisionGeometryHybridAligner`
  - 入力: `IReadOnlyList<OcrLine> geometryLines`, `IReadOnlyList<OcrLine> visionLines`, `int imageWidth`, `int imageHeight`
  - 出力: `HybridOcrAlignmentResult`

### 6.3 設定候補
- `EnableVisionGeometryHybridOcr`
- `VisionGeometryHybridBaseEngine` (`WinRt`, `Ndl`, `Paddle`)
- `VisionGeometryMatchMinScore`
- `VisionGeometryAllowSyntheticFallback`

### 6.4 設定の意味
- `EnableVisionGeometryHybridOcr`
  - `OcrEngine == VisionLlm` のときだけ意味を持つ
  - `false` のときは現行 VisionLLM text-only 経路を使う
- `VisionGeometryHybridBaseEngine`
  - 初期値は `WinRt`
  - 将来的に `WinRt`, `Ndl`, `Paddle` から選択する
  - `PaddleVllm` と `VisionLlm` 自身は選択肢に含めない
- 初期実装では `settings.json` だけに追加し、UI 露出は後回しにする

## 7. Host / 排他方針
### 7.1 基本原則
- `VisionLLM`, `PaddleOCR`, `PaddleOCR-VL` は主 OCR としては相互排他とする。
- `NDL` は `PaddleOCR` と共存可能な既存方針を維持する。
- hybrid 時の geometry source は「主 OCR」ではなく「補助 host」として扱う。

### 7.2 hybrid 時の例外
- `OcrEngine == VisionLlm` かつ `EnableVisionGeometryHybridOcr == true` のとき、
  - `VisionGeometryHybridBaseEngine == WinRt` なら追加 host は不要
  - `VisionGeometryHybridBaseEngine == Ndl` なら NDL host を補助として起動可能
  - `VisionGeometryHybridBaseEngine == Paddle` なら Paddle host を補助として起動可能
- このときの `Paddle` / `NDL` は主 OCR ではなく geometry helper であり、VisionLLM と同時常駐できる例外とする
- `PaddleOCR-VL` は geometry helper に含めない

### 7.3 resident / stop 方針
- `Paddle` と `NDL` の共存は維持する
- `VisionLLM` から `WinRt` / `NDL` へ切り替えたときの `VisionLLM` 自動停止は、ローカル `LlamaCpp` 翻訳が有効で VRAM 競合がある場合に限定する
- ローカル `LlamaCpp` 翻訳が無効な場合、`VisionLLM` を warm 状態で残す余地を認める
- `PaddleOCR`, `PaddleOCR-VL`, `VisionLLM` は主 OCR の resident 判定では排他を維持する

## 8. マッチング戦略
### 8.1 基本原則
- 最終表示テキストは常に VisionLLM を優先する。
- geometry OCR text は bbox 割当の参考にのみ使う。

### 8.2 スコア要素
- Reading order の近さ
- 行長の近さ
- 正規化文字列の類似度
- 記号・数字の一致度
- 文字種傾向の近さ（英字 / 数字 / かな / 漢字）
- bbox のサイズ妥当性

### 8.3 正規化
- 全角半角正規化
- 空白圧縮
- 句読点・記号の揺れ緩和
- CJK spacing fix 後の比較

### 8.4 マッチ方式
- 初期実装は greedy matching で十分。
- `visionLine[i]` に対して、近傍の `geometryLine[j]` 候補の中で最高スコアを採用する。
- 閾値未満なら未対応として synthetic fallback へ回す。

### 8.5 geometry text の役割
- geometry text は破棄しない。
- ただし最終表示には使わず、`visionText` をどの bbox に載せるかのスコアリング材料に限定する。

## 9. fallback 方針
- geometry line が不足する場合:
  - 近接 bbox の結合を試す
  - それでも不足なら synthetic line bbox を追加する
- geometry OCR が完全失敗した場合:
  - 現行 VisionLLM の synthetic full-width strip へフォールバックする
- VisionLLM が失敗した場合:
  - geometry OCR 単独へ戻す

### 9.1 synthetic fallback の表示位置
- synthetic fallback は現行の「全幅 strip」を常用せず、できるだけ既存 geometry 文脈へ寄せる。
- 配置優先順位は以下とする。
  1. 前後の matched bbox 間を補間する
  2. 最も近い geometry cluster に吸着させる
  3. ROI / capture 領域の下寄せスタックへ仮配置する
  4. それでも情報が足りない場合のみ full-width strip へ倒す
- 単一の unmatched Vision 行は、直前または直後の matched bbox の幅・高さ・行間を基準に補間配置する。
- 複数の unmatched Vision 行が連続する場合は、近傍 cluster の外接矩形と行間を基準に縦方向へ積む。
- synthetic fallback 由来の bbox には内部的に source kind を保持し、後でログ・見た目・再調整の対象にできるようにする。

## 10. 実装手順
### Step 1: 実験経路
- `settings.json` に `EnableVisionGeometryHybridOcr=false` と `VisionGeometryHybridBaseEngine=WinRt` を追加する。
- `VisionGeometryHybridAligner` を追加する。
- `VisionLLM + WinRT` だけを対象に、手動 Run 時のみ有効化する。
- ログで matched / unmatched / synthetic count を確認できるようにする。

### Step 2: NDL 対応
- geometry source に NDL を追加する。
- NDL の bbox 特性に合わせて matching score の重みを微調整する。

### Step 3: Paddle 対応
- geometry source に Paddle を追加する。
- 行分割の差を吸収するため bbox 結合ルールを追加する。

### Step 4: UI / 設定
- hybrid mode の ON/OFF
- geometry source の選択
- 診断ログ ON/OFF

## 11. 非機能要件チェック
### 性能
- OCR を 2 系統走らせるためコストは増える。
- 初期段階は manual run 中心で導入し、常時監視系にはすぐ乗せない。

### 可観測性
- `matchedCount`, `syntheticFallbackCount`, `geometryLineCount`, `visionLineCount` をログへ出す。
- mismatch が多いケースをあとで見返せるよう、必要なら診断ログを追加する。

### 互換性
- 既存の `OcrLine` と overlay 経路は再利用する。
- hybrid mode OFF 時は現行挙動を維持する。

## 12. リスクと緩和策
- Risk: reading order が崩れる画面で誤対応する。
- Mitigation: score 閾値を厳しめにし、合わない場合は synthetic fallback に倒す。
- Risk: geometry OCR の誤認識に text matching が引っ張られる。
- Mitigation: text similarity の重みを中程度に抑え、reading order と line size を優先する。
- Risk: OCR コストが重くなる。
- Mitigation: 初期段階は VisionLLM + 1 geometry engine だけに限定し、手動実行中心で評価する。
- Risk: host 排他の既存ポリシーと hybrid helper 起動が衝突する。
- Mitigation: 主 OCR 排他と geometry helper 例外を分け、helper として許可する engine を `WinRt`, `Ndl`, `Paddle` に限定する。

## 13. 影響範囲
- `Services/OcrEngine.cs`
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` または新規 hybrid stage
- `Services/VisionLlmGrpcOcrProvider.cs`
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml` / `MainWindow.xaml.cs`

## 14. Definition of Done
- VisionLLM text を主とした hybrid OCR が手動 Run で動く。
- WinRT geometry で 1 つ以上のゲーム画面に対して、現行 synthetic bbox より自然な配置になる。
- `Paddle` / `NDL` の共存ポリシーが維持される。
- VisionLLM helper 運用時の排他ルールが、主 OCR と geometry helper で矛盾しない。
- mismatch 時は落ちずに fallback する。
- hybrid mode OFF で現行挙動へ戻る。
