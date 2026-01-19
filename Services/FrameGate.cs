using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class FrameGate
{
    public bool IsBlack(Bitmap bitmap, AppSettings settings, out FrameLumaStats stats)
    {
        stats = default;

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * data.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var sampleStride = Math.Max(1, settings.BlackSampleStride);
            double sum = 0;
            double sumSquares = 0;
            var count = 0;

            for (var y = 0; y < data.Height; y += sampleStride)
            {
                var row = y * stride;
                for (var x = 0; x < data.Width; x += sampleStride)
                {
                    var offset = row + (x * 4);
                    var b = buffer[offset];
                    var g = buffer[offset + 1];
                    var r = buffer[offset + 2];
                    var luma = (0.299 * r) + (0.587 * g) + (0.114 * b);
                    sum += luma;
                    sumSquares += luma * luma;
                    count++;
                }
            }

            if (count == 0)
            {
                return false;
            }

            var mean = sum / count;
            var variance = (sumSquares / count) - (mean * mean);
            stats = new FrameLumaStats(mean, variance, count);

            return mean <= settings.BlackLumaThreshold && variance <= settings.BlackVarianceThreshold;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

public readonly record struct FrameLumaStats(double Mean, double Variance, int Samples);
