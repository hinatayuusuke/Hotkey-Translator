# User Glossary Term Fix Implementation Plan

## 1. 概要

ユーザが登録した特定用語を、翻訳エンジンの種類に依存せず固定訳へ置き換えるための実装案です。
対象は「文全体」ではなく「文中の用語」のみとし、ゲーム画面 OCR の ReadingUnit 単位の翻訳パイプラインへ差し込みます。

## 2. ゴール / 非ゴール

### ゴール

- ユーザが登録した用語を、DeepL / Gemini / LlamaCpp のどの翻訳エンジンでも同じ訳に固定できる
- OCR 由来の 1 ReadingUnit 内に複数の辞書語があっても安定して適用できる
- 既存の翻訳優先順位、翻訳キャッシュ、オーバーレイ表示フローを大きく壊さない
- 初期実装はシンプルで、誤爆しにくいことを優先する

### 非ゴール

- 文全体の完全一致辞書
- 正規表現辞書
- 品詞解析や文法解析を使った高度な置換
- エンジン別の専用 glossary API 利用
- UI 上でのインポート / エクスポート / ソート / 一括編集

## 3. 前提・仮定

- 現在の翻訳単位は `ReadingUnit.Text` であり、`TranslateStage` から翻訳プロバイダへ `List<string>` として送っている
- 翻訳結果は `sourceText -> translatedText` の辞書として戻る
- 本機能は翻訳プロバイダの内部ではなく、アプリ共通レイヤで適用する
- 初期実装では「最長一致」「単純部分一致」を基本とし、複雑な単語境界判定は最小限に留める

## 4. 現状整理

### 現行挙動

- OCR 結果は `ReadingUnit` にまとめられ、`TranslateStage` が pending テキストを収集する
- `TranslationFallbackService` が有効な翻訳エンジンへ送信する
- 翻訳結果は `TranslateStage` 側でキャッシュ保存され、最終的にオーバーレイ表示へ渡る

### 現状の問題

- 固有名詞、機体名、スキル名、地名などが翻訳エンジンごとに揺れる
- 1つのゲームでも翻訳エンジンや文脈により用語の訳が不安定になる
- 翻訳後の単純置換だけでは、モデルが崩した語形を確実に補正できない

### 制約

- OCR はノイズを含むため、辞書適用が強すぎると誤爆しうる
- ゲーム用オーバーレイでは誤訳より「表示が安定していること」が重要
- 既存キャッシュは正規化済み原文ベースなので、辞書更新時の扱いを設計する必要がある

## 5. 提案アーキテクチャ

### コンポーネント構成

- `Models/UserGlossaryEntry.cs`
  - 1件の用語辞書エントリ
- `Services/Translation/UserGlossaryService.cs`
  - 辞書の前処理、適用、復元を担当
- `Models/AppSettings.cs`
  - ユーザ辞書リストと辞書リビジョンを保持
- `Services/Orchestration/Stages/TranslateStage.cs`
  - 翻訳前保護、翻訳後復元、キャッシュ反映を担当

### データフロー

1. `TranslateStage` が pending な `ReadingUnit.Text` を集める
2. `UserGlossaryService` が各テキスト中の辞書語を検出する
3. 辞書語を翻訳されにくいプレースホルダへ置換したテキストを翻訳エンジンへ送る
4. 翻訳エンジンから結果を受け取る
5. `UserGlossaryService` がプレースホルダをユーザ指定訳へ復元する
6. 復元後の最終結果をキャッシュへ保存し、オーバーレイ表示へ渡す

### 既存パターンへの整合

- 翻訳エンジン本体には手を入れず、共通の orchestration 層で吸収する
- 既存の `TranslationFallbackService` の provider 切替には影響させない
- キャッシュ保存タイミングは従来通り `TranslateStage` に残す

## 6. インターフェース設計

### `UserGlossaryEntry`

- `string SourceTerm`
- `string TargetTerm`
- `string? SourceLanguage`
- `string? TargetLanguage`
- `bool Enabled`
- `int Priority`

初期実装では `MatchMode` は持たず、単純部分一致で固定する。
将来拡張したくなった時だけ追加する。

### `UserGlossaryService`

- `GlossaryPreparedBatch Prepare(IReadOnlyList<string> texts, AppSettings settings)`
- `IReadOnlyDictionary<string, string> Restore(IReadOnlyDictionary<string, string> translated, GlossaryPreparedBatch batch, AppSettings settings)`

### `GlossaryPreparedBatch`

- 元テキスト一覧
- 置換後テキスト一覧
- 各テキストごとの置換情報
- 使用したプレースホルダ一覧

### エラー / バリデーション

- `SourceTerm` 空文字は無効
- `TargetTerm` 空文字は無効
- 完全重複エントリは保存時に除外または上書き
- プレースホルダ未復元時はログを残す

## 7. 実装手順

### Step 1. モデル追加

- `UserGlossaryEntry` を追加
- `AppSettings` に以下を追加
  - `List<UserGlossaryEntry> UserGlossaryEntries`
  - `int UserGlossaryRevision`

### Step 2. 辞書サービス追加

- 有効な辞書エントリを抽出
- `SourceTerm` 長さ降順、`Priority` 昇順で並べる
- テキストごとに最長一致で置換箇所を決める
- プレースホルダを生成する

プレースホルダ例:

```text
[[HTG_0001]]
[[HTG_0002]]
```

### Step 3. 翻訳前保護

- `TranslateStage` の pending テキスト生成後に `Prepare` を呼ぶ
- 翻訳エンジンへは保護済みテキストを送る
- `BuildTranslationPayloadLog` では必要なら原文と保護後の両方を出せるようにする

### Step 4. 翻訳後復元

- 翻訳結果受信後、プレースホルダを `TargetTerm` へ戻す
- 復元後の文字列だけを以後の処理へ流す
- キャッシュ保存も復元後文字列を使う

### Step 5. キャッシュ整合

- 既存の cache key に `UserGlossaryRevision` を混ぜる
- 辞書更新時は `UserGlossaryRevision` をインクリメントする

### Step 6. 最小 UI

- 設定画面へ簡易な辞書編集 UI を追加
- 初期は以下だけで十分
  - Source term
  - Target term
  - Enabled
  - Add
  - Remove

## 8. 非機能要件チェック

### 性能

- 用語数は多くなりすぎると毎回の線形探索コストが増える
- 初期実装では数十〜百件程度を想定
- 必要なら将来 Trie 化を検討する

### セキュリティ

- 外部入力は settings.json 由来なので危険なコード実行はない
- ただしプレースホルダ文字列は固定フォーマットにし、ユーザ入力と衝突しにくくする

### 可観測性

- 以下をログ出力候補とする
  - glossary_prepare: hit 件数
  - glossary_restore: restored 件数
  - glossary_restore_miss: 未復元件数

### 互換性

- 辞書未設定時は現行挙動と完全一致にする
- 既存翻訳プロバイダの実装は変更しない

### 運用

- 初期は「用語辞書だけ」
- 文全体固定辞書は別機能として後から追加する

## 9. リスクと緩和策

### Risk

- OCR ノイズで誤った用語にヒットする

### Mitigation

- 最長一致優先
- 初期は単純で短すぎる語の登録を避ける
- 将来は最小文字数ガードを追加可能にする

### Risk

- モデルがプレースホルダを壊して未復元になる

### Mitigation

- 英数字と記号だけの壊れにくいプレースホルダを使う
- 未復元時はログを出す
- 初期は fail fast ではなく、その文だけ従来訳を使う

### Risk

- 辞書更新後に古いキャッシュが残る

### Mitigation

- `UserGlossaryRevision` を cache key に含める

## 10. 影響範囲

変更ファイル候補:

- `Models/AppSettings.cs`
- `Models/UserGlossaryEntry.cs`
- `Services/Translation/UserGlossaryService.cs`
- `Services/Orchestration/Stages/TranslateStage.cs`
- `Services/CacheKeyBuilder.cs` または同等の cache key 生成箇所
- 必要なら設定 UI 関連ファイル

ドキュメント更新候補:

- 翻訳設定 UI 説明
- ポータブル配布向け settings 説明

## 11. Definition of Done

- ユーザが登録した用語が DeepL / Gemini / LlamaCpp のどれでも同じ訳に固定される
- 辞書未設定時の挙動が従来と変わらない
- 同一テキスト内に複数辞書語があっても復元できる
- 辞書変更後、古いキャッシュを引きずらない
- ログで辞書適用件数と未復元を追える
- 初期 UI から辞書の追加 / 削除 / 有効無効ができる

