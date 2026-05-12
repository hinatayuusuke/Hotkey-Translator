# Gemini Model Selection Implementation Plan

## 1. 概要
Gemini 翻訳で使うモデルを固定値だけにせず、Gemini API のモデル一覧から `generateContent` 対応の安定版候補を取得し、設定画面の ComboBox で選択できるようにする。

既存の `AppSettings.GeminiModel` と `GeminiClient.BuildEndpoint()` は活かし、モデル名の保存先と実行時の参照経路は大きく変えない。

## 2. ゴール / 非ゴール
### ゴール
- Gemini API から利用可能モデル一覧を取得する。
- `generateContent` 対応モデルだけを候補にする。
- preview / experimental / latest / deprecated 系のモデルを通常候補から除外する。
- 設定 UI で Gemini モデルを選択・保存できるようにする。
- 一覧取得失敗時に既存の選択済みモデルを勝手に変更しない。

### 非ゴール
- Gemini 以外の翻訳プロバイダ選択仕様は変更しない。
- 通常翻訳と ForceGemini 画像翻訳で別モデルを選ぶ仕様は今回は追加しない。
- preview / experimental モデルを高度設定として表示する機能は今回は追加しない。
- 旧 Gemini API バージョンや別 endpoint への自動フォールバックは追加しない。

## 3. 前提・仮定
- 現状は `AppSettings.GeminiModel` にモデル名があり、`GeminiClient.BuildEndpoint()` が `{GeminiEndpoint}/{GeminiModel}:generateContent?key=...` を組み立てている。
- `GeminiEndpoint` の既定値は `https://generativelanguage.googleapis.com/v1beta/models`。
- 設定 UI には Gemini API key 入力はあるが、モデル選択 UI はない。
- Google 側のモデル分類は変わり得るため、安定版判定は専用フラグに依存せず、モデル名の除外ルールで扱う。

## 4. 現状整理
- `Models/AppSettings.cs`
  - `EnableGemini`
  - `GeminiModel`
  - `GeminiEndpoint`
  - `ApiKey`
- `Services/GeminiClient.cs`
  - 通常テキスト翻訳と ForceGemini 画像翻訳の両方で `BuildEndpoint(settings)` を使用する。
  - 失敗時は空辞書または `null` を返し、raw response dump は既存のデバッグ保存処理を使う。
- `ViewModels/SettingsViewModel.cs`
  - `EnableGemini` と `ApiKeyText` はある。
  - `GeminiModel` の ViewModel プロパティと保存反映はない。
- `UI/TranslationControl.xaml`
  - Gemini API key の入力欄のみ存在する。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `GeminiClient`
  - `ListModelsAsync(AppSettings settings, CancellationToken cancellationToken)` を追加する。
  - Gemini API の models list endpoint を呼び、候補 DTO を返す。
- `GeminiModelOption`
  - 表示名、API に渡すモデル名、説明、入力/出力 token 上限など必要最小限の情報を持つモデル候補。
- `SettingsViewModel`
  - `ObservableCollection<GeminiModelOption>` または表示用文字列リストを持つ。
  - `SelectedGeminiModel` / `GeminiModelText` を `AppSettings.GeminiModel` に同期する。
  - モデル一覧更新コマンドを持つ。
- `TranslationControl.xaml`
  - Gemini API key の近くに ComboBox と更新ボタンを追加する。

### データフロー
1. 設定画面を開く。
2. ViewModel が現在の `settings.GeminiModel` を ComboBox 候補に最低1件として入れる。
3. ユーザが更新ボタンを押す。
4. API key が空なら一覧取得せず、現在値だけを維持する。
5. API key がある場合、`GeminiClient.ListModelsAsync()` で一覧を取得する。
6. `generateContent` 対応かつ安定版扱いのモデルに絞る。
7. ComboBox 候補を更新する。
8. ユーザが選択した値を `settings.GeminiModel` に保存する。
9. 翻訳実行時は既存どおり `GeminiClient.BuildEndpoint()` が保存済みモデルを使う。

## 6. インターフェース設計
### Gemini models list
- Request:
  - `GET {settings.GeminiEndpoint}?key={settings.ApiKey}`
- Response で利用する主な項目:
  - `models[].name`
  - `models[].displayName`
  - `models[].description`
  - `models[].supportedGenerationMethods`
  - `models[].inputTokenLimit`
  - `models[].outputTokenLimit`

### フィルタ
- `supportedGenerationMethods` に `generateContent` を含む。
- モデル名または表示名に以下を含む候補は通常候補から除外する。
  - `preview`
  - `experimental`
  - `exp`
  - `latest`
  - `deprecated`
- API レスポンスの `name` が `models/gemini-...` 形式なら、保存値は既存 endpoint 組み立てに合わせて `gemini-...` へ正規化する。

### エラー
- API key が空:
  - 一覧取得は実行しない。
  - 現在の `GeminiModel` は維持する。
- HTTP エラー / JSON parse エラー:
  - 現在の `GeminiModel` は維持する。
  - UI ログまたは既存 logger に失敗理由を出す。
- 候補が0件:
  - 現在の `GeminiModel` を候補に残す。
  - 自動で別モデルへ変更しない。

## 7. 実装手順
### Step 1: モデル候補 DTO と API 取得処理
- `GeminiModelOption` を追加する。
- `GeminiClient.ListModelsAsync()` を追加する。
- `generateContent` 対応モデルの抽出と stable フィルタを実装する。

### Step 2: 設定 ViewModel へ接続
- `SettingsViewModel` に `GeminiModel` 相当の ObservableProperty を追加する。
- `LoadFromSettings` / `ApplyToSettings` の両方で `AppSettings.GeminiModel` と同期する。
- 一覧更新コマンドを追加する。

### Step 3: UI 追加
- `UI/TranslationControl.xaml` に Gemini モデル ComboBox と更新ボタンを追加する。
- 表示は `displayName` があれば表示名、なければモデル名にする。
- 保存値は必ず API 用モデル名にする。

### Step 4: 検証
- API key 未設定時に UI が壊れず、現在モデルだけが表示されることを確認する。
- API key 設定時に stable 候補が取得されることを確認する。
- モデル選択後、設定保存と再起動後の復元を確認する。
- 通常 Gemini 翻訳と ForceGemini 画像翻訳が選択モデルを使うことをログで確認する。

## 8. 非機能要件チェック
- 性能:
  - モデル一覧取得はユーザ操作時のみ実行する。翻訳実行ごとには呼ばない。
- セキュリティ:
  - API key は既存の DPAPI 保護保存を継続利用する。
  - ログや changes に API key を出力しない。
- 互換性:
  - 既存 settings.json の `GeminiModel` はそのまま読み込む。
  - 候補一覧に存在しない保存済みモデルも、現在値として ComboBox に残す。
- 可観測性:
  - 一覧取得成功件数、フィルタ後件数、失敗理由を既存 logger に出す。

## 9. リスクと緩和策
- Risk: Google 側のモデル命名や一覧レスポンスが変わる。
- Mitigation: 必須項目が不足した候補は除外し、現在モデルは維持する。

- Risk: stable 判定が過剰に除外して候補が空になる。
- Mitigation: 保存済みモデルだけは候補に残し、ユーザの既存設定を壊さない。

- Risk: `models/gemini-...` と `gemini-...` の形式差で endpoint が壊れる。
- Mitigation: 保存前に `models/` prefix を取り除く正規化を行う。

## 10. 影響範囲
- `Services/GeminiClient.cs`
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `UI/TranslationControl.xaml`
- `Resources/Strings.resx`
- `Resources/Strings.ja.resx`
- 必要に応じて model option 用の新規 model ファイル

## 11. Definition of Done
- Gemini モデル一覧を API から取得できる。
- ComboBox に stable な `generateContent` 対応モデルだけが表示される。
- 選択したモデルが `AppSettings.GeminiModel` に保存される。
- 翻訳実行時に選択済みモデルが使われる。
- API key 未設定または一覧取得失敗時に既存モデルが維持される。
- API key がログや変更記録に出力されない。
- 実装後は `./.agent/changes.md` に作業記録を追記する。
