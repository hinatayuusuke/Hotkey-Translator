# OCR前処理プレビュー即時反映 実装案

1. **概要（1–3行）**
- 設定変更時に、OCR本実行を待たず前処理プレビューを即時更新する。
- 対象は前処理に影響する設定のみとし、UI操作中は debounce + cancel で最終状態を反映する。
- 本番OCRパイプラインは維持し、プレビュー更新は軽量な専用経路で分離する。

2. **ゴール / 非ゴール**
- ゴール:
- 前処理関連設定を変更すると、現在のプレビュー画像が数百ms以内に更新される。
- スライダー連続操作でも過剰な再処理を避け、最後の値に収束する。
- OCR本実行（翻訳・差分判定・Overlay更新）と干渉しない。
- 非ゴール:
- 設定変更ごとにOCR認識結果まで再実行すること。
- プレビュー専用にUI項目を新設すること（初期実装）。
- 前処理アルゴリズム自体の改善。

3. **前提・仮定**
- 現在プレビュー更新は `PipelineOrchestrator.RunOnceAsync` 内でのみ実施される。
- `OcrPreprocessPreviewReady` イベントで `MainWindow` のプレビュー画像を更新している。
- 前処理の主対象は binarization / auto-threshold / auto-invert / gamma / downsample / two-pass 関連。
- 既存 `SaveSettingsAsync` は多数設定変更の共通経路である。

4. **現状整理**
- `PipelineOrchestrator`:
- `RunOnceAsync` で OCR前処理入力 (`ocrInput`) を生成し、`NotifyOcrPreprocessPreview` を発火している。
- `MainWindow`:
- `OnOcrPreprocessPreviewReady` で `OcrPreprocessPreviewImage.Source` を更新。
- `OnSettingChanged` は全設定変更で `SaveSettingsAsync` を呼ぶが、プレビュー再計算は行っていない。
- UI文言:
- 現在は `"Run OCR to update preview."` 表示になっている。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `PipelineOrchestrator` に「プレビュー専用更新API」を追加。
- `MainWindow` に「前処理プレビュー更新スケジューラ（debounce/cancel）」を追加。
- 既存イベント `OcrPreprocessPreviewReady` は再利用し、UI反映経路は増やさない。
- データフロー / シーケンス:
1. ユーザーが前処理関連設定を変更。
2. `OnSettingChanged` 後、対象設定ならプレビュー更新を debounce 予約。
3. 予約確定時に前回更新タスクを cancel。
4. `PipelineOrchestrator.RefreshOcrPreprocessPreviewAsync` を呼ぶ。
5. capture -> ROI crop -> downsample -> preprocess を実行（OCR認識はしない）。
6. `OcrPreprocessPreviewReady` を発火し、`MainWindow` が画像を即時更新。
- 既存パターン整合:
- 画像更新は既存イベント駆動（`OcrPreprocessPreviewReady`）を使うため、UI更新責務は現行のまま。

6. **インターフェース設計**
- `PipelineOrchestrator` 追加API（案）:
- `Task RefreshOcrPreprocessPreviewAsync(CancellationToken cancellationToken, bool forceCapture = false)`
- 役割:
- 現在設定を用いてプレビュー画像だけ再生成し、`OcrPreprocessPreviewReady` を発火。
- エラー:
- capture/ROI異常時は例外を握りつぶさずログ化し、UIは前回プレビューを維持。
- `MainWindow` 追加（案）:
- `ScheduleOcrPreviewRefresh()`（debounce起動）
- `IsPreprocessPreviewRelevantSender(object sender)`（対象設定判定）
- `CancellationTokenSource _previewRefreshCts`
- `DispatcherTimer _previewRefreshDebounceTimer`（150〜250ms）
- 対象設定（初期案）:
- `EnableOcrBinarizationCheck`
- `EnableOcrAutoThresholdCheck`
- `EnableOcrAutoInvertCheck`
- `EnableOcrGammaCheck`
- `OcrGammaSlider`
- `EnableOcrDownsamplingCheck`
- `OcrDownsampleScaleSlider`
- `EnableOcrTwoPassCheck`
- `OcrTwoPassPreferAutoCheck`
- `OcrTwoPassLowThresholdSlider`
- `OcrTwoPassHighThresholdSlider`
- `OcrBinarizationThresholdSlider`
- `CaptureModeBox` / `EnableRoiCheck` / ROI更新イベント（ROI境界が変わるため）

7. **実装手順（ステップ分割）**
- Step 1: `PipelineOrchestrator` にプレビュー専用更新APIを追加。
- Step 2: プレビュー専用経路で OCR 認識を呼ばず、前処理済み画像生成 + イベント発火のみ行う。
- Step 3: `MainWindow` に debounce/cancel 付きスケジューラを追加。
- Step 4: `OnSettingChanged` から対象設定変更時のみ `ScheduleOcrPreviewRefresh()` を呼ぶ。
- Step 5: ROI更新完了（`SelectRoiAsync` 成功時）でも即時プレビュー再生成を呼ぶ。
- Step 6: 失敗時ログとUIヒント文言を調整（必要なら `"Updating preview..."` を一時表示）。
- Step 7: 手動検証（スライダー連続変更、ON/OFF切替、ROI変更）とビルド確認。

8. **非機能要件チェック**
- 性能:
- OCR認識を省くため、設定変更時の処理負荷は現行RunOnceより軽い。
- debounce/cancelでスライダー連続入力時の負荷を抑制。
- 可観測性:
- 「preview refresh scheduled / canceled / completed / failed」をログ化。
- 互換性:
- 既存 `RunOnceAsync` のプレビュー更新経路は維持するため、従来動作を壊しにくい。

9. **リスクと緩和策**
- Risk: 設定変更連打で更新タスクが競合する。
- Mitigation: `CancellationTokenSource` と世代IDで古い更新結果を破棄。
- Risk: capture/ROI取得失敗時にプレビューが更新されない。
- Mitigation: 失敗時は前回画像を維持し、ログで原因を可視化。
- Risk: 対象設定判定漏れで一部設定変更が反映されない。
- Mitigation: まず明示リストで開始し、検証で不足項目を追加。

10. **影響範囲（変更ファイル候補）**
- `Services/PipelineOrchestrator.cs` — プレビュー専用再計算APIと補助ロジック追加。
- `MainWindow.xaml.cs` — debounce/cancel制御、対象設定判定、更新呼び出し追加。
- `MainWindow.xaml` — 必要時のみヒント文言更新（任意）。

11. **Definition of Done**
- [ ] 前処理関連設定を変更すると、OCR本実行なしでプレビューが更新される。
- [ ] スライダー連続操作時、最終値のプレビューが安定して反映される（過剰更新なし）。
- [ ] 非対象設定変更ではプレビュー更新を起動しない。
- [ ] ROI更新後にプレビューが現ROI基準で更新される。
- [ ] 失敗時にアプリは継続し、ログで失敗理由を確認できる。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
