# Fixed Capture Window Plan (Hotkey-Only)

## 1. 概要（1-3行）
現在の `ActiveWindow` OCR は毎回フォアグラウンドウィンドウを対象にします。  
本計画では「現在フォーカス中ウィンドウの固定/解除」をホットキーで行えるようにします。  
固定/解除ホットキーは既存の他ホットキーと同じ設定導線でユーザー変更可能にします。  
固定失敗時は既存の `ActiveWindow` 動作に自動フォールバックします。

## 2. ゴール / 非ゴール
### ゴール
- ホットキーで現在フォーカス中のウィンドウを固定できる。
- 別ホットキーで固定を解除できる。
- 固定/解除ホットキーを既存ホットキーと同じ方法でユーザー変更できる。
- 固定対象が無効になった場合、ログを出して `ActiveWindow` へ安全に戻る。

### 非ゴール
- 固定対象ウィンドウの専用UI（固定ボタン/一覧/状態パネル）追加。
- 複数固定ウィンドウの同時管理。
- 既存キャプチャ方式（WGC/DXGI/GDI）の再設計。

## 3. 前提・仮定
- 既存実装は各 Provider が `GetForegroundWindow()` を直接参照している。
- ホットキー基盤（`HotkeyManager`）は既に利用中で、追加キー割り当て可能。
- 既存ホットキー（F8/F9/F10/F11）には設定UIと保存導線があり、同方式で拡張できる。
- 固定対象は `HWND` 単体ではなく、再探索用メタ情報（PID/クラス名/タイトル）も保持する必要がある。

## 4. 現状整理
- `CaptureMode` は `Screen` / `ActiveWindow` の2値。
- `CaptureManager` は `CaptureMode` を Provider に渡して実行している。
- `MainWindow` は F8/F9/F10/F11 のホットキーを既に管理している。
- 既存ホットキー設定は `MainWindow` の Hotkeys セクションで変更可能。
- 固定対象の保存/解決ロジックは未実装。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `FixedCaptureWindowSpec`（新規 Model）
  - `Hwnd`, `ProcessId`, `ProcessName`, `ClassName`, `WindowTitle`, `LastSeenUtc`
- `WindowBindingService`（新規 Service）
  - 現在フォーカス中ウィンドウの取得
  - 固定対象の生存確認と再探索
- `CaptureTargetResolver`（`CaptureManager` 内または新規 Service）
  - 今回キャプチャで使う対象 `HWND` を解決

### データフロー / シーケンス
1. ユーザーが固定ホットキー（例: `F12`）を押す。  
2. `WindowBindingService` が現在フォーカス中ウィンドウを取得して設定へ保存。  
3. 実行時、`CaptureManager` が固定対象 `HWND` を優先解決。  
4. Provider が固定対象でキャプチャする。  
5. 無効時は再探索を試み、失敗したら `ActiveWindow` へフォールバック。  
6. 解除ホットキー（例: `Shift+F12`）で固定情報をクリア。

### 既存パターンへの整合
- Provider順序制御（Auto/Fixed）、クールダウン、黒画面判定は既存ロジックを維持。
- 設定永続化は `AppSettings` / `SettingsService` を利用。

## 6. インターフェース設計
### AppSettings 追加案
- `bool EnableFixedCaptureWindow`（既定 `false`）
- `long FixedCaptureWindowHandle`（既定 `0`）
- `int FixedCaptureWindowProcessId`（既定 `0`）
- `string FixedCaptureWindowProcessName`（既定 `""`）
- `string FixedCaptureWindowClassName`（既定 `""`）
- `string FixedCaptureWindowTitle`（既定 `""`）

### ホットキー追加案
- `HotkeyLockCaptureWindowKey`（既定 `F12`）
- `HotkeyLockCaptureWindowModifiers`（既定 `None`）
- `HotkeyUnlockCaptureWindowKey`（既定 `F12`）
- `HotkeyUnlockCaptureWindowModifiers`（既定 `Shift`）
- 既存ホットキーと同じ入力UI（Key + Modifiers）で変更可能にする。

### Provider I/F 変更案
- `ICaptureProvider`
  - `TryGetBounds(CaptureRequest request, out Rect bounds)`
  - `TryCapture(CaptureRequest request, out CaptureFrame frame, out string? error)`
- `CaptureRequest`
  - `CaptureMode Mode`
  - `IntPtr? TargetWindowHandle`

### エラー / バリデーション
- 固定登録失敗: ログ出力し、固定を有効化しない。
- 固定対象無効: `Fixed capture target invalid; fallback to active window.` を出力。
- 解除時: 固定情報をクリアし、通常 `ActiveWindow` に戻す。

## 7. 実装手順（ステップ分割）
### Step 1: 固定対象モデルとWin32解決
- `FixedCaptureWindowSpec` と `WindowBindingService` を追加。
- フォーカス中ウィンドウの `HWND/PID/Class/Title` 取得を実装。

### Step 2: キャプチャ要求I/F拡張
- `ICaptureProvider` を `CaptureRequest` ベースへ移行。
- `WgcCaptureProvider` / `DxgiDuplicationProvider` / `GdiCaptureProvider` を対象 `HWND` 対応に変更。
- `CaptureManager` で `CaptureRequest` を組み立て。

### Step 3: ホットキー導線追加（既存Hotkeys設定へ統合）
- `MainWindow` に固定/解除ホットキーを追加。
- 既存 Hotkeys 設定セクションへ固定/解除の Key/Modifiers 入力を追加。
- `ApplyHotkeySettingsFromUi` / `ApplySettingsToUi` / `TryUpdateHotkeys` の既存導線に統合。
- 固定成功/失敗/解除をログに記録。
- 既存 F8/F10/F11/F9 との競合を防ぐ。

### Step 4: フォールバックと監視性
- 解決失敗時の自動フォールバックを導入。
- `AppLogger` に固定解決イベントを追加。

### Step 5: 検証
- 手動テスト:
  - 固定直後にフォーカスを変えても固定対象をOCRする
  - 固定対象を閉じた後にフォールバックする
  - 解除後に通常 `ActiveWindow` 動作へ戻る

## 8. 非機能要件チェック
- 性能: 追加のWin32照会は1回の実行で軽量に維持。
- セキュリティ: 固定情報はローカル設定のみで外部送信なし。
- 可観測性: 固定/解除/再解決/フォールバックをログで追える。
- 互換性: 固定機能未使用時の挙動を従来どおり維持。
- 運用: 専用固定UIは作らず、Hotkeys設定 + ログで状態を扱う。

## 9. リスクと緩和策
- Risk: `HWND` 再生成で固定先ロスト。  
- Mitigation: `PID + ClassName + Title` で再探索する。

- Risk: 同名ウィンドウ誤認識。  
- Mitigation: PID優先で解決し、曖昧時はフォールバックしてログ通知。

- Risk: 権限境界で取得失敗。  
- Mitigation: 固定を無効化して `ActiveWindow` へ戻す。

## 10. 影響範囲（変更ファイル候補・移行・ドキュメント更新）
- `Models/AppSettings.cs`（固定対象とホットキー設定追加）
- `Services/HotkeyManager.cs`（既存運用に応じた追加配線）
- `Services/ICaptureProvider.cs`（I/F拡張）
- `Services/CaptureManager.cs`（対象解決）
- `Services/WgcCaptureProvider.cs`
- `Services/DxgiDuplicationProvider.cs`
- `Services/GdiCaptureProvider.cs`
- `MainWindow.xaml`（Hotkeys設定項目追加）
- `MainWindow.xaml.cs`（固定/解除ホットキーイベント）
- `Doc/`（運用手順追記）

移行方針:
- 新規設定項目はデフォルト値で後方互換を維持。

## 11. Definition of Done（完了条件）
- [ ] 固定ホットキーで現在フォーカスウィンドウを固定できる。  
- [ ] 解除ホットキーで固定を解除できる。  
- [ ] 固定/解除ホットキーが既存ホットキーと同じUIで変更・保存できる。  
- [ ] 固定中にフォーカス移動しても固定対象をOCRできる。  
- [ ] 固定対象無効時にフォールバックして処理継続できる。  
- [ ] ログのみで状態遷移（固定/解除/失敗）が追跡できる。  
- [ ] 固定機能未使用時は従来挙動と一致する。  
