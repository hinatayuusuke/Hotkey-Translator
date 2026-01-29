# DeepL 403 (Legacy authentication) 修正案

## 症状
- DeepL API が 403 で失敗
- メッセージ: "Legacy authentication method 'form body' is no longer supported"

## 原因
- 現在の実装は `auth_key` を **フォームボディ** で送信している
- 2025年11月の変更で **ヘッダ認証** が必須になった

## 修正方針
- `Authorization` ヘッダで API キーを送信する
- フォームボディから `auth_key` を削除する

## 具体的修正案

### 1) リクエストヘッダへ移行
- `DeepLTranslationProvider` の `BuildRequestContent` から `auth_key` を除外
- HTTP リクエスト作成時に以下を付与
  - `Authorization: DeepL-Auth-Key {API_KEY}`

### 2) エラーハンドリング
- 403 を受けた場合はログに "header auth required" を補足
- 既存のフォールバックロジックは維持

### 3) 設定/UI
- APIキーの保存方法はそのままでOK
- UI変更不要

## 影響
- DeepL の認証が最新仕様に対応する
- 403 の失敗が解消され、翻訳が返るようになる

## テスト観点
- DeepL 有効 + APIキー設定で 403 が消える
- DeepL 翻訳が返った場合にキャッシュ保存される
- エラー時は次の翻訳プロバイダへフォールバック