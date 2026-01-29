# CTranslate2 + MarianMT 導入案

## 目的
- ローカル実行可能な翻訳エンジン (CTranslate2 + MarianMT) を追加する
- ネットワーク不要のフォールバックとして使えるようにする
- 既存の翻訳優先度設計に組み込む

## 前提
- 翻訳パイプラインは `ITranslationProvider` + フォールバック前提 (Plan2)
- ローカルモデルをディスクに配置し、HTTP を使わずに翻訳する
- Windows/.NET 8 環境で動作

## 方針 (推奨)
- CTranslate2 を Python プロセスとして呼び出す (PaddleOCR と同様の構成)
- uv を使って Python 仮想環境 + 依存を管理
- まずは一時ファイル + 標準入出力方式で安定性を優先

## 変更点 (設計)

### 1) Settings 追加
`Models/AppSettings.cs` に CT/Marian 向け設定を追加。

- 例:
  - `public bool EnableCTranslate2 { get; set; } = false;`
  - `public string CtProjectDir { get; set; } = "Tools\\CTranslate2";`
  - `public string CtUvPath { get; set; } = "uv";`
  - `public string CtModelDir { get; set; } = "Models\\MarianMT";`
  - `public string CtSourceLanguage { get; set; } = "en";`
  - `public string CtTargetLanguage { get; set; } = "ja";`

### 2) UI
- 翻訳サービス一覧に CTranslate2 を追加
- モデルディレクトリ・言語設定を入力できる欄を追加

### 3) Provider 追加
- `CTranslate2TranslationProvider` を追加
- `TranslateAsync` で Python ブリッジを呼び出す
- 成功時のみ結果を返し、失敗時はフォールバックへ

### 4) Python ブリッジ (uv)
- `Tools/CTranslate2` に uv プロジェクトを配置
- `ct_translate_bridge.py` を用意

#### pyproject.toml 例
```toml
[project]
name = "ctranslate2-bridge"
version = "0.1.0"
requires-python = ">=3.10"
dependencies = [
  "ctranslate2",
  "sentencepiece",
  "transformers"
]
```

#### ブリッジ仕様 (例)
- 入力: JSON
  ```json
  {
    "source": "en",
    "target": "ja",
    "texts": ["...", "..."]
  }
  ```
- 出力: JSON
  ```json
  {
    "translations": ["...", "..."]
  }
  ```

### 5) モデル配置
- MarianMT の CTranslate2 変換済みモデルを配置
- 例: `Helsinki-NLP/opus-mt-en-ja` を変換し `CtModelDir` に置く

### 6) フォールバック
- 優先度順に従って CT/Marian -> 次のサービスへ進む
- エラーはログに残し、パイプラインは停止しない

## リスクと対策
- リスク: モデルサイズが大きく配布が重い
  - 対策: ダウンロード手順を別途ドキュメント化
- リスク: 初回読み込みが遅い
  - 対策: プロセス常駐化 (将来対応)

## テスト観点
- モデルパス未設定時にスキップされる
- 短文/長文で翻訳が返る
- ネットワークなしで翻訳が動作する

## 実装順 (推奨)
1) Settings/UI 追加
2) `CTranslate2TranslationProvider` 実装
3) Python ブリッジ作成
4) フォールバック統合