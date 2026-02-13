
現在のアプローチ（コードビハインドにロジックを書くスタイル）から、WPFの標準的な設計パターンである **MVVM (Model-View-ViewModel)** へ移行する

現状のコードを見ると、3300行（から減らしたとしてもまだ多い状態）の主な原因は以下の3点に集約されます。

1.  **手動データバインディング（Settings ⇔ UI）の記述が膨大**
2.  **外部プロセスの管理（ライフサイクル）をViewが持っている**
3.  **UIイベントハンドラにロジックが書かれている**

これらを解決するための具体的なリファクタリング案を提案します。

---

### 1. ViewModelの導入とデータバインディング (効果：最大)

現在のコードで最も行数を費やしているのは、`ApplySettingsToUi` や `ApplyUiInputToSettings`、そして大量の `On...Changed` イベントです。これらは「設定クラス」と「画面のコントロール」を仲介するためだけに存在しています。

WPFでは、これらを **データバインディング** で自動化するのが定石です。

**現状 (Code-Behind):**
```csharp
// UIへの反映
EnableRoiCheck.IsChecked = settings.EnableRoi;
PaddleConfidenceThresholdSlider.Value = settings.PaddleConfidenceThreshold;

// 設定への保存
settings.EnableRoi = EnableRoiCheck.IsChecked == true;

// 値が変わった時のイベント
private void OnPaddleConfidenceThresholdChanged(object sender, ...) {
    UpdatePaddleConfidenceThresholdValue();
    SaveSettingsAsync();
}
```

**改善案 (MVVM):**
`MainViewModel` クラスを作成し、そこにプロパティを持たせます。XAML側でバインドすれば、C#側の同期コードは**全削除**できます。

```csharp
// MainViewModel.cs
public class MainViewModel : INotifyPropertyChanged
{
    private AppSettings _settings;

    public bool EnableRoi
    {
        get => _settings.EnableRoi;
        set
        {
            if (_settings.EnableRoi != value)
            {
                _settings.EnableRoi = value;
                OnPropertyChanged();
                // 設定変更時の保存処理もここで一元管理可能
                SaveSettingsAsync();
            }
        }
    }
    // ... 他のすべての設定項目も同様
}
```

```xml
<!-- MainWindow.xaml -->
<CheckBox IsChecked="{Binding EnableRoi}" ... />
```

**削除できるもの:**
*   `ApplySettingsToUi` (全行)
*   `ApplyUiInputToSettings` (全行)
*   ほとんどの `On...Changed` イベントハンドラ
*   `Update...Value` 系メソッド（XAML上の `StringFormat` や Converter で解決可能）

これで**500〜1000行近く削減**できるはずです。

### 2. 外部プロセス管理の委譲 (Service Lifecycle)

現在、`MainWindow` が `LlamaGrpcHost` や `PaddleGrpcHost` の起動・停止・再起動（`EnsureResourceHostsAsync`, `TryStart...`）を直接管理しています。

これは View（画面）の責任範囲を超えています。これらを管理する専用のクラス（例：`BackgroundServiceManager`）に移動すべきです。

**改善案:**

```csharp
public class ResourceHostManager
{
    // Llama, Paddle, CTranslate2 などのホスト保持と管理ロジックをここに移動
    public async Task EnsureHostsAsync(AppSettings settings) { ... }
    public async Task RestartLlamaAsync() { ... }
}
```

`MainWindow` はこのマネージャクラスのメソッドを呼ぶだけ、あるいは ViewModel がコマンド経由で呼ぶ形にします。

**削除できるもの:**
*   `EnsureResourceHostsAsync`
*   `TryStart...HostAsync` 系
*   `ShouldLoad...` 系
*   `OnRestart...` 系の中身のロジック

### 3. ICommand の利用 (イベントハンドラの削除)

ボタンクリック時の処理（`OnRunOnce`, `OnToggleOverlay` など）がイベントハンドラとして記述されています。これを `ICommand` (RelayCommandなど) に置き換えると、ViewModel 内にロジックを移動できます。

**現状:**
```csharp
private async void OnRunOnce(object sender, RoutedEventArgs e)
{
    await RunOnceAsync().ConfigureAwait(true);
}
```

**改善案 (ViewModel):**
```csharp
public ICommand RunOnceCommand { get; }

public MainViewModel() {
    RunOnceCommand = new RelayCommand(async () => await _runCoordinator.RunOnceAsync(...));
}
```

### 今後のステップ推奨

いきなりすべてを変更するのは大変ですので、以下の順序で進めるのが最も効率的です。

1.  **`SettingsViewModel` の作成**:
    *   一番行数を食っている「設定値の読み書き」部分を ViewModel に移し、XAMLの Binding に書き換える。これだけでファイルの見通しが劇的に良くなります。
2.  **ホスト管理の分離**:
    *   `LlamaGrpcHost` や `PaddleGrpcHost` のフィールド変数を `MainWindow` から消し、新しい管理クラスへ移動させる。
3.  **UIロジックのコンバータ化**:
    *   「スライダーの値が変わったらラベルのテキストを更新する」といった処理は、C#コードではなく XAML の `Binding` と `Converter` で実現する。

このファイルを「単なるView（表示とユーザ操作の入り口）」に戻してあげれば、最終的には **200〜300行程度**（初期化とウィンドウ特有のイベント処理のみ）に収まるはずです。

