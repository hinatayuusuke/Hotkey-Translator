# Mirror Fullscreen で Dxgi/GDI を安全運用する CaptureSpace 分離実装計画

## 1. 概要（1-3行）
- ミラーフルスクリーン有効時、`WGC` は元ウィンドウ基準、`Dxgi/GDI` はミラー合成面基準になりやすく、座標系が混在してオーバーレイがズレる。
- 安全に `Dxgi/GDI` を使うには、キャプチャ結果の座標空間を明示管理し、ROI/OCR/Overlay の変換経路を `CaptureSpace` ごとに分岐する。
- 本計画は「Dxgi/GDI を禁止しない」前提で、回帰を抑えつつ段階導入する。

## 2. ゴール / 非ゴール
### ゴール
- ミラー有効時に `WGC / Dxgi / GDI` のいずれでも、OCR結果とオーバーレイ表示位置が一致する。
- フォールバックが発生しても、表示ズレが起きない（または安全側に倒れる）。
- 既存ホットキー/ミラー開始停止フローは維持する。

### 非ゴール
- Magpie 本体改造。
- すべてのGPU/ドライバ固有問題の同時解消。
- Hook overlay との同時最適化（別タスク）。

## 3. 前提・仮定
- ミラー開始/停止は既存 `MagpieSessionController` で管理済み。
- 既存オーバーレイ座標変換は「source window -> mirror monitor」前提で固定されている。
- `Dxgi/GDI` は状況により「ミラー面（合成結果）」をキャプチャするため、同じ変換をかけると二重変換になる。

## 4. 現状整理
- `WGC` は対象ウィンドウキャプチャで、source基準と一致しやすい。
- `Dxgi/GDI` はデスクトップ合成面を掴みやすく、ミラー時は `MirrorDesktop` になるケースがある。
- そのまま `MapScreenRect(source->mirror)` を適用すると、`Dxgi/GDI` のときだけズレる。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `CaptureSpace`（新規 enum）
  - `SourceWindow`
  - `MirrorDesktop`
- `CaptureFrame` 拡張（メタ追加）
  - `CaptureSpace Space`
  - `Rect? MirrorDestRect`（任意、デバッグ/補助）
- `MagpieCaptureSpaceResolver`（新規、または `MagpieSessionController` に内包）
  - provider + mirror state + scaling window 状態から `CaptureSpace` 判定
- `Overlay mapping gate`（既存 `OverlayPresenter.SetScreenRectMapper` の呼び出し側で制御）

### データフロー / シーケンス
1. `CaptureManager` が `CaptureFrame` を返す際、`Space` を確定する。
2. `PipelineOrchestrator` は `frame.Space` を見て ROI と Overlay 経路を分岐する。
3. `SourceWindow`:
   - 既存どおり source基準で OCR/Overlay を構築し、最後に `source->mirror` 変換。
4. `MirrorDesktop`:
   - すでにミラー座標系とみなし、`source->mirror` 変換を無効（identity）。
   - ROI も mirror座標系で処理する。

## 6. インターフェース設計
### 6.1 モデル
- `Models/CaptureSpace.cs`（新規）
  - `enum CaptureSpace { SourceWindow = 0, MirrorDesktop = 1 }`
- `Models/CaptureFrame.cs`（拡張）
  - `public CaptureSpace Space { get; }`
  - 既存 ctor は互換保持し、既定 `SourceWindow` を設定。

### 6.2 判定ロジック
- ミラー無効: 常に `SourceWindow`。
- ミラー有効:
  - `WGC`（window capture）: `SourceWindow`
  - `Dxgi/GDI`: 原則 `MirrorDesktop`（安全側）
  - 将来、厳密判定を足すなら `Magpie scaling window` の可視状態/矩形一致で補強。

### 6.3 オーバーレイ経路
- `frame.Space == SourceWindow` のときのみ `SetScreenRectMapper(_magpieSessionController.MapScreenRect)` を有効。
- `frame.Space == MirrorDesktop` のときは mapper 無効（`null`）。

### 6.4 ROI経路
- `SourceWindow`: 現行維持。
- `MirrorDesktop`: ROIの基準矩形を `frame.Bounds`（ミラー面）として扱う。

## 7. 実装手順（ステップ分割）
### Step 1: 診断ログ強化（先行）
- 追加ログ:
  - `stage=capture_space event=resolved provider=... mirror_active=... space=...`
  - `frame_bounds=... source_client=... monitor=...`
- 目的: 判定誤りを即座に可視化。

### Step 2: CaptureSpace導入
- `CaptureSpace` enum 追加。
- `CaptureFrame` に `Space` を追加（既存呼び出し互換維持）。

### Step 3: CaptureManager で Space 決定
- `CaptureManager`（または `CaptureAttemptCoordinator` 後段）で provider別に `Space` を設定。
- 初期ポリシー:
  - mirror off -> `SourceWindow`
  - mirror on + WGC -> `SourceWindow`
  - mirror on + Dxgi/GDI -> `MirrorDesktop`

### Step 4: Pipeline 分岐
- `PipelineOrchestrator` で `frame.Space` を受け取り、
  - ROI計算基準
  - Overlay mapper 適用有無
  を分岐。

### Step 5: Magpie mapper 適用条件の狭化
- `MainWindow`/`OverlayPresenter` 側で「ミラー有効」だけでなく「`frame.Space == SourceWindow`」を条件に mapper を有効化する。

### Step 6: フォールバック検証
- provider を `WGC -> Dxgi -> GDI` と切り替えて、同一シーンで矩形一致を確認。
- ミラー開始/停止の境界フレームでズレが出ないことを確認。

## 8. 非機能要件チェック
- 性能: 分岐追加のみで、重い画像処理は増やさない。
- セキュリティ: なし（外部入力増加なし）。
- 可観測性: `capture_space` ログで現場切り分けを高速化。
- 互換性: `CaptureFrame` ctor互換を維持し既存呼び出しを壊さない。
- 運用: 判定が不明な場合は `MirrorDesktop`（変換しない）側に倒してズレを防ぐ。

## 9. リスクと緩和策
- Risk: `Dxgi/GDI` でも実際は source基準が取れている環境で、`MirrorDesktop` 固定判定が過剰な可能性。
- Mitigation: 初期は安全側で導入し、ログ実測に応じて「厳密判定（矩形一致）」を後段で追加する。

- Risk: ROI基準切替で scene-change 判定の感度が変わる可能性。
- Mitigation: `Space` ごとに評価ログを出し、既定閾値を別管理できる余地を残す。

## 10. 影響範囲（変更ファイル候補）
- `Models/CaptureFrame.cs`（拡張）
- `Models/CaptureSpace.cs`（新規）
- `Services/CaptureManager.cs`（Space付与）
- `Services/Capture/CaptureAttemptCoordinator.cs`（必要なら）
- `Services/PipelineOrchestrator.cs`（ROI/overlay分岐）
- `MainWindow.xaml.cs`（mapper適用条件の調整）
- `Services/Application/MagpieSessionController.cs`（判定補助情報提供）
- `Doc/`（本計画書 + 追補）

## 11. Definition of Done
- [ ] mirror on + WGC で OCR矩形と overlay が一致。
- [ ] mirror on + Dxgi で OCR矩形と overlay が一致。
- [ ] mirror on + GDI で OCR矩形と overlay が一致。
- [ ] mirror off で既存挙動に回帰なし。
- [ ] フォールバック時に `capture_space` ログで space 変化を追跡可能。
