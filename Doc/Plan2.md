# Plan2: 翻訳サービスの優先度切替とフォールバック

## Goal
- 翻訳サービスを複数用意し、優先度順でフォールバックできるようにする
- ユーザが UI で優先度順を変更できるようにする
- 既存パイプライン (PipelineOrchestrator) を最小改修で拡張可能にする

## 現状整理 (現行コード)
- 翻訳は `GeminiClient` のみを利用
- `PipelineOrchestrator` が `GeminiClient` を直接呼び出している
- 設定は `AppSettings` に保存され、`MainWindow` で UI から更新

## 方針 (推奨)
- 翻訳サービスを `ITranslationProvider` として抽象化
- 優先度順のフォールバックは `TranslationFallbackService` が管理
- 優先度順は `AppSettings` に保存し、UI で編集可能にする

## 変更点 (設計)

### 1) 翻訳サービス抽象化
- `ITranslationProvider` を追加
  ```csharp
  Task<IReadOnlyDictionary<string, string>> TranslateAsync(
      IReadOnlyList<string> texts,
      AppSettings settings,
      CancellationToken ct);
  string Name { get; }
  bool IsEnabled(AppSettings settings);
  ```
- 既存 `GeminiClient` は `GeminiTranslationProvider` としてラップ

### 2) フォールバック管理
- `TranslationFallbackService` を追加
  - `IReadOnlyList<ITranslationProvider>` を受け取る
  - `settings.TranslationPriority` の順に実行
  - 失敗した場合はログに残し次へ
- すべて失敗したら空の辞書を返す (現行挙動に合わせる)

### 3) 優先度設定 (ユーザ選択)
- `AppSettings` に優先度順を保存
  - 例: `public List<string> TranslationPriority { get; set; } = new() { "Gemini", "DeepL", "GoogleWeb" };`
- UI で並べ替え可能にする
  - ボタン (↑/↓) で順序変更
  - 保存時に `TranslationPriority` を更新

### 4) サービス拡張 (例)
- `DeepLTranslationProvider` / `GoogleWebTranslationProvider` を追加する場合
  - API キー未設定時は `IsEnabled=false`
  - 失敗理由をログに残す

### 5) Pipeline 変更
- `PipelineOrchestrator` は `TranslationFallbackService` だけを呼ぶ
- 直接 `GeminiClient` を参照しない

## UI 案
- 翻訳サービス一覧 (ListBox)
- 選択した項目を上/下に移動するボタン
- 有効/無効は各サービスの設定で制御

## 互換性
- 既存ユーザは `TranslationPriority` 未設定でも既定順で動作
- 既存 `EnableGemini` 設定は維持 (将来の他サービス追加時も併用)

## リスクと対策
- リスク: 優先度順が空になる
  - 対策: 既定値を必ず投入する
- リスク: 複数サービスの切替時に遅延
  - 対策: 失敗時のみ次へ進む (成功時は即時終了)

## テスト観点
- 優先度順が UI で保存/復元される
- 上下移動で順序が正しく変更される
- 1つのサービスが失敗した場合に次へフォールバックする
- Gemini が無効/キーなしの場合の挙動確認

## 実装順 (推奨)
1) `ITranslationProvider` と `TranslationFallbackService` の追加
2) `GeminiTranslationProvider` で現行翻訳を包む
3) `AppSettings` に `TranslationPriority` を追加
4) UI の並べ替え対応
5) `PipelineOrchestrator` の呼び出し差し替え