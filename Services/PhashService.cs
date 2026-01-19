using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Hotkey_Translator.Services;

public sealed class PhashService
{
    private const int Size = 32;
    private const int ReducedSize = 8;

    public ulong ComputeHash(Bitmap bitmap)
    {
        using var resized = new Bitmap(Size, Size);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(bitmap, 0, 0, Size, Size);
        }

        var pixels = new double[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var color = resized.GetPixel(x, y);
                pixels[x, y] = (color.R * 0.299) + (color.G * 0.587) + (color.B * 0.114);
            }
        }

        var dct = ComputeDct(pixels);
        var values = new double[ReducedSize * ReducedSize - 1];
        var index = 0;
        for (var y = 0; y < ReducedSize; y++)
        {
            for (var x = 0; x < ReducedSize; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                values[index++] = dct[x, y];
            }
        }

        Array.Sort(values);
        var median = values[values.Length / 2];

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < ReducedSize; y++)
        {
            for (var x = 0; x < ReducedSize; x++)
            {
                var value = dct[x, y];
                if (x == 0 && y == 0)
                {
                    value = median;
                }

                if (value > median)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public int HammingDistance(ulong a, ulong b)
    {
        var value = a ^ b;
        var count = 0;
        while (value != 0)
        {
            count++;
            value &= value - 1;
        }

        return count;
    }

    public bool IsSimilar(ulong current, ulong previous, int threshold)
    {
        return HammingDistance(current, previous) <= threshold;
    }

    private static double[,] ComputeDct(double[,] input)
    {
        var result = new double[Size, Size];
        var cosTable = new double[Size, Size];
        var factor = Math.PI / (2.0 * Size);

        for (var u = 0; u < Size; u++)
        {
            for (var x = 0; x < Size; x++)
            {
                cosTable[u, x] = Math.Cos((2 * x + 1) * u * factor);
            }
        }

        for (var u = 0; u < Size; u++)
        {
            var cu = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
            for (var v = 0; v < Size; v++)
            {
                var cv = v == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
                var sum = 0.0;
                for (var x = 0; x < Size; x++)
                {
                    for (var y = 0; y < Size; y++)
                    {
                        sum += input[x, y] * cosTable[u, x] * cosTable[v, y];
                    }
                }

                result[u, v] = 0.25 * cu * cv * sum;
            }
        }

        return result;
    }
}
