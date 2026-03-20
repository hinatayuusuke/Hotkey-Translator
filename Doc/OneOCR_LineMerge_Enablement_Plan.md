# OneOCR Line Merge Enablement Plan

## 1. 概要（1–3行）
OneOCR native helper 自体の raw 出力は Python CLI と一致しており、枠結合の弱さは helper 差ではなくアプリ後段の merge 方針で説明できる。  
そのため OneOCR を `merge skip` 扱いするのをやめ、まず WinRT と同じ shared line merge 経路へ流す。  
v1 は OneOCR 専用 tuning を足さず、既存 `_lineGrouper.MergeLines(...)` の適用だけに絞って差分を最小化する。

## 2. ゴール / 非ゴール
### ゴール
- OneOCR 実行時にも WinRT / Paddle / NDL と同じ line merge を適用する。
- `Tools/OneOcrExperiment\input\test2.png` のような画像で、UI 上の line 分割が raw helper 出力より自然になることを確認する。
- wrapper 変更ではなく orchestration 方針の変更だけで改善できる形に留める。

### 非ゴール
- OneOCR helper の IPC や DLL bridge の再設計。
- OneOCR 専用 merge heuristic の新設。
- polygon ベースの新しい grouping ロジック追加。
- Paddle 系の confidence filter を OneOCR へ流用すること。

## 3. 前提・仮定
- `Tools/OneOcrExperiment\input\test2.png` を Python CLI と native helper に通した結果、両者とも `12 lines / 112 words` で line text も bbox も一致した。
- 現在のアプリでは `OcrAndGroupStage.cs` で `OneOcr` を `PaddleVllm / VisionLlm` と同じ `merge skip` 扱いにしている。
- OneOCR の app 上の見え方が WinRT より読みにくい主因は、この `merge skip` 方針にある可能性が高い。
- shared line merge は既に WinRT 系で常用されており、OneOCR に対してもまずは同じ経路を試す価値がある。

## 4. 現状整理
- helper から返る raw line は `OneOcrProcessOcrProvider.cs` で `OcrResultModel` に変換される。
- その後の grouping は `Services/Orchestration/Stages/OcrAndGroupStage.cs` が担当する。
- 現在の分岐は次の意図になっている。
  - `PaddleVllm / VisionLlm` は完成済み line とみなし merge skip
  - それ以外は `_lineGrouper.MergeLines(...)`
- しかし OneOCR は helper raw 出力の時点で Python CLI と一致しており、ここを skip するとアプリ側の line 単位が raw のまま固定される。
- つまり改善ポイントは provider ではなく `OcrAndGroupStage` の engine 分岐である。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `Services/OneOcrProcessOcrProvider.cs`
  - 変更なし。raw line を返す役割に留める。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
  - OneOCR を merge skip 対象から外し、shared merge 経路へ流す。
- `Models/AppSettings.cs`
  - v1 では設定追加なし。

### データフロー / シーケンス
1. OneOCR helper が raw line 群を返す。
2. `OneOcrProcessOcrProvider` が `OcrResultModel` に変換する。
3. `OcrAndGroupStage` が `filteredLines` を受け取る。
4. OneOCR は `PaddleVllm / VisionLlm` の skip 分岐へ入れず、`_lineGrouper.MergeLines(filteredLines, settings, OcrEngineKind.OneOcr)` を通す。
5. merge 後の line を overlay / translation / reading unit へ渡す。

### 既存パターンへの整合
- merge 実装は既存 shared line merge をそのまま使う。
- OneOCR 専用の設定や分岐は増やさず、まず既存 engine と同じ経路に戻す。
- `EnableLineMerge` など既存 global 設定の挙動に従う。

## 6. インターフェース設計
### 変更対象
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`

### 変更内容
- 現在の `effectiveEngineKind is OcrEngineKind.PaddleVllm or OcrEngineKind.VisionLlm or OcrEngineKind.OneOcr`
  を
- `effectiveEngineKind is OcrEngineKind.PaddleVllm or OcrEngineKind.VisionLlm`
  に戻す。

### 入出力
- 入力: `IReadOnlyList<OcrLine> filteredLines`
- 出力: `_lineGrouper.MergeLines(...)` を通した `groupedLocalLines`

### エラー / バリデーション
- 新しいエラー経路は増やさない。
- OneOCR helper failure や WinRT fallback の扱いは現状維持。

## 7. 実装手順（ステップ分割）
### Step 1
- `OcrAndGroupStage.cs` から `OcrEngineKind.OneOcr` を merge skip 条件から外す。

### Step 2
- `test2.png` 相当ケースで OneOCR 実行時の line 数と overlay 表示を確認する。

### Step 3
- WinRT と OneOCR の結果を見比べ、shared merge だけで十分かを確認する。
- 不十分な場合のみ次段で OneOCR 専用 tuning を別計画に切り出す。

## 8. 非機能要件チェック
### 性能
- OneOCR helper 本体の実行時間は変えない。
- 追加コストは既存 `_lineGrouper.MergeLines(...)` のみで、WinRT と同等水準に留まる。

### 互換性
- 設定ファイル形式の変更は不要。
- UI 変更も不要。

### 可観測性
- 既存 OCR / grouping ログで十分。
- 必要なら比較用に OneOCR raw line count と grouped line count のログを追加する余地はあるが、v1 では必須にしない。

### 運用
- OneOCR が期待より過剰結合するケースが出た場合は、その画像を追加サンプルとして別 tuning 計画へ回す。

## 9. リスクと緩和策
- Risk: shared merge を通すことで OneOCR の line が過剰結合される画像がある。
- Mitigation: v1 は既存 merge のみ適用し、問題が残るケースを収集してから OneOCR 専用 tuning を検討する。

- Risk: 原因が merge ではなく overlay 側にある可能性が残る。
- Mitigation: raw helper JSON、grouped line 数、UI 表示の3点を分けて確認する。

## 10. 影響範囲
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
- 必要なら比較検証用ログのみ `Services/` 配下の関連クラス
- Doc
  - この計画書

## 11. Definition of Done
- OneOCR 実行時に `merge skip` が適用されない。
- OneOCR が shared line merge を通る。
- `test2.png` 相当ケースで、raw helper 出力より UI 上の行分割が改善するかを確認できる。
- helper / provider / IPC には追加変更を入れない。
