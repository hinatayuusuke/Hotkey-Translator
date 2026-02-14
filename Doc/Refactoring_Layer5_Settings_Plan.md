# Refactoring_Layer5_Settings_Plan

1. **概要（1–3行）**
- 本計画は、層5（Settings）として `AppSettings` / `SettingsService` / `SettingsUiController` に分散している検証・正規化・互換・秘匿化責務を集約する実装案である。
- 目的は、設定追加時の影響範囲を局所化し、`settings.json` 互換を保ちながら保守性と検証可能性を上げること。
- 既存挙動互換（既存キー読込、DPAPI、正規化後の実行挙動）を維持した段階移行で実施する。

2. **ゴール / 非ゴール**
### ゴール
- `SettingsService` を「永続化・暗号化・排他保存」に限定し、正規化責務を `AppSettingsValidator` 系へ移管する。
- 現在 `SettingsUiController` にある `Normalize*` 群をルール単位の validator/migrator に分離する。
- 機能単位設定（Scene/OCR/Overlay/Translation/Host/Capture）の参照導線を導入し、変更時の副作用追跡を簡単にする。

### 非ゴール
- OCR/翻訳/Capture アルゴリズム変更。
- 一度で `settings.json` を完全に新スキーマへ置換すること。
- UI の項目構成変更。

3. **前提・仮定**
- 現行 `SettingsService` は JSON 読み書きと DPAPI（`ApiKeyProtected` / `DeepLApiKeyProtected`）を担当している。
- 正規化責務は主に `SettingsUiController` にあり、`NormalizeOnLoad` と `SaveFromUiAsync` で重複呼び出しされる。
- `ResourceHostFacade` 等の他モジュールが `SettingsUiController.NormalizeLlamaSettings` など静的正規化を直接利用している。
- `settings.json` は既存ユーザー資産のため後方互換が必須。

4. **現状整理**
- `Models/AppSettings.cs`
- 課題: 200+ 項目が単一型に集中し、機能境界が曖昧。
- 課題: デフォルト値・互換制約・型制約の根拠が型内で可視化されにくい。

- `Services/SettingsService.cs`
- 課題: 永続化責務は明確だが、保存前バリデーション/正規化契約が外部依存。
- 課題: 秘密情報保護（DPAPI）とファイルI/Oが密結合でテストしづらい。

- `Services/Application/SettingsUiController.cs`
- 課題: UI制御と設定正規化ロジックが混在。
- 課題: 互換マイグレーション（legacy hotkey 等）が UI レイヤに存在し、再利用性が低い。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `ISettingsRepository` / `JsonSettingsRepository`（新規）
- 役割: `settings.json` の読み書き、バックアップ、atomic save。

- `ISecretProtector` / `DpapiSecretProtector`（新規）
- 役割: APIキー系の保護/復号。`SettingsService` から分離。

- `AppSettingsValidator`（新規）
- 役割: 設定全体の検証/正規化を統括。
- 内部: `ISettingsRule`（Hotkey/Scene/Ocr/Paddle/Llama/Host/Capture 等）のルールチェーン。

- `AppSettingsMigrator`（新規）
- 役割: 互換移行（legacy key/default 競合）を version-aware に管理。

- `FeatureSettings` ビュー（新規）
- 役割: `AppSettings` を機能単位で読むための軽量参照モデル。
- 例: `SceneSettings`, `OcrSettings`, `HostSettings`, `OverlaySettings`。

- `SettingsFacade`（新規, 既存 `SettingsService` の上位）
- 役割: Load->Migrate->Validate->Expose、Save->Validate->Protect->Persist の一貫パイプライン。

### 5.2 データフロー / シーケンス
1. 起動時: `SettingsFacade.LoadAsync()` が repository から読み込み。
2. `AppSettingsMigrator` が互換移行を適用。
3. `AppSettingsValidator` がルール単位で正規化し、変更点を `SettingsValidationReport` に記録。
4. 実行中: 各層は `FeatureSettings` 経由で必要設定のみ参照。
5. 保存時: validator 再適用後、secret protector で保護して repository が atomic save。

### 5.3 既存パターンへの整合
- `SettingsService.Settings` の参照契約は段階的に維持し、当面は adapter 経由で互換提供。
- `SettingsUiController` は UI 連携に専念し、正規化/互換ロジックは `SettingsFacade` 呼び出しへ置換。

6. **インターフェース設計**
### 6.1 主要 DTO / I/F（案）
- `ISettingsRepository`
- `Task<AppSettings?> LoadAsync(CancellationToken token)`
- `Task SaveAsync(AppSettings settings, CancellationToken token)`

- `ISecretProtector`
- `string Protect(string plain)`
- `string? Unprotect(string cipher)`

- `ISettingsRule`
- `string RuleId { get; }`
- `bool Apply(AppSettings settings, SettingsValidationReport report)`

- `AppSettingsValidator`
- `SettingsValidationReport ValidateAndNormalize(AppSettings settings)`

- `AppSettingsMigrator`
- `SettingsMigrationReport Migrate(AppSettings settings)`

- `FeatureSettingsProvider`
- `SceneSettings GetScene(AppSettings settings)`
- `OcrSettings GetOcr(AppSettings settings)`
- `HostSettings GetHost(AppSettings settings)`

### 6.2 エラー・バリデーション
- load 失敗時は `new AppSettings()` fallback を維持しつつ、migration/validation report をログ出力。
- 秘密情報復号失敗時は現行同様 `null` 扱いで継続し、警告ログのみ追加。

7. **実装手順（ステップ分割）**
- Step 1: 既存挙動の固定
- `SettingsUiController.Normalize*` の結果差分をログ化し、回帰比較キーを固定。

- Step 2: 永続化責務分離
- `ISettingsRepository` と `ISecretProtector` を導入し、`SettingsService` から I/O と秘匿化を分離。

- Step 3: ルールベース正規化導入
- Hotkey/SceneSemantic/WritingMode/SmallBox/Paddle/Llama/CTranslate2 正規化を `ISettingsRule` 群へ移設。

- Step 4: 互換移行の分離
- legacy hotkey や mode 競合解消を `AppSettingsMigrator` に移し、UI 層から除去。

- Step 5: FeatureSettings 導入
- 主要利用箇所（SceneChangeController, ResourceHostFacade, PipelineOrchestrator）で feature view 参照を開始。

- Step 6: 呼び出し口統一
- `SettingsUiController` の `NormalizeOnLoad/SaveFromUiAsync` を `SettingsFacade` 呼び出しへ置換。

- Step 7: 旧経路削除
- `SettingsUiController` 内の static normalize helper と重複 clamp ロジックを削除し、validator/migrator に一本化。

8. **非機能要件チェック**
- 性能
- 設定ロード/保存の体感遅延を現行比 +5% 以内。

- 可観測性
- migration/validation の変更点（rule id, old->new）をログで追跡可能にする。

- 互換性
- 既存 `settings.json` を破壊せず読み込み可能。
- `ApiKeyProtected` / `DeepLApiKeyProtected` の保護方式を維持。

- 運用
- 失敗時は旧 `SettingsService` 経路へ戻せる feature flag（例: `UseSettingsFacadeV2`）を一時保持。

9. **リスクと緩和策**
- Risk: 正規化移設で値が微妙に変わり、実行挙動が回帰する。
- Mitigation: 既存 normalize 結果を golden テスト化し、移設前後差分を比較。

- Risk: 互換移行の順序誤りで古い設定が壊れる。
- Mitigation: `Migrate -> Validate` の順序を固定し、schema version と report を保存。

- Risk: FeatureSettings 導入で参照箇所が二重化する。
- Mitigation: 主要呼び出し口から段階置換し、完了後に直接 `AppSettings` 参照を削減。

10. **影響範囲**
- 新規候補
- `Services/Settings/ISettingsRepository.cs`
- `Services/Settings/JsonSettingsRepository.cs`
- `Services/Settings/ISecretProtector.cs`
- `Services/Settings/DpapiSecretProtector.cs`
- `Services/Settings/AppSettingsValidator.cs`
- `Services/Settings/Rules/*`
- `Services/Settings/AppSettingsMigrator.cs`
- `Services/Settings/FeatureSettings/*`
- `Services/Settings/SettingsFacade.cs`

- 既存更新
- `Services/SettingsService.cs`
- `Services/Application/SettingsUiController.cs`
- `Services/Application/SettingsChangeScheduler.cs`
- 必要に応じて `Services/Application/ResourceHostFacade.cs`（normalize 呼び出し口調整）

- ロールバック手順（案）
- `UseSettingsFacadeV2=false` で旧 `SettingsService + SettingsUiController` 正規化経路へ切戻し。

11. **Definition of Done**
- [ ] 正規化/互換/秘匿化責務が `SettingsFacade + Validator + Migrator + Protector` に集約されている。
- [ ] `SettingsUiController` は UI 同期と保存トリガー責務に限定されている。
- [ ] 既存主要設定シナリオ（起動読込、UI保存、Host設定変更、Hotkey変更）が互換動作する。
- [ ] `settings.json` 既存データで `dotnet run` が成功し、異常値は安定して正規化される。
- [ ] migration/validation のログで変更理由を追跡できる。
- [ ] 旧経路へのロールバック手順が明記されている。
