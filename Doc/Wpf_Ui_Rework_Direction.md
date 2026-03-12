# WPF UI Rework Direction

## 1. 概要
既存 UI に自前テーマだけを被せる方式はやめる。
今後は ModernWpf をテーマ基盤として使い、WPF の標準構造に沿って機能優先で UI を整理し直す。
見た目は ModernWpf の light/dark と共通コントロールをベースに整える。

## 2. ゴール
- OCR / 翻訳 / Hook / System 設定が迷わず操作できること
- ライト/ダーク両対応でも破綻しないこと
- 標準コントロールの挙動を壊さないこと
- 今後の設定追加時に局所修正で済む構造にすること

## 3. 非ゴール
- 既存画面の完全な見た目維持
- 他アプリの UI を厳密に模倣すること
- 全画面を一度に全面刷新すること

## 4. 基本方針
### 4.1 テーマ被せを主目的にしない
- 先に構造を整える
- その上で色、余白、枠線、タイポグラフィを載せる
- 自前で標準テンプレートを大量に置き換える設計は避ける
- light/dark、surface、標準コントロールの基礎見た目は ModernWpf に寄せる

### 4.2 WPF 標準コントロールの自然な動作を優先する
- ComboBox, CheckBox, TabControl などは、必要最小限の style に留める
- 開閉、キーボード操作、フォーカス移動などの標準挙動を壊さない
- 可能な限り ModernWpf の既存コントロール/テーマ定義を使い、独自テンプレート差し替えを減らす

### 4.3 レイアウトは「機能のまとまり」で再整理する
- 既存のサイドバー分類は維持してよい
- ただし各パネル内は、ラベル列、入力列、説明文、補助ボタン列を揃える
- 見た目ではなく情報設計を先に揃える

### 4.4 多少のレイアウト変更は許容する
- 入力欄高さ
- セクション間余白
- ラベル幅
- ボタン配置
は統一のため変更してよい

## 5. 画面設計の方向性
### 5.1 共通ルール
- セクション見出しを明確化する
- セクションごとにカード状またはグループ状のまとまりを持たせる
- ラベル幅は固定に寄せる
- 説明文は入力欄の直下に寄せる
- 補助ボタンは入力欄の右に並べる
- 重要度の低い説明文は muted text に統一する

### 5.2 パネル構成
- Home: 実行系の操作と現在状態を優先
- OCR Settings: OCR 共通設定だけを置く
- OCREngines: 各 OCR エンジン共通の切替とホスト管理
- VisionLLM: VisionLLM 固有設定と hybrid/translation 切替
- Auto Translate: 自動実行条件だけを集約
- Hook: Graphics Hook 固有設定のみ
- Translation: 翻訳エンジンと優先順位、翻訳補助設定
- Hotkey: ホットキーと入力経路
- System: ログ、予算、テーマ、運用設定

### 5.3 Bottom drawer
- Preview と Log は今の2分割を維持してよい
- ただし見た目は別カードとして独立感を出す
- 下部ステータスバーは単純な 1 行情報バーとして保つ

## 6. 実装方針
### Step 1. ModernWpf をテーマ基盤として導入
- light/dark 切替
- 基本ブラシと accent
- Window / Dialog / Menu / Tab / Button / TextBox / ComboBox の土台
- 既存画面全体が壊れない最小導入に留める

### Step 2. 共通レイアウト部品を定義
- セクション見出し
- ラベル付き入力行
- 説明文
- 補助ボタン付き入力行
- 2カラム/3カラムの基本 Grid

### Step 3. System パネルを最初の基準面として作り直す
- テーマ
- VRAM budget profile
- logging
を新しい共通部品へ寄せる

### Step 4. VisionLLM パネルを整理する
- model / mmproj / runtime / translation route / hybrid を塊で再配置する
- 情報量が多いため、ここを基準に密度を調整する

### Step 5. OCR Settings / Translation / Hook を順に整理する
- 同じ入力パターンを共通化する
- ローカルな margin/padding のばらつきを減らす

### Step 6. 最後に局所的な見た目調整を入れる
- 先に layout を固める
- ModernWpf だけで不足する箇所に限って軽い style を追加する
- 独自テンプレートは最小限に留める

## 7. テーマ適用の方針
- ライト/ダーク切替は維持する
- light/dark のベースは ModernWpf を使う
- Button / TextBox / Border / Panel / Text の軽い補助 style は許容する
- ComboBox など複雑コントロールは、ModernWpf 既定の見た目と挙動を優先する
- 自前テーマは ModernWpf で不足する spacing / muted text / section grouping に限定する

## 8. リスク
- 一部パネルで縦方向に長くなる可能性がある
- 一時的に既存の muscle memory が変わる
- 共通部品化の途中で見た目が揃わない期間が発生する

## 9. 緩和策
- 1 パネルずつ段階実装する
- Step 1 の ModernWpf 導入後に、System -> VisionLLM -> OCR Settings の順で進める
- 各段階で、操作フローを優先して確認する
- テーマ基盤は先に入れるが、独自見た目調整は最後に限定する

## 10. Definition of Done
- ModernWpf を導入しても標準操作が壊れていない
- System パネルが新しい共通レイアウトで整理されている
- VisionLLM パネルが読みやすい構造になっている
- 主要入力コントロールで標準操作が壊れていない
- ライト/ダーク切替でコントラスト破綻がない
- 新しい設定追加時に局所的なレイアウト部品再利用で対応できる
