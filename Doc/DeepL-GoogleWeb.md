# DeepL / GoogleWeb 翻訳サービス 実装案

## 目的
- DeepL と GoogleWeb の具体的な実装方針を整理する
- Plan2 で定義した翻訳優先度・フォールバック設計と整合させる

## DeepLTranslationProvider

### 概要
- DeepL API (Free/Pro) を REST で呼び出す
- 複数文をまとめて送信し、翻訳結果を辞書で返す

### 設定項目例
- `DeepLApiKey`
- `DeepLEndpoint` (Free/Pro 切替)

### 失敗条件
- 401/403: APIキー不正
- 429: レート制限
- 456: クォータ超過 (Free)

### 実装ポイント
- APIキーが空の場合は `IsEnabled=false` でスキップ
- リトライは行わず即フォールバック (遅延回避)
- ログに失敗理由を出して次のプロバイダへ

## 推奨優先度
1) Gemini
2) DeepL


