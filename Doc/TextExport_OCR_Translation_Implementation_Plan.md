# OCR / Translation Text Export Implementation Plan

## 1. 概要（1–3行）
アプリの直近のパイプライン結果から、OCR 原文と翻訳文をまとめて出力する機能を追加する。  
出力元は overlay ではなく、確定済みの pipeline state を使う。  
v1 は clipboard 出力を前提にし、既定形式は paired text、JSON は補助形式に留める。

## 2. ゴール / 非ゴール
### ゴール
- 直近の OCR / 翻訳結果を、明示的なユーザー操作で出力できるようにする。
- OCR 原文と翻訳文を同じ単位で出力できるようにする。
- 出力元を overlay 表示ではなく、pipeline の committed state に揃える。
- v1 は clipboard export を基本とし、paired text を既定、JSON を secondary にする。

### 非ゴール
- 自動保存や自動エクスポート。
- 監視フォルダ連携、ファイルの自動追記、常時ログ化。
- overlay 表示のレイアウト変更。
- OCR / 翻訳エンジンの再設計。
- 既存 diff / cache / scene change の挙動変更。

## 3. 前提・仮定
- パイプラインの最終結果は `PipelineOrchestrator` に保持されている。
  - `ReadingUnit` 群
  - `translations` 対応
- `OverlayStage` は表示用の整形を行うため、export の正本にはしない。
- 既存の hotkey / command / UI 配線パターンを流用できる。
- v1 は単一クライアント、単一直近結果のエクスポートで十分である。

## 4. 現状整理
- `Services/PipelineOrchestrator.cs` は `CommitOverlayState(...)` で直近の `ReadingUnit` と翻訳結果を保持している。
- `Models/ReadingUnit.cs` は OCR 原文の正規化済み単位として使える。
- `Services/Orchestration/Stages/OverlayStage.cs` は表示用に text を整形・折り返しするため、出力原本には向かない。
- `Services/Application/HotkeyCommandController.cs` はユーザー操作から pipeline へ作用させる既存の入口である。
- `MainWindow.xaml` / `MainWindow.xaml.cs` には、明示操作のボタン配線と確認ダイアログの既存パターンがある。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `Models/TextExportSnapshot.cs` 新規
  - OCR 原文、翻訳文、生成時刻、対象 ROI、エンジン情報を持つ。
- `Services/TextExportService.cs` 新規
  - snapshot を clipboard 用の文字列または JSON に整形する。
- `Services/PipelineOrchestrator.cs`
  - 直近結果を export 用に安全に読み出す API を追加する。
- `Services/Application/HotkeyCommandController.cs`
  - 明示操作のトリガーを受ける。
- `UI` / `MainWindow`
  - ボタンまたは hotkey から呼べるようにする。

### データフロー / シーケンス
1. OCR / 翻訳 pipeline が完了する。
2. `PipelineOrchestrator` が committed state として `ReadingUnit` と翻訳結果を保持する。
3. ユーザーが `Export OCR/Translation` を明示操作する。
4. `PipelineOrchestrator` が `TextExportSnapshot` を返す。
5. `TextExportService` が paired text または JSON に整形する。
6. 整形済みテキストを clipboard に入れる。

### 既存パターンへの整合
- `overlay` や `preview` とは分離し、結果の正本を export する。
- 既存の command / UI / settings の配線を壊さず、追加の明示操作として実装する。
- 自動保存は使わず、ユーザーが押した時だけ出力する。

## 6. インターフェース設計
### 追加候補
- `PipelineOrchestrator`
  - `TryGetTextExportSnapshot(out TextExportSnapshot snapshot)` または同等の read API
- `TextExportSnapshot`
  - `IReadOnlyList<ReadingUnit>`
  - `IReadOnlyDictionary<int, string>` または paired list
  - `GeneratedAt`
  - `SourceLanguage`
  - `TargetLanguage`
  - `OcrEngineKind`
- `TextExportService`
  - `BuildPairedText(...)`
  - `BuildJson(...)`
  - `CopyToClipboard(...)`

### 出力形式
- `paired text`
  - 1 unit ごとに `OCR` と `Translation` を並べる。
  - v1 の既定形式。
- `JSON`
  - デバッグ用途と外部連携向け。
  - v1 は clipboard 上の secondary format とする。

### エラー / バリデーション
- 最新 pipeline 結果が空なら export 失敗として明示する。
- 翻訳が未生成の unit は OCR 原文のみ出力する。
- clipboard 失敗は明示エラーにする。
- 文字列中の改行や余分な空白は、paired text では可読性を優先して整形する。

## 7. 実装手順（ステップ分割）
### Step 1
- `TextExportSnapshot` を追加する。
- `PipelineOrchestrator` に export 用 read API を追加する。

### Step 2
- `TextExportService` を追加し、paired text と JSON の整形を実装する。

### Step 3
- `HotkeyCommandController` から export を呼べるようにする。
- 既存の command パターンに合わせて、明示操作の入口を作る。

### Step 4
- `UI` に export ボタンを追加する。
- 必要なら hotkey も追加する。

### Step 5
- clipboard 書き込みと失敗時ログを追加する。
- 直近結果がない場合のメッセージを整える。

## 8. 非機能要件チェック
- 性能
  - export は直近結果の整形だけに留め、OCR / translation 本体を再実行しない。
- セキュリティ
  - clipboard へ出す内容はユーザー明示操作に限定する。
- 可観測性
  - 成功 / 失敗をログに残す。
- 互換性
  - 既存の OCR / translation / overlay の挙動を変えない。
- 運用
  - paired text を既定にして、機械可読な JSON は secondary とする。

## 9. リスクと緩和策
- Risk: overlay 用に整形された text を export に使うと、原文と翻訳の対応が崩れる。
- Mitigation: export 元は overlay ではなく committed pipeline state に固定する。

- Risk: 翻訳がない unit で出力フォーマットが壊れる。
- Mitigation: 原文のみでも成立する paired text にする。

- Risk: clipboard 以外の出力経路を先に入れると実装が膨らむ。
- Mitigation: v1 は clipboard のみに絞る。

## 10. 影響範囲
- `Models/TextExportSnapshot.cs` 新規
- `Services/TextExportService.cs` 新規
- `Services/PipelineOrchestrator.cs`
- `Services/Application/HotkeyCommandController.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- 必要なら `UI/` 配下に export ボタンを追加
- `Doc/` の本書

## 11. Definition of Done
- ユーザー操作で OCR 原文と翻訳文を clipboard に出せる。
- 出力元が overlay ではなく pipeline の committed state である。
- v1 の既定出力は paired text である。
- JSON は secondary 形式として出せる。
- 自動保存・自動エクスポートが入っていない。
- 既存の OCR / translation / overlay 動作に回帰がない。
