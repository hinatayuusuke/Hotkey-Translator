# Dxgi Resident Capture Plan（ホットキー時取得）

1. **概要（1–3行）**
   DXGI を選択しているときだけ D3D11/Duplication を初期化・常駐化し、
   ホットキー実行時は既存セッションから 1 フレームだけ取得する方式に変更する。
   初回黒フレーム・初期化コストを抑えつつ、常時キャプチャは行わない。

2. **ゴール / 非ゴール**
   **ゴール**

   * DXGI選択時に初期化コストを一度だけにし、RunOnceのレイテンシを安定化
   * 初回黒フレーム問題を warm-up 条件で抑制
   * AccessLost / モニタ変更への再初期化で安定性を確保

   **非ゴール**

   * 常時フレーム取得（録画/ストリーミング用途）
   * DXGIを常時起動してGPU負荷を上げる設計
   * マルチモニタ同時キャプチャの完全サポート

3. **前提・仮定**（不確実性の扱いを明確化）

   * DXGIキャプチャは `DxgiDuplicationProvider` を拡張し、インスタンス内でセッションを保持する。
   * DXGI利用条件は「ユーザがDXGIを選択している状態」とする。
     - 想定条件: `PreferredCaptureProvider == Dxgi` かつ `CaptureProviderMode == Fixed`。
     - Autoの場合でも先頭がDXGIである限り同様に動作させるかは実装で選択する。
   * 既存のフォールバック（WGC/GDI）と黒フレーム判定（FrameGate）は維持する。

4. **現状整理**（現行挙動、関連モジュール、既存制約）

   * `DxgiDuplicationProvider.TryCapture` は毎回 `CreateDeviceAndContext` / `CreateDuplication` を実行。
   * `AcquireNextFrame(500ms)` 後に staging + Bitmap を毎回作成している。
   * 初回フレームが黒/未確定になりやすい（毎回初期状態から開始）。

5. **提案アーキテクチャ**

   ### コンポーネント構成

   * `DxgiDuplicationProvider`
     * 内部に `DxgiResidentSession`（新規内部クラス）を保持
     * `EnsureSession(mode)` で初期化/再初期化
     * `TryAcquireFrame` で 1 フレーム取得

   ### データフロー / シーケンス（文章で可）

   1. CaptureManager が DXGI を選択すると `DxgiDuplicationProvider.TryCapture` が呼ばれる。
   2. Provider 内で `EnsureSession` を呼び、既存セッションが有効なら再利用。
   3. `AcquireNextFrame` を短めのタイムアウトで呼び出し（例: 50–200ms）。
   4. `frameInfo` を確認し、有効フレームのみ採用（warm-up）。
   5. staging へコピー → Bitmap 作成 → ROI/Window処理。

   ### 既存パターンへの整合

   * `ICaptureProvider.TryCapture` のシグネチャは維持し、呼び出し側の変更を最小化。
   * CaptureManager の「黒フレーム判定/クールダウン」フローはそのまま利用する。

6. **インターフェース設計**

   ### API / 関数 / イベント

   * `DxgiDuplicationProvider`
     * `EnsureSession(CaptureMode mode)`（内部）
     * `ResetSession(string reason)`（内部）

   * `DxgiResidentSession`（新規内部クラス）
     * `ID3D11Device Device` / `ID3D11DeviceContext Context`
     * `IDXGIOutputDuplication Duplication`
     * `ID3D11Texture2D Staging`
     * `Rect MonitorBounds`
     * `IntPtr MonitorHandle`

   ### 入出力、エラー、バリデーション

   * `DXGI_ERROR_ACCESS_LOST` / `DXGI_ERROR_INVALID_CALL` 等は `ResetSession` して再初期化。
   * モニタ変更（ActiveWindowが別モニタに移動）検出時は再初期化。
   * `frameInfo.LastPresentTime == 0` または `AccumulatedFrames == 0` は warm-up として無視。

7. **実装手順（ステップ分割）**

   ### Step 1: DxgiResidentSession の追加
   * `DxgiDuplicationProvider` 内に private class を追加。
   * 既存の `CreateDeviceAndContext` / `CreateDuplication` を再利用しつつ、
     セッション保持に切り替える。

   ### Step 2: EnsureSession / ResetSession の実装
   * 最初の `TryCapture` で初期化。
   * `CaptureMode` またはモニタハンドルが変化したら再初期化。

   ### Step 3: AcquireNextFrame の warm-up 判定
   * `frameInfo` を確認し、未確定フレームを破棄。
   * 破棄時は `ReleaseFrame` し、必要なら再試行（最大 N 回）。

   ### Step 4: エラー処理の強化
   * AccessLost → ResetSession → リトライ1回。
   * 連続失敗時は CaptureManager のクールダウンに委ねる。

   ### Step 5: タイムアウト調整
   * 常駐セッションなので `AcquireNextFrame` は短めに設定。
   * 取得できない場合は失敗としてフォールバック可能にする。

8. **非機能要件チェック**

   * **性能**：初期化コストを初回のみとし、ホットキー応答を短縮。
   * **セキュリティ**：フレーム内容はログ出力しない。
   * **可観測性**：初期化・再初期化・AccessLost をログで追跡。
   * **互換性**：Autoモードでは他プロバイダへのフォールバックを維持。

9. **リスクと緩和策**

   * **DXGIセッションのリーク/破棄忘れ**
     * 緩和: `DxgiDuplicationProvider.Dispose` を実装し、アプリ終了時に必ず破棄。
   * **モニタ変更で無効なDuplicationを保持**
     * 緩和: ActiveWindow のモニタが変わった場合に即再初期化。
   * **初期フレームが黒/未確定**
     * 緩和: frameInfo 条件で warm-up スキップ。

10. **影響範囲**（変更ファイル候補・移行・ドキュメント更新）

* 変更候補
  * `Services/DxgiDuplicationProvider.cs`（常駐セッション追加）
  * `Services/CaptureManager.cs`（必要ならDXGI選択条件の明確化）
* 移行
  * 既存設定はそのまま利用。DXGI選択時のみ効果を発揮。
* ドキュメント更新
  * キャプチャ方式の説明（DXGIは常駐セッション）。

11. **Definition of Done**（完了条件のチェックリスト）

* [ ] DXGI選択時にセッションが初期化・再利用される
* [ ] ホットキー実行時のDXGI取得が短時間で完了する
* [ ] AccessLost/モニタ変更で再初期化できる
* [ ] 初回黒フレームが warm-up 判定で抑制される
* [ ] 他プロバイダへのフォールバックは維持される
