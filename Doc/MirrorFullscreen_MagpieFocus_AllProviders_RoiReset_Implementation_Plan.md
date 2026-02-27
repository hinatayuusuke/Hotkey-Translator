# Mirror Fullscreen で Magpie 表示面に統一する実装計画（WGC/Dxgi/GDI + ROIリセット）

## 1. 概要（1-3行）
- ミラー有効時は、`WGC / Dxgi / GDI / ROI / Overlay` の座標基準をすべて Magpie のミラー表示面に統一する。
- これにより provider ごとの座標空間混在をなくし、オーバーレイ位置ズレを回避する。
- ミラー停止時は `EnableRoi=false` と `Roi/NormalizedRoi=null` を適用し、ROI状態を明示リセットする。

## 2. ゴール / 非ゴール
### ゴール
- ミラー有効時に provider を切り替えても OCR・翻訳オーバーレイの表示位置が一致する。
- ROIの扱いがユーザー視点で一貫する（ミラー中はミラー基準、停止後は未設定状態へ戻す）。
- 既存の F7/Shift+F7（ロック/解除）と Ctrl+F7（ミラートグル）運用を維持する。

### 非ゴール
- Magpie 本体の改造。
- Hook overlay の同時最適化。
- ミラー停止後にROIを自動復元する機能（今回対象外）。

## 3. 前提・仮定
- Magpieスケーリング窓クラスは `Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22`。
- ミラー中に `Dxgi/GDI` が合成結果を取得する場合がある。
- 現行の `source -> mirror` マッピングは provider により二重変換になる場合がある。

## 4. 現状整理
- `WGC` は元ウィンドウ内容を取得しやすく、現在は見た目上正常。
- `Dxgi/GDI` はミラー合成面を取得するケースがあり、現在のマッピングと衝突してズレる。
- ROIも同様に基準混在が起きるため、providerごとの挙動差がユーザーに見える。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `MirrorCaptureTargetResolver`（新規）
  - ミラー有効時は capture request の対象 HWND を Magpie scaling window に切替。
- `MirrorModeExecutionContext`（新規 or 既存拡張）
  - 現在フレームが `mirror_space` であることを明示。
- `OverlayMapperGate`（既存呼び出し条件の調整）
  - `mirror_space=true` 時は `source->mirror` 変換を無効化（identity）。

### データフロー / シーケンス
1. Ctrl+F7 でミラー開始。
2. Capture実行前に `MirrorCaptureTargetResolver` が動作。
3. ミラー有効なら provider 共通で `Magpie scaling window` を対象として bounds/capture を取得。
4. OCR/ROI/Overlay はそのまま mirror座標で処理。
5. ミラー停止時に ROI を明示リセット（`EnableRoi=false`, `Roi=null`, `NormalizedRoi=null`）。

## 6. インターフェース設計
### 6.1 設定/状態
- 既存設定を利用（新規設定は増やさない）。
- ミラー停止時の状態更新:
  - `EnableRoi = false`
  - `Roi = null`
  - `NormalizedRoi = null`

### 6.2 Captureターゲット切替
- ミラー有効時:
  - 第1候補: `MagpieScalingChanged` の最新 `lParam` HWND
  - 第2候補: `FindWindow("Window_Magpie_...", null)`
- 有効性チェック:
  - `IsWindow` / `IsWindowVisible`
  - 取得 bounds が空でないこと

### 6.3 Overlay座標
- ミラー有効時は `SetScreenRectMapper(null)`（identity）
- ミラー無効時は現行動作（必要なら source基準マッパー）

### 6.4 ROI
- ミラー有効中: ROI選択・評価は mirror座標で完結。
- ミラー停止時: ROIを全消去し、次回は未設定状態から開始。

## 7. 実装手順（ステップ分割）
### Step 1: ミラー対象窓の解決器を追加
- `MagpieSessionController` に scaling window HWND 取得APIを追加/強化。
- `CaptureTargetResolver` 経由で mirror active 時の強制ターゲット切替を実装。

### Step 2: provider共通のmirror target利用
- `WGC/Dxgi/GDI` で同一 request target HWND を使うよう統一。
- `bounds_success` ログに `target=magpie|source` を追加。

### Step 3: overlay mapper の条件見直し
- ミラー有効かつ capture target が Magpie のとき mapper を無効化。
- 二重変換を防止。

### Step 4: ROIリセット実装
- ミラー停止フロー（hotkey/settings/shutdown）でROIリセット処理を一元化。
- UI表示（ROI status）更新と settings save を同一トランザクションで実施。

### Step 5: 検証ログ追加
- `stage=mirror_capture event=target_resolved hwnd=... source=message|findwindow`
- `stage=mirror_roi event=reset reason=mirror_stopped`
- `stage=overlay_map event=mode mirror_space=1 mapper=identity`

## 8. 非機能要件チェック
- 性能: 追加処理は HWND解決と分岐のみで軽量。
- 可観測性: target/bounds/map mode をログで追跡可能。
- 互換性: ミラー無効時は既存動作維持。
- 運用: ミラー停止時にROI残骸が残らず再現性が高い。

## 9. リスクと緩和策
- Risk: Magpie scaling window が一時的に取得不能なタイミングがある。
- Mitigation: メッセージ優先 + FindWindowフォールバック + 未解決時は今回フレームをskipしてログ出力。

- Risk: ミラー停止でROIが消えるため、ユーザーが再設定を必要とする。
- Mitigation: 停止時に明示ログを表示し、仕様として固定する。

- Risk: Dxgi/GDIでオーバーレイ自己混入が発生しうる。
- Mitigation: 現時点では座標整合を優先し、自己混入は別タスクで検出/抑制を実装。

## 10. 影響範囲（変更ファイル候補）
- `Services/Application/MagpieSessionController.cs`
- `Services/CaptureTargetResolver.cs`
- `Services/CaptureManager.cs`（必要に応じて）
- `Services/WgcCaptureProvider.cs`
- `Services/DxgiDuplicationProvider.cs`
- `Services/GdiCaptureProvider.cs`
- `MainWindow.xaml.cs`
- `Services/OverlayPresenter.cs`（呼び出し条件のみ）
- `Doc/`（本計画書）

## 11. Definition of Done
- [ ] ミラー有効時、WGCでROI/OCR/overlay位置が一致。
- [ ] ミラー有効時、DxgiでROI/OCR/overlay位置が一致。
- [ ] ミラー有効時、GDIでROI/OCR/overlay位置が一致。
- [ ] ミラー停止時に `EnableRoi=false` かつ `Roi/NormalizedRoi=null` が反映される。
- [ ] ミラー無効時の既存動作に回帰がない。
