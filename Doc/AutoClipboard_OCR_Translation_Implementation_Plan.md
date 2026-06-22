# OCR / Translation Auto Clipboard Implementation Plan

## 1. 概要
OCR、翻訳、オーバーレイ更新が完了したタイミングで、原文と翻訳文を自動的にクリップボードへコピーする。  
出力元は overlay 表示テキストではなく、`PipelineOrchestrator` が保持する確定済みの `ReadingUnit` と `translations` を使う。

## 2. ゴール / 非ゴール
### ゴール
- 翻訳結果が overlay に反映されるタイミングで、原文と翻訳文を paired text として clipboard に保存する。
- WPF overlay と graphics hook overlay のどちらでも同じ確定データを使う。
- clipboard 書き込み失敗時はログに残し、pipeline 本体は失敗させない。

### 非ゴール
- ファイル保存、履歴保存、常時ログ化。
- JSON など複数形式の同時出力。
- OCR、翻訳、overlay の既存挙動変更。
- 手動 export UI / hotkey の追加。

## 3. 推奨アーキテクチャ
- `AppSettings`
  - `AutoCopyOcrTranslationToClipboard` を追加する。
  - 既定値は `false`。clipboard 上書きはユーザー操作を壊しやすいため opt-in とする。
- `PipelineOrchestrator`
  - 翻訳結果と overlay publish が揃った後に、自動コピー用イベントを発火する。
  - イベント payload は `ReadingUnit` と `translations` を保持する snapshot とする。
- `MainWindow`
  - イベントを受け、`Dispatcher` 経由で `Clipboard.SetText(...)` を実行する。
  - WPF clipboard API は STA/UI スレッド前提なので pipeline 内では直接呼ばない。
- `TextExportService` または小さな formatter
  - `ReadingUnit.Text` と `translations[unit.Id]` を paired text に整形する。

## 4. 推奨タイミング
通常 pipeline では以下の順序にする。

1. OCR grouping 完了。
2. 翻訳完了。
3. `OverlayStage.BuildItems(...)` で表示内容を構築。
4. `CommitOverlayState(...)` で直近状態を確定。
5. WPF overlay / graphics hook overlay を publish。
6. 自動 clipboard イベントを発火。

`OverlayPresenter.Updated` からコピーする方式は採用しない。表示用整形後の text しか見えず、原文と翻訳文の対応が崩れやすいため。

## 5. 出力形式
v1 は paired text のみとする。

```text
[1]
Original:
<OCR原文>

Translation:
<翻訳文>

[2]
Original:
<OCR原文>

Translation:
<翻訳文>
```

翻訳が存在しない unit は `Translation:` を空欄にするか、unit 自体を出力対象から外す。推奨は空欄出力。OCR-only 実行時にも状態を確認しやすい。

## 6. 実装手順
1. `AppSettings` に `AutoCopyOcrTranslationToClipboard` を追加し、設定 UI にチェックボックスを追加する。
2. export snapshot model を追加する。既存の `ReadingUnit` と `Dictionary<int, string>` を直接 UI に渡さず、コピー用に immutable な形へ詰め替える。
3. paired text formatter を追加する。
4. `PipelineOrchestrator` で overlay publish 後にイベントを発火する。
5. `MainWindow` でイベントを購読し、設定が有効な場合のみ clipboard に書き込む。
6. 成功/失敗を runtime log に残す。

## 7. リスクと緩和策
- Risk: 自動コピーがユーザーの通常 clipboard 内容を上書きする。
- Mitigation: 既定 OFF、設定 UI で明示 opt-in にする。

- Risk: pipeline スレッドから clipboard を触ると STA 制約で失敗する。
- Mitigation: `MainWindow.Dispatcher` 経由で UI スレッドから書き込む。

- Risk: overlay 用整形 text を使うと固定 overlay や折り返しで対応関係が崩れる。
- Mitigation: `ReadingUnit` と `translations` の確定 snapshot だけを出力元にする。

## 8. Definition of Done
- 設定 ON 時、翻訳済み overlay 更新後に clipboard へ paired text が入る。
- 設定 OFF 時、clipboard は変更されない。
- WPF overlay 抑止中の graphics hook overlay 経路でも同じ snapshot からコピーされる。
- clipboard 失敗時にログが残り、OCR / 翻訳 pipeline は継続する。
