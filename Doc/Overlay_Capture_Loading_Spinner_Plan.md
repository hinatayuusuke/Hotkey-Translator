# Overlay Capture Loading Spinner Plan

1. **概要（1–3行）**
- 実行開始からオーバーレイ更新完了まで、キャプチャ対象の右下に小型スピナーを表示する。
- 既存の `OverlayWindow` を流用し、別ウィンドウは増やさない。
- 既存のオーバーレイ表示で使っている Opacity 制御を壊さず、スピナーは独立レイヤとして描画する。

2. **ゴール / 非ゴール**
- ゴール: OCR/翻訳処理中であることを視覚的に即時把握できる。
- ゴール: 右下位置は `ROI 有効時=ROI右下 / 無効時=キャプチャ領域右下` に追従する。
- ゴール: 処理完了時は自動で消え、失敗時も表示残りしない。
- 非ゴール: 新しい全画面ローディングUIの追加。
- 非ゴール: 既存 `BusyOverlay` の置き換え（共存運用）。

3. **前提・仮定**
- オーバーレイ描画は `OverlayWindow` + `OverlayPresenter` 経由で行う。
- 既存では `OverlayCanvas.Opacity` を `SetOverlayVisibility()` で 0/1 切替している。
- 背景透明度は `OverlayBackgroundOpacity` が `OverlayWindow.ApplyStyle()` で反映される。

4. **現状整理**
- `OverlayWindow` は `OverlayCanvas`（本文表示）と `ToastCanvas`（通知）の2層。
- 表示ON/OFFは `OverlayCanvas.Opacity` の変更で制御。
- 実行中インジケータは中央 `BusyOverlay` のみで、キャプチャ右下追従はない。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `OverlayWindow` に `SpinnerCanvas` を追加（新規）
  - `OverlayPresenter` に `ShowLoadingSpinner(anchorRect)` / `HideLoadingSpinner()` を追加
  - `MainWindow` の `RunOnceAsync` 周辺で開始・終了イベントに連動
- データフロー / シーケンス:
  1. Run開始時に現在のキャプチャ境界（またはROI境界）を算出
  2. `OverlayPresenter.ShowLoadingSpinner()` を呼び出し
  3. OCR/翻訳/描画処理
  4. 正常終了・失敗・キャンセルの全経路で `HideLoadingSpinner()`
- 既存パターン整合:
  - 既存 `OverlayPresenter.InvokeOnUi()` 経由でUIスレッド更新
  - `OverlayWindow` を常駐させる設計は維持

6. **インターフェース設計**
- `OverlayPresenter`
  - `void ShowLoadingSpinner(Rect anchorScreenRect)`
  - `void HideLoadingSpinner()`
- `OverlayWindow`
  - `void ShowLoadingSpinner(Rect anchorDipRect)`
  - `void HideLoadingSpinner()`
- 表示仕様:
  - サイズ: 20–28px
  - 位置: 右下マージン 10–14px
  - アニメーション: `DoubleAnimation` で 0–360 度ループ

7. **実装手順（ステップ分割）**
- Step 1: `OverlayWindow.xaml` に `SpinnerCanvas` と `Path/Ellipse` ベースのスピナー要素を追加。
- Step 2: `OverlayWindow.xaml.cs` に表示/非表示APIと位置計算、回転Storyboard制御を追加。
- Step 3: `OverlayPresenter` に公開メソッドを追加し、`ScreenRect -> Dip` 変換を統一。
- Step 4: `MainWindow.RunOnceAsync` の try/finally に連動処理を追加。
  - 開始: キャプチャ境界取得後に表示
  - finally: 必ず非表示
- Step 5: `Overlay auto-hide` や `SetEnabled(false)` 時にスピナーも必ず消えるガードを追加。

8. **非機能要件チェック**
- 性能: 軽量ベクタ描画 + UIアニメ1本に限定し、OCR処理スレッドへ影響を与えない。
- 可観測性: スピナー表示開始/終了を必要最小限ログに出す（デバッグ用途）。
- 互換性: 既存オーバーレイ文言描画仕様を変更しない。

9. **リスクと緩和策**
- Risk: 既存 Opacity 制御に巻き込まれてスピナーが見えない/消えない。
- Mitigation: `OverlayCanvas` とは別の `SpinnerCanvas` を用意し、スピナー表示は独立制御にする。
- Risk: 例外経路でスピナーが残留する。
- Mitigation: `RunOnceAsync` と `OverlayPresenter.Hide/Clear` の finally 相当で強制非表示。
- Risk: DPI/マルチモニタで位置ズレ。
- Mitigation: 既存 `DpiHelper.ScreenRectToWindowDip()` を再利用して位置変換を統一。

10. **影響範囲（変更ファイル候補）**
- `UI/OverlayWindow.xaml` — スピナーUIレイヤ追加
- `UI/OverlayWindow.xaml.cs` — スピナー表示/非表示・位置・アニメ制御追加
- `Services/OverlayPresenter.cs` — スピナーAPI追加
- `MainWindow.xaml.cs` — 実行開始/終了の連動追加

11. **Definition of Done（完了条件）**
- [ ] 実行開始後、キャプチャ対象右下にスピナーが表示される。
- [ ] 処理完了/失敗/キャンセル時にスピナーが確実に消える。
- [ ] `OverlayBackgroundOpacity` を変更しても、スピナー表示制御が破綻しない。
- [ ] 既存のオーバーレイ本文・トースト表示が退行しない。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。

## Opacity留意点（重要）
- 現在の本文表示は `OverlayCanvas.Opacity` で制御されるため、同じキャンバスにスピナーを置くと巻き込まれる。
- スピナーは `SpinnerCanvas` を分離し、`OverlayBackgroundOpacity`（背景ブラシのアルファ）とは独立で表示制御する。
- ただし、オーバーレイ全体を非表示にする経路（`SetEnabled(false)` など）ではスピナーも同時に消す。
