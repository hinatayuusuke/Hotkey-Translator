# RealTime OCR Capture Pipeline Design（現行）

1. **概要（1–3行）**
   CaptureManager が WGC/DXGI/GDI の各プロバイダから 1 回分のキャプチャを取得し、
   PipelineOrchestrator が ROI 切り出し → 前処理 → OCR → 差分/翻訳 → オーバーレイ更新を行う。
   失敗時は最後のオーバーレイを保持し、黒フレームは FrameGate とクールダウンで抑制する。

2. **ゴール / 非ゴール**
   **ゴール**

   * 1回の RunOnce で OCR/翻訳/オーバーレイ更新まで完結できる構成にする（ホットキー実行想定）
   * プロバイダ失敗時にフォールバックし、黒フレーム時は表示を維持する
   * OCR前処理・差分判定・翻訳優先度を設定で調整できる

   **非ゴール**

   * 常駐キャプチャによる連続録画/全フレーム保存
   * マルチモニタ同時キャプチャの完全対応
   * ストリーミング型の超低遅延保証（現状は都度キャプチャ）

3. **前提・仮定**（不確実性の扱いを明確化）

   * 画像は `System.Drawing.Bitmap` を基準とし、WinRT OCR などへの変換は各プロバイダ/エンジン内で実施する。
   * WGC は OS 依存（`GraphicsCaptureSession.IsSupported()`）であり、DXGI は D3D11 を利用する。
   * ROI は `NormalizedRoi` を優先し、未設定なら `Roi` またはフレーム全体を使用する。
   * パフォーマンスログは `EnableOcrPerfLog` / `OcrPerfLogThresholdMs` で制御する。

4. **現状の構造**（主要コンポーネント）

   * `CaptureManager`

     * プロバイダ順序の決定（`PreferredCaptureProvider` + `CaptureProviderMode`）
     * 失敗時フォールバック、黒フレーム判定とクールダウン管理
   * `ICaptureProvider` 実装

     * `WgcCaptureProvider`（Windows Graphics Capture）
     * `DxgiDuplicationProvider`（DXGI Desktop Duplication）
     * `GdiCaptureProvider`（GDI CopyFromScreen）
   * `FrameGate`：黒フレーム判定（平均輝度/分散）
   * `PipelineOrchestrator`：RunOnce の本体、ROI/ハッシュ/OCR/翻訳/オーバーレイ制御
   * `OcrPreprocessCoordinator` + `OcrPreprocessService`：ガンマ補正/二値化/2パス評価/縮小
   * `OcrEngine`：WinRT / Paddle / Paddle vLLM / Florence2 を切替（失敗時は WinRT へフォールバック）
   * `OcrDiffService`：IoU ベースの差分抽出
   * `PhashService`：ROI の pHash 比較で無変化スキップ
   * `OcrLineGrouper`：行結合（縦方向コスト/重なり）
   * `TranslationFallbackService` + `CacheRepository` + `CacheKeyBuilder`：翻訳とキャッシュ
   * `OverlayPresenter`：オーバーレイ更新と表示制御
   * `MainWindow` の AutoHideWatcher：シーン変化による自動非表示監視（OCR パイプライン外）

5. **処理フロー（RunOnce）**

   1. `PipelineOrchestrator.RunOnceAsync` がセマフォで同時実行を抑止する。
   2. `CaptureManager.Capture(settings)` でフレーム取得。

      * `PreferredCaptureProvider` と `CaptureProviderMode` に従って順序を決定。
      * 失敗やクールダウン中のプロバイダはスキップ。
   3. 黒フレームの場合は `frame.IsBlack` として通知され、最後のオーバーレイを保持する。
   4. ROI を解決し、範囲外なら最後のオーバーレイを保持する。

      * `Roi` から `NormalizedRoi` への変換が成立した場合は設定を非同期保存。
   5. ROI を切り出し、pHash が前回と類似ならスキップする。
   6. 前処理（ガンマ/二値化/縮小）→ OCR 実行（設定によりエンジン選択）。
   7. OCR 結果を画面座標へ変換し、行結合・差分抽出を行う。
   8. 翻訳（キャッシュ優先、プロバイダ優先順位に従う）。
   9. オーバーレイ更新。失敗/キャンセル時は最後の表示を維持する。

6. **キャプチャプロバイダ詳細**

   * WGC: `Direct3D11CaptureFramePool` で 1 フレーム取得（最大 500ms 待機）。
   * DXGI: `AcquireNextFrame(500ms)` → staging へコピー → `Bitmap` へ転送。ActiveWindow 時はウィンドウ領域でクロップ。
   * GDI: `CopyFromScreen` による最終フォールバック。

7. **ブラックフレーム対策とクールダウン**

   * `FrameGate` で平均輝度と分散をサンプリングし、閾値以下を「黒」と判断。
   * `BlackFrameThreshold` 超過で該当プロバイダを `ProviderCooldownSeconds` 秒間クールダウン。
   * 閾値未満の黒フレームはパイプライン側で「最後のオーバーレイ保持」に留める。

8. **例外/失敗時の挙動**

   * キャプチャ失敗: 次のプロバイダへフォールバック。全失敗時は例外 → 最後のオーバーレイ保持。
   * OCR/翻訳失敗: ログ出力し、最後のオーバーレイ保持。
   * キャンセル: 途中で中断し、最後のオーバーレイ保持。

9. **非機能要件（現行）**

   * **性能**：`EnableOcrPerfLog` でステージ別時間をログ化。
   * **可観測性**：キャプチャ失敗・黒フレーム・OCR件数・翻訳結果をログ。
   * **互換性**：WGC はサポート環境のみ、DXGI は D3D11 依存。

10. **既知の制約 / 改善余地**

* DXGI/WGC は都度初期化のため初回フレームが黒になり得る。
* 常駐キャプチャやフレーム再利用は未実装。
* マルチモニタ同時キャプチャは未対応（単一ターゲットのみ）。
