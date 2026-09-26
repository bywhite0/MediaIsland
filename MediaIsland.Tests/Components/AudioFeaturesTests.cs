using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioFeaturesTests
{
    private const int SampleRate = 48000;
    private const int Bins = 1024;

    /// <summary>只在给定频率所在 bin 上有能量的谱。bin 宽 = sampleRate / (Bins*2)。</summary>
    private static float[] Tone(double hz)
    {
        var spectrum = new float[Bins];
        spectrum[(int)Math.Round(hz / (SampleRate / (Bins * 2.0)))] = 1f;
        return spectrum;
    }

    [Fact]
    public void Centroid_LowToneIsBelowHighTone()
    {
        var low = AudioFeatures.SpectralCentroid(Tone(200), SampleRate, 20, 20000);
        var high = AudioFeatures.SpectralCentroid(Tone(8000), SampleRate, 20, 20000);

        Assert.True(low < high, $"low={low} high={high}");
        Assert.InRange(low, 0f, 1f);
        Assert.InRange(high, 0f, 1f);
    }

    [Fact]
    public void Centroid_Silence_IsNeutral()
    {
        Assert.Equal(0.5f, AudioFeatures.SpectralCentroid(new float[Bins], SampleRate, 20, 20000));
    }

    [Fact]
    public void Centroid_EmptySpectrum_IsNeutral()
    {
        Assert.Equal(0.5f, AudioFeatures.SpectralCentroid([], SampleRate, 20, 20000));
    }

    [Fact]
    public void ChromaFold_A440_PutsTheMaximumOnA()
    {
        var chroma = AudioFeatures.ChromaFold(Tone(440), SampleRate, 20, 20000);

        Assert.Equal(12, chroma.Length);
        Assert.Equal(9, Array.IndexOf(chroma, chroma.Max()));
        Assert.Equal(1f, chroma[9]);
    }

    [Fact]
    public void ChromaFold_IgnoresBinsBelow110Hz()
    {
        var chroma = AudioFeatures.ChromaFold(Tone(55), SampleRate, 20, 20000);

        Assert.All(chroma, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ChromaFold_InvertedRange_DoesNotThrow()
    {
        var chroma = AudioFeatures.ChromaFold(Tone(440), SampleRate, 5000, 200);

        Assert.Equal(12, chroma.Length);
        Assert.Equal(9, Array.IndexOf(chroma, chroma.Max()));
    }

    [Fact]
    public void ChromaFold_Silence_IsAllZeroWithoutNaN()
    {
        var chroma = AudioFeatures.ChromaFold(new float[Bins], SampleRate, 20, 20000);

        Assert.All(chroma, v => Assert.Equal(0f, v));
    }
}
