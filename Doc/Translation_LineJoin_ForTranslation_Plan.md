# Translation Line Join For Translation Plan

## 1. 概要
同一 OCR 合併枠に含まれる複数行テキストについて、表示用の改行は維持したまま、翻訳送信時だけ必要に応じて改行を空白連結へ正規化する。
目的は、会話ウィンドウ本文の翻訳品質を上げつつ、メニューや列挙 UI のような構造化テキストを壊さないこと。

## 2. ゴール / 非ゴール
### ゴール
- 翻訳送信時だけ multi-line text を必要に応じて 1 文脈へ連結する。
- 同一枠本文は連結しやすくし、メニュー / 選択肢 / 項目列は改行維持を優先する。
- CJK 以外の言語でも同じ判定を使えるようにする。

### 非ゴール
- OCR / overlay 表示用の原文改変。
- `ReadingUnitBuilder` や `OcrLineGrouper` の結合ルール変更。
- 翻訳 provider ごとの個別判定導入。

## 3. 前提・仮定
- 判定対象は `ReadingUnit.Text` に含まれる改行であり、同一 OCR 合併枠に由来する。
- 既存 pipeline では、複数行結合済み `OcrLine` が `Environment.NewLine` 区切りで `ReadingUnit.Text` に入る。
- 表示上の改行と翻訳上の文境界は一致しないことがある。

## 4. 現状整理
- `OcrLineGrouper` は複数行をまとめる際に `Environment.NewLine` で text を連結する。
- `ReadingUnitBuilder` は `line.Text` をそのまま `ReadingUnit.Text` にコピーする。
- `TranslateStage` は `TranslationTextNormalizer.NormalizeForTranslation(...)` を通した text を翻訳送信に使う。
- つまり、翻訳専用の text 正規化レイヤはすでに存在する。

## 5. 提案アーキテクチャ
実装場所は `TranslationTextNormalizer` に限定する。

データフロー:
1. OCR / merge / reading unit 生成は現状維持。
2. `TranslateStage` が `unit.Text` を `TranslationTextNormalizer.NormalizeForTranslation(...)` へ渡す。
3. 正規化内で、単純な空白補正に加えて `NormalizeLineBreaksForTranslation(...)` を適用する。
4. 表示・overlay・diff 用 text は変更しない。

WHY:
- 表示と翻訳で責務を分けられる。
- overlay や hybrid 分配ロジックに副作用を出さない。
- 既存の CJK space 正規化と同じ層に置ける。

## 6. インターフェース設計
- 既存 public API は変更しない。
- `TranslationTextNormalizer` に private helper を追加する。

候補:
- `private static string NormalizeLineBreaksForTranslation(string text)`
- `private static bool ShouldJoinLinesForTranslation(IReadOnlyList<string> lines)`
- `private static bool IsMenuLikeBlock(IReadOnlyList<string> lines)`
- `private static bool IsSentenceLikeBlock(IReadOnlyList<string> lines)`

入出力:
- 入力: `ReadingUnit.Text`
- 出力: 翻訳送信用に正規化された string

## 7. アルゴリズム
### 7.1 前処理
- `\r\n` と `\r` を `\n` に正規化する。
- `Split('\n')` で行分割する。
- 各行は `Trim()` した上で、空行を除外する。
- 行が 0 or 1 件なら現状の空白正規化だけ行い、そのまま返す。

### 7.2 連結判定
連結対象にするのは、以下をすべて満たす場合だけ。

1. 同一 `ReadingUnit` 内に 2 行以上ある。
2. `IsMenuLikeBlock(lines) == false`
3. `IsSentenceLikeBlock(lines) == true`

### 7.3 `IsMenuLikeBlock(lines)`
以下の特徴が強い場合は menu-like とみなして改行維持する。

判定要素:
- 行頭記号率が高い
  - `-`, `*`, `・`, `>`, `▶`, `■`, `□`, `◆`, `◇`, `○`, `●`, 数字付き箇条書きなど
- 各行が短い語句中心
  - 例: 平均文字数が短い / 長短差が小さい / 1〜2語の列が多い
- 終端句読点率が低い
  - `。`, `．`, `.`, `!`, `?`, `！`, `？`, `:`, `：`, `;`, `；` などがほぼ無い
- タイトルケース / 単語列 / 数値列が多い
  - 例: `New`, `Back`, `Load Game`, `50 HP`, `20 MP`
- 文字種の散らばりが UI 項目っぽい
  - 例: 英単語 + 数字 + 記号だけの短い行群

最小実装ではスコア制にする。
- `menuScore` を 0.0〜1.0 で計算
- 一定以上なら menu-like

推奨スコア例:
- 行頭記号あり: +0.35
- 平均文字数が短い: +0.20
- 終端句読点が少ない: +0.20
- 英数記号主体の短文が多い: +0.25

### 7.4 `IsSentenceLikeBlock(lines)`
以下の特徴がある場合は sentence-like とみなして改行連結候補にする。

判定要素:
- 少なくとも 1 行以上が十分な長さを持つ
- 行中に助詞 / 接続 / 動詞語尾など、文っぽい連続がある
  - 日本語だけでなく、英語なら stop words や文末パターンも見る
- 終端句読点が一部に存在する、または行末が次行継続っぽい
- 連続行を空白連結したとき、極端な記号列にならない

最小実装ではこちらもスコア制にする。
- `sentenceScore` を 0.0〜1.0 で計算
- 一定以上なら sentence-like

推奨スコア例:
- 平均文字数が一定以上: +0.25
- 終端句読点あり: +0.20
- stop words / 助詞 / 文末語尾あり: +0.25
- 行頭記号が少ない: +0.15
- 英数記号主体でない: +0.15

### 7.5 実際の連結処理
連結する場合:
- 行間は単純に半角スペース 1 個でつなぐ。
- 連結後に既存の空白正規化を再適用する。

例:
- 入力:
  - `しかし`
  - `ここで戻るわけにはいかない。`
- 出力:
  - `しかし ここで戻るわけにはいかない。`

補足:
- 今回は CJK も英語も同じくスペース連結。
- 将来 CJK のスペース除去を強める場合は別ステップで調整する。

### 7.6 改行維持ケース
以下はそのまま `\n` を残す。
- menu-like 判定
- sentence-like が弱い
- 記号列 / 項目列 / 選択肢列
- 数値 UI / ステータス表示

## 8. 実装手順
### Step 1
- `TranslationTextNormalizer` に `NormalizeLineBreaksForTranslation(...)` を追加。
- multi-line text のみ対象にする。

### Step 2
- `IsMenuLikeBlock(...)` と `IsSentenceLikeBlock(...)` を追加。
- 最初は軽いヒューリスティックで十分。

### Step 3
- `NormalizeForTranslation(...)` の中で line-break 判定を挟む。
- 既存の CJK space 正規化との順序を調整する。

### Step 4
- ログを 1 行追加する。
- 例:
  - `stage=translation_text_normalize event=line_join join=yes menuScore=... sentenceScore=...`
- WHY: 判定の良し悪しを後で評価しやすくするため。

## 9. 非機能要件チェック
- 性能: 文字列処理だけなので影響は軽微。
- セキュリティ: 影響なし。
- 可観測性: line join 判定ログを追加する価値がある。
- 互換性: 表示 text は不変。影響は翻訳送信 text のみ。

## 10. リスクと緩和策
- Risk: 本文なのに menu-like と誤判定して改行が残る。
- Mitigation: menu 判定は保守的にし、sentence-like が強い時は join を優先する。

- Risk: メニューなのに join して翻訳が崩れる。
- Mitigation: 行頭記号、短文率、英数記号主体など、UI 的特徴を優先して menu-like に寄せる。

- Risk: 既存の CJK space 正規化と干渉する。
- Mitigation: line join 後に既存正規化を適用し、出力の一貫性を保つ。

## 11. 影響範囲
- `Services/TranslationTextNormalizer.cs`
- 必要なら `Services/Orchestration/Stages/TranslateStage.cs` に診断ログ追加
- Doc 更新はこのファイルで足りる

## 12. Definition of Done
- 同一 OCR 合併枠の multi-line 本文が翻訳送信時だけスペース連結される。
- menu / 選択肢 / 項目列は改行維持される。
- overlay 表示や OCR grouped line 数は変わらない。
- 翻訳 payload preview で join 判定の結果を観察できる。
