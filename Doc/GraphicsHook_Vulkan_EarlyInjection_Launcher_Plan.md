# GraphicsHook Vulkan 早期注入 Launcher 実装案

1. **概要（1–3行）**
- Vulkan 後追い Hook の取りこぼしを減らすため、`CREATE_SUSPENDED -> inject -> resume` の Launcher 経路を最小実装する。
- UI は `EXE選択 + 引数 + 起動してHook` の最小操作に限定する。
- 誤操作防止として、Vulkan早期注入モード中は「起動済みプロセス選択」を無効化する。

2. **ゴール / 非ゴール**
### ゴール
- ユーザーが EXE を選択して対象を起動し、起動直後に Hook を成立させる。
- Vulkan早期注入モード中に後追いAttach操作へ入れないよう UI/ロジックでガードする。
- 失敗時に理由を表示し、通常運用（既存後追いモード）へ戻せるようにする。

### 非ゴール
- 既に起動済みのプロセスに対して、早期注入同等の成功率を保証すること。
- Vulkan Layer 方式の実装。
- DX11/DX12/OpenGL の起動フロー変更。

3. **前提・仮定**
- 現行アプリは `HookHost.exe` を介して Attach/Detach を行う構成。
- Vulkan は後追い注入で `vkGet*ProcAddr` 取得後に間に合わないケースがある。
- リリース前段階のため、破壊的変更よりも最小導入を優先する。

4. **現状整理**
- 現行操作は「起動済みウィンドウを選択して固定 -> Hook attach」。
- Vulkan タイトルで `install_hook_result=ok` でも `queue_present_enter` 未到達のケースがある。
- これは「注入は成功したが dispatch取得時点を取り逃がした」可能性が高い。

5. **提案アーキテクチャ**
### コンポーネント構成
- `LauncherService`（新規）
  - EXE 起動 (`CreateProcess`)。
  - 早期注入時は `CREATE_SUSPENDED` で起動。
  - HookHost へ attach 要求成功後に `ResumeThread`。
- `MainWindow / SettingsViewModel`（既存拡張）
  - Vulkan早期注入モードON/OFF。
  - EXE参照、引数入力、起動ボタン。
  - モード中の起動済みプロセス選択ボタン無効化。

### データフロー / シーケンス
1. ユーザーが `参照...` で EXE を選択。
2. `起動してHook` で `CreateProcess(..., CREATE_SUSPENDED)`。
3. 子プロセス PID を使って HookHost へ `attach(api=Vulkan)`。
4. attach 成功で `ResumeThread`。
5. 失敗時は子プロセスを終了してエラー表示、または未resumeで中断。

### 既存パターンへの整合
- 既存の `ApplySettingsAsync` / attach 経路を再利用し、起動責務だけ追加する。
- 後追いAttachモードは維持し、Vulkan早期注入モード時のみ UI ガードを適用する。

6. **インターフェース設計**
### 設定追加
- `EnableVulkanEarlyInjectionLauncher: bool`（既定 `false`）
- `VulkanLauncherExePath: string`（既定空）
- `VulkanLauncherArgs: string`（既定空）

### UI追加
- `参照...` ボタン（`OpenFileDialog`、`*.exe`）
- `EXEパス` テキスト
- `引数` テキスト
- `起動してHook` ボタン

### 制御ガード
- `EnableVulkanEarlyInjectionLauncher == true` かつ `GraphicsHookApi == Vulkan` のとき:
  - F7固定や既存の「起動済みプロセス選択」を無効化。
  - Tooltip: `Vulkan早期注入モード中は起動済みプロセス選択は使用できません。`

7. **実装手順（ステップ分割）**
- Step 1: 設定とUI最小追加
  - 設定3項目追加。
  - EXE参照/引数/起動ボタン追加。
- Step 2: LauncherService 実装
  - `CreateProcess`（suspended）
  - attach 呼び出し
  - success で `ResumeThread`
  - fail で cleanup
- Step 3: 誤操作防止ガード
  - Vulkan早期注入モード時に起動済みプロセス選択をUI・コマンド両面で無効化。
- Step 4: 監視ログ追加
  - `stage=graphics_hook event=launcher_start/launcher_attach_ok/launcher_resume/launcher_fail`

8. **非機能要件チェック**
- 性能: 起動時のみ追加処理。ランタイム負荷は増やさない。
- セキュリティ: 起動対象はユーザー選択EXEのみ。コマンドライン組み立て時に引用符を厳密化。
- 可観測性: Launcher各段階を既存ログへ出力。
- 互換性: モードOFF時は既存挙動を維持。

9. **リスクと緩和策**
- Risk: attach失敗時に suspend プロセスが残る。
- Mitigation: fail時に `TerminateProcess` または `Resume + Detach` を必ず実施しログ出力。
- Risk: ユーザーがモードを理解せず既存操作を期待する。
- Mitigation: モードON時の無効化表示と明確な補助文言を常時表示。

10. **影響範囲**
- `Models/AppSettings.cs` — 設定項目追加。
- `ViewModels/SettingsViewModel.cs`（または相当） — 設定バインド追加。
- `MainWindow.xaml` — Launcher UI追加、既存選択UIの有効/無効バインド。
- `MainWindow.xaml.cs`（またはコントローラ） — 起動コマンドとガード。
- `Services/Hook/GraphicsHookClientService.cs` — launcher用attach補助API（必要時）。
- `Services/Hook/` 新規 `VulkanLauncherService.cs` — 起動と早期注入。

11. **Definition of Done**
- Vulkan早期注入モードで EXE選択 -> 起動してHook が実行できる。
- 同モード中は起動済みプロセス選択が UI と内部コマンドの両方で拒否される。
- 失敗時に理由付きエラーメッセージと cleanup が動作する。
- ログに launcher 各段階が出力される。
- モードOFF時に従来操作（起動済みプロセス固定）がそのまま使える。
