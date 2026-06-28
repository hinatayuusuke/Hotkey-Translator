# OCR Correction SymSpell + Weighted Edit Distance Implementation Plan

1. **概要（1-3行）**
OCR訂正を、各OCRエンジン個別ではなくC#本体の共通OCR pipelineへ追加する。
候補生成はNuGetの `SymSpell`、OCR誤認識向けの再ランキングは自前 `OcrWeightedEditDistance` で実装する。
初期実装は英語/Latin系の単語単位訂正に限定し、自動誤修正を避けるため既定OFF・安全条件付きで導入する。

2. **ゴール / 非ゴール**
- ゴール
  - WinRT / Paddle / Paddle-VL / NDL / OneOCR / VisionLLMなど、共通 `OcrLine` に集約された後のOCR結果へ同じ訂正処理を適用できる。
  - `SymSpell` で高速に辞書候補を生成する。
  - `OcrWeightedEditDistance` で `0/O`, `1/l/I`, `rn/m`, `cl/d` などOCRらしい混同を低コストに評価する。
  - 元単語が辞書に存在する場合は原則自動訂正せず、valid word errorを避ける。
  - 訂正結果が翻訳、cache、overlay原文、clipboard exportへ自然に反映される。
- 非ゴール
  - 文脈モデルによる文章レベル訂正。
  - CJK向けの形態素解析ベース訂正。
  - OCRエンジンのPythonサイドカー側への個別実装。
  - 候補提示UIや手動承認UI。
  - Hunspell辞書・活用形処理の同時導入。

3. **前提・仮定**
- 現在のOCR結果は `OcrAndGroupStage` で `OcrLine` に統合され、行結合後に `ReadingUnit` へ変換される。
- 翻訳入力は `ReadingUnit.Text` を `TranslationTextNormalizer.NormalizeForTranslation(...)` へ通した文字列である。
- OCR訂正は翻訳用 `UserGlossaryService` とは別責務にする。用語固定は翻訳後の訳語制御、OCR訂正は翻訳前の原文復元であるため。
- 初期辞書はアプリ同梱の読み取り専用頻度辞書から開始し、ユーザー辞書は後続ステップで追加する。
- 互換性対応や複数辞書フォーマットのフォールバックは、明示要件が出るまで追加しない。

4. **現状整理**
- 関連モジュール
  - `Services/Orchestration/Stages/OcrAndGroupStage.cs`
    - OCR実行、confidence filter、ruby filter、line merge、Vision geometry hybrid alignmentを行う。
    - `groupedLocalLines` を `MapLinesToScreen(...)` でscreen座標へ変換し、`ReadingUnitBuilder.Build(...)` へ渡す。
  - `Models/OcrLine.cs`
    - OCR行の `Text`, `Rect`, `Confidence`, `LineCount`, `LineHeight` を保持する。
  - `Services/ReadingUnitBuilder.cs`
    - `OcrLine.Text` を `ReadingUnit.Text` として引き継ぐ。
  - `Services/Orchestration/Stages/TranslateStage.cs`
    - `ReadingUnit.Text` を翻訳入力として使う。
  - `Services/Translation/UserGlossaryService.cs`
    - 翻訳前の用語保護と翻訳後の復元を担当する。
- 現行挙動
  - OCR行の文字列は、エンジン固有の正規化と行結合以外では訂正されない。
  - 翻訳cache keyは翻訳前正規化後のテキストに依存する。
- 制約
  - OCR訂正を翻訳後に入れるとcacheやoverlay原文と整合しない。
  - OCR訂正をOCRエンジン内に入れると、エンジンごとに重複し、WinRT/OneOCR/VisionLLMに同じ処理を適用しにくい。

5. **提案アーキテクチャ**
- コンポーネント構成
  - `Services/OcrCorrection/OcrCorrectionService.cs`
    - `IReadOnlyList<OcrLine>` を受け取り、訂正済み `OcrLine` を返す統合サービス。
  - `Services/OcrCorrection/SymSpellOcrCandidateProvider.cs`
    - `SymSpell` を保持し、tokenごとの候補を生成する。
  - `Services/OcrCorrection/OcrWeightedEditDistance.cs`
    - OCR混同表を使い、候補の距離を計算する自前実装。
  - `Services/OcrCorrection/OcrCorrectionDictionary.cs`
    - 頻度辞書、許可語、保護語を読み込む。
  - `Services/OcrCorrection/OcrCorrectionTokenizer.cs`
    - 行テキストを単語tokenと非単語spanに分割し、元の空白・記号を維持して再構築する。
  - `Models/OcrCorrectionDecision.cs`
    - 訂正理由、候補、score、skip理由をログ・テスト用に保持する内部モデル。
  - `Resources/OcrCorrection/en_frequency.tsv`
    - `term<TAB>frequency` 形式の初期辞書。
  - `Resources/OcrCorrection/ocr_confusions.json`
    - OCR混同ルール。
- データフロー
  1. `OcrAndGroupStage` がOCR結果を取得する。
  2. confidence filter、ruby filter、line merge、Vision geometry hybrid alignmentを先に実行する。
  3. `OcrCorrectionService.CorrectLines(...)` が `groupedLocalLines` の `Text` だけを訂正する。
  4. `MapLinesToScreen(...)` で座標変換する。
  5. `ReadingUnitBuilder.Build(...)` で訂正済み文字列を `ReadingUnit.Text` にする。
  6. 翻訳、cache、overlay、text exportは既存経路のまま訂正済み原文を使う。
- 既存パターンへの整合
  - `OcrCandidateScorer` と同じくOCR pipeline内の軽量サービスとして扱う。
  - 設定は `AppSettings` に追加し、`Settings/Rules` で範囲正規化する。
  - ログは既存の `stage=... event=...` 形式に寄せる。

6. **インターフェース設計**
- `AppSettings`
  - `bool EnableOcrCorrection { get; set; } = false`
  - `string OcrCorrectionLanguage { get; set; } = "auto"`
  - `int OcrCorrectionMaxEditDistance { get; set; } = 2`
  - `int OcrCorrectionMaxCandidates { get; set; } = 8`
  - `double OcrCorrectionAutoAcceptMaxCost { get; set; } = 1.25`
  - `double OcrCorrectionMinScoreMargin { get; set; } = 0.35`
  - `int OcrCorrectionMinTokenLength { get; set; } = 3`
- `OcrCorrectionService`
  - `IReadOnlyList<OcrLine> CorrectLines(IReadOnlyList<OcrLine> lines, AppSettings settings, OcrEngineKind effectiveEngineKind)`
  - 入力
    - OCR行一覧
    - 現在設定
    - 実効OCRエンジン種別
  - 出力
    - 訂正なしの場合は入力相当の行一覧
    - 訂正ありの場合は `line with { Text = correctedText }`
  - エラー
    - 辞書未ロードや設定不正は初期化時に明示失敗させる。
    - 実行中の候補ゼロはエラーではなくskip扱い。
- `SymSpellOcrCandidateProvider`
  - `IReadOnlyList<OcrCandidate> Lookup(string token, int maxEditDistance, int maxCandidates)`
  - 候補には `Term`, `Frequency`, `SymSpellDistance` を含める。
- `OcrWeightedEditDistance`
  - `double Compute(string source, string target)`
  - サポートする操作
    - 置換
    - 挿入
    - 削除
    - OCR向け複数文字置換
  - 混同表例
    - `0 <-> O`, `1 <-> l`, `1 <-> I`, `5 <-> S`, `8 <-> B`
    - `rn <-> m`, `cl <-> d`, `vv <-> w`, `li <-> h`
- `OcrCorrectionDecision`
  - `string Original`
  - `string? Corrected`
  - `string Reason`
  - `double? Cost`
  - `double? ScoreMargin`

7. **実装手順（ステップ分割）**
- Step 1: 設定とno-op差し込み
  - `AppSettings` にOCR訂正設定を追加する。
  - `OcrCorrectionService` をno-op実装で追加する。
  - `OcrAndGroupStage` の `groupedLocalLines` 確定後、`MapLinesToScreen(...)` 前に差し込む。
  - `EnableOcrCorrection == false` では完全no-opにする。
- Step 2: 辞書ロード
  - `Resources/OcrCorrection/en_frequency.tsv` を追加する。
  - `OcrCorrectionDictionary` で `term -> frequency` を読み込む。
  - ASCII/Latin token判定と、辞書存在判定を提供する。
- Step 3: SymSpell候補生成
  - `SymSpell` NuGet packageを追加する。
  - `SymSpellOcrCandidateProvider` を実装する。
  - `maxEditDistance` と `maxCandidates` は設定から受ける。
- Step 4: `OcrWeightedEditDistance` 自前実装
  - 基本のweighted edit distanceを実装する。
  - 置換・挿入・削除の既定costは `1.0` とする。
  - OCR混同文字の置換costは `0.15-0.35` 程度にする。
  - 複数文字混同は、DP遷移に `sourceSpan -> targetSpan` の追加遷移として入れる。
  - WHY: NuGetの一般的なWeighted Levenshteinは単一文字置換には強いが、OCRで多い `rn/m` のような複数文字混同を直接表現しにくい。
- Step 5: 再ランキングと自動採用条件
  - 候補scoreを以下のように計算する。
    - `score = weightedCost + frequencyPenalty`
    - `frequencyPenalty = -log10(candidateFrequency / maxFrequency) * 0.15`
  - 自動採用条件
    - 元tokenが辞書に存在しない。
    - token長が `OcrCorrectionMinTokenLength` 以上。
    - 数字だけ、記号混じりID、URL、ファイル名風tokenではない。
    - best候補のweighted costが `OcrCorrectionAutoAcceptMaxCost` 以下。
    - 2位候補との差が `OcrCorrectionMinScoreMargin` 以上。
  - 条件を満たさない場合はskipする。
- Step 6: ログと診断
  - 訂正があったframeだけsummaryログを出す。
  - 例: `stage=ocr_correction event=summary corrected=3 skippedKnown=12 skippedAmbiguous=1 engine=Paddle.`
  - debug相当の詳細ログは既存ログ設定に合わせて抑制する。
- Step 7: UI
  - OCR設定画面に以下を追加する。
    - enable checkbox
    - max edit distance
    - auto accept max cost
    - minimum score margin
  - 詳細項目は初期は折りたたみ相当、または設定ファイル編集前提でもよい。
- Step 8: テスト
  - `OcrWeightedEditDistance` のunit test相当を追加する。
  - `rn -> m`, `1 -> l`, `0 -> O`, 通常置換、挿入、削除のcostを検証する。
  - `OcrCorrectionService` で valid word error をskipするケースを検証する。

8. **非機能要件チェック**
- 性能
  - OCR行数は通常少ないため、token単位のSymSpell lookupと候補再ランキングで十分軽い。
  - `SymSpell` インスタンスと辞書はアプリ起動後に再利用する。
  - 1 tokenあたりの候補数を `OcrCorrectionMaxCandidates` で制限する。
- セキュリティ
  - 辞書ファイルは実行コードではなくデータとして扱う。
  - ユーザー辞書を後続で追加する場合も、外部path実行や動的コード評価は行わない。
- 可観測性
  - frameごとの訂正件数、skip件数、処理時間をログ可能にする。
  - 詳細な原文全文ログは個人情報・画面内容の漏えいになり得るため、既存ログ設定が有効な場合だけ短いpreviewに制限する。
- 互換性
  - 既定OFFのため既存挙動は変えない。
  - ON時は翻訳cache keyに訂正済みtextが入るため、訂正前cacheとは別扱いになる。
- 運用
  - 誤修正が出た場合は `EnableOcrCorrection=false` で即時無効化できる。
  - 閾値を厳しくすることで自動訂正範囲を狭められる。

9. **リスクと緩和策**
- Risk: `cot -> cat` のように元tokenも正しい単語の場合に誤修正する。
- Mitigation: 元tokenが辞書に存在する場合は自動訂正しない。
- Risk: ゲームUIの固有名詞、キャラクター名、造語を一般単語へ誤修正する。
- Mitigation: 大文字混在、短いtoken、辞書外だが繰り返し出るtokenは初期実装ではskipする。ユーザー保護語は後続で追加する。
- Risk: `rn/m` など複数文字混同を入れると過剰に近い候補が増える。
- Mitigation: 複数文字混同のcostは低くしすぎず、自動採用にはscore marginを要求する。
- Risk: OCR訂正後に翻訳cacheが増える。
- Mitigation: cache keyは訂正済みtextに依存するため整合性は保てる。容量問題が出た場合だけcache evictionを別途検討する。
- Risk: VisionLLMの自然文出力に単語訂正をかけると文章が壊れる。
- Mitigation: 初期設定では全engine共通ONではなく、source languageとtoken条件で限定する。必要なら `OcrCorrectionEngineScope` を追加する。

10. **影響範囲**
- 追加候補
  - `Services/OcrCorrection/OcrCorrectionService.cs`
  - `Services/OcrCorrection/SymSpellOcrCandidateProvider.cs`
  - `Services/OcrCorrection/OcrWeightedEditDistance.cs`
  - `Services/OcrCorrection/OcrCorrectionDictionary.cs`
  - `Services/OcrCorrection/OcrCorrectionTokenizer.cs`
  - `Models/OcrCorrectionDecision.cs`
  - `Resources/OcrCorrection/en_frequency.tsv`
  - `Resources/OcrCorrection/ocr_confusions.json`
- 変更候補
  - `Hotkey-Translator.csproj` - `SymSpell` packageと辞書resourceの追加。
  - `Models/AppSettings.cs` - OCR訂正設定の追加。
  - `Services/Orchestration/Stages/OcrAndGroupStage.cs` - 訂正stageの差し込み。
  - `Services/Settings/AppSettingsValidator.cs` または関連rule - 設定値の範囲正規化。
  - `ViewModels/SettingsViewModel.cs` - UI binding追加。
  - `UI/OcrSettingsControl.xaml` - OCR訂正設定UI追加。
  - `Resources/Strings.resx`
  - `Resources/Strings.ja.resx`

11. **Definition of Done**
- `EnableOcrCorrection=false` で既存OCR/翻訳/overlay挙動が変わらない。
- `EnableOcrCorrection=true` で、辞書外OCR tokenがSymSpell候補とweighted distanceにより訂正される。
- 元tokenが辞書にある場合は自動訂正されない。
- `rn/m` のような複数文字混同が通常編集距離より低costで評価される。
- 訂正済みtextが `ReadingUnit.Text` に入り、翻訳入力とcache keyに反映される。
- 誤修正調査に必要なsummaryログが出る。
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` が成功する。

12. **Open Questions**
- 初期辞書をどの英語頻度リストから同梱するか。
- OCR訂正をONにした場合、VisionLLMにも適用するか、初期はgeometry系OCRのみに限定するか。
- ユーザー保護語・ユーザー訂正辞書を初回実装に含めるか、後続機能に分けるか。
- 訂正前原文をoverlayやexportで確認できる診断UIを必要とするか。
