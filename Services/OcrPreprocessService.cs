using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrPreprocessService
{
    public Bitmap Apply(Bitmap source, AppSettings settings)
    {
        return Apply(source, settings, null, null);
    }

    public Bitmap Apply(Bitmap source, AppSettings settings, int? thresholdOverride, bool? autoThresholdOverride)
    {
        if (!settings.EnableOcrBinarization)
        {
            return (Bitmap)source.Clone();
        }

        return ApplyBinarization(source, settings, thresholdOverride, autoThresholdOverride);
    }

    private static Bitmap ApplyBinarization(
        Bitmap source,
        AppSettings settings,
        int? thresholdOverride,
        bool? autoThresholdOverride)
    {
        var input = source;
        var disposeInput = false;
        if (source.PixelFormat != PixelFormat.Format32bppPArgb)
        {
            // NOTE: Some capture providers may output non-32bpp formats; convert to ensure consistent strides.
            input = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(input))
            {
                graphics.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            disposeInput = true;
        }

        var threshold = Math.Clamp(thresholdOverride ?? settings.OcrBinarizationThreshold, 0, 255);
        var useAutoThreshold = autoThresholdOverride ?? settings.EnableOcrAutoThreshold;

        var rect = new Rectangle(0, 0, input.Width, input.Height);
        var output = new Bitmap(input.Width, input.Height, PixelFormat.Format32bppPArgb);
        var inputData = input.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var outputData = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var width = input.Width;
            var height = input.Height;
            var inputStride = inputData.Stride;
            var outputStride = outputData.Stride;
            var inputRowBytes = Math.Abs(inputStride);
            var outputRowBytes = Math.Abs(outputStride);
            var inputBuffer = new byte[inputRowBytes * height];
            var outputBuffer = new byte[outputRowBytes * height];
            var histogram = new int[256];
            long sumLuma = 0;

            Marshal.Copy(inputData.Scan0, inputBuffer, 0, inputBuffer.Length);

            for (var y = 0; y < height; y++)
            {
                var inputRow = inputStride < 0 ? (height - 1 - y) * inputRowBytes : y * inputRowBytes;
                for (var x = 0; x < width; x++)
                {
                    var inputIndex = inputRow + (x * 4);
                    var b = inputBuffer[inputIndex];
                    var g = inputBuffer[inputIndex + 1];
                    var r = inputBuffer[inputIndex + 2];
                    var luma = (int)((0.299 * r) + (0.587 * g) + (0.114 * b));
                    histogram[luma]++;
                    sumLuma += luma;
                }
            }

            var pixelCount = width * height;
            if (useAutoThreshold)
            {
                threshold = ComputeOtsuThreshold(histogram, pixelCount, threshold);
            }

            // WHY: Mean luminance provides a cheap signal for dark backgrounds with bright text.
            var invert = settings.EnableOcrAutoInvert && pixelCount > 0 && (sumLuma / (double)pixelCount) < 128.0;

            for (var y = 0; y < height; y++)
            {
                var inputRow = inputStride < 0 ? (height - 1 - y) * inputRowBytes : y * inputRowBytes;
                var outputRow = outputStride < 0 ? (height - 1 - y) * outputRowBytes : y * outputRowBytes;
                for (var x = 0; x < width; x++)
                {
                    var inputIndex = inputRow + (x * 4);
                    var b = inputBuffer[inputIndex];
                    var g = inputBuffer[inputIndex + 1];
                    var r = inputBuffer[inputIndex + 2];
                    var luma = (int)((0.299 * r) + (0.587 * g) + (0.114 * b));
                    if (invert)
                    {
                        luma = 255 - luma;
                    }
                    var v = (byte)(luma >= threshold ? 255 : 0);

                    var outputIndex = outputRow + (x * 4);
                    outputBuffer[outputIndex] = v;
                    outputBuffer[outputIndex + 1] = v;
                    outputBuffer[outputIndex + 2] = v;
                    outputBuffer[outputIndex + 3] = 255;
                }
            }

            Marshal.Copy(outputBuffer, 0, outputData.Scan0, outputBuffer.Length);
        }
        finally
        {
            input.UnlockBits(inputData);
            output.UnlockBits(outputData);
            if (disposeInput)
            {
                input.Dispose();
            }
        }

        return output;
    }

    private static int ComputeOtsuThreshold(int[] histogram, int pixelCount, int fallbackThreshold)
    {
        if (pixelCount <= 0)
        {
            return fallbackThreshold;
        }

        long sumAll = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            sumAll += (long)i * histogram[i];
        }

        long sumBackground = 0;
        var weightBackground = 0;
        var weightForeground = 0;
        var maxVariance = 0.0;
        var threshold = fallbackThreshold;

        for (var t = 0; t < histogram.Length; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
            {
                continue;
            }

            weightForeground = pixelCount - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += (long)t * histogram[t];
            var meanBackground = sumBackground / (double)weightBackground;
            var meanForeground = (sumAll - sumBackground) / (double)weightForeground;
            var varianceBetween = weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);

            if (varianceBetween > maxVariance)
            {
                maxVariance = varianceBetween;
                threshold = t;
            }
        }

        // NOTE: When the image is nearly uniform, Otsu can be unstable; fall back to the manual threshold.
        return threshold;
    }
}
