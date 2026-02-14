using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Hotkey_Translator.Services.Application;

internal sealed class PreviewFrameDispatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _shouldRenderAccessor;
    private readonly Action<BitmapSource> _applyPreview;
    private readonly Action<Exception> _onUpdateError;
    private readonly object _frameGate = new();
    private Bitmap? _latestFrame;
    private bool _flushScheduled;

    public PreviewFrameDispatcher(
        Dispatcher dispatcher,
        Func<bool> shouldRenderAccessor,
        Action<BitmapSource> applyPreview,
        Action<Exception> onUpdateError)
    {
        _dispatcher = dispatcher;
        _shouldRenderAccessor = shouldRenderAccessor;
        _applyPreview = applyPreview;
        _onUpdateError = onUpdateError;
    }

    public void Enqueue(Bitmap bitmap)
    {
        Bitmap? staleFrame = null;
        lock (_frameGate)
        {
            staleFrame = _latestFrame;
            _latestFrame = (Bitmap)bitmap.Clone();
        }

        staleFrame?.Dispose();
        RequestFlushIfVisible();
    }

    public void RequestFlushIfVisible()
    {
        // PERF: Keep only the latest frame to avoid backlog spikes when OCR previews arrive faster than UI can render.
        if (!_shouldRenderAccessor())
        {
            return;
        }

        bool shouldSchedule;
        lock (_frameGate)
        {
            shouldSchedule = !_flushScheduled && _latestFrame != null;
            if (shouldSchedule)
            {
                _flushScheduled = true;
            }
        }

        if (!shouldSchedule)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushLatestFrame));
    }

    public void Dispose()
    {
        Bitmap? staleFrame;
        lock (_frameGate)
        {
            staleFrame = _latestFrame;
            _latestFrame = null;
            _flushScheduled = false;
        }

        staleFrame?.Dispose();
    }

    private void FlushLatestFrame()
    {
        while (true)
        {
            if (!_shouldRenderAccessor())
            {
                lock (_frameGate)
                {
                    _flushScheduled = false;
                }

                return;
            }

            Bitmap? frame;
            lock (_frameGate)
            {
                frame = _latestFrame;
                _latestFrame = null;
                if (frame == null)
                {
                    _flushScheduled = false;
                    return;
                }
            }

            try
            {
                var source = CreateBitmapSource(frame);
                _applyPreview(source);
            }
            catch (Exception ex)
            {
                _onUpdateError(ex);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    private static BitmapSource CreateBitmapSource(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                bitmap.HorizontalResolution,
                bitmap.VerticalResolution,
                System.Windows.Media.PixelFormats.Pbgra32,
                null,
                buffer,
                stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
