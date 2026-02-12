using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.UI;

public partial class OverlayWindow : Window
{
    private Brush _foreground = Brushes.White;
    private Brush _background = new SolidColorBrush(Color.FromArgb(136, 0, 0, 0));
    private double _fontSize = 18;
    private bool _isFixedRoiOverlay;
    private bool _enableFontStabilization = true;
    private bool _enableSmallBoxReadabilityBoost;
    private double _smallTextThresholdPx = 22;
    private double _smallBoxMaxScale = 1.6;
    private double _smallBoxFontScaleWeight = 0.7;
    private double _smallBoxSlenderAspectThreshold = 3.0;
    private double _smallBoxSlenderThresholdBoost = 1.2;
    private VerticalModeOverride _verticalModeOverride = VerticalModeOverride.Auto;
    private Rect? _smallBoxClipBoundsDip;
    private Dictionary<string, double> _fontSizeCache = new();
    private static readonly Thickness OverlayPadding = new(4, 2, 4, 2);
    private const double MinFontSize = 8;
    private const double MaxFontSize = 72;
    private const int FitIterations = 7;
    private const double FontQuantizeStepPx = 5.0;
    private const double FontHysteresisThreshold = 1.0;
    private const double MinFallbackFontSize = 6.0;
    private const int ToastDurationMs = 1200;
    private const int ToastMinIntervalMs = 500;
    private const double ToastMargin = 12.0;
    private const double ToastMaxWidth = 260.0;
    private const double SpinnerSize = 24.0;
    private const double SpinnerMargin = 12.0;
    private static readonly Duration SpinnerRotationDuration = new(TimeSpan.FromMilliseconds(900));
    private const double AutoVerticalAspectThreshold = 1.25;
    private const double DominantAxisBoostRatio = 0.90;
    private const double SecondaryAxisBoostRatio = 0.45;
    private readonly DispatcherTimer _toastTimer;
    private DateTime _lastToastAtUtc = DateTime.MinValue;

    private enum OverlayWritingMode
    {
        Horizontal,
        Vertical
    }

    private readonly record struct OverlayItemLayout(Rect Rect, double BaseFontSize);

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background);
        _toastTimer.Interval = TimeSpan.FromMilliseconds(ToastDurationMs);
        _toastTimer.Tick += OnToastTimerTick;
    }

    public void ApplyStyle(AppSettings settings)
    {
        _fontSize = settings.OverlayFontSize;
        _foreground = ParseBrush(settings.OverlayForeground, Brushes.White);
        // NOTE: Opacity slider overrides the alpha channel from OverlayBackground.
        var background = ParseBrush(settings.OverlayBackground, new SolidColorBrush(Color.FromArgb(136, 0, 0, 0)));
        _background = ApplyOverlayOpacity(background, settings.OverlayBackgroundOpacity);
        _isFixedRoiOverlay = settings.EnableFixedRoiOverlay;
        _enableFontStabilization = settings.EnableOverlayFontStabilization;
        _enableSmallBoxReadabilityBoost = settings.EnableSmallBoxReadabilityBoost;
        _smallTextThresholdPx = ClampFinite(settings.SmallTextThresholdPx, 8.0, 48.0, 22.0);
        _smallBoxMaxScale = ClampFinite(settings.SmallBoxMaxScale, 1.0, 3.0, 1.6);
        _smallBoxFontScaleWeight = ClampFinite(settings.SmallBoxFontScaleWeight, 0.0, 1.0, 0.7);
        _smallBoxSlenderAspectThreshold = ClampFinite(settings.SmallBoxSlenderAspectThreshold, 1.0, 8.0, 3.0);
        _smallBoxSlenderThresholdBoost = ClampFinite(settings.SmallBoxSlenderThresholdBoost, 1.0, 2.0, 1.2);
        _verticalModeOverride = Enum.IsDefined(typeof(VerticalModeOverride), settings.VerticalModeOverride)
            ? settings.VerticalModeOverride
            : VerticalModeOverride.Auto;
    }

    public void UpdateItems(IReadOnlyList<OverlayItem> items)
    {
        OverlayCanvas.Children.Clear();
        var nextCache = new Dictionary<string, double>(items.Count);
        foreach (var item in items)
        {
            var layout = ResolveOverlayItemLayout(item);
            var rect = layout.Rect;
            var availableWidth = rect.Width > 0
                ? Math.Max(0, rect.Width - OverlayPadding.Left - OverlayPadding.Right)
                : double.PositiveInfinity;
            var availableHeight = rect.Height > 0
                ? Math.Max(0, rect.Height - OverlayPadding.Top - OverlayPadding.Bottom)
                : double.PositiveInfinity;
            var cacheKey = BuildFontCacheKey(item, rect);
            var fontSize = ResolveFontSize(item, availableWidth, availableHeight, cacheKey, layout.BaseFontSize);
            nextCache[cacheKey] = fontSize;
            var textBlock = new TextBlock
            {
                Text = item.Text,
                Foreground = _foreground,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap
            };

            var container = new Border
            {
                Background = _background,
                CornerRadius = new CornerRadius(2),
                Padding = OverlayPadding,
                Child = textBlock,
                MaxWidth = rect.Width > 0 ? rect.Width : double.PositiveInfinity,
                MaxHeight = rect.Height > 0 ? rect.Height : double.PositiveInfinity
            };

            if (rect.Width > 0)
            {
                container.Width = rect.Width;
                textBlock.MaxWidth = availableWidth;
            }

            if (rect.Height > 0)
            {
                container.Height = rect.Height;
                textBlock.MaxHeight = availableHeight;
            }

            Canvas.SetLeft(container, rect.X);
            Canvas.SetTop(container, rect.Y);
            OverlayCanvas.Children.Add(container);
        }

        _fontSizeCache = nextCache;
    }

    public void SetOverlayVisibility(bool visible)
    {
        OverlayCanvas.Opacity = visible ? 1.0 : 0.0;
    }

    public void SetSmallBoxClipBounds(Rect? boundsDip)
    {
        if (boundsDip is not { } rect || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            _smallBoxClipBoundsDip = null;
            return;
        }

        _smallBoxClipBoundsDip = rect;
    }

    public void ShowLoadingSpinner(Rect anchorDipRect)
    {
        if (SpinnerContainer == null || SpinnerRotateTransform == null)
        {
            return;
        }

        var target = anchorDipRect.IsEmpty
            ? new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight))
            : anchorDipRect;
        var spinnerWidth = SpinnerContainer.Width > 0 ? SpinnerContainer.Width : SpinnerSize;
        var spinnerHeight = SpinnerContainer.Height > 0 ? SpinnerContainer.Height : SpinnerSize;
        var left = target.X + target.Width - spinnerWidth - SpinnerMargin;
        var top = target.Y + target.Height - spinnerHeight - SpinnerMargin;
        var maxLeft = Math.Max(SpinnerMargin, Math.Max(0, ActualWidth) - spinnerWidth - SpinnerMargin);
        var maxTop = Math.Max(SpinnerMargin, Math.Max(0, ActualHeight) - spinnerHeight - SpinnerMargin);
        left = Math.Clamp(left, SpinnerMargin, maxLeft);
        top = Math.Clamp(top, SpinnerMargin, maxTop);

        Canvas.SetLeft(SpinnerContainer, left);
        Canvas.SetTop(SpinnerContainer, top);
        SpinnerContainer.Visibility = Visibility.Visible;
        SpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, BuildSpinnerAnimation());
    }

    public void HideLoadingSpinner()
    {
        if (SpinnerContainer == null || SpinnerRotateTransform == null)
        {
            return;
        }

        SpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinnerRotateTransform.Angle = 0;
        SpinnerContainer.Visibility = Visibility.Collapsed;
    }

    public void ShowToast(string text, Rect anchor)
    {
        if (ToastContainer == null || ToastText == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        // WHY: Avoid flooding the UI with repeated toast updates.
        if ((now - _lastToastAtUtc).TotalMilliseconds < ToastMinIntervalMs)
        {
            return;
        }

        _lastToastAtUtc = now;
        ToastText.Text = text;
        ToastText.MaxWidth = ToastMaxWidth;
        ToastContainer.Visibility = Visibility.Visible;

        ToastContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ToastContainer.DesiredSize;
        var target = anchor.IsEmpty
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : anchor;
        var anchorDip = DpiHelper.ScreenRectToWindowDip(this, target);
        var left = anchorDip.X + anchorDip.Width - size.Width - ToastMargin;
        var top = anchorDip.Y + anchorDip.Height - size.Height - ToastMargin;
        left = Math.Max(ToastMargin, Math.Min(left, Math.Max(ToastMargin, ActualWidth - size.Width - ToastMargin)));
        top = Math.Max(ToastMargin, Math.Min(top, Math.Max(ToastMargin, ActualHeight - size.Height - ToastMargin)));
        Canvas.SetLeft(ToastContainer, left);
        Canvas.SetTop(ToastContainer, top);

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var extended = GetWindowLongPtr(hwnd, GwlExStyle);
        var updated = new IntPtr(extended.ToInt64() | WsExTransparent | WsExToolWindow | WsExNoActivate);
        SetWindowLongPtr(hwnd, GwlExStyle, updated);
        // WHY: Exclude the overlay from capture so scene-change checks and OCR use clean frames.
        _ = SetWindowDisplayAffinity(hwnd, WindowDisplayAffinityExcludeFromCapture);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateBounds();
        HideLoadingSpinner();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        if (ToastContainer != null)
        {
            ToastContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private OverlayItemLayout ResolveOverlayItemLayout(OverlayItem item)
    {
        var rect = item.Rect;
        var baseFontSize = _fontSize;
        if (!_enableSmallBoxReadabilityBoost || _isFixedRoiOverlay || rect.Width <= 0 || rect.Height <= 0)
        {
            // WHY: Fixed ROI mode renders one aggregate block, so per-item small-box boosting should stay disabled.
            return new OverlayItemLayout(rect, baseFontSize);
        }

        var shortSide = Math.Min(rect.Width, rect.Height);
        if (shortSide <= 0)
        {
            return new OverlayItemLayout(rect, baseFontSize);
        }

        var writingMode = ResolveWritingMode(rect);
        var effectiveTextPx = ResolveEffectiveTextPx(item, rect, writingMode);
        var dynamicThreshold = _smallTextThresholdPx;
        if (item.LineCount <= 1)
        {
            var aspect = Math.Max(rect.Width, rect.Height) / Math.Max(1.0, shortSide);
            if (aspect >= _smallBoxSlenderAspectThreshold)
            {
                dynamicThreshold *= _smallBoxSlenderThresholdBoost;
            }
        }

        if (effectiveTextPx >= dynamicThreshold)
        {
            return new OverlayItemLayout(rect, baseFontSize);
        }

        var scaleRaw = dynamicThreshold / Math.Max(1.0, effectiveTextPx);
        var boxScale = Math.Clamp(scaleRaw, 1.0, _smallBoxMaxScale);
        var expandedRect = ExpandRectWithAnchor(rect, boxScale, writingMode);
        var clippedRect = ClipRectToOverlayBounds(expandedRect);
        var fontScale = 1.0 + ((boxScale - 1.0) * _smallBoxFontScaleWeight);
        var boostedBaseFont = Math.Clamp(baseFontSize * fontScale, MinFontSize, MaxFontSize);
        return new OverlayItemLayout(clippedRect, boostedBaseFont);
    }

    private OverlayWritingMode ResolveWritingMode(Rect rect)
    {
        if (_verticalModeOverride == VerticalModeOverride.Horizontal)
        {
            return OverlayWritingMode.Horizontal;
        }

        if (_verticalModeOverride == VerticalModeOverride.Vertical)
        {
            return OverlayWritingMode.Vertical;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return OverlayWritingMode.Horizontal;
        }

        var aspect = rect.Height / Math.Max(1.0, rect.Width);
        return aspect >= AutoVerticalAspectThreshold
            ? OverlayWritingMode.Vertical
            : OverlayWritingMode.Horizontal;
    }

    private static double ResolveEffectiveTextPx(OverlayItem item, Rect rect, OverlayWritingMode writingMode)
    {
        var shortSide = Math.Min(rect.Width, rect.Height);
        var lineCount = Math.Max(1, item.LineCount);
        if (writingMode == OverlayWritingMode.Vertical)
        {
            var widthBased = rect.Width / lineCount;
            var lineHeightBased = item.LineHeight > 0 ? Math.Min(item.LineHeight, rect.Width) : widthBased;
            return Math.Max(1.0, Math.Min(shortSide, lineHeightBased));
        }

        var heightBased = rect.Height / lineCount;
        var lineHeightFromItem = item.LineHeight > 0 ? Math.Min(item.LineHeight, rect.Height) : heightBased;
        return Math.Max(1.0, Math.Min(shortSide, lineHeightFromItem));
    }

    private Rect ClipRectToOverlayBounds(Rect rect)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        if (width <= 0 || height <= 0)
        {
            return rect;
        }

        var windowBounds = new Rect(0, 0, width, height);
        var bounds = _smallBoxClipBoundsDip is { } clip
            // WHY: Active-window capture should keep readability boost expansion inside the capture window.
            ? Rect.Intersect(windowBounds, clip)
            : windowBounds;
        if (bounds.IsEmpty)
        {
            bounds = windowBounds;
        }

        var clipped = Rect.Intersect(rect, bounds);
        return clipped.IsEmpty ? rect : clipped;
    }

    private Rect ExpandRectWithAnchor(Rect rect, double scale, OverlayWritingMode writingMode)
    {
        if (scale <= 1.0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return rect;
        }

        var delta = scale - 1.0;
        var (ratioX, ratioY) = writingMode == OverlayWritingMode.Vertical
            ? (DominantAxisBoostRatio, SecondaryAxisBoostRatio)
            : (SecondaryAxisBoostRatio, DominantAxisBoostRatio);
        var scaleX = 1.0 + (delta * ratioX);
        var scaleY = 1.0 + (delta * ratioY);
        var width = rect.Width * scaleX;
        var height = rect.Height * scaleY;
        // WHY: Expanding from center reduces clipping bias when OCR boxes jitter between frames.
        var x = rect.X - ((width - rect.Width) / 2.0);
        var y = rect.Y - ((height - rect.Height) / 2.0);
        return new Rect(x, y, width, height);
    }

    private double ResolveFontSize(OverlayItem item, double availableWidth, double availableHeight, string cacheKey, double baseFontSize)
    {
        var baseSize = Math.Clamp(baseFontSize, MinFontSize, MaxFontSize);
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return baseSize;
        }

        if (availableWidth <= 0 || availableHeight <= 0 || double.IsInfinity(availableWidth) || double.IsInfinity(availableHeight))
        {
            return baseSize;
        }

        var quantizedWidth = _enableFontStabilization ? QuantizeLength(availableWidth) : availableWidth;
        var quantizedHeight = _enableFontStabilization ? QuantizeLength(availableHeight) : availableHeight;
        var resolved = FitMaxFont(item.Text, MinFontSize, baseSize, quantizedWidth, quantizedHeight);
        if (!Fits(item.Text, resolved, availableWidth, availableHeight))
        {
            resolved = FitMaxFont(item.Text, MinFontSize, baseSize, availableWidth, availableHeight);
        }

        if (!Fits(item.Text, MinFontSize, availableWidth, availableHeight))
        {
            var upper = Math.Min(MinFontSize, baseSize);
            resolved = FitMaxFont(item.Text, MinFallbackFontSize, upper, availableWidth, availableHeight);
        }

        if (!_enableFontStabilization)
        {
            return Math.Clamp(resolved, MinFallbackFontSize, MaxFontSize);
        }

        if (_fontSizeCache.TryGetValue(cacheKey, out var last))
        {
            if (resolved < last)
            {
                return Math.Clamp(resolved, MinFallbackFontSize, MaxFontSize);
            }

            if (resolved - last < FontHysteresisThreshold)
            {
                return Math.Clamp(last, MinFallbackFontSize, MaxFontSize);
            }
        }

        return Math.Clamp(resolved, MinFallbackFontSize, MaxFontSize);
    }

    private bool Fits(string text, double fontSize, double maxWidth, double maxHeight)
    {
        if (fontSize <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize,
            _foreground,
            dpi.PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            // WHY: Keep MaxTextHeight unset so we can detect vertical overflow via measured Height.
            TextAlignment = TextAlignment.Left
        };

        return formatted.Width <= maxWidth && formatted.Height <= maxHeight;
    }

    private double FitMaxFont(string text, double minFont, double maxFont, double maxWidth, double maxHeight)
    {
        var lower = Math.Clamp(minFont, MinFallbackFontSize, MaxFontSize);
        var upper = Math.Clamp(maxFont, lower, MaxFontSize);
        if (!Fits(text, lower, maxWidth, maxHeight))
        {
            return lower;
        }

        var best = lower;
        for (var i = 0; i < FitIterations; i++)
        {
            var mid = (lower + upper) / 2.0;
            if (Fits(text, mid, maxWidth, maxHeight))
            {
                best = mid;
                lower = mid;
            }
            else
            {
                upper = mid;
            }
        }

        return best;
    }

    private static double QuantizeLength(double value)
    {
        if (FontQuantizeStepPx <= 0 || value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }

        return Math.Round(value / FontQuantizeStepPx, MidpointRounding.AwayFromZero) * FontQuantizeStepPx;
    }

    private string BuildFontCacheKey(OverlayItem item, Rect rect)
    {
        var x = _enableFontStabilization ? QuantizeLength(rect.X) : rect.X;
        var y = _enableFontStabilization ? QuantizeLength(rect.Y) : rect.Y;
        var width = _enableFontStabilization ? QuantizeLength(rect.Width) : rect.Width;
        var height = _enableFontStabilization ? QuantizeLength(rect.Height) : rect.Height;
        var text = item.Text ?? string.Empty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0}|{1:0.0}|{2:0.0}|{3:0.0}|{4}",
            x,
            y,
            width,
            height,
            text);
    }

    private static double ClampFinite(double value, double min, double max, double fallback)
    {
        if (!double.IsFinite(value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static Brush ParseBrush(string value, Brush fallback)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush ApplyOverlayOpacity(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush;
        }

        var clamped = Math.Clamp(opacity, 0.0, 1.0);
        var alpha = (byte)Math.Round(clamped * 255.0);
        return new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
    }

    private static DoubleAnimation BuildSpinnerAnimation()
    {
        return new DoubleAnimation(0, 360, SpinnerRotationDuration)
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint WindowDisplayAffinityExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
}
