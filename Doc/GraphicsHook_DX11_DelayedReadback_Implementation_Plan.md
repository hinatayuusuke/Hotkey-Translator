# DX11 Graphics Hook 遅延 Readback 実装案

1. **概要（1–3行）**
- 本計画は `DX11` hook の `Present` 上で行っている同期 readback を、複数 staging を使った遅延 readback に置き換える実装案である。
- 目的は平均 FPS ではなく、`1% low` を悪化させる `Map(D3D11_MAP_READ)` 起因の spike を減らすことにある。
- 現行の共有メモリ出力と C# 側契約は極力維持し、まずは `HookAgentDx11.dll` 内の capture パスだけを改修対象にする。

2. **ゴール / 非ゴール**
### ゴール
- `Present` フレーム内で GPU 完了待ちを起こしにくい capture 経路へ移行する。
- `GraphicsHookCaptureFpsLimit=1` でも 1 秒に 1 回の capture spike が `1% low` を崩しにくい構造にする。
- 既存の `SharedFrameWriter` / C# 側 `GraphicsHookCaptureProvider` を大きく壊さず段階導入できるようにする。

### 非ゴール
- GPU shared texture への全面移行。
- DX12 / Vulkan / DX9 への同時展開。
- OCR 側や C# 側 consumer の根本見直し。

3. **前提・仮定**
- 問題の本体は `Present` 内の `CopyResource -> Map(D3D11_MAP_READ)` 同期待ちである。
- 数フレーム古い画像でも OCR 品質上は許容できる。
- 共有メモリへ出すフレームは「最新必須」ではなく「十分新しいフレーム」で成立する。

4. **現状整理**
- 現行 `CaptureAndShareFrameLocked` は `Present` 内で backbuffer を staging にコピーし、その場で `Map` して CPU へ読み出している。
- `GraphicsHookCaptureFpsLimit` を下げても、capture 実行フレームでは数 ms の spike が残り、60 FPS 固定タイトルの `1% low` に直撃する。
- steady state の perf log では `capture_ms` が `p99/max` を支配し、`lock_wait` や `status` は主要因ではない。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `HookAgentDx11.dll`
  - `Dx11CaptureRing`
    - staging texture を複数枚保持
    - slot の状態管理（free / pending / ready）
  - `Dx11CaptureScheduler`
    - `Present` ごとに copy と readback の役割を分離
  - `SharedFrameWriter`
    - 既存の共有メモリ writer を再利用

### 5.2 シーケンス
1. `Present` で capture 対象フレームに達したら、free slot の staging に `CopyResource` だけ発行する。
2. その slot は `pending` とし、同フレームでは `Map` しない。
3. 次以降の `Present` で `pending` slot のうち「古いもの」から `Map` を試す。
4. `Map` がすぐ通る slot だけ CPU バッファ化し、共有メモリへ書き込む。
5. `Map` がまだ重い / 失敗する slot はそのフレームでは見送り、`Present` を優先する。

6. **インターフェース設計**
### 6.1 外部 I/F
- C# 側共有メモリ契約は変更しない。
- `FrameHeader` と `GraphicsHookCaptureProvider` の読み取り方式は維持する。

### 6.2 内部データ構造（案）
- `CaptureSlot`
  - `ID3D11Texture2D* Staging`
  - `UINT Width`
  - `UINT Height`
  - `DXGI_FORMAT Format`
  - `std::uint64_t FrameId`
  - `std::uint64_t CopyIssuedQpc`
  - `enum State { Free, Pending, ReadyToMap }`

### 6.3 設定
- v1 では設定追加なし。
- 実装切替用に内部 env var を置く場合のみ:
  - `HT_HOOK_CAPTURE_RING_SIZE`（default 3）
  - `HT_HOOK_DISABLE_DELAYED_READBACK`（診断用）

7. **実装詳細**
### 7.1 基本方針
- `Present` で重いのは `Map` 側なので、まず `CopyResource` と `Map` を別フレームへ分離する。
- 直近フレームの即時性より、`Present` の tail latency を優先する。
- free slot がなければ capture をスキップし、`Present` を止めない。

### 7.2 `Present` フロー変更
現行:
1. `GetBuffer`
2. `EnsureStaging`
3. `CopyResource`
4. `Map`
5. `memcpy`
6. `WriteFrame`

変更後:
1. `GetBuffer`
2. free slot を探す
3. free slot があれば `CopyResource` を発行して `pending`
4. 一番古い `pending` slot を 1 つだけ readback 対象に選ぶ
5. 即座に `Map` できた場合だけ `WriteFrame`
6. できなければそのフレームはスキップ

### 7.3 staging 管理
- 初期実装は ring size = 3 を推奨
- backbuffer の `Width/Height/Format` が変わったら全 slot を破棄して再作成
- `ResizeBuffers` と Alt+Tab では現行 `ResetDeviceStateLocked` と同じタイミングで ring を破棄する

### 7.4 共有メモリ書き込み
- 既存 `SharedFrameWriter` をそのまま使う
- `frameId` は「共有メモリへ publish した回数」で進める
- copy を発行しただけの slot では `frameId` を進めない

### 7.5 失敗時挙動
- free slot 不足: そのフレームの capture をスキップ
- `Map` 失敗: slot は次フレームで再試行、一定回数超過なら破棄
- format 不一致 / resize 検出: ring 全破棄・再初期化

8. **実装手順（ステップ分割）**
- Step 1: `CaptureSlot` / ring 管理構造を `Dx11PresentHook.cpp` に追加
- Step 2: 単一 staging 実装を ring 実装へ置換し、`CopyResource` と `Map` を別フレーム化
- Step 3: perf log に `capture_copy_ms`, `capture_map_ms`, `capture_skipped`, `capture_slot_busy` を追加
- Step 4: resize / device reset / detach の後始末を整理
- Step 5: 実機検証で ring size と readback 選択方針を調整

9. **非機能要件チェック**
- 性能:
  - `Present` steady state の `capture_ms avg` を大幅に下げる
  - `capture_ms p99/max` を現行より縮小する
- 安定性:
  - `Map` 遅延時は fail fast ではなく capture skip を優先する
- 可観測性:
  - `slot busy`, `readback skipped`, `readback success` を perf log で観測できるようにする
- 互換性:
  - 共有メモリフォーマットは変えない

10. **リスクと緩和策**
- Risk: 数フレーム遅れた画像で OCR 結果がわずかに古くなる。
- Mitigation: まずは ring size を 3 に抑え、最新に最も近い ready slot を優先して読む。

- Risk: `pending` slot が溜まり続けると capture が止まる。
- Mitigation: free slot 不足時は古い pending を捨てるか、そのフレームの copy を見送るポリシーを入れる。

- Risk: ドライバ差異で `Map` 再試行の振る舞いが不安定。
- Mitigation: 診断用に旧実装へ戻せるフラグを残し、タイトル別の切り戻しを可能にする。

11. **影響範囲**
- `Native/HookAgentDx11/Dx11PresentHook.cpp`
  - capture path の本体変更
- `Native/HookCommon/SharedFrameWriter.cpp`
  - 原則変更なし
- `Services/GraphicsHookCaptureProvider.cs`
  - 原則変更なし
- `Doc/`
  - 本計画書追加

12. **Definition of Done**
- [ ] `Present` 内の即時 `Map` を廃止し、別フレーム readback へ置き換えた
- [ ] 共有メモリ publish が現行 consumer で読める
- [ ] `GraphicsHookCaptureFpsLimit=1` で 1% low の悪化が現行より改善する
- [ ] perf log で `capture_ms avg/p99/max` が縮小したことを確認できる
- [ ] resize / Alt+Tab / detach でリークやクラッシュがない
