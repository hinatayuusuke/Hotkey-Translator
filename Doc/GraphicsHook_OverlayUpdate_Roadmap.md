# GraphicsHook OverlayUpdate (Rect-only) Roadmap

## 概要
WPF の Topmost 競合（Magpie 等）を根本回避するため、DX11 Present フック側で「ゲーム内へ合成描画」できる経路を優先して育てる。
当面は **Rect 描画（ROI枠/認識枠/バッジ枠など）** を確実にし、その後に描画品質・コマンドI/F・多API対応へ拡張する。

## 現状（v1 到達点）
- DX11 hook pipeline:
  - HookHost が DLL 注入し、HookAgentDx11 が Present/ResizeBuffers をフック。
  - 共有メモリ `Local\\HT_HOOK_FRAME_<api>_<pid>` に BGRA8 フレームを出力。
- OverlayUpdate v1（Rectのみ）:
  - C# → HookHost: `overlayUpdate`（rectsB64 + count + pid）
  - HookHost → HookAgent: 共有メモリ `Local\\HT_HOOK_CMD_<api>_<pid>` に Rect コマンドを最新上書き
  - HookAgent: Present 内でコマンドを読み、矩形枠を描画
- 追加対応:
  - `IDXGISwapChain1::Present1` もフック（exclusive/flip対策）
  - swapchain が `R8G8B8A8` の場合も RGBA→BGRA swizzle で共有フレーム生成
  - `rectsB64` の JSON エスケープ問題（`\\/`, `\\u002B` 等）を HookHost で unescape

## ゴール / 非ゴール
### ゴール
- hook capture と overlay rect が **安定してゲーム内に表示される**（WPF overlay に依存しない）
- 座標系/DPI/クライアント領域差のズレを最小化し、設定/ROI/認識枠の整合を取る
- 将来の OpenGL/Vulkan 対応に向けて API・プロトコル・命名を拡張しやすい状態に保つ

### 非ゴール（当面）
- テキスト描画（フォント/アウトライン/改行制御/言語）をゲーム内へ完全移植
- DLL アンロードの完全対応（常駐ポリシーを維持しつつ detach で無効化）
- DX12/ゲームごとの独自レンダラの完全網羅

## 次のステップ案（優先度順）

### 1) 座標系/DPI/クロップ整合（最優先）
目的: 「枠が出る」から「枠が正しい位置に出る」へ。
- 問題になりやすい要因
  - DWM の ExtendedFrameBounds / クライアント領域差
  - DPI スケーリング
  - WGC と GraphicsHook の bounds 定義差
- 方針
  - Rect の基準を `frame.Bounds` だけに依存しない
  - HookAgent 側が swapchain/backbuffer のサイズを把握しているので、必要なら
    - クライアント領域（または表示領域）基準の矩形座標系を定義
    - その変換パラメータを HookHost→C# に返す（hookState payload を拡張）

### 2) 描画品質の改善（ClearView→ブレンド可能なライン描画）
目的: v1 の「上書き枠」を、半透明・太さ・AA を扱える描画へ。
- 現状は `ID3D11DeviceContext1::ClearView` による 4辺矩形の塗り。
- 次案
  - 最小の D3D11 パイプライン（動的VB + 単純PS/VS）でライン/矩形を描画
  - 透過（alpha blend）と線幅を扱う
  - 可能なら SRGB も意識（ただし v1 はデバッグ優先）

### 3) Rect コマンド拡張（デバッグ効率向上）
目的: 画面内の状態が一目で分かるようにする。
- 例
  - ROI枠、認識枠
  - scene-change gate の accepted/rejected 枠
  - translation pending/failed の枠
  - 固定ターゲットのクライアント領域枠
- 仕様
  - v2 以降で layer/z-order、色プリセット、表示TTL（一定時間で消す）などを検討

### 4) コマンド I/F を v2 化（JSON/base64 廃止）
目的: 重い/壊れやすい経路を脱却し、遅延と失敗を減らす。
- 現状: C#→HookHost は JSON + base64（文字列抽出/エスケープで落ちうる）
- 次案
  - NamedPipe をバイナリメッセージ化
  - もしくは C# が `HT_HOOK_CMD` を直接書き、HookHost は attach/detach のみ担当

### 5) 更新頻度の最適化（負荷低減）
目的: overlayUpdate の送信/描画の負荷を抑える。
- 例
  - rectが変化した時だけ送信
  - 最大Hz制限（例: 15Hz）
  - 最終コマンド保持（最新のみ上書き）を維持

### 6) 観測性（原因切り分けを速くする）
目的: 「mapping not found」「描画されない」等の原因を即座に判断できるようにする。
- HookAgent→C# へ出したい指標例
  - present_called_count / last_present_qpc
  - 使用された Present/Present1
  - backbuffer format / width/height
  - overlay cmd last updatedQpc / count

## 多API対応（将来）
- 命名は `Local\\HT_HOOK_*_<api>_<pid>` を継続し、DX11以外でも同じI/Fで扱えるようにする。
- HookAgent は `HookAgentDx11`, `HookAgentVulkan`, `HookAgentGl` のように分割し、HookHost は api別の注入/attach を統合。
- UI では「既存方式（WGC/GDI）」「フック方式（DX11/…）」を選択可能にする。

## Definition of Done（Rect overlay 段階）
- GraphicsHook capture が安定して成功し、`HT_HOOK_FRAME` が継続生成される
- overlayUpdate が `overlay_ok` で通り続け、枠がゲーム内に表示される
- 代表的な環境差（DPI、ウィンドウ/フルスクリーン）で座標が破綻しない
