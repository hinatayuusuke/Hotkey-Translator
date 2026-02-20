# GraphicsHook RuntimeConfig: publish-last(seq) 移行実装案

## 1. 概要（1–3行）
F9 の overlay 再表示が不安定になる原因を先に止血しつつ、設定共有メモリ（`HT_HOOK_CFG_*`）を将来的に `publish-last(seq)` 契約へ移行する。
短期は「失敗可視化 + 再同期フォールバック」、中期で `seq` ベース判定へ段階移行する。

## 2. ゴール / 非ゴール
### ゴール
- F9 で `overlayEnabled` を OFF/ON した際、Hook 側の表示状態が確実に追従する。
- 設定IPCの更新判定を `QPC` 依存から `seq` 依存へ移行できる基盤を作る。
- DX11 以外（OpenGL/Vulkan）にも再利用しやすい更新契約を確立する。

### 非ゴール
- 本タスクで OpenGL/Vulkan 実装を追加すること。
- 設定I/F（UI）の全面改修。

## 3. 前提・仮定
- 現在、F9 は `TryPublishRuntimeConfig(...)` の best-effort 書き込みのみで、失敗時フォールバックがない。
- Hook 側は `overlayEnabled==0` の間、描画処理を早期 return する。
- 既存の `HookConfigHeader` は `version=1` で `updatedQpc` を更新判定に使っている。

## 4. 現状整理
### 4.1 現在の更新ルート
- UI hotkey(F9) -> `MainWindow.OnToggleOverlayHotkeyPressed`
- `TryPublishRuntimeConfig(pid, fps, overlayEnabled)` を実行
- Hook 側 `RefreshConfigLocked` が `updatedQpc` の変化を検知して反映

### 4.2 問題点
- `TryPublishRuntimeConfig` は失敗時の戻り値/ログが弱く、反映失敗が可視化されない。
- F9 では attach し直さないため、設定反映失敗の自己回復がない。
- 更新判定が `updatedQpc` のみで、将来の共通化観点で契約が弱い。

## 5. 提案アーキテクチャ
### 5.1 二段階導入
- Phase A（先行止血）: F9 再表示問題を潰す
  - `TryPublishRuntimeConfig` を結果付きにし、失敗時に `ApplySettingsAsync` で再同期
- Phase B（契約強化）: config IPC を `publish-last(seq)` 化
  - Writer は設定本体を書いた後に `updatedSeq` を最後に publish
  - Reader は `updatedSeq` の増加で反映判定

### 5.2 互換方針
- 破壊的に v1 を消さず、v1/v2 共存期間を設ける。
- Hook Reader は「v2(seq)優先、v1(qpc)フォールバック」で読む。

## 6. インターフェース設計
### 6.1 Config Header v2（案）
既存サイズを極力維持し、将来拡張余地を残す。

```c
struct HookConfigHeaderV2
{
    uint32 magic;          // "HCTF"
    uint32 version;        // 2
    uint32 api;
    uint32 targetPid;
    uint32 captureFpsLimit;
    uint32 overlayEnabled;
    uint32 flags;          // 将来用
    uint32 reserved0;
    uint64 updatedSeq;     // monotonic, publish-last
};
```

補足:
- `updatedQpc` は v1 互換期間のみ維持してもよい（v2では任意）。
- 既存バイナリ互換を優先するなら、`reserved0/reserved1` を `seqLow/seqHigh` として流用する案も可。

### 6.2 Reader 判定ルール
- `version>=2 && updatedSeq>0` のときは `updatedSeq` で判定。
- それ以外は既存 `updatedQpc` 判定へフォールバック。

### 6.3 Writer publish ルール
- 設定フィールドを書き込む
- `MemoryBarrier`
- `updatedSeq = updatedSeq + 1` を最後に書く
- `MemoryBarrier`

## 7. 実装手順（ステップ分割）
### Step 1: 止血（F9再表示不具合）
- `Dx11HookClientService.TryPublishRuntimeConfig` を `bool` 返却へ変更。
- 失敗理由（pid mismatch / mmf write fail / disposed）をログ化。
- `MainWindow.OnToggleOverlayHotkeyPressed` で publish 失敗時に `ApplySettingsAsync(settings)` を実行。

### Step 2: 監視ログの追加
- F9 トグル時に `effectiveHookOverlayEnabled` と publish 成否を INFO ログ化。
- Hook 側 `RefreshConfigLocked` に「更新受理時の seq/qpc」をデバッグログ出力（環境変数制御）。

### Step 3: HookCommon v2 ヘッダ追加
- `Native/HookCommon/HookIpcProtocol.h` に `kConfigHeaderVersion=2` 用 struct 追加。
- C++ `SharedHookConfigWriter/Reader` を v2 読み書き対応。

### Step 4: C# Writer v2 対応
- `Services/Hook/Dx11HookConfigWriter.cs` を v2 struct へ対応。
- インスタンス内に `ulong _nextSeq` を保持し単調増加。

### Step 5: Hook Reader v2 優先化
- `Native/HookAgentDx11/Dx11PresentHook.cpp` の `RefreshConfigLocked` を
  - v2: seq 判定
  - v1: qpc 判定
  の順に実装。

### Step 6: 互換確認と切替
- 一定期間 v1/v2 互換運用。
- 問題なければ v1 判定を段階的に縮小。

## 8. 非機能要件チェック
### 性能
- 設定更新は低頻度なので、seq導入によるコスト影響はほぼ無視可能。

### 可観測性
- publish 成功/失敗をログで追えるようにし、現場切り分け時間を短縮。

### 互換性
- v1/v2 並行対応で、既存 HookHost/Agent の混在期間を許容。

### 運用
- 緊急時は既存 `ApplySettingsAsync` 再同期で回復可能。

## 9. リスクと緩和策
- Risk: v1/v2 混在時に判定分岐のバグで設定が反映されない。
- Mitigation: Reader 側を「v2優先・v1フォールバック」の単純ルールに限定し、受理ログを追加。

- Risk: F9 連打で `ApplySettingsAsync` フォールバックが多発する。
- Mitigation: publish 失敗時のみフォールバックし、成功時は実行しない。

- Risk: seq 初期値/巻き戻り時の誤判定。
- Mitigation: `seq==0` は未更新扱い、前回値より増加した時のみ更新適用。

## 10. 影響範囲（変更ファイル候補）
- `MainWindow.xaml.cs` — F9 時の publish 失敗フォールバック。
- `Services/Hook/Dx11HookClientService.cs` — `TryPublishRuntimeConfig` の戻り値化/ログ化。
- `Services/Hook/Dx11HookConfigWriter.cs` — seq付き v2 書き込み。
- `Native/HookCommon/HookIpcProtocol.h` — Config v2 header 定義。
- `Native/HookCommon/SharedHookConfig.*` — v2 reader/writer。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — Config v2 読み取り判定。

## 11. Definition of Done
- F9 OFF->ON で Hook overlay が安定して再表示される（F7 を挟まなくても再現しない）。
- publish 成否がログで確認できる。
- Config v2 (`updatedSeq`) 更新を Hook 側が受理できる。
- v1 writer でも Hook が動作継続できる（フォールバック確認）。

## Migration（推奨）
1. 先に Step 1-2 をリリース（止血）。
2. 次に Step 3-5 を feature flag 付きで導入。
3. 運用で安定後、v1(qpc)依存を縮小。
