# DX9 Graphics Hook 2段階 off-Present publish 実装案

1. **概要（1–3行）**
- 本書は DX9 hook に対して、DX11/Vulkan と同じ方向で `Present` hot path の負荷を下げるための 2 段階実装案を整理したものである。
- 第1段階は `LockRect` 後の CPU publish を worker へ逃がすこと、第2段階は `stagingSurface` を ring 化して capture issue と publish を重ねることにある。
- DX9 は DX11/Vulkan ほど素直な delayed readback 手段を持たないため、まずは「CPU publish を外す」ことを優先する。

2. **ゴール / 非ゴール**
### ゴール
- `Present` / `PresentEx` / `SwapChain::Present` から `memcpy + SharedFrameWriter.WriteFrame(...)` を外す。
- DX11/Vulkan と同じく Win32 publish worker ベースで lifecycle を揃える。
- 第2段階で `stagingSurface` ring を導入し、publish 中でも次の capture issue ができる余地を作る。

### 非ゴール
- 初手から DX9 を完全 delayed readback 化すること。
- shared texture 契約への移行。
- C# consumer 側契約の変更。

3. **前提・現状整理**
- 現行 [Dx9PresentHook.cpp](/g:/Local%20App/Hotkey-Translator/Native/HookAgentDx9/Dx9PresentHook.cpp) は `GetRenderTargetData -> LockRect -> SharedFrameWriter.WriteFrame(...) -> UnlockRect` を `Present` 系 hook 内で完結している。
- `stagingSurface` は単発で、publish 中に次の capture を並列に仕込めない。
- DX9 では `Map(DO_NOT_WAIT)` や Vulkan の persistent mapped staging のような素直な ready 判定が無いため、DX11/Vulkan よりも「publish 分離」の価値が先に立つ。

4. **第1段階: Win32 publish worker で CPU publish を Present 外へ逃がす**
### 目的
- `LockRect` 後の `memcpy` と `SharedFrameWriter.WriteFrame(...)` を `Present` 外へ出し、CPU 側の tail latency を下げる。

### 方針
- DX11/Vulkan と同じく `std::thread` ではなく Win32 thread handle + wake/stop/idle event を使う。
- `Present` 側は `GetRenderTargetData` と `LockRect` 成功までを担当し、`locked.pBits` / pitch / width / height / format 情報を request として queue に積む。
- worker は request を pop して `scratch` へ copy、必要なら pixel format 変換後に `SharedFrameWriter.WriteFrame(...)` を行う。
- `UnlockRect` と slot 解放は worker 完了通知後に hook thread 側 cleanup で行う。

### データ構造の最小案
- `CaptureSlotState`
  - `Free`
  - `LockedReadyToPublish`
  - `Publishing`
- `Dx9PublishRequest`
  - `CaptureSlot* slot`
  - `DWORD pid`
  - `std::uint64_t frameId`
  - `std::uint64_t publishQpc`
  - `std::uint32_t width`
  - `std::uint32_t height`
  - `std::uint32_t stride`
  - `std::size_t bytes`

### 終了設計
- reset / device lost / uninstall 前に publish queue を drain する。
- process exit は DX11/Vulkan と同じく `DllMain` から graceful shutdown を強制せず、Win32 worker にして destructor abort を避ける。

### 期待効果
- `Present` 内の CPU payload copy と shared memory write が消える。
- DX9 でも「まず CPU publish を外す」という最低限の off-Present 化ができる。

### 制約
- `GetRenderTargetData` と `LockRect` 自体はまだ `Present` 内に残る。
- したがって改善幅は DX11/Vulkan より小さい可能性がある。

5. **第2段階: stagingSurface の ring 化**
### 目的
- 1 枚の `stagingSurface` に依存せず、publish 中の surface と次の capture 用 surface を分離する。

### 方針
- 単発の `stagingSurface` を `N=2 or 3` の ring にする。
- 各 slot は `stagingSurface`, `lockedRect`, `width`, `height`, `format`, `state`, `frameSeq` を持つ。
- `Present` 側は free slot を選んで `GetRenderTargetData` と `LockRect` を行い、ready slot を worker に渡す。
- worker 完了後に completed slot を `UnlockRect` して `Free` に戻す。

### 状態遷移
- `Free`
- `Capturing`
- `LockedReadyToPublish`
- `Publishing`

### backlog ポリシー
- ring が埋まった場合は最古 slot を捨てて最新優先に寄せる。
- WHY: OCR 用途では古いフレームの完全保全より最新性が重要だから。

### 期待効果
- 前フレーム publish 中でも次フレームの capture issue を試せる。
- 第1段階よりも `Present` の待ちをさらに分散できる。

### 制約
- DX9 の `GetRenderTargetData` 自体が同期寄りなため、ring 化しても GPU readback 待ちが完全には消えない。
- それでも CPU publish と surface 所有権を分離できるので、1% low 改善の余地はある。

6. **実装順序**
- Step 1: DX9 runtime に Win32 publish worker 基盤を追加する
- Step 2: 現行単発 `stagingSurface` で `LockRect` 後 publish を enqueue 化する
- Step 3: reset / device lost / uninstall の drain を入れる
- Step 4: `stagingSurface` を ring 化し、slot state を拡張する
- Step 5: ring 詰まり時の drop policy と perf log を追加する

7. **リスクと緩和策**
- Risk: `LockRect` / `UnlockRect` 所有権が崩れる
- Mitigation: `UnlockRect` は completed cleanup に限定し、state 遷移を単方向にする

- Risk: device lost / reset 中に worker が古い surface を触る
- Mitigation: drain 後にのみ surface を解放し、必要なら generation を持たせる

- Risk: 第1段階だけでは改善幅が小さい
- Mitigation: 初手は publish 分離だけを小さく入れ、第2段階の ring 化で重なりを増やす

8. **Definition of Done**
- [ ] 第1段階で `Present` 系 hook から `SharedFrameWriter.WriteFrame(...)` が外れている
- [ ] 第1段階で worker drain が reset / uninstall で成立する
- [ ] 第2段階で `stagingSurface` ring が導入されている
- [ ] publish 中でも次の capture issue が可能になっている
- [ ] device lost / Alt+Tab / uninstall でクラッシュや解放漏れがない
