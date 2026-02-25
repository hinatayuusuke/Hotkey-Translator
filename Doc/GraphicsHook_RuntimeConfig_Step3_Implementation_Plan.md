# GraphicsHook RuntimeConfig Step3（HookCommon v2ヘッダ追加）実装案

1. **概要（1–3行）**
現行コードを壊さずに、RuntimeConfig IPC の v2（publish-last seq）を `HookCommon` 層へ追加する。
Step3 では「使える土台」を作ることを目的とし、既存 v1(qpc) の動作は維持する。

2. **ゴール / 非ゴール**
- ゴール
  - `Native/HookCommon` に Config v2 構造体と read/write API を追加する。
  - v1 と v2 を同一マッピング名/同一サイズで共存させる。
  - Step4（C# Writer v2）と Step5（Hook Reader v2優先）の実装準備を完了する。
- 非ゴール
  - 本Stepでは `Dx11PresentHook.cpp` の判定ロジックを v2優先へ切り替えない。
  - 本Stepでは `Dx11HookConfigWriter.cs` を v2 送信へ切り替えない。

3. **前提・仮定**
- Step1-2 は実装済み（publish成否可視化・失敗時再同期あり）。
- 現在の Config は `HookConfigHeader(version=1, updatedQpc)` 固定。
- Config MMF 名は `Local\\HT_HOOK_CFG_<api>_<pid>` を継続利用する。

4. **現状整理（現行コード）**
- プロトコル定義: `Native/HookCommon/HookIpcProtocol.h`
  - `HookConfigHeader` は v1 固定（`updatedQpc` のみ）。
- Native Writer/Reader: `Native/HookCommon/SharedHookConfig.h`, `Native/HookCommon/SharedHookConfig.cpp`
  - `SharedHookConfigWriter::Write(...)` は v1を書き込む。
  - `SharedHookConfigReader::TryRead(HookConfigHeader&)` は v1前提の読み出し。
- 呼び出し側
  - HookHost attach 時に `SharedHookConfigWriter::Write(...)` を実行: `Native/HookHost/main.cpp`
  - HookAgent 側は `updatedQpc` 差分で反映: `Native/HookAgentDx11/Dx11PresentHook.cpp`

5. **提案アーキテクチャ**
- 方針: 「API追加で先行対応、既存呼び出しは保持」
  - 既存 `Write(...)` / `TryRead(...)` は残し、v1挙動を維持。
  - v2用の型・APIを追加し、後続Stepで呼び出し元を段階移行する。
- データ契約（v2）
  - v2ヘッダ例（40 bytes、v1と同サイズ）
    - `magic, version, api, targetPid, captureFpsLimit, overlayEnabled, flags, reserved0, updatedSeq`
  - v1との同居を容易にするため、サイズは維持する。

6. **インターフェース設計**
- `HookIpcProtocol.h`
  - 追加定数
    - `kConfigHeaderVersionV1 = 1`
    - `kConfigHeaderVersionV2 = 2`
  - 追加構造体
    - `HookConfigHeaderV1`（既存相当）
    - `HookConfigHeaderV2`（`updatedSeq` + `flags`）
  - 互換のため `HookConfigHeader` は v1 alias として残す（呼び出し側破壊を避ける）。
- `SharedHookConfig.h`
  - 追加API（案）
    - `bool WriteV2(DWORD pid, GraphicsApi api, uint32_t captureFpsLimit, bool overlayEnabled, uint64_t seq, uint32_t flags = 0);`
    - `bool TryReadV2(HookConfigHeaderV2& outHeader) const;`
    - `bool TryReadAny(HookConfigHeaderV1& outV1, HookConfigHeaderV2& outV2, bool& isV2) const;`
  - 既存 `Write(...)` / `TryRead(...)` は残す。

7. **実装手順（Step3内）**
- Step 3-1: プロトコル型追加
  - `Native/HookCommon/HookIpcProtocol.h` に Config v1/v2定数・構造体を追加。
  - `static_assert(sizeof(HookConfigHeaderV1) == sizeof(HookConfigHeaderV2))` を追加。
- Step 3-2: Writer v2 API追加
  - `Native/HookCommon/SharedHookConfig.h/.cpp` に `WriteV2(...)` を追加。
  - publish-last 規約を明記: 本体フィールド -> `MemoryBarrier` -> `updatedSeq` を最後に反映。
- Step 3-3: Reader v2/両対応 API追加
  - `TryReadV2(...)` と `TryReadAny(...)` を実装。
  - `version` 判定で v1/v2 を選別し、不正versionは false。
- Step 3-4: 呼び出し互換維持
  - 既存の `HookHost/main.cpp` は当面 `Write(...)` のまま（動作差分なし）。
  - 既存 `Dx11PresentHook.cpp` は未変更（Step5で移行）。

8. **非機能要件チェック**
- 互換性
  - 既存v1 APIを保持するため、Step3単独導入で実行挙動を変えない。
- 安全性
  - 同一MMF名・同一サイズ維持により、移行期の運用リスクを下げる。
- 可観測性
  - Step3時点ではログ仕様は増やさず、Step5で seq/qpc 受理ログを追加する。

9. **リスクと緩和策**
- Risk: v1/v2 構造体のサイズ差異で読取り崩れが起きる。
- Mitigation: `static_assert` でビルド時に強制検証。
- Risk: Step3だけ入れても体感改善がない。
- Mitigation: Step3は基盤タスクと明示し、効果は Step4/5 実装後に出ることを共有する。
- Risk: publish-last 実装順序の誤りで torn read が起きる。
- Mitigation: Writer実装に `MemoryBarrier` と更新順序コメント（WHY）を必須化。

10. **影響範囲（変更ファイル候補）**
- `Native/HookCommon/HookIpcProtocol.h`
- `Native/HookCommon/SharedHookConfig.h`
- `Native/HookCommon/SharedHookConfig.cpp`
- （互換確認のみ）`Native/HookHost/main.cpp`

11. **Definition of Done**
- `HookCommon` に Config v2 用 struct/定数/API が追加される。
- 既存 v1 呼び出し（HookHost/HookAgent）が未変更でビルド・実行できる。
- `WriteV2` / `TryReadV2` / `TryReadAny` の単体確認が完了する。
- `dotnet build Hotkey-Translator.sln -c Release` と Native ビルド（HookHost/HookAgentDx11）が成功する。

## 補足（次Stepへの接続）
- Step4 で `Dx11HookConfigWriter.cs` を v2 publish-last(seq) に切り替える。
- Step5 で `Dx11PresentHook.cpp` を v2優先（seq）/v1フォールバック（qpc）判定へ移行する。
