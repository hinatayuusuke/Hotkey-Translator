# Graphics Hook DX9 追加方針（x86対応必須）

## 概要
- DX9 は既存の DX11 / Vulkan と同じ Graphics Hook パイプラインへ統合する。
- 前提として **x86 対応を必須** とし、x64 だけで完了としない。
- 初期段階は capture 先行 で実装し、overlay は段階導入する。

## 前提
- DX9 タイトルは 32bit 実行が多いため、x86 HookAgent/Host 構成を先に成立させる。
- 既存の共有メモリ仕様（Frame/Config/Status/OverlayV2）は API 共通で再利用する。

## 実装方針

### 1) 制御面の API 拡張（先行）
- GraphicsHookApiKind に Dx9 を追加。
- HookHost の attach ルーティングを Dx11 / Vulkan / Dx9 に拡張。
- Capture reader 側の API 許容判定を Dx11/Vulkan から Dx11/Vulkan/Dx9 へ拡張。

### 2) x86/x64 デュアル配布を成立
- HookHost.exe と HookAgentDx9.dll の x86/x64 を用意。
- attach 時にターゲットプロセス bitness を判定し、対応する Host/Agent を選択。
- ログに bitness と選択結果を必ず出す（診断容易化）。

### 3) HookAgentDx9（MVP: capture-only）
- 新規 HookAgentDx9 を追加。
- IDirect3DDevice9::Present と Reset をフック。
- 可能なら IDirect3DDevice9Ex（PresentEx / ResetEx）も同時対応。
- キャプチャは RenderTarget を SystemMemSurface へコピーし、LockRect で BGRA を取得して SharedFrameWriter へ出力。

### 4) 安定化（device lost / reset 追従）
- Alt+Tab / 解像度変更 / フルスクリーン切替でサーフェス再作成を保証。
- reset 後の参照切れを明示ログで検出できるようにする。

### 5) Overlay は第2段階
- 第1段階は WPF overlay 許可で運用（DX9 in-game overlay は未実装）。
- 第2段階で imgui_impl_dx9 を導入し、OverlayV2 テキスト描画を追加。
- Reset 時の ImGui device object 再生成を必須要件にする。

## 推奨ステップ順
1. API 拡張（Dx9 enum / routing / reader）
2. x86/x64 Host/Agent の起動・選択基盤
3. HookAgentDx9 capture-only
4. reset/device-lost 安定化
5. OverlayV2（imgui_impl_dx9）

## 受け入れ条件（最小）
- x86 DX9 タイトルで attach 成功し、フレームが GraphicsHookCaptureProvider で読める。
- x64 DX9 タイトルでも同様に動作する。
- 設定の API=Dx9 で動作し、ログに equested_api=Dx9 と bitness 選択結果が出る。
