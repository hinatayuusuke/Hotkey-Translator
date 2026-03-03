# Topmost Toolbar + Countdown Target Lock Implementation Plan

## 1. 概要
ホットキーが一部アプリで効かないケースに対して、`MainWindow` とは別に「最上位・半透明ツールバー」を追加し、
ボタン操作でオーバーレイ制御と対象ウィンドウ固定を実行できるようにする。

## 2. ゴール / 非ゴール
### ゴール
- 別ウィンドウの半透明ツールバーから以下を操作可能にする。
  - Overlay Toggle（F9相当）
  - Run Once（F8相当）
  - Force Run（F10相当）
  - Text Mode Toggle（F11相当）
  - Lock Target（3秒カウントダウン後にForeground固定）
- カウントダウン中にユーザーが対象アプリへフォーカスを戻し、0秒時点のForegroundを固定できる。

### 非ゴール
- ツールバー上で詳細設定（翻訳エンジン設定、OCR設定）を編集すること
- プロセス/ウィンドウ一覧の高度な選択UI（今回は採用しない）
- 独占フルスクリーン上での完全な入力保証

## 3. 前提・仮定
- 既存の固定ロック処理（`WindowBindingService` + settings反映）を再利用する。
- ツールバーはクリック可能（入力透過しない）。
- 最小実装では位置保存は任意（初期は画面端固定でも可）。

## 4. 現状整理
- ホットキーは `RegisterHotKey + WM_HOTKEY` 方式で動作している。
- 一部アプリのフォーカス中にホットキーが通らないため、代替操作経路が必要。
- 既存機能（F8/F9/F10/F11/F7ロック）は `MainWindow` 側に処理済みで、呼び出し先は存在する。

## 5. 提案アーキテクチャ
### コンポーネント
- `ToolbarWindow`（新規WPF Window）
  - 常時最前面、半透明、最小ボタン群
- `ToolbarController`（新規または `MainWindow.xaml.cs` 内最小実装）
  - ツールバーイベントを既存操作へ委譲
- `CountdownLockService`（新規軽量クラス）
  - 3秒カウントダウン、終了時にForeground取得・固定

### データフロー / シーケンス
1. ユーザーが `Lock Target` をクリック
2. ツールバー上で `3..2..1` 表示
3. 0秒時点で `GetForegroundWindow` を取得
4. 自分自身/無効ウィンドウを除外後、既存固定ロジックで settings 反映 + 保存
5. 成功/失敗をツールバー上ステータスに表示

## 6. インターフェース設計
### Toolbar -> MainWindow ブリッジ（案）
- `Action ToggleOverlay`
- `Func<Task> RunOnceAsync`
- `Func<Task> ForceRunAsync`
- `Action ToggleOverlayTextMode`
- `Func<IntPtr, Task<bool>> LockWindowByHwndAsync`

### CountdownLockService
- `Task StartAsync(TimeSpan delay, Func<IntPtr> getForeground, Func<IntPtr, Task<bool>> lockAction, CancellationToken ct)`
- 失敗理由を enum/string で返却し、表示文言はUI側で決定。

## 7. 実装手順（段階分割）
### Step 1: ツールバーUI骨格
- `ToolbarWindow.xaml(.cs)` 追加
- ボタン5個 + 状態ラベル追加
- `Topmost=true`, 半透明スタイル適用

### Step 2: 既存操作への接続
- `MainWindow` 起動時に `ToolbarWindow` を生成・表示
- 各ボタンを既存の F8/F9/F10/F11 相当処理へ接続

### Step 3: カウントダウン固定
- `Lock Target` クリックで3秒カウント開始
- カウント中にUI更新（`3..2..1`）
- 0秒でForeground取得して固定

### Step 4: ガード・失敗系
- 自ウィンドウを固定対象から除外
- `IntPtr.Zero` や固定不可時の失敗表示
- カウント中キャンセル（任意: ESC/再クリック）

### Step 5: ライフサイクル整理
- MainWindow終了時にToolbarも終了
- 設定保存と表示状態同期

## 8. 非機能要件チェック
- 性能: タイマー更新のみで負荷軽微
- セキュリティ: 外部実行なし、既存ロジック再利用
- 可観測性: 操作ログ（lock start/success/fail）を最小追加
- 互換性: 既存ホットキー処理は温存（補助経路追加）

## 9. リスクと緩和策
- リスク: ツールバークリックで一時的にフォーカスが奪われる
  - 緩和: カウントダウン方式で最終Foregroundを固定
- リスク: 独占フルスクリーン上でツールバー可視性が下がる環境
  - 緩和: MainWindow側にも同等操作を残し二重経路化

## 10. 影響範囲（候補）
- `MainWindow.xaml.cs`（生成・接続・終了処理）
- `UI/ToolbarWindow.xaml`（新規）
- `UI/ToolbarWindow.xaml.cs`（新規）
- `Services/Application` 配下（必要なら `CountdownLockService` 新規）

## 11. Definition of Done
- ツールバーから F8/F9/F10/F11 相当操作が実行できる
- `Lock Target` で3秒後のForegroundを固定できる
- 自ウィンドウ固定など異常系で安全に失敗できる
- MainWindow終了時にツールバーも確実に終了する
