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
        if (!settings.EnableOcrBinarization)
        {
            return (Bitmap)source.Clone();
        }

        var threshold = Math.Clamp(settings.OcrBinarizationThreshold, 0, 255);
        return ApplyBinarization(source, threshold);
    }

    private static Bitmap ApplyBinarization(Bitmap source, int threshold)
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

            Marshal.Copy(inputData.Scan0, inputBuffer, 0, inputBuffer.Length);

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
}
