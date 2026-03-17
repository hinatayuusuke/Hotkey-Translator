# GitHub Pages Site Policy for Hotkey Translator

## 1. 概要
この方針書は、Hotkey Translator の公開用サイトを **GitHub Pages + Vite + React + Tailwind CSS** で作るときの、初心者向けの進め方をまとめたものです。  
最初の目的は「Windows デスクトップアプリを紹介し、スクリーンショット・導入手順・ダウンロード先を分かりやすく見せること」に限定します。

GitHub Pages は静的サイト配信向けです。  
そのため、このサイトは **Web 版アプリ本体** ではなく、**配布ページ / 紹介ページ / ドキュメント入口** として設計します。

## 2. ゴール / 非ゴール

### ゴール
- GitHub Pages 上で安定して公開できること
- 初見ユーザーが「何のアプリか」「どう入れるか」を 1 ページで理解できること
- 初心者でもローカル開発、ビルド、デプロイの流れを追えること
- 将来スクリーンショットや FAQ を追加しやすい構成にすること

### 非ゴール
- WPF アプリ本体をブラウザで動かすこと
- 最初から多ページ構成や複雑な SPA ルーティングを入れること
- CMS、バックエンド、認証、検索などを先に入れること
- UI ライブラリを大量導入して構成を複雑にすること

## 3. 前提・仮定
- このリポジトリの本体は Windows 向け WPF デスクトップアプリである
- 公開サイトの主な役割は、アプリ紹介、機能説明、導入案内、GitHub Releases への導線である
- Pages 公開先は、まず `https://<GitHubユーザー名>.github.io/Hotkey-Translator/` のような **project site** を想定する
- 独自ドメインは初期段階では使わない

## 4. 現状整理
- 既存リポジトリは .NET / WPF を中心に構成されており、Web フロントエンド基盤はまだない
- README には機能、動作環境、ビルド方法、サードパーティ注意点がまとまっている
- ポータブル配布の要件があり、サイト側でも「単体 EXE だけで完結しない機能がある」点を明示した方がよい

## 5. 提案アーキテクチャ

### 基本方針
- Web サイトはリポジトリ直下ではなく、`site/` 配下に独立した Vite プロジェクトとして置く
- 最初は **1 ページ構成** にする
- React Router は導入しない
- データ取得 API は使わず、まずは静的コンテンツ中心にする
- デプロイはローカル手動ではなく **GitHub Actions** に寄せる

### なぜこの構成にするか
- `site/` 分離にすると、WPF 側の .NET 構成と Node.js 側の構成が混ざらず、初心者でも見通しが良い
- GitHub Pages は深い SPA ルーティングで 404 を踏みやすいため、最初から多ページ SPA にしない方が安全
- Actions デプロイにすると、手元の環境差分よりも「main に入ったら公開される」流れを作りやすい

### コンポーネント構成
- `site/src/App.tsx`
  - 1 ページ全体の組み立て
- `site/src/components/`
  - `Hero`
  - `FeatureList`
  - `ScreenshotGallery`
  - `InstallSteps`
  - `Requirements`
  - `LinksSection`
  - `Footer`
- `site/src/data/`
  - 機能一覧、導入手順、外部リンクを配列で保持
- `site/public/`
  - スクリーンショット、OG 画像、favicon

### 画面構成
1. Hero
   - アプリ名
   - 1 行説明
   - `Download` と `GitHub` ボタン
2. Features
   - OCR、翻訳、オーバーレイ、Graphics Hook などの特徴
3. Screenshots
   - 実際の見た目を見せる
4. Install
   - Windows での導入手順
5. Requirements / Notes
   - `.NET`、`uv`、補助サービス、実験機能の注意
6. Footer
   - Releases、README、License、Issue へのリンク

## 6. インターフェース設計

### Vite / Pages 設定
- `vite.config.ts` の `base` は project site 前提で `/Hotkey-Translator/` を設定する
- 独自ドメイン化するまでは `/` を使わない

### コンテンツ設計
- アプリ説明は README と整合を取る
- ダウンロードリンクは GitHub Releases を正とする
- サイト内の機能説明は「全部入り」よりも「初心者が始める導線」を優先する

### エラー方針
- データ取得や API 通信を持たないため、初期段階の失敗点は主にビルドとパス設定になる
- `base` 設定ミスで CSS / JS が 404 になるため、公開 URL 前提の確認を必須にする

## 7. 実装手順

### Step 1. 土台を作る
- `site/` に Vite + React プロジェクトを作る
- Tailwind CSS を追加する
- `App.tsx` を 1 ページのランディング構成に置き換える
- `src/components` と `src/data` を作る

### Step 2. 最低限の公開内容を入れる
- README から機能説明を整理して反映する
- スクリーンショットを 2〜4 枚入れる
- GitHub Releases への導線を付ける
- 導入に必要な補助要件を短く書く

### Step 3. GitHub Pages に載せる
- GitHub Actions で `npm ci` → `npm run build` → Pages deploy を構成する
- `main` 更新時に自動デプロイする
- 公開 URL でアセットパスが壊れていないか確認する

### Step 4. 仕上げる
- モバイル表示を整える
- OGP、favicon、タイトル、説明文を整備する
- README からサイト URL へリンクする

## 8. 非機能要件チェック

### 性能
- 画像を大きい PNG のまま置かない
- 初期段階では動画や重いアニメーションを避ける
- Hero 直下の情報を先に読めるようにする

### セキュリティ
- GitHub Pages は静的配信前提とし、秘密情報や API キーは埋め込まない
- 問い合わせフォームを作る場合も、初期段階では外部サービス連携を急がない

### 可観測性
- 初期段階では解析ツールを無理に入れない
- 必要になってから軽量なアクセス解析を検討する

### 互換性
- 初期ターゲットはモダンブラウザに限定する
- 互換性対応のためだけの複雑な分岐は入れない

## 9. 初心者向けの具体方針
- **まず 1 ページで完成させる**
  - ページ数を増やすより、1 ページで配布導線を通す方が失敗しにくい
- **React は「部品分割」のために使う**
  - 状態管理や複雑なフックは初期段階では不要
- **Tailwind は「余白・色・レイアウト」を素早く整えるために使う**
  - デザインシステムを先に作り込みすぎない
- **UI ライブラリは追加しない**
  - Tailwind 単体で足りる範囲に抑える
- **GitHub Pages 特有の罠を先に潰す**
  - `vite.config.ts` の `base`
  - 画像やリンクの相対パス
  - SPA ルーティングによる 404

## 10. リスクと緩和策
- Risk: Web サイトを作るつもりが、Web アプリ化の議論に広がって止まる
- Mitigation: 今回は「紹介・配布ページ」に目的を固定する

- Risk: GitHub Pages の `base` 設定を忘れて公開後に崩れる
- Mitigation: project site 前提で最初から `vite.config.ts` を固定し、公開 URL で確認する

- Risk: README の内容とサイトの説明がずれて古くなる
- Mitigation: 機能説明の元情報を README に寄せ、サイト側は要約中心にする

- Risk: Tailwind や React の学習が主目的化して公開が遅れる
- Mitigation: 最初の完成条件を「1 ページ + ダウンロード導線 + 主要機能説明」に限定する

## 11. 影響範囲
- 新規追加が中心になる
- 想定追加先:
  - `site/`
  - `.github/workflows/`
  - `README.md` の公開リンク
- WPF 本体コードへの直接変更は原則不要

## 12. Definition of Done
- `site/` で `npm run dev` が通る
- `site/` で `npm run build` が通る
- GitHub Pages でトップページが表示される
- Hero、機能紹介、スクリーンショット、導入手順、ダウンロードリンクが見える
- モバイル幅でも崩れすぎない
- Releases と README への導線がある

## 13. 採用する実装ルール
- 初回は **Vite + React + Tailwind CSS のみ** を採用する
- 状態管理ライブラリ、ルーター、CMS、i18n は後回しにする
- GitHub Pages 公開の安定化が済むまで、追加ライブラリは最小限にする

## 14. 次の実装候補
次に着手するなら、以下の順を推奨します。

1. `site/` の初期作成
2. Tailwind セットアップ
3. 1 ページ構成の実装
4. GitHub Actions による Pages デプロイ
5. README からのリンク追加
