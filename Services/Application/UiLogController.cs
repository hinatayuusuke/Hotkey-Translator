using System;
using System.Collections.Concurrent;
using System.Text;
using System.Windows.Threading;

namespace Hotkey_Translator.Services.Application;

internal sealed class UiLogController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _flushPayload;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly DispatcherTimer _flushTimer;
    private bool _flushPending;
    private volatile bool _enabled = true;

    public UiLogController(Dispatcher dispatcher, Action<string> flushPayload, int flushIntervalMs)
    {
        _dispatcher = dispatcher;
        _flushPayload = flushPayload;
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(flushIntervalMs)
        };
        _flushTimer.Tick += OnFlushTick;
    }

    public void Start()
    {
        if (_flushTimer.IsEnabled)
        {
            return;
        }

        _flushTimer.Start();
    }

    public void AppendLog(string message)
    {
        if (!_enabled)
        {
            return;
        }

        _queue.Enqueue(message);
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            ClearQueue();
            _flushPending = false;
        }
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlushTick;
        ClearQueue();
        _flushPending = false;
    }

    private void OnFlushTick(object? sender, EventArgs e)
    {
        FlushLogs();
    }

    private void FlushLogs()
    {
        if (!_enabled)
        {
            ClearQueue();
            return;
        }

        if (_flushPending)
        {
            return;
        }

        var builder = new StringBuilder();
        while (_queue.TryDequeue(out var message))
        {
            builder.AppendLine(message);
        }

        if (builder.Length == 0)
        {
            return;
        }

        var payload = builder.ToString();
        _flushPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _flushPending = false;
            _flushPayload(payload);
        }));
    }

    private void ClearQueue()
    {
        while (_queue.TryDequeue(out _))
        {
        }
    }
}
