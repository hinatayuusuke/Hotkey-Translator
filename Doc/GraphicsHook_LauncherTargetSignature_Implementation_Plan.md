# GraphicsHook Launcher Target Signature Implementation Plan

> NOTE: This document describes an earlier countdown-learning-oriented alternative.
> Current implementation direction is [GraphicsHook_DiscoveryFirstSignature_Implementation_Plan.md](./GraphicsHook_DiscoveryFirstSignature_Implementation_Plan.md).

1. **概要（1–3行）**
- Graphics Hook launcher が bootstrap process と実 render process を取り違えるタイトルに対して、`process/window signature` を用いて正しい PID を選ぶ。
- まずは汎用 heuristics と最小のタイトル別 signature list を組み合わせ、正しい PID に早期 attach できる launcher handoff を実現する。
- signature は手書き登録だけでなく、ユーザーが通常起動したゲームをタイマー経由で学習して作れる形にする。
- Vulkan を主対象にするが、設計は Dx9 / Dx11 の handoff 問題にも流用できる形に寄せる。

2. **ゴール / 非ゴール**
### ゴール
- launcher 起動後、同名 bootstrap / child / re-spawn process が混在しても、正しい render process を選べること。
- 問題タイトルごとに「変わらない識別情報」を持てる最小の signature list を導入すること。
- ユーザーが countdown 後の foreground window から正解 signature を学習できること。
- Vulkan で「正しい PID に早期 attach する」ための判断ロジックを実装できる状態まで設計を固めること。

### 非ゴール
- すべてのゲームを初回から自動判定する巨大なゲームDBを作ること。
- anti-cheat / 保護プロセス / 権限昇格対応。
- Vulkan late attach を完全に成立させるための HookAgent 大改修。
- リモート更新やクラウド配布の仕組みをこの段階で入れること。

3. **前提・仮定**
- 現行 launcher は `CreateProcess(..., CREATE_SUSPENDED)` で起動した `launched.ProcessId` を、そのまま `FixedCaptureWindowProcessId` と hook attach 対象にしている。
- 実ログでは、`attachPid` と最終 top-level window owner PID が異なるケースが確認されている。
- x86 Vulkan は `procaddr_only` のため、正しい PID が後から分かっても late attach 成功率は高くない。したがって「正しい PID を早く見つけて attach する」必要がある。
- 変わらない識別情報としては `ExeName`、`ExePath`、`WindowClass`、`WindowTitle` の傾向、可視 top-level window 条件、window size が利用可能である。
- Hook を使う主用途は独占フルスクリーンであり、ユーザーが countdown 中に他の操作をしない前提なら、`GetForegroundWindow()` を learning 用の正解候補として扱える。

4. **現状整理**
- 現在の probe で `bootstrapPid != windowPid` が確認できるタイトルがある。
- 同名 process が複数存在するケースでは `ProcessName` 単独では不十分である。
- `WindowBindingService.TryResolveWindowHandle(...)` は「既に PID が合っている window を見つける」処理には使えるが、「どの PID に attach すべきか」を決める責務は持っていない。
- 現在の launcher は title-specific な識別情報を保存しておらず、`FixedCaptureWindowClassName` / `FixedCaptureWindowTitle` も launcher 起動直後は空である。

5. **提案アーキテクチャ**
### コンポーネント構成
- `LauncherTargetSignatureRegistry`
  - タイトル別の signature list を保持する。
- `LauncherTargetResolver`
  - 起動後に process / top-level window を監視し、signature と heuristics を使って本命 PID を選ぶ。
- `LauncherTargetLearningService`
  - countdown 完了時の foreground window を採取し、signature の学習データを作る。
- `GraphicsHookLauncherService`
  - launcher 起動と監視開始の責務を維持する。
- `GraphicsHookClientService`
  - 最終決定した PID に対して attach を行う。

### データフロー / シーケンス
1. launcher が bootstrap process を suspended で起動する。
2. bootstrap process に対して現行どおり最小 attach を試みるか、あるいは resume 後に監視専用モードへ入る。
3. `LauncherTargetResolver` が短時間 process / window を監視する。
4. `LauncherTargetSignatureRegistry` に対象タイトルの signature があれば優先適用する。
5. signature が無ければ汎用 heuristics で候補を選ぶ。
6. 候補 PID が確定したら、その PID / HWND / class / title を `FixedCaptureWindow*` へ反映し、本命 PID に attach する。

### learning フロー
1. ユーザーが UI で countdown 秒数を指定して learning を開始する。
2. ユーザーはゲームを通常起動し、その後は操作せずに待つ。
3. countdown が 0 になった時点で `LauncherTargetLearningService` が `GetForegroundWindow()` を取得する。
4. foreground window が visible / ownerless / 十分なサイズを満たすか検証する。
5. 検証を通ったら process / window 情報から signature を生成して保存する。
6. 次回 launcher 起動時は、その signature を優先して正しい PID を選ぶ。

### 既存パターンへの整合
- settings へ重い DB を埋め込まず、まずはローカルの小さな signature list と resolver service を追加する。
- 既存の `WindowBindingService` は window 再解決に専念させ、PID 選択は launcher resolver へ分離する。
- fail fast 方針を維持し、曖昧な候補しかない場合は明示ログを出して attach しない。

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
    public int MinClientWidth { get; set; } = 640;
    public int MinClientHeight { get; set; } = 360;
    public bool RequireOwnerlessWindow { get; set; } = true;
    public int MonitorWindowScoreBonus { get; set; } = 0;
    public bool LearnedFromForegroundCountdown { get; set; }
    public DateTimeOffset? LearnedAtUtc { get; set; }
}
```

### 6.2 runtime candidate モデル
候補: `Services/Hook/LauncherTargetCandidate.cs`

```csharp
internal readonly record struct LauncherTargetCandidate(
    int ProcessId,
    string ProcessName,
    string ExePath,
    IntPtr Hwnd,
    string WindowClass,
    string WindowTitle,
    bool Visible,
    bool Ownerless,
    int Width,
    int Height,
    int Score,
    string Reason);
```

### 6.3 learning request モデル
候補: `Models/GraphicsHookLauncherLearningRequest.cs`

```csharp
public sealed class GraphicsHookLauncherLearningRequest
{
    public int DelaySeconds { get; set; } = 5;
    public GraphicsHookApiKind? PreferredApi { get; set; }
    public bool RequireFullscreenLikeWindow { get; set; } = true;
}
```

### 6.4 registry 形式
初期段階ではコード内 static list か JSON ファイルのどちらかを選ぶ。

推奨初期案:
- `Doc/` ではなく実行対象側に `Data/GraphicsHookLauncherSignatures.json` のような JSON を置く
- 無ければ空リストで動作
- まずは 1〜数タイトルだけを登録

JSON 例:

```json
[
  {
    "Key": "senran-kagura-shinovi-versus",
    "ExeName": "SKShinoviVersus",
    "PreferredApi": "Vulkan",
    "WindowClassAllowList": [ "SKShinoviVersus", "SDL_app" ],
    "WindowTitleContainsAny": [ "SENRAN KAGURA SHINOVI VERSUS" ],
    "RequireVisibleTopLevel": true,
    "MinClientWidth": 1280,
    "MinClientHeight": 720,
    "RequireOwnerlessWindow": true,
    "LearnedFromForegroundCountdown": true,
    "LearnedAtUtc": "2026-03-15T03:10:00Z"
  }
]
```

### 6.5 resolver API
候補:

```csharp
internal sealed class LauncherTargetResolver
{
    public Task<LauncherTargetResolutionResult> ResolveAsync(
        int bootstrapPid,
        string expectedExeName,
        string? expectedExePath,
        CancellationToken cancellationToken);
}
```

戻り値:

```csharp
internal readonly record struct LauncherTargetResolutionResult(
    bool Success,
    int SelectedPid,
    IntPtr SelectedHwnd,
    string Reason,
    bool SignatureMatched,
    bool DescendantMatched);
```

### 6.6 learning API
候補:

```csharp
internal sealed class LauncherTargetLearningService
{
    public Task<GraphicsHookLauncherTargetSignature?> LearnFromForegroundAsync(
        GraphicsHookLauncherLearningRequest request,
        CancellationToken cancellationToken);
}
```

成功時の出力:
- `ExeName`
- `ExePathSuffix`
- `WindowClassAllowList`
- `WindowTitleContainsAny`
- `MinClientWidth` / `MinClientHeight`
- `LearnedFromForegroundCountdown`
- `LearnedAtUtc`

7. **候補選定ロジック**
### 優先順位
1. `ExeName` 一致
2. `ExePathSuffix` 一致
3. `signature.WindowClassAllowList` 一致
4. `signature.WindowTitleContainsAny` 一致
5. visible top-level window
6. ownerless window
7. 一定以上の rect / client size
8. bootstrap の子孫であれば加点

### 重要な判断
- `descendant` は必須条件ではなく加点条件に下げる。
  理由: 実ログ上、同名の本命 process が bootstrap の子孫でないケースがあるため。
- `ProcessName` 単独一致では attach しない。
  理由: bootstrap と本体が同名のケースを区別できないため。
- learning 時の `foreground window` は runtime attach 条件にしない。
  理由: countdown learning では有効でも、毎回 foreground 待ちすると Vulkan attach が遅れるため。
- 候補が複数で同点なら attach しない。
  理由: Vulkan の誤 attach コストが高いため。

8. **実装手順（ステップ分割）**
- Step 1: signature モデルと registry を追加
  - static list でもよいが、将来的な拡張を考えると JSON 読み込みのほうが扱いやすい。
  - 読み込み失敗時は空リストで継続する。

- Step 2: resolver を実装
  - process 一覧
  - top-level window 一覧
  - score 計算
  - best candidate の選定
  - candidate と score をログへ出す

- Step 3: launcher flow へ統合
  - launcher resume 後に resolver を開始
  - best candidate が安定した時点で `FixedCaptureWindow*` を更新
  - 既存 attach 済み PID と異なる場合は detach / reattach

- Step 4: 初回 learning 補助
  - countdown learning UI を追加する
  - countdown 完了時の foreground window から signature を生成して保存する
  - 保存前に visible / ownerless / size 妥当性チェックを行う
  - 初回だけは通常起動で正解 window を学習し、次回 launcher からその signature を使う

- Step 5: 問題タイトルで検証
  - `SKShinoviVersus`
  - `vkcube`
  - 既存成功タイトル

9. **learning / 運用案**
### 最小運用
- 問題タイトルが出たら一度通常起動で正しい window を学習する
- learning で得た情報から signature を 1 件保存する
- 次回以降 launcher が自動で本命 PID を選ぶ

### learning 用の入力候補
- `DelaySeconds`
- `PreferredApi`

### countdown learning 手順
1. ユーザーがアプリで learning 用の秒数を設定して `開始` を押す
2. ユーザーがゲームを通常起動する
3. 起動後は触らずに待つ
4. 0 秒時点の `foreground window` を採取する
5. visible / ownerless / 十分なサイズであれば signature を保存する

### learning で保存する情報
- `ExeName`
- `ExePath`
- `WindowClass`
- `WindowTitle`
- `ClientRect`
- `PreferredApi`
- `LearnedAtUtc`
- `LearnedFromForegroundCountdown`

### learning 補助の形
- 最初の段階では明示ボタンでのみ保存する
- 誤学習を避けるため、採取直後に確認ダイアログかログ要約を出す
- runtime attach は saved signature を使い、foreground 待ちはしない

10. **非機能要件チェック**
- 性能: launcher 後数秒の監視と score 計算のみで、常駐コストは小さい。
- セキュリティ: ローカルの process / window 列挙のみ。権限昇格なし。
- 可観測性: candidate 一覧、selected candidate、mismatch reason をログへ残す。
- 互換性: signature が無いタイトルは現行 heuristics で動作する。
- 運用: signature list が増えすぎないよう、タイトル別の問題が確認されたものだけ追加する。

11. **リスクと緩和策**
- Risk: 同名 process が複数存在し、signature が弱いと誤選択する。
- Mitigation: class/title/rect/ownerless を複合条件にし、同点時は attach しない。

- Risk: 正しい PID 発見が遅れて Vulkan 初期化後になる。
- Mitigation: launcher resume 直後から高頻度監視し、候補が安定したら即 attach する。

- Risk: countdown 時点で Steam overlay や別 window が foreground になり、誤学習する。
- Mitigation: learning は独占フルスクリーン前提に限定し、visible / ownerless / size 条件を満たさない場合は保存しない。

- Risk: タイトルごとの signature list が増え、保守負荷が上がる。
- Mitigation: デフォルトは heuristics、signature は問題タイトルのみ追加する。

- Risk: title/class がアップデートで変わる。
- Mitigation: `WindowTitleContainsAny` を補助条件に留め、複数の弱い条件を組み合わせる。

12. **影響範囲**
- `Models/GraphicsHookLauncherTargetSignature.cs` — signature モデル追加
- `Models/GraphicsHookLauncherLearningRequest.cs` — countdown learning 要求モデル追加
- `Services/Hook/LauncherTargetSignatureRegistry.cs` — signature list 読み込み
- `Services/Hook/LauncherTargetResolver.cs` — candidate 選定と score 計算
- `Services/Hook/LauncherTargetLearningService.cs` — foreground countdown learning 実装
- `MainWindow.xaml.cs` または launcher orchestration 層 — resolver 結果の反映と reattach
- 必要なら `AppSettings` — learning の保存先や countdown 秒数
- 必要なら settings UI — learning 開始ボタンと秒数入力
- `Data/GraphicsHookLauncherSignatures.json` — 問題タイトル用 signature list

13. **Definition of Done**
- `SKShinoviVersus` のような bootstrap / render PID 分離タイトルで、本命 PID を自動選定できる。
- 同名 process が複数あっても、window signature により正しい PID を選べる。
- ユーザーが countdown learning で正解 signature を保存できる。
- candidate 選定理由がログで追える。
- signature が無いタイトルでも既存挙動を大きく壊さない。
- Vulkan で「正しい PID に早期 attach」できる launcher handoff 実装へ進める前提が揃う。
