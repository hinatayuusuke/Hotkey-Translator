# Translation Engine Legacy Cleanup + Extensible Registry 実装案

## 1. 概要（1-3行）
現状の翻訳パイプラインは実運用エンジン（LlamaCpp / Gemini / DeepL）と、既に退役扱いの CTranslate2 残留コードが混在している。  
本実装案では、残留コードを段階的に削除しつつ、将来の翻訳エンジン追加を「決め打ち」しない拡張可能なレジストリ構造へ整理する。  
目的は、保守コスト削減・設定互換維持・将来拡張の同時達成である。

## 2. ゴール / 非ゴール
### ゴール
- 退役済み翻訳エンジン（CTranslate2）関連の .NET 側残留コードを削除する。
- 翻訳エンジンの有効一覧・優先順位正規化を、固定配列依存から「登録済みプロバイダ依存」に変更する。
- 既存ユーザー設定（旧 `settings.json`）読み込み時に破綻しない互換パスを維持する。
- 将来エンジン追加時に、MainWindow/Settings全域の手作業改修を最小化できる土台を作る。

### 非ゴール
- 新規翻訳エンジン（例: DeepLX 等）の実装。
- OCR系（Paddle/NDL/VL）の構成変更。
- 既存翻訳品質ロジック（DeepL/Gemini/Llama の翻訳仕様）変更。

## 3. 前提・仮定
- 実際に `TranslationFallbackService` へ登録されている翻訳プロバイダは、現状 3 種のみ。
  - `MainWindow.xaml.cs` で登録: `LlamaGrpcTranslationProvider`, `DeepLTranslationProvider`, `GeminiTranslationProvider`
- CTranslate2 は「退役済み」扱いで、ホスト起動判定が常に false。
  - `Services/Application/ResourceHostFacade.cs` の `ShouldLoadCTranslate2` は常に false を返す。
- ただし設定/UI/ホスト/プロバイダ実装は多数残留しており、認知負荷と保守対象を増やしている。

## 4. 現状整理（精査結果）
### 4.1 実運用の翻訳実行経路
- `MainWindow.xaml.cs`
  - 翻訳プロバイダ登録は 3 種のみ（Llama/Gemini/DeepL）。
- `Services/TranslationFallbackService.cs`
  - 優先順位は `settings.TranslationPriority` + `TranslationProviderNames.Defaults` で正規化。

### 4.2 CTranslate2 残留コード（削除候補）
- 実装本体
  - `Services/CTranslate2GrpcTranslationProvider.cs`
  - `Services/CTranslate2GrpcHost.cs`
- ホスト管理
  - `Services/Application/ResourceHostFacade.cs`（Host descriptor / config / stop / disable）
- 設定
  - `Models/AppSettings.cs`（`EnableCTranslate2` と CTranslate2 一式）
  - `Services/Settings/SettingsHostNormalizer.cs`（CTranslate2 正規化）
  - `Services/Settings/Rules/CTranslate2HostSettingsRule.cs`
  - `Services/Settings/AppSettingsValidator.cs`（CTranslate2 rule 登録）
  - `Services/Settings/FeatureSettings/HostFeatureSettings.cs`
  - `Services/Settings/FeatureSettings/FeatureSettingsProvider.cs`
- UI
  - `MainWindow.xaml`（`Visibility="Collapsed"` の CTranslate2 セクション）
  - `ViewModels/SettingsViewModel.cs`（`EnableCTranslate2` / `CTranslate2DeviceTag`）
- 名称定義
  - `Models/TranslationProviderNames.cs` の `CTranslate2` 定数

### 4.3 その他の残留
- `Models/TranslationProviderNames.cs` に `GoogleWeb` 定数が存在（未使用）。
- `TranslationService/`（Python CTranslate2 gRPC サービス）がリポジトリ管理対象として残っている。

## 5. 提案アーキテクチャ
### 5.1 方針
- 「対応エンジン名の固定配列」に依存する箇所を減らし、**登録済みプロバイダから自動的に有効候補を得る**構造へ移行する。
- CTranslate2 削除はこの移行と同時に行い、削除後も次エンジン追加が容易な形を維持する。

### 5.2 コンポーネント構成（案）
- `ITranslationProvider`（既存）に加え、レジストリ層を導入。
  - 例: `TranslationProviderRegistry`（登録済み provider インスタンスから Name 一覧を提供）
- 優先順位正規化はレジストリ依存へ変更。
  - 現在: `TranslationProviderNames.Defaults` 固定
  - 変更後: `registry.GetRegisteredProviderNames(defaultOrderHint)`

### 5.3 データフロー
1. 起動時に provider を構築して registry に登録。
2. `settings.TranslationPriority` を registry の登録名でフィルタ/補完。
3. `TranslationFallbackService` は正規化済み順序で実行。
4. 未登録（削除済み）名は自動的にドロップして保存時に自然消滅。

## 6. インターフェース設計
### 6.1 追加I/F（案）
- `IReadOnlyList<string> ITranslationProviderCatalog.GetRegisteredProviderNames()`
- `IReadOnlyList<string> ITranslationProviderCatalog.GetDefaultPriority()`

### 6.2 既存I/Fの変更
- `TranslationProviderNames.Defaults` 依存箇所を catalog 依存へ置換。
- `MainWindow` の `NormalizeTranslationPriority` は catalog を入力として処理。

### 6.3 互換・バリデーション
- `settings.TranslationPriority` に未知名があれば drop（既存と同等挙動）。
- 旧 CTranslate2 設定値は migrate 時に無視し、必要なら1回だけログを出す。

## 7. 実装手順（ステップ分割）
### Step 1: Registry 導入（機能変更なし）
- provider 名取得を固定定数配列から切り離す。
- 正規化ロジックは従来通り、入力ソースだけ置換。

### Step 2: CTranslate2 実行コード削除
- 削除対象:
  - `Services/CTranslate2GrpcTranslationProvider.cs`
  - `Services/CTranslate2GrpcHost.cs`
- `ResourceHostFacade` から CTranslate2 descriptor / config / stop / disable を削除。

### Step 3: 設定モデル・正規化から CTranslate2 削除
- `AppSettings` の CTranslate2 プロパティ群を削除。
- `SettingsHostNormalizer` の CTranslate2 正規化関数を削除。
- `CTranslate2HostSettingsRule` 削除、`AppSettingsValidator` から rule 登録削除。
- `HostFeatureSettings` / `FeatureSettingsProvider` から `EnableCTranslate2` を除去。

### Step 4: UI / ViewModel クリーンアップ
- `MainWindow.xaml` の CTranslate2 セクションを削除（現在 collapsed の箇所）。
- `SettingsViewModel` から `EnableCTranslate2` / `CTranslate2DeviceTag` を削除。

### Step 5: 名称定義と残留定数整理
- `TranslationProviderNames` から未使用定数（`CTranslate2`, `GoogleWeb`）を削除、または legacy 領域へ分離。
- `TranslationPriority` の既定値生成は registry/catalog 経由に統一。

### Step 6: Python 側資産の扱い決定
- `TranslationService/` を削除対象とするか、`archive/` へ退避するかを決定。
- 削除する場合、README/Doc へ「退役済み」明記。

## 8. 非機能要件チェック
- 性能: 変化なし（主にコード整理）。
- セキュリティ: 不要な外部実行経路（CTranslate2 host）削減。
- 可観測性: 設定マイグレーション時に旧キー除去ログを残す。
- 互換性: 旧 `settings.json` 読込互換を維持（未知キーは JSON 読み込み時に無害）。
- 運用: 翻訳エンジン追加時に、registry登録＋provider実装で完結しやすくする。

## 9. リスクと緩和策
- Risk: 旧設定に CTranslate2 優先名が残っていると順序が意図せず変わる。
- Mitigation: 起動時正規化で未知名を drop し、保存時に確定順序を書き戻す。

- Risk: UI から項目を消したことで既存利用者が混乱する。
- Mitigation: リリースノート/Doc に「CTranslate2退役・削除済み」を明記。

- Risk: 将来のエンジン追加時に再び固定配列が増殖する。
- Mitigation: registry/catalog 経由以外で provider 名を参照しないルールを追加。

## 10. 影響範囲（変更ファイル候補）
- `MainWindow.xaml.cs`
- `MainWindow.xaml`
- `ViewModels/SettingsViewModel.cs`
- `Models/AppSettings.cs`
- `Models/TranslationProviderNames.cs`
- `Services/TranslationFallbackService.cs`
- `Services/Application/ResourceHostFacade.cs`
- `Services/CTranslate2GrpcTranslationProvider.cs`（削除候補）
- `Services/CTranslate2GrpcHost.cs`（削除候補）
- `Services/Settings/AppSettingsMigrator.cs`
- `Services/Settings/AppSettingsValidator.cs`
- `Services/Settings/SettingsHostNormalizer.cs`
- `Services/Settings/Rules/CTranslate2HostSettingsRule.cs`（削除候補）
- `Services/Settings/FeatureSettings/HostFeatureSettings.cs`
- `Services/Settings/FeatureSettings/FeatureSettingsProvider.cs`
- `TranslationService/*`（運用方針に応じて削除/退避）

## 11. Definition of Done
- [ ] CTranslate2 の .NET 実行経路（provider + host + facade 経路）が削除されている。
- [ ] CTranslate2 設定項目が AppSettings / SettingsViewModel / XAML から除去されている。
- [ ] 翻訳優先順位の正規化が固定配列依存から registry/catalog 依存へ移行している。
- [ ] 既存設定ファイル読込でクラッシュせず、未知エンジン名を安全に除去できる。
- [ ] 実運用翻訳（LlamaCpp / Gemini / DeepL）が従来通り動作する。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。

## Open Questions
- `TranslationService/`（Python CTranslate2資産）を完全削除するか、`archive` として残すか。
- provider registry を `MainWindow` 層に置くか、`Services` 層に分離するか。
