# DX9 Hook Overlay Implementation Plan

## 1. 概要
DX9 Hook は現状 capture-only で、`overlayEnabled` 設定値は受け取るが、`OverlayV2` 読み取りと実描画は未実装である。DX11 / Vulkan と同じ `SharedOverlayV2` を利用しつつ、DX9 では `Present / PresentEx / SwapChain::Present` に追従する Dear ImGui ベースの描画経路を追加する。

## 2. ゴール / 非ゴール
### ゴール
- DX9 Hook で `OverlayV2` テキスト枠と ROI preview 枠を描画できるようにする。
- `F9` 等の既存 overlay ON/OFF と同じ RuntimeConfig で制御できるようにする。
- `Present`, `PresentEx`, `SwapChainPresent` のいずれでも同じ overlay 描画が動くようにする。
- 既存の DX9 capture パイプラインを壊さずに、overlay を best-effort で追加する。

### 非ゴール
- V1 overlay の復活や互換維持。
- D3DX / ID3DXFont ベースの独自描画。
- 今回の段階での fancy animation や DX11/Vulkan と完全同一の debug UI。

## 3. 前提・仮定
- 既存の `SharedOverlayV2` IPC は DX9 でもそのまま使える。
- DX9 backend は現状 vendor されていないため、`imgui_impl_dx9.cpp/.h` の追加が必要。
- `GraphicsHookClientService` は現状 `Dx11/Vulkan` のみ overlay 対応扱いなので、C# 側も DX9 を許可する変更が必要。
- fail fast 方針とし、DX9 overlay 初期化に失敗した場合は capture は継続、overlay のみ無効化する。

## 4. 現状整理
### DX9 現状
- `Native/HookAgentDx9/Dx9PresentHook.cpp`
  - `overlayEnabled` は RuntimeConfig から取得している。
  - `SharedOverlayV2Reader`、overlay block/text blob 保持、ImGui 初期化、描画処理は未実装。
  - `Present / PresentEx / SwapChainPresent` フックは capture 用に既に動いている。
- `Native/HookAgentDx9/CMakeLists.txt`
  - imgui 本体も backend も link していない。
- `Services/Hook/GraphicsHookClientService.cs`
  - `IsHookOverlaySupportedApi()` は `Dx11 or Vulkan` のみで、DX9 では runtime config publish 時に overlay が自動で false へ落ちる。

### DX11 参考点
- `Native/HookAgentDx11/Dx11PresentHook.cpp`
  - `SharedOverlayV2Reader` を持ち、`RefreshOverlayV2Locked()` で seq 更新を読む。
  - `EnsureImGuiLocked()` で context + font + backend を初期化する。
  - `DrawImGuiOverlayV2Locked()` で DrawList 中心に背景枠とテキストを描画する。
  - 実 overlay が来るまでは attach-success indicator を短時間だけ描画する。

### Vulkan 参考点
- `Native/HookAgentVulkan/VulkanPresentHook.cpp`
  - `RefreshOverlayV2Locked()` で v2 payload を読む。
  - `EnsureOverlaySwapchainStateLocked()` と `EnsureImGuiLocked()` で swapchain 追従する。
  - `BuildImGuiOverlayDrawDataLocked()` で canvas -> target size をスケール変換して描画する。

## 5. 提案アーキテクチャ
### 5.1 コンポーネント構成
- C# 側
  - `GraphicsHookClientService` の DX9 overlay 許可
  - 既存 `GraphicsHookOverlayV2CommandWriter` / `GraphicsHookConfigWriter` はそのまま利用
- Native DX9 側
  - `SharedOverlayV2Reader`
  - overlay payload の runtime 保持 (`OverlayV2Header`, `blocks`, `textBlob`, `lastOverlayV2Seq/Qpc`)
  - Dear ImGui DX9 backend 初期化/破棄
  - `DrawImGuiOverlayV2Locked()`
  - attach-success indicator

### 5.2 データフロー
1. C# が DX9 attach 中も `OverlayV2` と RuntimeConfig を publish する。
2. DX9 hook が `Present / PresentEx / SwapChainPresent` の outer-most call で config と overlay payload を refresh する。
3. capture が必要なら既存 capture を行う。
4. overlay が必要なら同フレーム内で backbuffer に ImGui draw data を描く。
5. status writer に `lastCmdQpc`, `lastCmdCount` 等の overlay 状態も publish する。

### 5.3 既存パターンへの整合
- payload 形式は DX11/Vulkan と同じ `OverlayV2` を使う。
- ROI preview は `wrap == 2` / `textLen == 0` の特殊 block として同じ扱いにする。
- テキスト描画は DX11 と同じく DrawList 優先にして、白枠や window border artifact を避ける。

## 6. インターフェース設計
### 6.1 C# 側
- `Services/Hook/GraphicsHookClientService.cs`
  - `IsHookOverlaySupportedApi()` を `Dx9 or Dx11 or Vulkan` に変更。
- 既存 API 変更は不要。

### 6.2 Native DX9 runtime 拡張
`Dx9Runtime` に追加:
- `ipc::SharedOverlayV2Reader overlayV2Reader;`
- `ipc::OverlayV2Header overlayV2Header{};`
- `std::vector<ipc::OverlayTextBlockV2> overlayV2Blocks;`
- `std::vector<std::uint8_t> overlayV2TextBlob;`
- `std::uint64_t lastOverlayV2Seq = 0;`
- `std::uint64_t lastOverlayV2Qpc = 0;`
- `bool hookSuccessIndicatorArmed = false;`
- `bool hookSuccessIndicatorDone = false;`
- `std::uint64_t hookSuccessIndicatorStartQpc = 0;`
- `bool imguiInitialized = false;`
- `ImGuiContext* imguiContext = nullptr;`
- 必要なら font 管理構造 (`OverlayFontSet` 相当)

### 6.3 新規関数
- `bool RefreshOverlayV2Locked(Dx9Runtime& rt)`
- `bool EnsureImGuiLocked(Dx9Runtime& rt, IDirect3DDevice9* device)`
- `void ResetImGuiLocked(Dx9Runtime& rt)`
- `void DrawImGuiOverlayV2Locked(Dx9Runtime& rt, IDirect3DDevice9* device)`
- `void PublishStatusLocked(Dx9Runtime& rt)` の overlay status 拡張

## 7. 実装手順
### Step 1: C# 側で DX9 overlay publish を許可
- `GraphicsHookClientService.IsHookOverlaySupportedApi()` に DX9 を追加する。
- これで RuntimeConfig の `overlayEnabled` と `OverlayV2` publish が DX9 attach 時にも有効になる。

### Step 2: DX9 build に ImGui DX9 backend を追加
- `Native/ThirdParty/imgui/backends/imgui_impl_dx9.cpp/.h` を vendor 追加。
- `Native/HookAgentDx9/CMakeLists.txt` に以下を追加:
  - `SharedOverlayV2.cpp`
  - `imgui.cpp`, `imgui_draw.cpp`, `imgui_tables.cpp`, `imgui_widgets.cpp`
  - `imgui_impl_dx9.cpp`
- include path に `../ThirdParty/imgui` と `../ThirdParty/imgui/backends` を追加。

### Step 3: DX9 runtime に OverlayV2 reader/state を追加
- `Dx9Runtime` に overlay payload の保持領域を追加。
- `RefreshConfigLocked()` で既存 `overlayEnabled` をそのまま使う。
- `RefreshOverlayV2Locked()` で seq 更新時のみ payload を取り込み、real payload 到着後は success icon を止める。

### Step 4: DX9 ImGui 初期化と device reset 追従
- `EnsureImGuiLocked()` を追加して `ImGui::CreateContext()` と `ImGui_ImplDX9_Init(device)` を行う。
- フォントは DX11 に合わせて `meiryo.ttc` 優先で preload する。
- `Reset / ResetEx` の前後で `ImGui_ImplDX9_InvalidateDeviceObjects()` / `ImGui_ImplDX9_CreateDeviceObjects()` を適切に呼ぶ。
- `UninstallPresentHook()` と runtime reset でも backend/context を破棄する。

### Step 5: DrawList ベースの overlay 描画を追加
- `DrawImGuiOverlayV2Locked()` を追加し、DX11 の実装をベースに移植する。
- 方針:
  - 背景枠: `ImGui::GetBackgroundDrawList()->AddRectFilled()`
  - テキスト: `ImGui::GetForegroundDrawList()->AddText()` を既定にする
  - ROI preview: `wrap==2 && textLen==0` は最後に `AddRect()` だけ描く
  - attach-success indicator は payload 未着時のみ短時間描画する
- D3D9 は state 破壊の影響が大きいので、backend 任せにしつつ、必要なら `IDirect3DStateBlock9` の明示 save/restore を追加する。

### Step 6: Present 経路へ overlay を差し込む
- `HookedPresent`, `HookedPresentEx`, `HookedSwapChainPresent` の outer-most path で:
  - `RefreshConfigLocked()`
  - `RefreshOverlayV2Locked()`
  - `CaptureAndShareFrameLocked()`
  - `DrawImGuiOverlayV2Locked()`
  の順に実行する。
- `SwapChainPresent` は device 解決 (`GetDevice`) が必要。取得した device は draw/capture 後に release する。
- capture-only と overlay-only のどちらでも動くように、両者を独立判定にする。

### Step 7: status / diag を DX11/Vulkan に寄せる
- `PublishStatusLocked()` で `lastCmdQpc`, `lastCmdCount`, `reserved0/1` に overlay 情報を出す。
- perf/diag は既存 DX9 のログスイッチに従い、overlay refresh / draw / reset 失敗を必要最小限で出す。

## 8. 非機能要件チェック
### 性能
- overlay payload 未更新時は DrawList 準備を最小化する。
- capture と overlay は同じ Present に載せるが、overlay-only 時は capture を走らせない。

### 可観測性
- `event=overlay_v2_refresh`, `event=overlay_draw`, `event=overlay_reset`, `event=overlay_canvas_mismatch` 程度の diag を用意する。
- DX9 の既存 `perfDiagLogEnabled` と file sink を流用する。

### 互換性
- 既存の capture-only 動作は維持する。
- overlay 初期化失敗時は overlay のみ fail fast で無効化し、capture は継続する。

### 運用
- `F9` / `F8` / `F10` の既存 overlay toggle/run-once ルールは C# 側の publish でそのまま効く。

## 9. リスクと緩和策
- Risk: D3D9 state 汚染で対象タイトルの描画が崩れる。
- Mitigation: ImGui DX9 backend を基本とし、必要なら `IDirect3DStateBlock9` の明示 restore を追加する。まずは overlay feature flag で段階検証する。

- Risk: `Reset / ResetEx` 後に ImGui device object が壊れる。
- Mitigation: reset hook に backend invalidate/recreate を入れ、失敗時は overlay を再初期化待ちに戻す。

- Risk: x86 DX9 タイトルで backend 追加が build / runtime に影響する。
- Mitigation: `HookAgentDx9` の x86/x64 両方を build し、まず x86 Terraria / 吉里吉里系で overlay-only を検証する。

- Risk: DX11/Vulkan と同じ text path をそのまま移すと D3D9 で白枠やにじみが出る。
- Mitigation: 既定は DrawList path に固定し、window path は troubleshooting 用の env switch に留める。

## 10. 影響範囲
- `Services/Hook/GraphicsHookClientService.cs`
- `Native/HookAgentDx9/CMakeLists.txt`
- `Native/HookAgentDx9/Dx9PresentHook.cpp`
- `Native/ThirdParty/imgui/backends/imgui_impl_dx9.cpp`（新規）
- `Native/ThirdParty/imgui/backends/imgui_impl_dx9.h`（新規）

## 11. Definition of Done
- DX9 attach 中でも `OverlayV2` publish が有効になる。
- DX9 タイトルで ROI preview 枠とテキスト overlay が表示される。
- `Present`, `PresentEx`, `SwapChainPresent` の少なくとも 1 経路で overlay が確認できる。
- `Reset / ResetEx` 後も overlay が回復する。
- x86 / x64 の `HookAgentDx9` build が通る。
- capture-only の既存動作が維持される。
