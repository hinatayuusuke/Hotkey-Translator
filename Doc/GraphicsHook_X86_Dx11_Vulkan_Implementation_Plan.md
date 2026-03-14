# GraphicsHook x86 Dx11/Vulkan 実装案

1. **概要（1–3行）**
- x86 の Dx11 / Vulkan ターゲットで Hook が失敗する主因は、x86 対応の Host / Agent 配置と選択が未整備な点にある。
- 推奨は「ターゲットと同一 bitness の Host / Agent を選ぶ」構成へそろえること。
- まずは注入を成立させる最小実装を優先し、cross-bitness 注入や DXVK 専用分岐は導入しない。

2. **ゴール / 非ゴール**
### ゴール
- x86 の Dx11 タイトルへ Hook できること。
- x86 の Vulkan タイトルへ Hook できること。
- x64 タイトルの既存挙動を壊さず、bitness ごとに適切な Host / Agent を選択できること。
- DLL 実体が不足している場合、Attach 前に明示的に失敗させること。

### 非ゴール
- x64 Host から x86 ターゲットへ注入する cross-bitness 対応。
- DXVK 専用の API 自動判定や専用フォールバック。
- Dx12 / OpenGL 対応の同時実装。
- 権限昇格や保護プロセス回避の導入。

3. **前提・仮定**
- 現行実装は `HookHost.exe` が対象プロセスへ Agent DLL を注入し、各 API の `Install*HookThread` を呼ぶ構成。
- `GraphicsHookClientService` はターゲット bitness を取得できている。
- x86 の `HookHost.exe` はすでに存在する。
- Dx11 / Vulkan の x86 Agent DLL は未配置、または少なくとも現行の出力先・選択ロジックでは利用されていない。
- fail fast 方針に従い、要件外の互換レイヤや自動フォールバックは追加しない。

4. **現状整理**
- `GraphicsHookClientService.ResolveHostPath(...)` は Dx9 のみ x86 Host を選び、Dx11 / Vulkan は x86 ターゲットでも x64 Host を選ぶ。
- `ResolveDx9AgentPath(...)` は Dx9 専用で、Dx11 / Vulkan には bitness ごとの Agent 解決処理がない。
- `HookAgentDx11` / `HookAgentVulkan` の CMake 出力先は `HookHost/bin` 固定で、Dx9 のような `HookHost/bin/x86` 出力分岐がない。
- `HookHost` 側は自分の exe ディレクトリから DLL leaf 名を解決しているため、Host / Agent を同一ディレクトリにそろえれば流用できる。
- 失敗ログは `attach_failed:Remote_LoadLibraryW_failed` または `attach_failed:VirtualAllocEx_failed` で止まっており、API Hook install 前の注入段階で失敗している。

5. **提案アーキテクチャ**
### コンポーネント構成
- `GraphicsHookClientService`
  - ターゲット bitness を基準に Host / Agent を選択する。
  - Agent 不足時に Attach を開始しない。
- `HookHost.exe`（x64 / x86）
  - 自プロセスと同じ bitness の Agent DLL をロードして対象へ注入する。
- `HookAgentDx11.dll` / `HookAgentVulkan.dll`（x64 / x86）
  - 各 bitness 用の成果物を生成し、対応する Host 配下へ配置する。

### データフロー / シーケンス
1. `GraphicsHookClientService` が PID から `target_bitness` を取得する。
2. bitness に応じて `HookHost.exe` を `bin` または `bin/x86` から選択する。
3. API と bitness に応じて Agent DLL を解決する。
4. Host / Agent 実体がそろっていれば attach を送る。
5. `HookHost` は自分と同じ bitness の DLL を対象へ `LoadLibraryW` し、成功後に `Install*HookThread` を呼ぶ。

### 既存パターンへの整合
- Dx9 が採用済みの「bitness ごとに Host / Agent を分ける」方針に合わせる。
- `HookHost` の DLL 解決方式は変えず、配置と選択だけをそろえる。
- 設定 UI や API 種別の意味は変えない。

6. **インターフェース設計**
### パス解決
- `ResolveHostPath(ProcessBitness targetBitness)` に整理する。
- `ResolveAgentPath(ProcessBitness targetBitness, GraphicsHookApiKind api)` を追加する。
- `ResolveDx9AgentPath(...)` は置き換える。

### ファイル配置
- x64:
  - `Native/HookHost/bin/HookHost.exe`
  - `Native/HookHost/bin/HookAgentDx11.dll`
  - `Native/HookHost/bin/HookAgentVulkan.dll`
- x86:
  - `Native/HookHost/bin/x86/HookHost.exe`
  - `Native/HookHost/bin/x86/HookAgentDx11.dll`
  - `Native/HookHost/bin/x86/HookAgentVulkan.dll`

### バリデーション
- `HostSelection` は `Dx9AgentPath` のような API 固有名をやめ、`AgentPath` に寄せる。
- Attach 前に `HostPath` と `AgentPath` の両方を `File.Exists` で検証する。
- `agent_missing(<path>)` を API 共通で返せるようにする。

### ログ
- `host_selection` に `agent` と `target_bitness` を残す現行方針を維持する。
- `WHY:` コメントで「Dx11 / Vulkan も target bitness に合わせる理由」を残す。

7. **実装手順（ステップ分割）**
- Step 1: `GraphicsHookClientService` の bitness 選択整理
  - `ResolveHostPath` を API 非依存にする。
  - `ResolveAgentPath` を追加する。
  - `HostSelection` の `Dx9AgentPath` を `AgentPath` に改名する。
  - Dx11 / Vulkan でも x86 ターゲットなら x86 Host を選ぶ。

- Step 2: Agent 実体チェックの共通化
  - Dx9 専用の `agent_missing` 分岐を API 共通へ広げる。
  - Agent 未配置時は attach を送らず fail fast する。

- Step 3: CMake 出力先修正
  - `HookAgentDx11/CMakeLists.txt` に Dx9 同様の x86 出力分岐を追加する。
  - `HookAgentVulkan/CMakeLists.txt` にも同様の分岐を追加する。
  - x86 ビルド時に `HookHost/bin/x86` へ成果物が出ることを保証する。

- Step 4: 配布 / ビルド確認
  - Debug / Release の両方で x86 / x64 の Host / Agent が配置されることを確認する。
  - publish やコピー処理があるなら、x86 Dx11 / Vulkan DLL も落ちないように更新する。

- Step 5: 動作確認
  - x86 Dx11 タイトルで attach できることを確認する。
  - x86 Vulkan タイトルで `Remote_LoadLibraryW_failed` が解消することを確認する。
  - x64 Dx11 / Vulkan の既存成功ケースが回帰していないことを確認する。

8. **非機能要件チェック**
- 性能: bitness 判定とファイル存在確認のみで、ランタイム負荷はほぼ増えない。
- セキュリティ: 権限昇格や保護回避を入れず、現行権限モデルを維持する。
- 可観測性: `host_selection` / `attach_requested` / `hook_state` で bitness と失敗理由を追跡できる。
- 互換性: x64 の既存構成を維持しつつ、x86 構成を追加するだけに留める。
- 運用: x86 Vulkan は Vulkan SDK の x86 向けリンク条件を満たす必要がある。

9. **リスクと緩和策**
- Risk: x86 `HookAgentVulkan.dll` のビルドが Vulkan SDK / 依存 DLL 条件で失敗する。
- Mitigation: CMake の x86 構成を先に単体で通し、成果物生成を確認してからアプリ統合へ進む。

- Risk: x86 用成果物が publish / bin コピー処理から漏れる。
- Mitigation: 実行ディレクトリに最終配置されたファイルを確認するテストを追加する。

- Risk: x86 対応後も権限差で `VirtualAllocEx_failed(gle=5)` が残る。
- Mitigation: これは別問題として切り分け、まず `Remote_LoadLibraryW_failed` 解消を完了条件にしない。

- Risk: `HostSelection` の構造変更でログや呼び出し側が壊れる。
- Mitigation: API 固有名を削る変更は最小範囲に限定し、ログ項目名は維持する。

10. **影響範囲**
- `Services/Hook/GraphicsHookClientService.cs` — Host / Agent の bitness 選択と存在チェックを共通化。
- `Native/HookAgentDx11/CMakeLists.txt` — x86 出力先対応。
- `Native/HookAgentVulkan/CMakeLists.txt` — x86 出力先対応。
- 必要なら `Native/HookHost/bin/x86/` 配置に関わる build / copy 定義 — x86 成果物の最終配置保証。
- `Doc/` の関連計画書 — 実装完了後に結果反映を追記する場合あり。

11. **Definition of Done**
- x86 Dx11 タイトルで `host_selection target_bitness=x86` 時に `bin/x86/HookHost.exe` が選ばれる。
- x86 Vulkan タイトルで `host_selection target_bitness=x86` 時に `bin/x86/HookHost.exe` が選ばれる。
- x86 `HookAgentDx11.dll` と `HookAgentVulkan.dll` が `HookHost/bin/x86` に生成される。
- Agent 不足時に `attach_failed:Remote_LoadLibraryW_failed` ではなく、Attach 前の明示的な不足エラーになる。
- x86 対象で少なくとも DLL 注入段階を通過し、Dx11 / Vulkan の hook install フェーズまで進める。
- x64 Dx11 / Vulkan の既存動作が回帰しない。
