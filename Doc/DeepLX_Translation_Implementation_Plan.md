# DeepLX 翻訳エンジン統合 実装案（APIキーなし）

## 1. 概要（1-3行）
既存の翻訳プロバイダ構成（`ITranslationProvider` + `TranslationFallbackService`）に、`DeepLX` を新規追加する。  
初期実装は **APIキーなし** を前提にし、ローカル/自己ホスト `DeepLX` エンドポイントへHTTP送信する。  
既存の翻訳キュー、差分、オーバーレイ経路は変更せず、プロバイダ追加のみで導入する。

## 2. ゴール / 非ゴール
### ゴール
- UIから `DeepLX` を有効化/無効化できる。
- `TranslationPriority` に `DeepLX` を追加し、既存プロバイダと同じフォールバック運用に載せる。
- 1回の翻訳要求で複数テキスト（`IReadOnlyList<string>`）を処理できる。
- 失敗時は既存同様に次プロバイダへフォールバックする。

### 非ゴール
- APIキー認証（`Authorization`/`x-api-key` 等）の実装。
- DeepLXサーバ実装自体の改修。
- 既存の翻訳キャッシュ仕様・読み取り単位アルゴリズムの作り直し。

## 3. 前提・仮定
- 対象プロジェクトの翻訳IFは `ITranslationProvider` で統一されている。
- `TranslationFallbackService` は `TranslationProviderNames.Defaults` に含まれる名前のみ優先順位候補として採用する。
- DeepLXのAPI互換は実装差があるため、レスポンスJSONは複数形式を許容する必要がある。
- 初期利用はローカルネットワーク/自己ホスト（例: `http://127.0.0.1:1188`）を想定する。

## 4. 現状整理
- 既存プロバイダ: `LlamaCpp`, `Gemini`, `DeepL`（`TranslationProviderNames.Defaults`）。
- 設定モデル `AppSettings` には `EnableDeepL`, `DeepLEndpoint` があり、DeepLのみAPIキー保護実装がある。
- フォールバック実行は `TranslationFallbackService.TranslateAsync()` が担当。
- 送信単位は `TranslateStage` で組み立てた `pendingTexts`（読み取り単位の配列）。

## 5. 提案アーキテクチャ
### コンポーネント構成
- 新規: `Services/DeepLXTranslationProvider.cs`
- 既存更新:
  - `Models/TranslationProviderNames.cs`（`DeepLX` 追加）
  - `Models/AppSettings.cs`（`EnableDeepLX`, `DeepLXEndpoint` 追加）
  - `ViewModels/SettingsViewModel.cs`（設定バインド追加）
  - `MainWindow.xaml`（設定UI追加）
  - `App.xaml.cs` or DI登録箇所（Provider登録）

### データフロー / シーケンス
1. `TranslateStage` が `pendingTexts` を作成。
2. `TranslationFallbackService` が優先順位で `DeepLX` を選択。
3. `DeepLXTranslationProvider` が `pendingTexts` をDeepLX APIへPOST。
4. レスポンスを `Dictionary<source, translated>` に変換。
5. 成功時はキャッシュ保存、失敗時は次プロバイダへフォールバック。

### 既存パターンへの整合
- `DeepLTranslationProvider` と同じ同期点（HTTP 1リクエスト→辞書返却）を踏襲する。
- ログ方針は既存に揃え、HTTP失敗時は情報ログ + 空辞書返却でフォールバック可能にする。

## 6. インターフェース設計
### 設定追加（APIキーなし）
- `AppSettings`
  - `EnableDeepLX: bool = false`
  - `DeepLXEndpoint: string = "http://127.0.0.1:1188/translate"`

### リクエスト仕様（初期案）
- HTTP `POST` `DeepLXEndpoint`
- JSON Body（互換性重視）:
  - `{"text":"...","source_lang":"ja","target_lang":"en"}` をテキスト件数分送る
- 実装方針:
  - サーバ互換差を吸収するため、初期は **1テキストずつ送信**（バルクAPI依存を避ける）
  - 将来、互換確認後にバッチ送信へ最適化

### レスポンス受け取り（互換吸収）
- 代表候補:
  - `{"data":"..."}`
  - `{"translation":"..."}`
  - `{"text":"..."}`
- 上記いずれかを抽出し、空なら失敗扱い（空辞書/部分辞書）

### バリデーション / エラー
- `EnableDeepLX=true` かつ `DeepLXEndpoint` 非空で `IsEnabled=true`。
- HTTP非成功はログ出力して空辞書返却（フォールバック前提）。
- タイムアウト/JSON不正も同様に空辞書返却。

## 7. 実装手順（ステップ分割）
### Step 1: モデルと名前の追加
- `TranslationProviderNames` に `DeepLX` 定数を追加。
- `Defaults` に `DeepLX` を含める（推奨: `LlamaCpp, Gemini, DeepL, DeepLX`）。
- `AppSettings` に `EnableDeepLX`, `DeepLXEndpoint` を追加。

### Step 2: Provider本体の追加
- `DeepLXTranslationProvider` を新規実装。
- `ITranslationProvider` 準拠で `TranslateAsync()` を実装。
- 1件ずつPOST + 辞書化 + 失敗時ログを実装。

### Step 3: DI登録とフォールバック統合
- DIコンテナに `DeepLXTranslationProvider` を登録。
- `TranslationFallbackService` の既存処理で動くことを確認。

### Step 4: UI/設定配線
- `SettingsViewModel` に `EnableDeepLX`, `DeepLXEndpoint` バインディング追加。
- `MainWindow.xaml` に設定UI（ON/OFF + Endpoint入力）追加。

### Step 5: 動作検証
- DeepLX単独有効で翻訳成功を確認。
- DeepLX停止時に次プロバイダへフォールバックすることを確認。
- 設定保存/再起動後の復元を確認。

## 8. 非機能要件チェック
- 性能: 初期は1件送信で安全優先。必要時にバッチ化。
- セキュリティ: APIキーは扱わない。ローカルEndpoint利用を推奨。
- 可観測性: `provider=DeepLX` の成功/失敗ログを追加。
- 互換性: 既存翻訳IFを維持し、既存プロバイダを壊さない。
- 運用: Endpoint変更だけで各DeepLX実装へ追従可能にする。

## 9. リスクと緩和策
- Risk: DeepLX実装ごとにリクエスト/レスポンス形式が違う。
- Mitigation: 1件送信 + 複数レスポンスキーのフォールバック抽出を採用。

- Risk: Endpoint未起動時に毎回タイムアウトで遅延。
- Mitigation: 短めタイムアウト（例: 5-8秒）と、失敗時の即フォールバック。

- Risk: APIキーなし運用で公開サーバ利用時の不正利用リスク。
- Mitigation: 初期要件ではローカル/自己ホスト限定を明記（公開利用は非推奨）。

## 10. 影響範囲（変更ファイル候補）
- `Models/TranslationProviderNames.cs`
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml`
- `Services/DeepLXTranslationProvider.cs`（新規）
- `App.xaml.cs`（DI登録箇所）
- `Doc/DeepLX_Translation_Implementation_Plan.md`（本書）

## 11. Definition of Done
- [ ] UIで `DeepLX` の有効/無効とEndpointを設定できる。
- [ ] `EnableDeepLX=true` でDeepLX翻訳が実行される。
- [ ] DeepLX失敗時に他プロバイダへフォールバックする。
- [ ] 設定保存後、再起動しても値が復元される。
- [ ] APIキー未実装の前提がDocとUI文言で明示される。
- [ ] 主要ログ（有効/無効、HTTP失敗、JSON解析失敗、成功件数）が出力される。
