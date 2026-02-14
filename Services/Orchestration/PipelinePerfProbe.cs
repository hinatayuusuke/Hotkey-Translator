using System.Diagnostics;

namespace Hotkey_Translator.Services.Orchestration;

internal sealed class PipelinePerfProbe
{
    private readonly bool _enabled;
    private readonly long _thresholdMs;
    private readonly Stopwatch? _totalStopwatch;

    public PipelinePerfProbe(bool enabled, int thresholdMs)
    {
        _enabled = enabled;
        _thresholdMs = thresholdMs < 0 ? 0 : thresholdMs;
        _totalStopwatch = _enabled ? Stopwatch.StartNew() : null;
    }

    public long QueueWaitMs { get; private set; }

    public long CaptureMs { get; private set; }

    public long CropMs { get; private set; }

    public long OcrMs { get; private set; }

    public long GroupMs { get; private set; }

    public long DiffMs { get; private set; }

    public long OverlayMs { get; private set; }

    public bool Enabled => _enabled;

    public Stopwatch? BeginStep()
    {
        return _enabled ? Stopwatch.StartNew() : null;
    }

    public void RecordQueueWait(Stopwatch queueWaitStopwatch)
    {
        if (!_enabled)
        {
            return;
        }

        QueueWaitMs = queueWaitStopwatch.ElapsedMilliseconds;
    }

    public void RecordCapture(Stopwatch? stepStopwatch)
    {
        CaptureMs = StopStep(stepStopwatch);
    }

    public void RecordCrop(Stopwatch? stepStopwatch)
    {
        CropMs = StopStep(stepStopwatch);
    }

    public void RecordOverlay(Stopwatch? stepStopwatch)
    {
        OverlayMs = StopStep(stepStopwatch);
    }

    public void RecordOcr(long elapsedMs)
    {
        if (_enabled)
        {
            OcrMs = elapsedMs;
        }
    }

    public void RecordGroup(long elapsedMs)
    {
        if (_enabled)
        {
            GroupMs = elapsedMs;
        }
    }

    public void RecordDiff(long elapsedMs)
    {
        if (_enabled)
        {
            DiffMs = elapsedMs;
        }
    }

    public void LogIfThresholdExceeded(AppLogger logger)
    {
        if (!_enabled || _totalStopwatch == null)
        {
            return;
        }

        _totalStopwatch.Stop();
        if (_totalStopwatch.ElapsedMilliseconds < _thresholdMs)
        {
            return;
        }

        logger.Info(
            $"[Perf] QueueWait={QueueWaitMs}ms, Capture={CaptureMs}ms, Crop={CropMs}ms, OCR={OcrMs}ms, " +
            $"Group={GroupMs}ms, Diff={DiffMs}ms, Overlay={OverlayMs}ms (total={_totalStopwatch.ElapsedMilliseconds}ms).");
    }

    private long StopStep(Stopwatch? stepStopwatch)
    {
        if (!_enabled || stepStopwatch == null)
        {
            return 0;
        }

        stepStopwatch.Stop();
        return stepStopwatch.ElapsedMilliseconds;
    }
}
