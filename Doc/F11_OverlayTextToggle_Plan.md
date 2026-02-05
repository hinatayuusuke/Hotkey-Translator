# F11 オーバーレイ表示切替（OCR原文/翻訳）実装案

1. **概要（1–3行）**
F11 を「OCR-only 実行」から「オーバーレイ表示内容の切り替え（OCR原文 / 翻訳）」に変更する。
直近のOCR結果と翻訳結果を保持し、再OCRなしで表示モードを切り替える。

2. **ゴール / 非ゴール**
- ゴール: F11 でオーバーレイ表示を原文/翻訳に切替できる。
- ゴール: 切替時にOCRや翻訳の再実行は行わない。
- ゴール: OCR/翻訳中の F11 は無視する（競合回避）。
- 非ゴール: UI設定画面への切替トグル追加。
- 非ゴール: 設定ファイルへの永続化（再起動でデフォルトに戻る）。

3. **前提・仮定**
- オーバーレイは `PipelineOrchestrator` が生成した `OverlayItem` を表示する。
- 翻訳未取得の行は現状でも原文フォールバックで表示されている。
- F11 の既存「OCR-only 実行」は廃止する。
- F11 は OCR/翻訳が進行中のタイミングでは無視する（状態は切り替えない）。

4. **現状整理**
- F11 は `OnOcrOnlyHotkeyPressed` で `RunOnceAsync(SkipTranslation=true)` を実行。
- `OverlayItem` は表示用テキストのみを持つため、後から表示内容を切替できない。
- `PipelineOrchestrator` は `groupedLines` と `translations` をローカル変数で保持し、再利用しない。
- `RunOnceAsync` は `_gate` により排他制御されているが、F11 トグルはその外にある。

5. **提案アーキテクチャ**
- コンポーネント構成: `OverlayTextMode`（Translated/Source）を追加し、`PipelineOrchestrator` に「直近のOCR行/翻訳/ROI」を保持させる。
- データフロー / シーケンス: RunOnce 完了時に直近の `groupedLines` と `translations` を保存し、F11 トグル時に保存データから `OverlayItem` を再構築して `OverlayPresenter.Update`。
- 既存パターンへの整合: `BuildOverlayItems` に表示モードを渡して既存の合成ロジックを再利用する。
- 競合回避: `PipelineOrchestrator` に `TrySetOverlayTextMode` を追加し、`_gate.Wait(0)` に失敗したら「F11 無視」を返す。

6. **インターフェース設計**
- API / 関数:
- `enum OverlayTextMode { Translated, Source }` を `Models` に追加。
- `PipelineOrchestrator` に `TrySetOverlayTextMode(OverlayTextMode mode)` を追加し、最後のデータから再描画する（取得できない場合は `false` を返す）。
- `MainWindow` の F11 ハンドラはモードをトグルし、`_pipeline.TrySetOverlayTextMode(...)` を呼ぶ。
- 入出力、エラー、バリデーション:
- 直近データが無い場合は no-op（`ShowLast` を維持）で安全に終了し、ログに「データなし」を出す。
- OCR/翻訳が進行中で `TrySetOverlayTextMode` が失敗したら F11 を無視し、ログに理由を出す。
- 翻訳が無い行は既存と同様に原文フォールバックで表示。

7. **実装手順（ステップ分割）**
1. `OverlayTextMode` を追加し、`MainWindow` に現在モードを保持するフィールドを追加。
2. `PipelineOrchestrator` に `lastGroupedLines` / `lastTranslations` / `lastRoiScreen` を保存するフィールドを追加。
3. `BuildOverlayItems` にモード引数を追加し、原文/翻訳の切替を実装。
4. `TrySetOverlayTextMode` を追加して、`_gate.Wait(0)` に成功した場合のみ直近データから `OverlayPresenter.Update` を行う。
5. F11 ハンドラをトグル仕様へ変更し、失敗時（実行中）やデータなし時はログを出す。

8. **非機能要件チェック**
- 性能: 切替時にOCR/翻訳は実行しない（軽量な再構築のみ）。
- セキュリティ: 既存の外部I/F変更なし。
- 可観測性: 既存ログに「切替先モード」「無視理由（実行中/データなし）」を追加。
- 互換性: 既存ホットキー構成は維持、機能のみ変更。
- 運用: 追加設定なし。

9. **リスクと緩和策**
- リスク: 直近データが無いと切替しても何も変わらない。
- Mitigation: ログで「データなし」を明示し、挙動を可視化。
- リスク: 実行中のトグルが無視され、ユーザーが反応なしと感じる。
- Mitigation: ログに「OCR/翻訳実行中のため無視」を出す。

10. **影響範囲**
- `Models/OverlayTextMode.cs`（追加）
- `Services/PipelineOrchestrator.cs`（直近データ保存、再構築、モード切替API）
- `MainWindow.xaml.cs`（F11ハンドラの切替仕様変更）
- 既存の `OverlayPresenter` は変更不要

11. **Definition of Done**
- [ ] F11 で原文/翻訳が切り替わる。
- [ ] 切替時にOCR/翻訳の再実行が発生しない。
- [ ] 直近データが無い場合でも例外が起きない。
- [ ] OCR/翻訳実行中の F11 は無視され、ログで理由が確認できる。
- [ ] 通常の F8/F10 実行で従来通りオーバーレイが表示される。
