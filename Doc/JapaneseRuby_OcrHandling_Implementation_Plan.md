# Japanese Ruby OCR Handling Implementation Plan

## 1. 概要（1–3行）
日本語のルビが本文 merge や翻訳入力を壊すのを防ぐため、OCR 直後・line merge 前にルビ候補を本文から分離する。  
v1 は「ルビを翻訳入力から外す」ことに絞り、overlay での再表示や完全復元までは扱わない。  
geometry と文字種の軽量 heuristic で判定し、既存 merge パイプラインへの影響を最小化する。

## 2. ゴール / 非ゴール
### ゴール
- ルビ候補が本文 line merge を壊すケースを減らす。
- ルビ文字列が翻訳入力へ混入するケースを減らす。
- 実装範囲を `OcrAndGroupStage` 周辺に限定し、既存 OCR provider の I/F を変えない。

### 非ゴール
- ルビの完全認識や本文との完全対応付け。
- ルビを overlay に再配置して表示すること。
- 注釈、小見出し、UI ラベルなど非ルビ小文字列の完全分類。
- OCR engine ごとの専用モデルや追加 OCR pass の導入。

## 3. 前提・仮定
- 現在の `OcrLineGrouper` は geometry ベースで line merge を行い、ルビ専用の扱いは持っていない。
- `ReadingUnitBuilder` は merge 後の `OcrLine` をそのまま reading unit 化している。
- 日本語ではルビが本文より小さく、横書きでは上側、縦書きでは右側に付くケースが多い。
- v1 は完璧な判定よりも、「本文に混ざると明確に悪いルビ」を先に外す方が効果対コストが高い。

## 4. 現状整理
- OCR provider は raw な `OcrLine` 群を返す。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` が line merge と reading unit 化の入口になっている。
- `Services/OcrLineGrouper.cs` は horizontal / vertical merge を持つが、ルビ判定はない。
- `Services/ReadingUnitBuilder.cs` は grouped line を 1 unit = 1 line として流している。
- そのため、ルビが本文として merge された場合は、そのまま translation / overlay / diff に波及する。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `Services/RubyCandidateDetector.cs`
  - raw `OcrLine` 群から本文候補とルビ候補を分離する。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
  - detector を呼び出し、本文候補だけを `_lineGrouper.MergeLines(...)` に渡す。
- `Models/`
  - v1 では既存 model を維持し、新しい永続設定は増やさない。

### データフロー / シーケンス
1. OCR provider が raw `OcrLine` 群を返す。
2. `OcrAndGroupStage` が `RubyCandidateDetector` を呼ぶ。
3. detector は raw line を
   - 本文候補
   - ルビ候補
   - 本文とルビの対応情報
   に分ける。
4. 本文候補だけを `_lineGrouper.MergeLines(...)` に流す。
5. merge 後の本文 line だけを `ReadingUnitBuilder` へ流す。
6. ルビ候補は v1 では translation input に含めず、必要最小限のログ用途だけに留める。

### 既存パターンへの整合
- OCR provider には手を入れず、orchestration の前処理として差し込む。
- line merge の責務は `OcrLineGrouper` に残し、ルビ判定ロジックを混ぜない。
- v1 では fallback や設定追加を増やさず、fail fast に判定を見送る。

## 6. インターフェース設計
### 新規クラス
- `RubyCandidateDetector`

### 想定 I/F
```csharp
public sealed class RubyCandidateDetector
{
    public RubyDetectionResult Detect(IReadOnlyList<OcrLine> lines, AppSettings settings);
}
```

```csharp
public sealed record RubyDetectionResult(
    IReadOnlyList<OcrLine> BodyLines,
    IReadOnlyList<OcrLine> RubyLines,
    IReadOnlyDictionary<int, IReadOnlyList<int>> RubyIndicesByBodyIndex);
```

### 判定ルール（v1）
- box 面積または短辺が近傍本文候補より明確に小さい
- 横書きでは本文の上側、縦書きでは本文の右側に位置する
- 本文の進行方向に十分な overlap がある
- テキスト長が短い
- かな比率が高い

### エラー / バリデーション
- detector 内で十分な本文候補が見つからない場合は、何も除外せず全 line を本文として返す。
- OCR engine 依存の例外経路は増やさない。

## 7. 実装手順（ステップ分割）
### Step 1
- `RubyCandidateDetector` を追加する。
- geometry と文字種だけの軽量 heuristic を実装する。

### Step 2
- `OcrAndGroupStage` で detector を呼び出し、本文候補のみを `_lineGrouper.MergeLines(...)` に渡す。

### Step 3
- ルビ候補数、本文候補数、除外可否を既存 logger に必要最小限だけ出す。

### Step 4
- 日本語サンプルで、ルビ混入による過剰 merge と翻訳入力汚染が減るかを確認する。

## 8. 非機能要件チェック
### 性能
- 追加コストは `OcrLine` 群の局所比較と文字種判定だけに抑える。
- 追加 OCR pass は行わない。

### セキュリティ
- 外部入出力は増えない。

### 可観測性
- `stage=ocr_ruby` 相当の軽いログを追加できるが、v1 では詳細トレースは不要。

### 互換性
- 設定ファイル形式は変更しない。
- OCR provider、translation provider、overlay provider の I/F は維持する。

## 9. リスクと緩和策
- Risk: 小さい注釈や UI 小文字列をルビと誤判定して本文から外す可能性がある。
- Mitigation: v1 は「かな比率」「位置関係」「サイズ差」の複合条件にし、単独条件では除外しない。

- Risk: 縦書きや特殊組版でルビ位置が想定から外れる可能性がある。
- Mitigation: 位置判定は writing mode を見て切り替え、判定に迷う場合は除外しない。

- Risk: 本文との対応付けが不完全でも結果が不安定になる可能性がある。
- Mitigation: v1 は対応情報を翻訳処理に使わず、本文除外だけに用途を限定する。

## 10. 影響範囲
- `Services/RubyCandidateDetector.cs`
- `Services/Orchestration/Stages/OcrAndGroupStage.cs`
- 必要なら軽微なログ追加先として `Services/`
- Doc
  - この計画書

## 11. Definition of Done
- OCR 直後・line merge 前にルビ候補を判定する前処理が入る。
- ルビ候補が translation input に含まれない。
- 既存 provider の I/F や設定ファイル形式を変えない。
- 日本語のルビ付きサンプルで、本文 merge と翻訳入力の汚染が減ることを確認できる。
