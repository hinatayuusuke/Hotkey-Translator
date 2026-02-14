# MVVM_PostRefactor_Stabilization_Plan

1. **概要（1–3行）**
- 本計画は、現時点のMVVM分割を前提に、これ以上の細分化よりも「品質の安定化」に投資するための実装案である。
- 主眼は、回帰を防ぐテスト整備・失敗時フローの統一・View境界の文書固定であり、運用時の不具合コストを下げる。
- 既存のユーザー体験（Hotkey動作、設定保存、gRPC host操作）を維持したまま進める。

2. **ゴール / 非ゴール**
### ゴール
- `ResourceHostFacade` / `ResourceHostCommandController` / `HotkeyCommandController` の主要シナリオを自動テストで固定する。
- 失敗時処理（ログ、設定巻き戻し、ユーザー通知）を共通ポリシーへ寄せる。
- `MainWindow` に残す責務（HWND、Window lifecycle、Composition Root）を文書化して今後の設計ブレを防ぐ。

### 非ゴール
- `MainWindow.xaml.cs` をさらに強引に分割して行数だけ削ること。
- OCR/翻訳アルゴリズムの仕様変更。
- Hotkeyの純MVVM化（Window依存境界を崩す変更）。

3. **前提・仮定**
- 現在、host制御は `ResourceHostFacade`、host操作コマンドは `ResourceHostCommandController`、hotkey実処理は `HotkeyCommandController` に抽出済み。
- `MainWindow.xaml.cs` は約900行まで縮小済みで、分割の主目的は達成に近い。
- 今後の変更リスクは「構造不足」より「回帰検出不足」の比重が高い。

4. **現状整理**
- リファクタリング直後で、経路は整理されているが自動テストが薄く、将来の改修で回帰しやすい。
- 失敗時挙動（例: host起動失敗、restart失敗）の実装は揃ってきたが、共通ポリシーとしての見える化が不足している。
- 責務境界は実装上は改善されているが、判断基準がドキュメントに十分固定されていない。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- 既存構成を維持（`MainWindow` + 各Controller/Facade）。
- 追加は「品質層」中心。
- `Application` 層向けユニットテストプロジェクト（または既存テストプロジェクト拡張）。
- 失敗時フローの共通ヘルパ/ポリシー（必要最小限）。

### 5.2 データフロー / シーケンス
1. 操作（Hotkey/UI Command）
2. CommandController/Fascade実行
3. 成功時: 保存・ログ
4. 失敗時: ログ + 設定巻き戻し + ユーザー通知
5. テストで各分岐を固定

### 5.3 既存パターンとの整合
- 既存の Controller + Facade 構成を維持し、追加するのはテストと小規模な共通化に限定する。
- View依存責務は `MainWindow` に残す方針を明文化する。

6. **インターフェース設計**
- 既存公開APIは極力維持。
- テスト容易化のため、必要に応じて以下を最小追加。
- ログ/通知を差し替え可能にする delegate / interface 注入ポイント。
- 失敗時処理を1箇所で呼べる内部メソッド（例: `HandleHostOperationFailure(...)`）。
- バリデーションは既存 `Normalize*` を流用し、新規ルール追加は最小化。

7. **実装手順（ステップ分割）**
- Step 1: テスト基盤整備
- Controller/Facade単体テストを追加できる土台を作る（mock/stub方針を固定）。

- Step 2: 重要シナリオの回帰テスト追加
- `ResourceHostFacade`: host起動成功/失敗、設定巻き戻し。
- `ResourceHostCommandController`: restart/stopのガード条件、ログ/通知。
- `HotkeyCommandController`: 各Hotkey操作の状態遷移と保存呼び出し。

- Step 3: 失敗時フローの共通化
- ログ・通知・設定反映の重複箇所を抽出し、共通ヘルパ化。

- Step 4: 境界ドキュメント更新
- `Doc/MainWindow_View_Boundary.md` を拡張し、残置責務と禁止事項を明記。

- Step 5: 回帰確認
- build/run + 主要手動シナリオ（F5/F8/F9/F10/F11/F6/F7、host restart/stop）を確認。

8. **非機能要件チェック**
- 性能: 実行時性能への影響は軽微（主にテスト追加）。
- セキュリティ: 既存境界を維持。権限周りの仕様変更なし。
- 可観測性: 失敗時ログの統一で原因追跡を容易化。
- 互換性: 既存 `settings.json` と Hotkey動作を維持。
- 運用: 障害時の再現性が改善し、保守の初動が速くなる。

9. **リスクと緩和策**
- Risk: テスト追加で内部実装に過剰依存し、将来リファクタを阻害する。
- Mitigation: 振る舞いベース（入力/出力/副作用）中心のテストに限定する。

- Risk: 共通化し過ぎて可読性が下がる。
- Mitigation: 重複3箇所以上かつ同一意味の処理のみ共通化するルールを適用する。

- Risk: 追加ドキュメントがメンテされず陳腐化する。
- Mitigation: DoDに「実装差分と文書差分の同時更新」を含める。

10. **影響範囲（変更ファイル候補・移行・ドキュメント更新）**
- 追加候補
- `Tests/Application/ResourceHostFacadeTests.cs`
- `Tests/Application/ResourceHostCommandControllerTests.cs`
- `Tests/Application/HotkeyCommandControllerTests.cs`

- 更新候補
- `Services/Application/ResourceHostFacade.cs`
- `Services/Application/ResourceHostCommandController.cs`
- `Services/Application/HotkeyCommandController.cs`
- `Doc/MainWindow_View_Boundary.md`

- 移行
- 既存機能の移行は不要（互換維持）。

11. **Definition of Done（完了条件）**
- [ ] 3つの主要Controller/Facadeに対して主要成功/失敗シナリオの自動テストが追加されている。
- [ ] 失敗時フローの重複が整理され、ログ/通知/設定反映の方針が統一されている。
- [ ] `Doc/MainWindow_View_Boundary.md` に責務境界と禁止事項が明記されている。
- [ ] `dotnet build` / `dotnet run` が成功し、主要手動シナリオが互換動作する。
- [ ] 今後の変更方針として「行数削減優先ではなく品質安定優先」がチーム内で合意可能な形で記録されている。
