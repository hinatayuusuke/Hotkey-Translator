# Graphics Hook Capture/Overlay Plan (DX/OpenGL/Vulkan)

1. **概要（1–3行）**
- 本計画は、ゲーム向けに `DirectX / OpenGL / Vulkan` の描画パイプラインへフックし、キャプチャ取得とオーバーレイ描画を同一フレーム経路で実行する実装案である。
- 現行の WGC/DXGI/GDI + WPF Overlay を即置換せず、段階導入で品質と安定性を検証する。
- 最初の到達点は `DX11` での PoC（取得 + 描画）とし、順次 DX12/OpenGL/Vulkan へ拡張する。

2. **ゴール / 非ゴール**
### ゴール
- フック経路で対象ウィンドウのフレーム取得とオーバーレイ描画を実現する。
- API 別（DX11/DX12/OpenGL/Vulkan）バックエンドを抽象化し、段階的に追加可能な構造にする。
- 既存経路（WGC/DXGI/GDI + WPF Overlay）をフォールバックとして残し、実行時切替できるようにする。

### 非ゴール
- 初回実装で全 API を同品質・同機能に揃えること。
- 抗チート環境への対応保証。
- 注入方式のみへの一本化（既存非注入経路は維持）。

3. **前提・仮定**
- 対象は主に Windows デスクトップゲームであり、描画 API は DX11/DX12/OpenGL/Vulkan のいずれか。
- 既存アプリには OCR/翻訳パイプラインと設定UIがあるため、キャプチャとオーバーレイを差し替え可能にするのが合理的。
- フックは相性問題やクラッシュリスクがあるため、既定OFFの段階リリースが前提。

4. **現状整理**
- 現状キャプチャ: `WGC / DXGI / GDI` の選択・フォールバック。
- 現状オーバーレイ: WPF Window の Topmost 表示（他オーバーレイと競合しやすい）。
- 課題:
  - 対象アプリ上での表示優先順位が不安定。
  - マルチモニタや独占表示で表示/位置ずれが起きやすい。
  - API ごとの描画特性を直接利用できない。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `HookHost`（Native, 別プロセス推奨）
  - API 判定
  - フック注入/解除
  - 共有メモリ/IPC 管理
- `HookBackend`（API別）
  - `Dx11HookBackend`
  - `Dx12HookBackend`
  - `OpenGlHookBackend`
  - `VulkanHookBackend`
- `Core App`（既存WPF）
  - OCR/翻訳/設定
  - オーバーレイ描画コマンド送信
  - フック失敗時の既存経路フォールバック

### 5.2 データフロー / シーケンス
1. Core App が対象PIDとモードを HookHost に送信。
2. HookHost が API を判定し該当バックエンドを起動。
3. Present 前後でフレーム参照ポイントを取得し、必要最小限の画像データを共有メモリへ出力。
4. Core App が OCR/翻訳実行後、`OverlayCommandBuffer` を HookHost へ返送。
5. HookBackend が同フレームでテキスト/矩形を描画して Present。
6. 異常時は HookHost が Core App へ状態通知し、既存経路へ自動フォールバック。

### 5.3 既存パターンへの整合
- 既存 `PipelineOrchestrator` は維持し、Capture/Overlay Provider の差し替え点を追加する。
- `MainWindow` は制御UIに限定し、描画実体は HookHost 側へ寄せる。
- 設定は既存 `AppSettings`/`SettingsViewModel` に統合して一元管理する。

6. **インターフェース設計**
### 6.1 Core App <-> HookHost IPC（案）
- Transport: `NamedPipe` + `SharedMemory`
- Request:
  - `AttachRequest { pid, preferredApi, captureMode, overlayEnabled }`
  - `DetachRequest { pid }`
  - `OverlayUpdateRequest { frameId, commands[] }`
- Event:
  - `HookStateChanged { pid, api, state, reason }`
  - `FrameReady { frameId, width, height, format, sharedHandle }`
  - `BackendError { api, code, detail }`

### 6.2 OverlayCommand（案）
- `DrawText { rect, text, fontSize, fgColor, bgColor, align }`
- `DrawRect { rect, strokeColor, strokeWidth, fillColor }`
- `DrawSpinner { anchor, visible }`
- 互換性維持のため、既存 `OverlayItem` からの変換層を置く。

### 6.3 CaptureSurface（案）
- GPU優先: 共有テクスチャハンドル
- CPUフォールバック: BGRA8 byte buffer
- `frameId` と `timestamp` を必須化し、遅延/順序逆転を検知可能にする。

7. **実装手順（ステップ分割）**
- Step 1: 契約とFeature Flag
  - `EnableGraphicsHookPipeline` を追加（既定OFF）。
  - IPC契約（Attach/Detach/Frame/Overlay）を定義。

- Step 2: DX11 PoC
  - `Present` フックでフレーム取得。
  - 単純矩形 + テキスト描画を実装。
  - フック失敗時の即時フォールバックを実装。

- Step 3: Core App 統合
  - CaptureProvider/OverlayProvider の選択ロジックに Hook 経路を追加。
  - 設定UI（ON/OFF、API優先度、診断ログ）を追加。

- Step 4: DX12 対応
  - コマンドキュー/バックバッファ管理差分を吸収。

- Step 5: OpenGL 対応
  - `wglSwapBuffers` 系でのフレーム境界取得と描画注入。

- Step 6: Vulkan 対応
  - `vkQueuePresentKHR` を中心に最小実装。
  - 拡張/Layer 相性による無効化条件を実装。

- Step 7: 安定化
  - クラッシュ隔離（HookHost再起動）
  - タイムアウト/再接続
  - 既存経路へのロールバック自動化

8. **非機能要件チェック**
- 性能:
  - 目標: 追加オーバーヘッド平均 < 2ms（DX11基準）
  - フレームコピー回数最小化（GPU共有優先）
- セキュリティ:
  - IPC入力検証（サイズ上限、型検証）
  - Hook対象PIDの許可制御
- 可観測性:
  - API判定、attach/detach、fallback理由、描画失敗回数をログ化
- 互換性:
  - Hook無効時は現行機能が完全に維持されること
- 運用:
  - 既定OFF、対象タイトル単位で有効化する段階運用

9. **リスクと緩和策**
- Risk: API/ドライバ差異でクラッシュやハングが発生。
- Mitigation: HookHostを別プロセス化し、監視再起動 + 自動フォールバックを実装。

- Risk: 抗チート・保護環境で動作不可またはブロック。
- Mitigation: 対象外を明示し、検出時は即座に既存経路へ戻す。

- Risk: レンダースレッド負荷増でFPS低下。
- Mitigation: 描画コマンド最適化、更新間引き、GPU共有を優先。

10. **影響範囲（変更ファイル候補・移行・ドキュメント更新）**
- 新規（候補）
  - `HookHost/*`（Nativeプロジェクト）
  - `Interop/HookIpcContracts/*`
  - `Services/Capture/HookCaptureProvider.cs`
  - `Services/Overlay/HookOverlayProvider.cs`
- 既存（候補）
  - `Models/AppSettings.cs`
  - `ViewModels/SettingsViewModel.cs`
  - `MainWindow.xaml`
  - `Services/PipelineOrchestrator.cs`
- ドキュメント
  - API別制限一覧
  - トラブルシュート（attach失敗・描画不一致・フォールバック条件）

11. **Definition of Done**
- [ ] `EnableGraphicsHookPipeline` OFFで既存挙動が完全維持される。
- [ ] DX11で「フレーム取得 + オーバーレイ描画 + OCR連携」が実機確認できる。
- [ ] フック失敗時に既存経路へ自動フォールバックし、処理継続できる。
- [ ] ログで API判定/attach/fallback 理由を追跡できる。
- [ ] DX12/OpenGL/Vulkanの導入順序と未対応制約がDocに明示されている。
