# GraphicsHook: ImGui Translation Overlay v2 Spec (IPC + Coordinate Contract)

目的: OCR が提供する座標に合わせて、ゲーム内へ「半透明パネル + 翻訳テキスト」を合成描画する。
将来の Vulkan/OpenGL 対応を見据え、描画バックエンド差分は最小化し、IPC コマンド契約を共通化する。

本ドキュメントは `Doc/GraphicsHook_ImGui_TranslationOverlay_Plan.md` を実装しやすい形に落とした補助仕様書である。

## Goals
- HookAgent が Present 内で Dear ImGui により半透明パネルとテキストを描画できる。
- 表示座標は OCR/WPF overlay と同一の「画面上の位置」を基準にしつつ、Hook 描画は backbuffer ピクセル座標で安定させる。
- IPC は「最新のみ」上書きで破綻しない。Hook 側の失敗はゲームを落とさない。
- Vulkan/OpenGL でも同じ v2 コマンド契約を再利用できる。

## Non-Goals (v2初期)
- クリック/ドラッグ/コピー等の入力 UI。
- 高度な文字組 (禁則、Harfbuzz shaping 完備、複雑合字、双方向など)。
- DX12 対応。

## Terms
- ScreenRect: 画面座標 (screen pixel)。WPF overlay と同じ座標系。
- FrameBounds: キャプチャ対象ウィンドウの client area を screen 座標で表した矩形。
  - 既存コードでは `Services/GraphicsHookCaptureProvider.cs` が client rect を優先して算出している。
- Canvas: HookAgent が描画する backbuffer のピクセル座標 (左上原点、単位は pixel)。
- Writer: C# 側 (Hotkey-Translator) が共有メモリへコマンドを書き込む側。
- Reader: HookAgent (ゲームプロセス内) が共有メモリからコマンドを読む側。

## Coordinate Contract
### Rule 1: v2 コマンド座標は Canvas (backbuffer pixel) を使用する
WHY: HookAgent は DPI/Window 座標や DWM を知らない方が安全で、Vulkan/OpenGL でも同じ契約を維持しやすい。

Writer は OCR が返す ScreenRect を、FrameBounds と Canvas サイズを使って Canvas へ変換してから v2 に書く。

### Rule 2: ScreenRect -> CanvasRect 変換
入力:
- `screenRect`: OCR/OverlayItem の矩形 (screen)
- `frameBounds`: capture frame の境界 (screen, client area)
- `canvasW/canvasH`: hook frame bitmap のピクセルサイズ (backbuffer 相当)

変換:
- `scaleX = canvasW / frameBounds.Width`
- `scaleY = canvasH / frameBounds.Height`
- `left   = (screenRect.X - frameBounds.X) * scaleX`
- `top    = (screenRect.Y - frameBounds.Y) * scaleY`
- `right  = (screenRect.X - frameBounds.X + screenRect.Width)  * scaleX`
- `bottom = (screenRect.Y - frameBounds.Y + screenRect.Height) * scaleY`
- clamp: `[0..canvasW]`, `[0..canvasH]`

NOTE: 現状 v1 の `Services/PipelineOrchestrator.cs TryBuildHookRect(...)` が同種の変換を実装している。

### Rule 3: v2 更新を発行する条件
- Hook capture が有効で、Canvas サイズが確定している場合のみ v2 を更新する。
- Hook pipeline が fallback で別 provider を使っている場合、v2 はクリア (textBlockCount=0) する。

## IPC v2 (Shared Memory Mapping)
### Recommended Constants (v2初期)
- Mapping bytes: `256 * 1024`
- Header magic (ASCII): `"HOV2"`
  - u32 little-endian value example: bytes `48 4F 56 32` => `0x32564F48`
- Version: `2`
- Max text blocks: 64 (まずは 1 から開始し、上限だけ決めておく)

### Mapping Name
- v1: `Local\\HT_HOOK_CMD_<api>_<pid>` (Rect-only)
- v2: `Local\\HT_HOOK_OVL_<api>_<pid>` (Text + Panel)

`<api>` は `Native/HookCommon/HookIpcProtocol.h GraphicsApi` の数値を使用する。

### Mapping Size
推奨: 256KB 固定。
- WHY: 翻訳テキストは 64KB だと容易に溢れる。固定サイズのまま余裕を持たせる方が運用が楽。

### Update Semantics
v2 は `updatedSeq` を採用する。
- Writer は payload を書いた後、最後に header の `updatedSeq` をインクリメントして書く。
- Reader は `updatedSeq` が前回と同じなら描画更新を省略できる。

NOTE: v1 は `updatedQpc` を使っているが、v2 ではプロセス間の QPC 同期を前提にしない。

### Binary Layout (Little-endian, packed)
メモリ先頭から順に配置する。
1. `OverlayV2Header`
2. `TextBlockV2[textBlockCount]`
3. `textBlob[textBytes]` (UTF-8)

#### OverlayV2Header (Pack=1)
提案フィールド:

| Field | Type | Notes |
| --- | --- | --- |
| magic | u32 | `"HOV2"` など固定値 |
| version | u32 | `2` |
| api | u32 | GraphicsApi 数値 |
| targetPid | u32 | 対象 PID |
| updatedSeq | u64 | 単調増加。Writer が更新ごとに +1 |
| canvasW | u32 | backbuffer width (pixel) |
| canvasH | u32 | backbuffer height (pixel) |
| textBlockCount | u32 | `TextBlockV2` の個数 |
| textBytes | u32 | UTF-8 blob のバイト数 |
| flags | u32 | 予約 (将来) |
| reserved0 | u32 | 予約 |

#### TextBlockV2 (Pack=1)
最初は「翻訳 1 ブロック」から開始し、安定後に複数を許可する。

| Field | Type | Notes |
| --- | --- | --- |
| x,y,w,h | f32 x4 | Canvas 座標 (pixel) |
| paddingPx | f32 | 内側余白 |
| roundingPx | f32 | 角丸 |
| fontPx | f32 | フォントサイズ |
| fgArgb | u32 | 文字色 (ARGB) |
| bgArgb | u32 | 背景色 (ARGB, alpha含む) |
| wrap | u32 | 0/1。1なら幅 `w` 内で折り返し |
| textOffset | u32 | textBlob 内 offset |
| textLen | u32 | UTF-8 byte length |
| zOrder | i32 | 予約。大きいほど前面 (将来) |

### Reference Structs (Draft)
C++ (example):
```cpp
#pragma pack(push, 1)
struct OverlayV2Header
{
    std::uint32_t magic;
    std::uint32_t version;
    std::uint32_t api;
    std::uint32_t targetPid;
    std::uint64_t updatedSeq;
    std::uint32_t canvasW;
    std::uint32_t canvasH;
    std::uint32_t textBlockCount;
    std::uint32_t textBytes;
    std::uint32_t flags;
    std::uint32_t reserved0;
};

struct TextBlockV2
{
    float x, y, w, h;
    float paddingPx;
    float roundingPx;
    float fontPx;
    std::uint32_t fgArgb;
    std::uint32_t bgArgb;
    std::uint32_t wrap;
    std::uint32_t textOffset;
    std::uint32_t textLen;
    std::int32_t zOrder;
};
#pragma pack(pop)
```

C# (example):
```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct OverlayV2Header
{
    public uint Magic;
    public uint Version;
    public uint Api;
    public uint TargetPid;
    public ulong UpdatedSeq;
    public uint CanvasW;
    public uint CanvasH;
    public uint TextBlockCount;
    public uint TextBytes;
    public uint Flags;
    public uint Reserved0;
}
```

### Writer Rules (C#)
- Writer は `textOffset/textLen` が blob 範囲内であることを保証する。
- text は UTF-8 とし、`textLen` は必ず文字境界で切る。
- `textBytes` が mapping 容量を超える場合:
  - 方針A: テキスト末尾を truncate し、`textLen/textBytes` を縮める。
  - 方針B: 1ブロックのみ許可し、溢れたらブロック自体を無効化する。
- Writer は payload を先に書き、最後に header を書く。

擬似コード:
```text
write(textBlob)
write(textBlocks)
memory_barrier()
header.updatedSeq = header.updatedSeq + 1
write(header)
memory_barrier()
```

### Reader Rules (HookAgent)
- header の magic/version/api/pid を検証し、不正なら描画しない。
- `updatedSeq` が未更新なら前回の描画状態を維持する (最新のみ)。
- header 読み取り後、payload を読み取り、必要なら header を再読して `updatedSeq` が変わっていないことを確認する。

擬似コード:
```text
seq0 = read(header.updatedSeq)
if seq0 == lastSeq: return
read(payload)
seq1 = read(header.updatedSeq)
if seq1 != seq0: drop_this_frame (race)
lastSeq = seq0
```

## Rendering (HookAgent, Dear ImGui)
### Why ImGui Window (v2初期)
ImDrawList の `AddText` 単体だと折り返しやレイアウト制御が面倒になりやすい。
v2初期は「表示のみ + 折り返し」が要件なので、フルスクリーンの透明 ImGui window 内で `TextUnformatted` と `PushTextWrapPos` を使うのが実装が早い。

### Frame Loop (Present)
1. `EnsureDevice/Context` (既存)
2. `EnsureImGuiInitialized` (device/context/backbuffer 確定後に1回)
3. `ReadOverlayV2` (updatedSeq で更新検知)
4. `RenderOverlayV2` (ImGui)
5. 元の Present を呼ぶ

### Device State Save/Restore (必須)
現状 v1 の Rect 描画は `ClearView` を使い、パイプライン状態をほぼ汚さない設計になっている。
ImGui は広範囲の state を変更するため、v2 は D3D11 の state を退避/復帰して「タイトル破壊」を避ける。

最低限退避したい候補:
- OM: render targets, depth-stencil, blend state
- RS: rasterizer state, viewports, scissor rects
- IA: input layout, vertex/index buffer, primitive topology
- VS/PS/GS/HS/DS/CS: shader と resources, samplers, constant buffers

SECURITY/SAFETY: state restore を失敗してもゲームプロセスを落とさない。例外は SEH で飲み、overlay を無効化できるようにする。

### Alpha Blend
背景パネルは `bgArgb` の alpha を反映して半透明にする。
- DX11 backend の blend state を適切に設定する必要がある (ImGui backend が通常セットする)。

### Font
v2初期:
- default font でまず動作確認

日本語が必要になったら:
- HookAgent DLL の相対パスから TTF を読み `AddFontFromFileTTF` を使用
- フォントファイル同梱のポリシーを決める (サイズ、ライセンス、更新)

## C# Integration Notes
### Composer (推奨責務)
- `OverlayItem` (screen rect) + 翻訳テキストから `TextBlockV2` を生成する。
- ROI固定モード (`EnableFixedRoiOverlay`) の場合は 1ブロックへ集約しても良い。

### Throttling
推奨: v2 の writer は 5-15Hz へ間引き可能にしておく。
- WHY: OCR/翻訳の更新頻度が高いと IPC 書き込みと ImGui レイアウトコストが増える。

## Compatibility
- v1 (`HT_HOOK_CMD`) は残す。v2 が壊れても v1 へ戻せる。
- HookHost の pipe JSON 経路は attach/detach のみに寄せ、overlay 更新は共有メモリへ直接書き込む設計を推奨する。

## Implementation Steps (Suggested)
1. `HookIpcProtocol.h` に v2 定義を追加し、`HT_HOOK_OVL` の命名を確定する。
2. v2 writer/reader を追加する (C++/C#)。
3. HookAgentDx11 に ImGui を組み込み、固定の半透明パネルを描画できるようにする。
4. v2 を読み、TextBlock 1つを描画する。
5. C# pipeline と接続し、OCR座標に追従することを確認する。
6. ResizeBuffers/Alt+Tab を含む安定化を行う。

## Verification Checklist
- Windowed/Borderless/Exclusive fullscreen で表示できる。
- DPI 125%/150% でも OCR と overlay の位置が一致する。
- 翻訳連打や auto-translate でも「最新のみ」で破綻しない。
- Hook 側例外発生時にゲームが落ちず、overlay が無効化またはスキップされる。
