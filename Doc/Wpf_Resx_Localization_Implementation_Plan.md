# WPF Resx Localization Implementation Plan

## 1. 概要
- WPF UI の表示文言を `.resx` に集約し、日本語 UI と英語 UI を切り替え可能にする。
- 外部 i18n ライブラリは導入せず、`.NET / WPF` 標準の `resx + CultureInfo` を使う。
- 対象はまず設定コンソール UI と、その UI から発生する利用者向けメッセージに限定する。

## 2. ゴール / 非ゴール
### ゴール
- UI 文言をコードと XAML の直書きから切り離し、翻訳差し替え可能にする。
- `AppSettings` に UI 言語設定を保存し、次回起動時も同じ表示言語を維持する。
- UI 上で `English / 日本語 / System` を切り替えられるようにする。
- XAML と code-behind の両方で同じ翻訳キーを使える状態にする。

### 非ゴール
- OCR / 翻訳エンジンの言語設定 (`SourceLanguage`, `TargetLanguage`) を UI 言語設定と統合すること。
- ログ全文や内部診断文字列まで全面的に多言語化すること。
- 3 言語以上を前提にした複雑な文法変化、複数形ルール、外部翻訳配信基盤を導入すること。
- 設定画面の大規模再設計を同時に進めること。

## 3. 前提・仮定
- 現在の UI 文言は `MainWindow.xaml` と `UI/*.xaml` に多数直書きされている。
- 利用者向け文言は XAML だけでなく code-behind 側にも存在しうるため、XAML 専用の仕組みでは不十分である。
- 設定保存は `AppSettings` と `SettingsService` に集約されており、UI 言語設定の保存先として流用できる。
- 対応言語は当面 `ja` と `en` を主対象とし、必要時のみ将来拡張する。

## 4. 現状整理
### 現行 UI の状態
- [MainWindow.xaml](G:/APP Local/Hotkey-Translator/MainWindow.xaml) にサイドバー、タブ、共通ダイアログ文言が直書きされている。
- [UI/OverviewControl.xaml](G:/APP Local/Hotkey-Translator/UI/OverviewControl.xaml) など複数の `UserControl` に `Content=` / `Header=` / `Text=` が直書きされている。
- [Models/AppSettings.cs](G:/APP Local/Hotkey-Translator/Models/AppSettings.cs) に UI 言語設定は存在しない。
- [Services/SettingsService.cs](G:/APP Local/Hotkey-Translator/Services/SettingsService.cs) は設定のロード / 保存の責務を持っている。

### 現行課題
- UI 文言の変更箇所が分散しており、日本語 UI を追加すると差し替え漏れが起きやすい。
- XAML 専用のローカライズに寄せると、`MessageBox` や busy message など C# 側文言と仕組みが分裂する。
- 表示ラベルと内部値が同じ場所に混在しているため、翻訳対象と非対象を先に分ける必要がある。

## 5. 提案アーキテクチャ
### 5.1 基本方針
- ローカライズ基盤は `Resources/Strings.resx` を基準とし、日本語は `Resources/Strings.ja.resx` で上書きする。
- 表示文言だけを翻訳し、設定値、列挙値、内部キー、ファイルパス、API パラメータは翻訳しない。
- UI 言語は `AppSettings.UiLanguage` で保存し、起動時に `CultureInfo.CurrentUICulture` 相当へ反映する。

### 5.2 なぜ Resx を採用するか
- `WPF / .NET` 標準機構であり、追加ライブラリなしで運用できる。
- XAML と C# の両方で同じ翻訳キーを利用できる。
- 当面 `ja / en` の 2 言語運用であれば、過剰な抽象化を避けつつ十分な拡張性を持つ。
- AGENTS の方針どおり、互換性やフォールバックを必要最小限に留められる。

### 5.3 構成要素
1. `Resources/Strings.resx`
- 既定言語の UI 文字列を定義する。

2. `Resources/Strings.ja.resx`
- 日本語 UI 文字列を定義する。

3. `Services/LocalizationService.cs`
- 現在言語の保持、`CultureInfo` 解決、言語変更通知、文字列取得を担う。

4. `UI/Localization/LocExtension.cs`
- XAML から `{uiLoc:Loc Key=Sidebar_Overview}` のように参照するための `MarkupExtension` を提供する。

5. `Models/AppSettings.UiLanguage`
- `system`, `en`, `ja` のいずれかを保存する。

### 5.4 UI 切り替え方式
- 推奨は「アプリ再起動不要の即時反映」である。
- ただし初期段階では、まず `MainWindow` 配下の表示切り替えが成立することを優先する。
- `LocalizationService` の変更通知でバインディングを再評価し、開いている画面のラベルを即時更新する。
- `MessageBox` など都度生成される文言は、表示時点の現在言語で解決する。

### 5.5 翻訳対象 / 非対象の線引き
#### 翻訳対象
- サイドバー名、タブ名、セクション見出し、ラベル、ボタン文言、説明文。
- 利用者向けのエラーメッセージ、確認メッセージ、busy message。

#### 翻訳しないもの
- `Tag`, enum 値、保存 JSON 値、内部識別子。
- `DX11`, `VisionLLM`, `PaddleOCR`, `DeepL`, `Gemini`, `LlamaCpp` などの製品名 / エンジン名。
- 開発者向けログや診断出力。

## 6. インターフェース設計
### 6.1 設定モデル
- `AppSettings` に `public string UiLanguage { get; set; } = "system";` を追加する。
- `system` は OS の UI 言語に追従する。
- `en` と `ja` は明示固定とする。

### 6.2 LocalizationService
- `CultureInfo ResolveUiCulture(AppSettings settings)`
- `void ApplyUiLanguage(string uiLanguage)`
- `string GetString(string key)`
- `event EventHandler? LanguageChanged`

### 6.3 XAML 参照方式
- 直書きの `Content="Overview"` は `{uiLoc:Loc Key=Sidebar_Overview}` へ置き換える。
- WHY: `DynamicResource` だけでは C# 側の文字列取得基盤と分断されるため、キー解決の入口を 1 つに揃える。

### 6.4 UI 置き場
- 言語切り替え UI はまず `System` 設定画面に置く。
- 項目名は `UI Language` とし、選択肢は `System`, `English`, `日本語` とする。
- 常用操作ではないため、`Overview` に最初から常設しない。

## 7. 実装手順（ステップ分割）
### Step 1: 基盤追加
- `Resources/Strings.resx` と `Resources/Strings.ja.resx` を追加する。
- `AppSettings.UiLanguage` を追加し、既定値を `system` とする。
- `LocalizationService` を追加する。

### Step 2: 起動時反映
- アプリ起動時に保存済み `UiLanguage` を読み込み、UI カルチャを設定する。
- `system` 指定時は OS の UI 言語を `ja` / `en` に正規化する。

### Step 3: 切り替え UI 追加
- `SystemSettingsControl` に UI 言語選択 UI を追加する。
- 変更時は `LocalizationService` に即時反映し、保存対象にも反映する。

### Step 4: 主要画面のキー化
- `MainWindow.xaml` のサイドバー、タブ、共通ボタンを `LocExtension` に置換する。
- `OverviewControl`, `TranslationControl`, `SystemSettingsControl`, `RuntimeLogsControl` の主要文言をキー化する。

### Step 5: code-behind 文言の置換
- `MessageBox`、ロード失敗文言、busy message などの利用者向け文言を `LocalizationService.GetString()` 経由へ置換する。

### Step 6: 例外 / 未移行箇所の整理
- 一時的に英語直書きが残る箇所を棚卸しし、翻訳キーへ寄せる。
- 未翻訳キーは既定言語へフォールバックし、空文字にはしない。

## 8. 非機能要件チェック
### 性能
- `.resx` 参照は軽量であり、このアプリ規模では性能ボトルネックになりにくい。
- 即時切り替え時は再生成コストを避けるため、必要な範囲だけ通知更新する。

### セキュリティ
- ローカライズ基盤には秘密情報を入れない。
- `ApiKey` などの機微情報は翻訳対象にしない。

### 可観測性
- 未解決キーはログへ出す余地を残す。
- WHY: 文字列欠落を無音で見逃すと、日本語化の完了判定が曖昧になるため。

### 互換性
- `UiLanguage` は新規設定キー追加のみで、既存設定互換を壊さない。
- 保存済み設定に `UiLanguage` が無い場合は `system` 扱いでロードする。

## 9. リスクと緩和策
- Risk: 文言直書きが広範囲に散っており、キー化漏れが起きる。
- Mitigation: `rg -n 'Content=\"|Header=\"|Text=\"' MainWindow.xaml UI/*.xaml` のような棚卸しで移行対象を一覧化する。

- Risk: XAML 側は翻訳できても C# 側メッセージが英語のまま残る。
- Mitigation: `MessageBox`, `BusyMessage`, `ShowLoadFailure` 系を優先対象として別枠で洗い出す。

- Risk: UI 切り替え時に一部コントロールが即時更新されない。
- Mitigation: 初期実装では「主要ラベルが即時更新されること」を完了条件にし、難所は再描画トリガを追加して段階対応する。

## 10. 影響範囲（変更候補）
- `Models/AppSettings.cs`
- `Services/SettingsService.cs`
- `App.xaml.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `UI/SystemSettingsControl.xaml`
- `UI/OverviewControl.xaml`
- `UI/TranslationControl.xaml`
- `UI/RuntimeLogsControl.xaml`
- `UI/*Control.xaml` の表示文言を持つファイル
- `Resources/Strings.resx`
- `Resources/Strings.ja.resx`
- `Services/LocalizationService.cs`
- `UI/Localization/LocExtension.cs`

## 11. Definition of Done
- [ ] `AppSettings` に `UiLanguage` が追加され、保存 / 再読み込みできる。
- [ ] `System` 設定画面で `System / English / 日本語` を選択できる。
- [ ] `MainWindow` のサイドバーと主要タブが選択言語に応じて切り替わる。
- [ ] `Overview` と `System` の主要ラベルが選択言語に応じて切り替わる。
- [ ] code-behind 側の主要な利用者向けメッセージが翻訳経由になる。
- [ ] `ja` / `en` の両方で未翻訳キーや空表示が発生しない。

## 12. 補足
- 初期フェーズでは `.resx` ベースの最小実装を優先する。
- 将来、3 言語以上の継続運用や非開発者による翻訳更新が必要になった場合のみ、外部 i18n 基盤の再評価を行う。
