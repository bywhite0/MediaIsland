using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MediaIsland.Helpers;

/// <summary>
/// 从专辑封面提取可用作进度条前景的主题色。
/// </summary>
public static class CoverThemeColorHelper
{
    /// <summary>
    /// 尝试从 Avalonia Bitmap 提取主题色。失败时返回 null。
    /// </summary>
    public static Color? TryExtract(Bitmap? bitmap)
    {
        if (bitmap == null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);
            using var skStream = new SKManagedStream(stream);
            using var skBitmap = SKBitmap.Decode(skStream);
            return TryExtract(skBitmap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 尝试从 Skia 位图提取主题色。失败时返回 null。
    /// </summary>
    public static Color? TryExtract(SKBitmap? bitmap)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return null;
        }

        try
        {
            // 缩小采样，控制开销
            const int sampleSize = 32;
            using var sampled = bitmap.Resize(new SKImageInfo(sampleSize, sampleSize), SKFilterQuality.Medium)
                               ?? bitmap.Copy();
            if (sampled == null)
            {
                return null;
            }

            var count = 0;

            // 分桶统计更饱和的像素，优先选有代表性的色相
            var hueBuckets = new double[36];
            var hueWeights = new double[36];

            for (var y = 0; y < sampled.Height; y++)
            {
                for (var x = 0; x < sampled.Width; x++)
                {
                    var pixel = sampled.GetPixel(x, y);
                    if (pixel.Alpha < 128)
                    {
                        continue;
                    }

                    var r = pixel.Red / 255.0;
                    var g = pixel.Green / 255.0;
                    var b = pixel.Blue / 255.0;
                    RgbToHsv(r, g, b, out var h, out var s, out var v);

                    // 过暗/过亮/过灰的像素对进度条着色帮助不大
                    if (v is < 0.18 or > 0.92 || s < 0.12)
                    {
                        continue;
                    }

                    var weight = s * s * (0.35 + 0.65 * (1.0 - Math.Abs(v - 0.55) * 2.0));
                    if (weight <= 0)
                    {
                        continue;
                    }

                    var bucket = (int)(h / 10.0) % 36;
                    if (bucket < 0)
                    {
                        bucket += 36;
                    }

                    hueBuckets[bucket] += h * weight;
                    hueWeights[bucket] += weight;
                    count++;
                }
            }

            if (count == 0 || hueWeights.All(w => w <= 0))
            {
                // 退化：用全图平均色并抬高饱和度
                return ExtractAverageFallback(sampled);
            }

            var bestBucket = 0;
            var bestWeight = hueWeights[0];
            for (var i = 1; i < hueWeights.Length; i++)
            {
                if (hueWeights[i] > bestWeight)
                {
                    bestWeight = hueWeights[i];
                    bestBucket = i;
                }
            }

            var avgHue = hueBuckets[bestBucket] / bestWeight;
            // 固定较高饱和与适中亮度，确保进度条在岛上可读
            HsvToRgb(avgHue, 0.72, 0.72, out var rr, out var gg, out var bb);
            return Color.FromRgb(
                (byte)Math.Clamp((int)Math.Round(rr * 255), 0, 255),
                (byte)Math.Clamp((int)Math.Round(gg * 255), 0, 255),
                (byte)Math.Clamp((int)Math.Round(bb * 255), 0, 255));
        }
        catch
        {
            return null;
        }
    }

    private static Color? ExtractAverageFallback(SKBitmap sampled)
    {
        long sumR = 0, sumG = 0, sumB = 0, count = 0;
        for (var y = 0; y < sampled.Height; y++)
        {
            for (var x = 0; x < sampled.Width; x++)
            {
                var pixel = sampled.GetPixel(x, y);
                if (pixel.Alpha < 128)
                {
                    continue;
                }

                sumR += pixel.Red;
                sumG += pixel.Green;
                sumB += pixel.Blue;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var r = sumR / (double)count / 255.0;
        var g = sumG / (double)count / 255.0;
        var b = sumB / (double)count / 255.0;
        RgbToHsv(r, g, b, out var h, out var s, out var v);
        s = Math.Clamp(Math.Max(s, 0.45), 0, 1);
        v = Math.Clamp(Math.Max(v, 0.55), 0.45, 0.85);
        HsvToRgb(h, s, v, out var rr, out var gg, out var bb);
        return Color.FromRgb(
            (byte)Math.Clamp((int)Math.Round(rr * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(gg * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(bb * 255), 0, 255));
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
            return;
        }

        if (Math.Abs(max - r) < double.Epsilon)
        {
            h = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (Math.Abs(max - g) < double.Epsilon)
        {
            h = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / delta) + 4.0);
        }

        if (h < 0)
        {
            h += 360.0;
        }
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;

        double r1, g1, b1;
        if (h < 60)
        {
            r1 = c; g1 = x; b1 = 0;
        }
        else if (h < 120)
        {
            r1 = x; g1 = c; b1 = 0;
        }
        else if (h < 180)
        {
            r1 = 0; g1 = c; b1 = x;
        }
        else if (h < 240)
        {
            r1 = 0; g1 = x; b1 = c;
        }
        else if (h < 300)
        {
            r1 = x; g1 = 0; b1 = c;
        }
        else
        {
            r1 = c; g1 = 0; b1 = x;
        }

        r = r1 + m;
        g = g1 + m;
        b = b1 + m;
    }
}
