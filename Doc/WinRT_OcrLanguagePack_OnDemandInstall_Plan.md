# WinRT OCR Language Pack On-Demand Install Plan

## 1. Overview
WinRT OCR を選択中に、OS に対象言語の OCR 言語パックが存在しない場合、ユーザーに確認ダイアログを出し、同意時のみ OCR 言語パックをインストールする。

## 2. Goal / Non-Goal
### Goal
- WinRT OCR 実行前に言語対応可否を判定する。
- 未対応時に OK/Cancel ダイアログでユーザー意思を確認する。
- OK 時に OCR 言語パック導入を試行し、成功後に再判定して WinRT OCR を継続する。

### Non-Goal
- 複数言語を一括導入する UI。
- WinRT 以外の OCR エンジンに対する自動導入。
- 高度な OS 管理機能（企業ポリシー連携、GPO 検出）の自動対応。

## 3. Scope
- 対象: `OcrEngineKind.WinRt` のみ。
- トリガ: RunOnce 実行前（F8/F10/自動実行経路で WinRT 使用時）。
- UI: 最小ダイアログ 1枚（OK/Cancel）。

## 4. Current State
- WinRT OCR は `Services/OcrEngine.cs` の `WinRtOcrProvider` 経路で実行される。
- 言語未対応時は実行失敗またはフォールバックで原因が見えづらい。
- 既存で最小ダイアログ基盤（MessageBox）が `MainWindow.xaml.cs` にある。

## 5. Proposed Design
### Components
- `WinRtOcrLanguagePackCoordinator`（新規・Service）
  - 言語可否判定
  - ダイアログ表示判断（1回表示制御キー）
  - インストール試行
- `WindowsCapabilityInstaller`（新規・Service）
  - `Add-WindowsCapability`（または DISM）実行
  - 結果コード・stderr 収集

### Flow
1. 実行前に `settings.OcrEngine == WinRt` を確認。
2. `source language` から WinRT OCR locale を解決。
3. `IsLanguageSupported` で可否判定。
4. 未対応なら確認ダイアログ:
   - OK: 導入処理へ
   - Cancel: 今回の WinRT OCR を中止（既存挙動に合わせて run 中断）
5. 導入成功後に再判定:
   - 対応済み: WinRT OCR 継続
   - 未対応: エラーダイアログ表示して中止

## 6. Interface Design
### Coordinator
- `Task<WinRtLanguagePackResult> EnsureLanguagePackAsync(AppSettings settings, CancellationToken ct)`
- 戻り値:
  - `Ready`
  - `UserCanceled`
  - `InstallFailed`
  - `NotApplicable`（WinRT以外）

### Installer
- `Task<CapabilityInstallResult> InstallOcrLanguageCapabilityAsync(string localeTag, CancellationToken ct)`
- 戻り値:
  - `Succeeded`
  - `FailedExitCode`
  - `PermissionDenied`
  - `UnsupportedEnvironment`

## 7. Capability Mapping
- `en` -> `en-US`（既存の WinRT 解決ロジックに準拠）
- `ja` -> `ja-JP`
- `ko` -> `ko-KR`
- `zh-Hans` -> `zh-CN`
- `zh-Hant` -> `zh-TW`
- `ru` -> `ru-RU`

Capability 名は `Language.OCR~~~<locale>~0.0.1.0` を第一候補として実装。

## 8. UX Policy
- ダイアログタイトル: `OCR language pack required`
- メッセージ:
  - 不足言語
  - インストールに管理者権限とネットワークが必要な可能性
  - OK/Cancel の意味
- 同じ言語で同セッション中は 1回だけ表示（過剰通知防止）。

## 9. Failure Handling
- 管理者権限不足: 明示メッセージ（再実行/手動導入案内）
- ネットワーク不可: 明示メッセージ
- 組織ポリシーで禁止: 明示メッセージ
- 失敗時は WinRT OCR 実行を中止し、誤検出結果を返さない。

## 10. Implementation Steps
1. WinRT 前提チェック Coordinator を追加。
2. OS capability インストーラを追加（PowerShell/DISM 実行）。
3. `MainWindow` 実行前フローに Coordinator 呼び出しを組み込む。
4. ダイアログ（OK/Cancel）と 1回表示制御を追加。
5. 成功/失敗/キャンセルの分岐を RunOne フローへ反映。
6. ログを最小追加（開始/結果/失敗理由）。

## 11. Non-Functional Considerations
- Security: 外部コマンド実行は引数固定・ユーザー入力の直結禁止。
- Observability: install 開始/終了/exit code をログ化。
- Compatibility: Win10/Win11 でコマンド差異を吸収（PowerShell 優先、必要なら DISM fallback）。
- Performance: 通常時（言語対応済み）は判定のみで即時復帰。

## 12. Risk / Mitigation
- Risk: インストール処理が長時間化。
- Mitigation: 明確な進行表示とキャンセル方針、タイムアウト設定。

- Risk: 管理者権限で失敗。
- Mitigation: 失敗理由をユーザー向けに短文表示し、手動導入手順へ誘導。

## 13. DoD
- WinRT 未対応言語で OK/Cancel ダイアログが出る。
- OK 後に導入成功すれば WinRT OCR が継続される。
- Cancel で実行が安全に中止される。
- 同じ言語で同セッション中にダイアログ連打が起きない。
- ビルド成功。
