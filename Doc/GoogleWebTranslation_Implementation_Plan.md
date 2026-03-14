# Google Web 翻訳 実装方針

## 1. 概要
- `Doc/reference/lunatranslator/LunaTranslator/translator/google.py` の `TS` 実装を参考に、非公式 Google Web 翻訳をこのリポジトリの翻訳 provider として追加する。
- 既存の優先度順フォールバックにそのまま載せ、固定優先度や専用フォールバック分岐は追加しない。

## 2. ゴール / 非ゴール
### ゴール
- `GoogleWeb` provider を `ITranslationProvider` として追加する。
- `TranslationFallbackService` の優先度順実行へ統合する。
- UI から有効/無効と優先度順を扱えるようにする。

### 非ゴール
- 公式 Google Cloud Translation API の実装。
- ブラウザ自動操作方式 (`cdp_gg`) の導入。
- 自動 retry、多重フォールバック、将来互換のためだけの抽象化。

## 3. 前提・仮定
- 参照するのは `google.py` の `TS.translate_1()` 系統のみとする。
- 初版は API Key 入力欄を追加しない。
- 非公式 endpoint のため、失敗時は空辞書を返して次 provider へ進む fail fast を採用する。

## 4. 現状整理
- 翻訳 provider の共通契約は `Services/ITranslationProvider.cs`。
- 優先度順実行は `Services/TranslationFallbackService.cs`。
- 翻訳呼び出し元は `Services/Orchestration/Stages/TranslateStage.cs`。
- provider 登録は `MainWindow.xaml.cs`。
- 既存 provider は `LlamaGrpcTranslationProvider`、`DeepLTranslationProvider`、`GeminiTranslationProvider`。
- キャッシュは `TranslateStage` が共通で扱い、provider 個別には持たない。

## 5. 推奨アーキテクチャ
### 5.1 Provider 追加
- `Services/GoogleWebTranslationProvider.cs` を新規追加する。
- `ITranslationProvider` を実装し、`Name` は `GoogleWeb` とする。
- `MainWindow.xaml.cs` の provider 登録配列へ追加する。

### 5.2 設定追加
- `Models/AppSettings.cs` に `EnableGoogleWeb` を追加する。
- `ViewModels/SettingsViewModel.cs` に対応するプロパティ、`LoadFrom`、`ApplyTo`、自動保存フックを追加する。
- `MainWindowViewModel` の翻訳ルート要約へ `GoogleWeb` を追加する。

### 5.3 UI 追加
- 翻訳設定 UI に `EnableGoogleWeb` の ON/OFF を追加する。
- `TranslationPriority` は既存の並べ替え UI をそのまま使う。

## 6. 送信方式の推奨
- 1 リクエスト内に複数 text を載せる方針を優先する。
- ただし「複数入力を 1 本の巨大文字列に連結して送る」方式は採用しない。
- このリポジトリの DeepL 実装と同様に、入力は item 単位のまま維持する。

### 理由
- `TranslateStage` は reading unit 単位で結果を元へ戻す前提になっている。
- 連結送信は区切り文字衝突や再分割失敗のリスクが高い。
- provider ごとの差分を最小にできる。

## 7. HTTP 実装方針
- endpoint は `https://translate-pa.googleapis.com/v1/translateHtml` を使用する。
- header と payload 形式は `lunatranslator` の `TS` 実装を参考にする。
- HTML entity はデコードして返す。
- HTTP エラー、レスポンス parse failure、件数不一致はログを残して空辞書を返す。

## 8. 実装詳細
### 8.1 `GoogleWebTranslationProvider`
- `bool IsEnabled(AppSettings settings)` は `settings.EnableGoogleWeb` のみで判定する。
- `TranslateAsync` は `texts.Count == 0` の場合に空辞書を返す。
- 応答は source text の順序に従って `Dictionary<string, string>` に戻す。
- 失敗時例外は provider 内で握りつぶさず、空辞書で返すか `TranslationFallbackService` の既存例外処理に乗せる。

### 8.2 設定・登録
- `Models/TranslationProviderNames.cs` に `GoogleWeb` 定数を追加する。
- `MainWindow.xaml.cs` の provider 登録順に `GoogleWebTranslationProvider` を追加する。
- `UpdateTranslationStatus` と prerequisite 表示へ `GoogleWeb` 状態を反映する。

### 8.3 キャッシュ
- 現在の cache key は provider 名を含まない。
- `GoogleWeb` を追加すると、別 provider の翻訳結果をそのまま再利用する。
- provider ごとの出力差を優先度変更へ正しく反映したいなら、`CacheKeyBuilder` に provider 名を含める見直しを推奨する。

## 9. リスクと緩和策
- Risk: 非公式 endpoint や固定キーが突然無効化される可能性がある。
- Mitigation: provider 失敗時は空辞書を返し、既存の優先度順フォールバックへ即時移行する。

- Risk: provider を切り替えても既存キャッシュが先にヒットし、Google の結果が見えない。
- Mitigation: `CacheKeyBuilder` に provider 名を含めるか、少なくとも仕様として明記する。

- Risk: 複数入力の応答形式が変わる可能性がある。
- Mitigation: 初版は安全側で実装し、件数不一致時は部分適用せずログを残す。

## 10. 実装手順
1. `TranslationProviderNames` に `GoogleWeb` を追加する。
2. `AppSettings` と `SettingsViewModel` に `EnableGoogleWeb` を追加する。
3. `GoogleWebTranslationProvider` を新規実装する。
4. `MainWindow.xaml.cs` の provider 登録へ追加する。
5. 翻訳状態表示と優先度 UI の表示文言へ `GoogleWeb` を追加する。
6. 必要なら `CacheKeyBuilder` の key 設計を見直す。
7. `dotnet build Hotkey-Translator.csproj` でビルド確認する。

## 11. Definition of Done
- `GoogleWeb` provider が優先度リストに表示される。
- `EnableGoogleWeb` ON/OFF が設定保存と復元に反映される。
- `GoogleWeb` が優先度順に実行され、失敗時は次 provider へ進む。
- 既存 provider の挙動を壊さない。
- キャッシュ仕様の影響が把握され、必要なら provider 別 key へ変更されている。
