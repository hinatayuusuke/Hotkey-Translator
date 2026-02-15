# DX11 Graphics Hook 詳細実装案（C++含む）

1. **概要（1–3行）**
- 本計画は `DX11` のみを対象に、フック方式で「フレーム取得」と「オーバーレイ描画」を同一 `Present` 経路で実現する詳細実装案である。
- C++ 側（HookHost/HookAgent）を中核にし、C# 側は OCR/翻訳と設定・制御に専念する構成とする。
- 既存 `WGC/DXGI/GDI + WPF Overlay` はフォールバックとして維持し、段階移行する。

2. **ゴール / 非ゴール**
### ゴール
- DX11 タイトルでフレーム取得とオーバーレイ表示を安定動作させる。
- C++ 側のクラッシュ/ハングが対象アプリ全体に波及しにくい構造にする。
- C# 既存パイプラインへ最小侵襲で接続できる IPC 契約を定義する。

### 非ゴール
- DX12/OpenGL/Vulkan の同時実装。
- 抗チート環境への対応保証。
- 初回で GPU 共有パスを完全最適化（まずは CPU readback 許容）。

3. **前提・仮定**
- Windows 10/11 + D3D11 タイトルを対象にする。
- Hook 実装ライブラリは `MinHook` を前提（差替可能）。
- 文字描画は初期段階で簡素化（矩形 + 短文）し、品質改善は次段で行う。

4. **現状整理**
- 現行構成では外部 Topmost オーバーレイ競合が発生し、表示優先が不安定。
- キャプチャは OS API 依存であり、ゲームレンダーと同相での取得ではない。
- DX11 フック導入で、表示競合と取得遅延を同時に改善できる見込みがある。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `HookHost.exe`（C++/x64, 常駐管理）
  - プロセス監視
  - DLL 注入/解除
  - NamedPipe/SharedMemory のハブ
- `HookAgentDx11.dll`（C++/x64, 対象プロセス内）
  - `IDXGISwapChain::Present` フック
  - フレーム取得（CopyResource + Map）
  - オーバーレイ描画（D3D11）
- `Hotkey-Translator (C#)`
  - OCR/翻訳
  - OverlayCommand 送信
  - Hook異常時フォールバック

### 5.2 シーケンス（DX11）
1. C# から HookHost へ `Attach(pid)` を送信。
2. HookHost が対象へ `HookAgentDx11.dll` を注入。
3. HookAgent が `Present` をフックし、初回 `Present` で device/context/swapchain 情報を初期化。
4. HookAgent が最新フレームを共有メモリへ書き込み、`FrameReady(frameId)` を通知。
5. C# が OCR/翻訳を実行し `OverlayUpdate(frameId, commands)` を送信。
6. HookAgent が次回 `Present` 内で commands を描画して本来の `Present` を呼ぶ。
7. 異常時は HookHost が `FallbackRequired` を通知、C# が既存経路へ切替。

6. **インターフェース設計**
### 6.1 IPC 契約（最小）
- Pipe: `\\.\pipe\hotkey_translator_hook`
- Shared Memory:
  - `HT_DX11_FRAME_RING_{pid}`
  - `HT_DX11_OVERLAY_CMD_{pid}`

#### Control Message（JSONまたは固定長バイナリ）
- `AttachRequest { pid, captureFpsLimit, enableOverlay }`
- `DetachRequest { pid }`
- `OverlayUpdate { frameId, commands[] }`
- `HookState { state, reason, api="DX11" }`
- `FrameReady { frameId, width, height, stride, format="BGRA8" }`

### 6.2 OverlayCommand（v1）
- `DrawRect { x,y,w,h, argb, thickness }`
- `DrawText { x,y,w,h, textUtf8, fontPx, fgArgb, bgArgb }`
- `Clear`（描画バッファクリア）

### 6.3 共有メモリヘッダ（案）
- `uint32 magic`
- `uint32 version`
- `uint64 frameId`
- `uint32 width`
- `uint32 height`
- `uint32 stride`
- `uint32 payloadBytes`
- `uint64 timestampQpc`

7. **C++ 実装詳細（HookAgentDx11.dll）**
### 7.1 主要クラス
- `Dx11HookBootstrap`
  - ダミー swapchain 作成で `Present` の vtable を取得
  - MinHook 初期化・適用
- `Dx11PresentInterceptor`
  - `PresentHook` 本体
  - 初期化・再初期化（Resize対応）
- `Dx11FrameCapture`
  - BackBuffer -> StagingTexture コピー
  - `Map(D3D11_MAP_READ)` で CPU バッファ化
- `Dx11OverlayRenderer`
  - RTV 構築
  - Rect/Text 描画
- `IpcBridge`
  - 共有メモリ ring buffer 書込
  - 最新 overlay command の lock-free 参照

### 7.2 フック対象
- `IDXGISwapChain::Present`（必須）
- `IDXGISwapChain::ResizeBuffers`（推奨）

### 7.3 PresentHook の最小処理フロー
1. ガード（再入防止、例外隔離）
2. 初回初期化（device/context/backbuffer format）
3. フレーム取得（レート制限付き）
4. Overlay 描画（enable時のみ）
5. 元の `Present` 呼び出し

### 7.4 C++ 最小コード骨子
```cpp
// NOTE: 骨子。実際にはエラーハンドリングと同期を追加する。
using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
PresentFn g_originalPresent = nullptr;

HRESULT __stdcall HookedPresent(IDXGISwapChain* swap, UINT sync, UINT flags)
{
    __try {
        Dx11Runtime::Instance().OnPresent(swap);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // WHY: Game process crash回避を最優先
    }
    return g_originalPresent(swap, sync, flags);
}

bool InstallPresentHook(void* presentPtr)
{
    if (MH_Initialize() != MH_OK) return false;
    if (MH_CreateHook(presentPtr, &HookedPresent,
        reinterpret_cast<void**>(&g_originalPresent)) != MH_OK) return false;
    return MH_EnableHook(presentPtr) == MH_OK;
}
```

### 7.5 描画（v1）
- 初期版は D3D11 の簡易描画を優先し、テキストは以下いずれかを採用:
  - A: DirectWrite + Direct2D on DXGI surface
  - B: Bitmapフォント（固定グリフ）
- 推奨: 初期は `A`、失敗時は `Rect only` へ劣化。

### 7.6 性能ガード
- Capture は `captureFpsLimit` で間引き（例 15fps）
- Overlay command は「最新のみ」採用
- `Present` 内の重処理上限を設け、超過時はそのフレームの capture/overlay をスキップ

8. **C++ 実装詳細（HookHost.exe）**
### 8.1 責務
- 注入管理（Attach/Detach）
- HookAgent 健全性監視（心拍）
- C# との IPC 終端

### 8.2 注入方式（段階）
- v1: `CreateRemoteThread + LoadLibraryW`（最小）
- v2: 安定化後に injector 抽象化（将来差替可能）

### 8.3 異常時動作
- Attach失敗: 即 `HookState=Failed` を返却
- 心拍停止: `HookState=Lost` -> C# 側にフォールバック要求
- Detach時: フック解除後に DLL unload を試行

9. **C# 側統合詳細**
### 9.1 追加サービス（案）
- `Dx11HookClientService`
  - HookHost 接続
  - `Attach/Detach/OverlayUpdate` API
  - `FrameReady` を `CaptureFrame` へ変換
- `HookCaptureProvider`
  - 既存 `CaptureManager` に provider として追加
- `HookOverlayProvider`
  - 既存 overlay item から `OverlayCommand` へ変換

### 9.2 設定追加（案）
- `EnableDx11HookPipeline: bool`（default false）
- `Dx11HookCaptureFpsLimit: int`（default 15）
- `Dx11HookOverlayEnabled: bool`（default true）
- `Dx11HookFallbackOnError: bool`（default true）

### 9.3 切替ロジック
- 有効時: `HookCaptureProvider` を最優先
- 失敗時: `WGC -> DXGI -> GDI` の既存順へ即時フォールバック
- 回復時: 明示再接続（自動再試行は間隔制限）

10. **実装手順（ステップ分割）**
- Step 1: DX11 Hookの骨組み（C++）
  - HookHost/HookAgent プロジェクト作成
  - Present hook install/uninstall まで実装

- Step 2: フレーム共有（C++ -> C#）
  - Staging readback + shared memory ring
  - C# 受信デコーダ実装

- Step 3: Overlay command（C# -> C++）
  - command schema + 受信/描画
  - Rect描画から開始し、Text描画を追加

- Step 4: 既存Pipeline統合（C#）
  - provider切替
  - 設定UI追加
  - エラーフォールバック

- Step 5: 安定化
  - Resize/Alt+Tab/DeviceLost対応
  - ログ/メトリクス整備
  - 負荷最適化

11. **非機能要件チェック**
- 性能:
  - 1080p時、Hook処理追加コスト平均 < 2ms 目標
  - OCR送出は最大 15fps の間引き
- セキュリティ:
  - IPCメッセージ長上限、バリデーション必須
  - 不正pid attach 拒否
- 可観測性:
  - `hook_attach`, `hook_present`, `hook_capture_drop`, `hook_fallback` ログを標準化
- 互換性:
  - Hook無効時は現行挙動と同一

12. **リスクと緩和策**
- Risk: HookAgent例外で対象プロセスが不安定化。
- Mitigation: `__try/__except` と処理時間ガード、失敗時は処理スキップ優先。

- Risk: ドライバ/タイトル差異で描画が崩れる。
- Mitigation: タイトル別 blacklist、フォールバック自動切替。

- Risk: テキスト描画の実装コストが高い。
- Mitigation: v1 は rect中心 + 短文描画、必要時に段階強化。

13. **影響範囲（変更ファイル候補）**
- 新規（C++）
  - `Native/HookHost/*`
  - `Native/HookAgentDx11/*`
  - `Native/HookCommon/*`
- 新規/変更（C#）
  - `Services/Capture/HookCaptureProvider.cs`
  - `Services/Overlay/HookOverlayProvider.cs`
  - `Services/Hook/Dx11HookClientService.cs`
  - `Models/AppSettings.cs`
  - `ViewModels/SettingsViewModel.cs`
  - `MainWindow.xaml`

14. **Definition of Done**
- [ ] DX11タイトルで frame 取得が実機確認できる。
- [ ] Rect/Text の overlay command が対象ウィンドウ内に描画される。
- [ ] Hook失敗時に既存キャプチャ経路へ自動フォールバックする。
- [ ] Hook無効時に既存挙動へ回帰がない。
- [ ] ログで attach/present/capture/fallback が追跡可能。
