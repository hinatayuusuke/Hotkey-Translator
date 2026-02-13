using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Hotkey_Translator.Services.Application;

internal interface ISettingsChangeScheduler
{
    void RequestSave();
    Task FlushAsync();
    void CancelPending();
}

internal sealed class SettingsChangeScheduler : ISettingsChangeScheduler, IDisposable
{
    private readonly Func<Task> _saveAsync;
    private readonly Action<Exception>? _onError;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _pending;
    private bool _disposed;

    public SettingsChangeScheduler(
        Dispatcher dispatcher,
        Func<Task> saveAsync,
        TimeSpan debounceDelay,
        Action<Exception>? onError = null)
    {
        _saveAsync = saveAsync;
        _onError = onError;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = debounceDelay
        };
        _timer.Tick += OnTick;
    }

    public void RequestSave()
    {
        if (_disposed)
        {
            return;
        }

        _pending = true;
        _timer.Stop();
        _timer.Start();
    }

    public async Task FlushAsync()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        if (!_pending)
        {
            return;
        }

        _pending = false;
        await ExecuteSaveAsync().ConfigureAwait(true);
    }

    public void CancelPending()
    {
        _timer.Stop();
        _pending = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _saveGate.Dispose();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_pending)
        {
            return;
        }

        _pending = false;
        await ExecuteSaveAsync().ConfigureAwait(true);
    }

    private async Task ExecuteSaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await _saveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
