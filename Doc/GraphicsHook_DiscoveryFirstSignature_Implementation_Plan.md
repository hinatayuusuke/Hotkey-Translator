# GraphicsHook Discovery-First Signature Implementation Plan

1. **概要（1–3行）**
- 初回 launcher 実行では `discovery + best-effort attach` を行い、観測した正解 window/process 情報を signature として保存する。
- 2 回目以降は保存済み signature を使って本命 PID を早く選び、Vulkan の late attach リスクを下げる。

2. **ゴール / 非ゴール**
### ゴール
- 問題タイトルの初回起動で、本命 window/process 情報を自動収集して保存できること。
- 初回で hook が間に合わなくても、2 回目以降は保存済み signature を使って早めに attach できること。
- 独占フルスクリーン title を主対象に、最小限の heuristics で運用できること。

### 非ゴール
- 初回起動で必ず capture 成功まで保証すること。
- OBS 同等の汎用 window matcher、compatibility hook、広範な blacklist を作ること。
- すべての windowed title や borderless title を同じ精度で扱うこと。
- x86 Vulkan late attach 自体を大改修で成立させること。

3. **前提・仮定**
- 現状の launcher は `launched.ProcessId` を即 `FixedCaptureWindowProcessId` に入れて attach するため、bootstrap PID と render PID が分かれるタイトルに弱い。
- x86 Vulkan は `procaddr_only` で late attach に弱く、正しい PID を選べても attach が遅れると失敗しやすい。
- Hook の主要対象は独占フルスクリーン title であり、`foreground window` と monitor 全面一致は本命判定に使える。
- 初回は「正しい process/window を観測して signature を保存すること」を最優先にし、capture 成功は best-effort と割り切る。

4. **現状整理**
- 現在は launcher 後に bootstrap PID へ即 attach しており、後段の window resolve が外れると Vulkan hook は回復しにくい。
- 既存 probe により、`attachPid != windowPid` のタイトルが実在することは確認済みである。
- 問題タイトルでは「初回 discovery で情報収集し、2 回目から改善する」方式のほうが実用的である。

5. **提案アーキテクチャ**
### コンポーネント構成
- `LauncherDiscoveryResolver`
  - 初回実行時に process / foreground fullscreen window を観測し、本命候補を選ぶ。
- `LauncherTargetSignatureRegistry`
  - discovery で保存した signature を保持する。
- `LauncherTargetResolver`
  - 保存済み signature があるとき、より早い段階で本命 PID を選ぶ。
- `GraphicsHookClientService`
  - 最終的に決まった PID に attach する。

### データフロー / シーケンス
1. launcher が target を起動する。
2. 保存済み signature が無ければ discovery mode に入る。
3. discovery mode は短時間だけ process 出現と foreground fullscreen window を監視する。
4. 本命らしい window/process を観測したら、その時点で signature を保存する。
5. attach は best-effort で試すが、初回成功は保証しない。
6. 次回 launcher 起動では保存済み signature を優先し、process 出現直後から本命候補を追跡する。
7. 本命 PID が十分確からしい時点で attach し、初回より早く hook を開始する。


6. **インターフェース設計**
### 6.1 signature モデル
候補: `Models/GraphicsHookLauncherTargetSignature.cs`

```csharp
public sealed class GraphicsHookLauncherTargetSignature
{
    public string Key { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;
    public string? ExePathSuffix { get; set; }
    public GraphicsHookApiKind? PreferredApi { get; set; }
    public string[] WindowClassAllowList { get; set; } = Array.Empty<string>();
    public string[] WindowTitleContainsAny { get; set; } = Array.Empty<string>();
    public bool RequireVisibleTopLevel { get; set; } = true;
    public bool RequireOwnerlessWindow { get; set; } = true;
    public bool RequireExclusiveFullscreen { get; set; } = true;
    public int MinClientWidth { get; set; } = 1280;
    public int MinClientHeight { get; set; } = 720;
    public bool LearnedFromDiscovery { get; set; }
    public DateTimeOffset? LearnedAtUtc { get; set; }
}
```

### 6.2 discovery result モデル
候補: `Services/Hook/LauncherDiscoveryResult.cs`

```csharp
internal readonly record struct LauncherDiscoveryResult(
    bool Success,
    int ProcessId,
    IntPtr Hwnd,
    string ProcessName,
    string ExePath,
    string WindowClass,
    string WindowTitle,
    int Width,
    int Height,
    bool IsExclusiveFullscreen,
    string Reason);
```

### 6.3 registry 形式
- `Data/GraphicsHookLauncherSignatures.json`
- 初回 discovery 成功時に 1 件追加または更新する。
- 保存条件は「capture 成功」ではなく「window/process identification 成功」とする。

JSON 例:

```json
[
  {
    "Key": "senran-kagura-shinovi-versus",
    "ExeName": "SKShinoviVersus",
    "ExePathSuffix": "Senran Kagura Shinovi Versus\\SKShinoviVersus.exe",
    "PreferredApi": "Vulkan",
    "WindowClassAllowList": [ "SENRAN KAGURA SHINOVI VERSUS" ],
    "RequireVisibleTopLevel": true,
    "RequireOwnerlessWindow": true,
    "RequireExclusiveFullscreen": true,
    "MinClientWidth": 1920,
    "MinClientHeight": 1080,
    "LearnedFromDiscovery": true,
    "LearnedAtUtc": "2026-03-15T03:00:00Z"
  }
]
```

### 6.4 resolver API
候補:

```csharp
internal sealed class LauncherDiscoveryResolver
{
    public Task<LauncherDiscoveryResult> DiscoverAsync(
        string expectedExeName,
        string? expectedExePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

7. **候補選定ロジック**
### 初回 discovery の優先順位
1. `ExePath` 一致
2. `ExeName` 一致
3. foreground window
4. monitor 全面一致
5. visible top-level
6. ownerless
7. class 一致または class を採取可能

### 2 回目以降の優先順位
1. 保存済み `ExePathSuffix`
2. 保存済み `WindowClassAllowList`
3. 保存済み `RequireExclusiveFullscreen`
4. foreground であることは補助加点に留める

### 重要な判断
- 初回は `window identification 成功` で signature を保存する。
  理由: x86 Vulkan では attach が遅れて capture に失敗しても、識別情報自体は次回に活用できるため。
- 2 回目以降は fullscreen 完全確定を待ちすぎない。
  理由: saved signature があるなら process 出現時点から候補を絞り、attach を前倒ししたい。

8. **実装手順（ステップ分割）**
- Step 1: signature registry を discovery-first 前提に拡張
  - `LearnedFromDiscovery`
  - `LearnedAtUtc`
  - `RequireExclusiveFullscreen`

- Step 2: `LauncherDiscoveryResolver` を追加
  - process 列挙
  - foreground window 判定
  - fullscreen 判定
  - `HWND -> PID -> exe/class/title` 採取

- Step 3: launcher flow に discovery mode を追加
  - saved signature が無いときだけ discovery を動かす
  - 本命候補観測時に signature を保存
  - attach は best-effort で試す

- Step 4: saved signature を使う fast path を追加
  - 2 回目以降は process 出現直後から候補を絞る
  - fullscreen window が出る前でも attach 候補を前倒しできるかを段階的に検証する


9. **非機能要件チェック**
- 性能: launcher 後の短時間監視のみで、常駐コストは小さい。
- セキュリティ: process / window 列挙と local JSON 保存のみ。
- 可観測性: `discovery_start`、`discovery_candidate`、`discovery_saved`、`signature_reused`、`attach_too_late_possible` をログへ残す。
- 互換性: signature が無いタイトルは discovery mode へ、あるタイトルは fast path へ分岐する。
- 運用: 初回で失敗しても 2 回目で改善されることを許容する設計にする。

10. **リスクと緩和策**
- Risk: 初回 discovery でも attach が遅く、capture に失敗する。
- Mitigation: 初回は識別情報保存を主目的にし、capture 成功は best-effort と割り切る。

- Risk: foreground が Steam overlay などへ一時的に移り、誤学習する。
- Mitigation: `ExePath/ExeName` 一致を必須にし、visible top-level と fullscreen 条件を併用する。

- Risk: 2 回目でも fullscreen 完了待ちだと遅い。
- Mitigation: saved signature がある場合は process 出現時点から候補を絞り、attach タイミングを前倒しする。

- Risk: window class がアップデートで変わる。
- Mitigation: class だけに依存せず、exe path と fullscreen 条件を併用する。

11. **影響範囲**
- `Models/GraphicsHookLauncherTargetSignature.cs` — discovery 保存用フィールド追加
- `Services/Hook/LauncherTargetSignatureRegistry.cs` — discovery 保存/更新ロジック追加
- `Services/Hook/LauncherDiscoveryResolver.cs` — 初回 discovery 実装
- `Services/Hook/LauncherTargetResolver.cs` — saved signature fast path 追加
- `MainWindow.xaml.cs` または launcher orchestration 層 — discovery mode と fast path の分岐追加
- `Data/GraphicsHookLauncherSignatures.json` — discovery で自動蓄積する signature list

12. **Definition of Done**
- 初回 launcher 実行で、本命 window/process を観測した時点で signature を保存できる。
- 初回 capture が失敗しても、2 回目以降は saved signature を再利用できる。
- 問題タイトルで `signature_reused` ログを確認できる。
- 独占フルスクリーン title で、bootstrap PID ではなく render PID を優先して attach できる。
- manual learning を使わずに、最低限の self-learning launcher flow が成立する。
