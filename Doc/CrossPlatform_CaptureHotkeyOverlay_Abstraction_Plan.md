# CrossPlatform Capture/Hotkey/Overlay Abstraction Plan

1. **概要（1–3行）**
- 本計画は、`Capture / Hotkey / Overlay` を OS 依存実装から分離し、将来のマルチプラットフォーム対応を可能にするための大枠設計である。
- 方式は `Ports & Adapters (Hexagonal)` を採用し、Application/Core が Windows 固有型に依存しない構造へ寄せる。
- 既存 Windows 実装は即時置換せず、まずはアダプタ化して互換挙動を維持する。

2. **ゴール / 非ゴール**
### ゴール
- `Capture / Hotkey / Overlay` の契約（interface）を `Abstractions` 層へ集約する。
- Core/Application から `System.Windows.*` / HWND / Win32 呼び出しを排除する。
- Windows 実装をアダプタとして残しつつ、Linux/macOS 向け差し替え可能な構造を作る。

### 非ゴール
- 1回の変更で Linux/macOS をフル実装すること。
- OCR/翻訳アルゴリズムの変更。
- UI フレームワーク（WPF/Avalonia/Tauri）の最終決定。

3. **前提・仮定**
- 現行アプリは WPF + Windows API 依存であり、特に Global Hotkey と Overlay 表示が OS 密結合。
- 既存リファクタで Application/Orchestration の責務分離は進行しており、次段で I/O 境界を切り出しやすい。
- マルチプラットフォーム移行は段階的に行い、まず Windows の挙動互換を固定する。

4. **現状整理**
- `Capture`: DXGI/WGC/GDI の選択・フォールバック・ROI が Windows 前提。
- `Hotkey`: HWND を使ったグローバル登録/解除が View 寄りコードに残る。
- `Overlay`: WPF Window/Canvas 直結で、描画データと描画手段が同層にある。
- 問題: Core 側の利用コードが OS/GUI 型に引きずられ、移植時に全面書き換えになりやすい。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `Hotkey-Translator.Abstractions`（新規）
  - `ICaptureService`
  - `IGlobalHotkeyService`
  - `IOverlayService`
  - `Platform-neutral DTO`（Rect, Point, Frame, OverlayItem など）
- `Hotkey-Translator.Infrastructure.Windows`（既存実装を移送）
  - `WindowsCaptureService`（DXGI/WGC/GDI を内包）
  - `WindowsGlobalHotkeyService`
  - `WindowsOverlayService`
- `Hotkey-Translator.Application`
  - 上記 interface のみ参照
  - 実装選択は Composition Root（起動時 DI）で注入

### 5.2 データフロー / シーケンス
1. Application は `ICaptureService.CaptureAsync(...)` を呼ぶ。
2. 返却 `CaptureFrame` を OCR/差分/翻訳へ流す（OS 型なし）。
3. Hotkey は `IGlobalHotkeyService` のイベントで Command を起動。
4. Overlay は `IOverlayService.Update(items)` に `OverlayItemDto` を渡す。
5. 実際の描画や OS API 呼び出しは各 Platform Adapter が担当。

### 5.3 既存パターンへの整合
- 既存 `Controller` / `Orchestrator` は維持し、依存先を具象から interface へ置換する。
- `MainWindow` は View 役割（Window ライフサイクル）に限定し、OS API 直呼び出しを段階的に削減する。

6. **インターフェース設計（大枠）**
### 6.1 Capture
- `ICaptureService`
  - `Task<CaptureFrame?> CaptureAsync(CaptureRequest request, CancellationToken ct)`
  - `CaptureBounds GetBounds(CaptureTarget target)`
- `CaptureFrame`
  - `Width`, `Height`, `PixelFormat`, `byte[] Buffer`, `TimestampUtc`
- 禁止事項
  - interface 層に `Bitmap`, `System.Windows.Rect`, `HWND` を出さない。

### 6.2 Hotkey
- `IGlobalHotkeyService`
  - `bool Register(HotkeyBinding binding)`
  - `void UnregisterAll()`
  - `event EventHandler<HotkeyPressedEventArgs> Pressed`
- `HotkeyBinding`
  - `Id`, `KeyCode`, `Modifiers`, `Scope(Global/Foreground)`

### 6.3 Overlay
- `IOverlayService`
  - `void SetEnabled(bool enabled)`
  - `void Show()` / `void Hide()`
  - `void Update(IReadOnlyList<OverlayItemDto> items)`
  - `void ShowLoading(OverlayAnchor anchor)` / `void HideLoading()`
- `OverlayItemDto`
  - `RectD Bounds`, `string Text`, `StyleKey`, `Confidence`, `Metadata`

7. **実装手順（ステップ分割）**
- Step 1: 契約と DTO を追加（`Abstractions` 新設）
  - 型を platform-neutral に統一。
  - 既存 Core から直接参照している Windows 型を棚卸し。

- Step 2: Windows Capture をアダプタ化
  - 現行 `CaptureManager` 内の provider 呼び出しを `WindowsCaptureService` に移送。
  - 既存挙動（provider順序、cooldown、black判定）は保持。

- Step 3: Hotkey をサービス境界へ移送
  - HWND 依存部分のみ `WindowsGlobalHotkeyService` に隔離。
  - Application 側はイベント購読で動作。

- Step 4: Overlay を描画サービス境界へ移送
  - `OverlayPresenter/OverlayWindow` を `WindowsOverlayService` 背後へ集約。
  - Application は DTO 投入のみ担当。

- Step 5: Composition Root 置換
  - `MainWindow` 起動時に `Windows*Service` を `Abstractions` へ注入。
  - 呼び出し側の具象依存を段階的に削除。

- Step 6: 非Windows向け stub 実装追加
  - `NoopGlobalHotkeyService` などでコンパイル成立を先に達成。
  - 後続で macOS/Linux 実装を段階追加。

8. **非機能要件チェック**
- 性能
  - Capture/Overlay パスで余分なコピーを増やさない（フレーム複製を最小化）。
- 可観測性
  - すべての Adapter 呼び出しに `stage=platform_adapter` 系ログを追加。
- 互換性
  - 既存 `settings.json` 互換を維持。
- 運用
  - 失敗時は Windows 既存実装へ戻せる feature flag を一時保持。

9. **リスクと緩和策**
- Risk: DTO 変換時の座標系（DPI/正規化）ズレで Overlay 位置がずれる。
- Mitigation: 座標変換責務を1箇所に集約し、ゴールデン画像で差分確認する。

- Risk: Hotkey 実装差異で OS ごとの挙動が不一致。
- Mitigation: `Scope` と失敗時ポリシー（ログのみ/再試行）を契約で明文化する。

- Risk: 変換層追加で複雑化し、逆に保守性が下がる。
- Mitigation: Stepごとに「削除できた Windows 直依存」を計測し、純増コードを抑制する。

10. **影響範囲（変更ファイル候補）**
- 新規
  - `Abstractions/ICaptureService.cs`
  - `Abstractions/IGlobalHotkeyService.cs`
  - `Abstractions/IOverlayService.cs`
  - `Abstractions/Dto/*.cs`
- 既存（段階移送）
  - `MainWindow.xaml.cs`
  - `Services/CaptureManager.cs`
  - `Services/Application/HotkeyController.cs`
  - `Services/OverlayPresenter.cs`
  - `UI/OverlayWindow.xaml.cs`

11. **Definition of Done**
- [ ] Application/Core が `System.Windows.*` と Win32 API に直接依存しない。
- [ ] `Capture/Hotkey/Overlay` が interface 経由で注入される。
- [ ] Windows 実装で現行主要シナリオ（Run/Hotkey/Overlay）が互換動作する。
- [ ] 非Windows stub でビルドが通る。
- [ ] 移行ガイド（どこまで抽象化済みか、未対応機能は何か）が Doc 化されている。
