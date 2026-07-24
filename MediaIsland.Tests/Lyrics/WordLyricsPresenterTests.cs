using Avalonia;
using MediaIsland.Controls;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class WordLyricsPresenterTests
{
    [Fact]
    public void WordLiftResponse_UsesTheWordEffectiveDuration()
    {
        var word = new LyricsWord(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            "rise");

        Assert.Equal(
            0,
            WordLyricsPresenter.GetCharacterLiftResponse(word.StartTime, word.StartTime, word.EndTime),
            6);
        Assert.Equal(
            0.875,
            WordLyricsPresenter.GetCharacterLiftResponse(TimeSpan.FromSeconds(3), word.StartTime, word.EndTime),
            6);
        Assert.Equal(
            1,
            WordLyricsPresenter.GetCharacterLiftResponse(word.EndTime, word.StartTime, word.EndTime),
            6);
        Assert.Equal(
            1,
            WordLyricsPresenter.GetCharacterLiftResponse(TimeSpan.FromSeconds(5), word.StartTime, word.EndTime),
            6);
    }

    [Fact]
    public void WordEmphasis_UsesAmllDurationBasedStrength()
    {
        var oneSecondWord = new LyricsWord(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            "hold");
        var longWord = oneSecondWord with { EndTime = TimeSpan.FromSeconds(4) };

        Assert.False(WordLyricsPresenter.IsWordEmphasisEligible(oneSecondWord));
        Assert.True(WordLyricsPresenter.IsWordEmphasisEligible(longWord));
        var amount = WordLyricsPresenter.GetWordEmphasisAmount(
            longWord.StartTime,
            longWord.EndTime,
            false);
        var lastWordAmount = WordLyricsPresenter.GetWordEmphasisAmount(
            longWord.StartTime,
            longWord.EndTime,
            true);

        Assert.Equal(0.6, amount, 6);
        Assert.Equal(0.96, lastWordAmount, 6);
        Assert.Equal(
            1.06,
            WordLyricsPresenter.GetCharacterEmphasisScale(
                TimeSpan.FromSeconds(3),
                longWord.StartTime,
                longWord.EndTime,
                amount),
            6);
        Assert.Equal(
            1.096,
            WordLyricsPresenter.GetCharacterEmphasisScale(
                TimeSpan.FromSeconds(3),
                longWord.StartTime,
                longWord.EndTime,
                lastWordAmount),
            6);
        Assert.Equal(
            1,
            WordLyricsPresenter.GetCharacterEmphasisScale(
                longWord.EndTime,
                longWord.StartTime,
                longWord.EndTime,
                amount),
            6);
    }

    [Fact]
    public void CharacterEmphasisWindows_UseAmllStaggeredWaveTiming()
    {
        var wordStart = TimeSpan.FromSeconds(2);
        var wordEnd = TimeSpan.FromSeconds(4);
        var first = WordLyricsPresenter.GetCharacterEmphasisWindow(
            wordStart,
            wordEnd,
            0,
            4,
            false);
        var second = WordLyricsPresenter.GetCharacterEmphasisWindow(
            wordStart,
            wordEnd,
            1,
            4,
            false);
        var lastWordSecond = WordLyricsPresenter.GetCharacterEmphasisWindow(
            wordStart,
            wordEnd,
            1,
            4,
            true);

        Assert.True(first.End > second.Start);
        Assert.Equal(wordStart, first.Start);
        Assert.Equal(wordEnd, first.End);
        Assert.Equal(TimeSpan.FromSeconds(2.2), second.Start);
        Assert.Equal(TimeSpan.FromSeconds(4.2), second.End);
        Assert.Equal(TimeSpan.FromSeconds(2.24), lastWordSecond.Start);
        Assert.Equal(TimeSpan.FromSeconds(4.64), lastWordSecond.End);
        Assert.Equal(0, WordLyricsPresenter.GetEmphasisWaveResponse(first.Start, first.Start, first.End), 6);
        Assert.Equal(0, WordLyricsPresenter.GetEmphasisWaveResponse(first.End, first.Start, first.End), 6);
    }

    [Fact]
    public void EmphasisWaveResponse_UsesAmllRiseAndFallBezierCurves()
    {
        Assert.Equal(0, WordLyricsPresenter.GetEmphasisWaveResponse(0), 6);
        Assert.Equal(0.739398, WordLyricsPresenter.GetEmphasisWaveResponse(0.25), 6);
        Assert.Equal(1, WordLyricsPresenter.GetEmphasisWaveResponse(0.5), 6);
        Assert.Equal(0.43052, WordLyricsPresenter.GetEmphasisWaveResponse(0.75), 6);
        Assert.Equal(0, WordLyricsPresenter.GetEmphasisWaveResponse(1), 6);
    }

    [Fact]
    public void EmphasisTransform_SpreadsCharactersAndReturnsToTheLiftedPosition()
    {
        var start = TimeSpan.FromSeconds(2);
        var end = TimeSpan.FromSeconds(4);
        const double amount = 0.96;

        Assert.Equal(
            -1.152,
            WordLyricsPresenter.GetCharacterEmphasisHorizontalOffset(20, amount, 1, 0, 4),
            6);
        Assert.Equal(
            0.576,
            WordLyricsPresenter.GetCharacterEmphasisHorizontalOffset(20, amount, 1, 3, 4),
            6);
        Assert.Equal(
            -1.48,
            WordLyricsPresenter.GetCharacterEmphasisVerticalOffset(
                20,
                amount,
                TimeSpan.FromSeconds(3),
                start,
                end),
            6);
        Assert.Equal(
            0,
            WordLyricsPresenter.GetCharacterEmphasisVerticalOffset(20, amount, end, start, end),
            6);
    }

    [Fact]
    public void SplitTextElements_PreservesUnicodeCharacters()
    {
        var elements = WordLyricsPresenter.SplitTextElements("你A😀e\u0301");

        Assert.Equal(new[] { "你", "A", "😀", "e\u0301" }, elements);
    }

    [Fact]
    public void ShortCharacterLiftDuration_FallsBackToTheWholeWord()
    {
        var start = TimeSpan.FromSeconds(2);

        Assert.True(WordLyricsPresenter.IsCharacterLiftDurationTooShort(
            start,
            start + TimeSpan.FromMilliseconds(100)));
        Assert.False(WordLyricsPresenter.IsCharacterLiftDurationTooShort(
            start,
            start + TimeSpan.FromMilliseconds(120)));
    }

    [Fact]
    public void EmphasisGlow_UsesAmllBlurAndTextShadowModel()
    {
        var start = TimeSpan.FromSeconds(2);
        var end = TimeSpan.FromSeconds(4); // 2000ms
        // blur = (2000/3000)^3 * 0.5 = (2/3)^3 * 0.5
        var blur = WordLyricsPresenter.GetWordEmphasisBlur(start, end, false);
        var lastBlur = WordLyricsPresenter.GetWordEmphasisBlur(start, end, true);
        Assert.Equal(8.0 / 27.0 * 0.5, blur, 6);
        Assert.Equal(8.0 / 27.0 * 0.5 * 1.5, lastBlur, 6);

        // 3s 分界：du/3000 = 1 → blur = 0.5；末词 0.75
        var threeSecondBlur = WordLyricsPresenter.GetWordEmphasisBlur(
            start,
            start + TimeSpan.FromSeconds(3),
            false);
        var threeSecondLastBlur = WordLyricsPresenter.GetWordEmphasisBlur(
            start,
            start + TimeSpan.FromSeconds(3),
            true);
        Assert.Equal(0.5, threeSecondBlur, 6);
        Assert.Equal(0.75, threeSecondLastBlur, 6);

        // 上限 0.8
        var cappedBlur = WordLyricsPresenter.GetWordEmphasisBlur(
            start,
            start + TimeSpan.FromSeconds(20),
            true);
        Assert.Equal(0.8, cappedBlur, 6);

        Assert.Equal(0, WordLyricsPresenter.GetEmphasisGlowLevel(blur, 0), 6);
        Assert.Equal(0, WordLyricsPresenter.GetEmphasisGlowLevel(0, 1), 6);
        Assert.Equal(blur, WordLyricsPresenter.GetEmphasisGlowLevel(blur, 1), 6);
        Assert.Equal(blur * 0.5, WordLyricsPresenter.GetEmphasisGlowLevel(blur, 0.5), 6);
        Assert.Equal(0.8, WordLyricsPresenter.GetEmphasisGlowLevel(0.8, 1), 6);

        // radius = fontSize * min(0.3, blur * 0.3)
        Assert.Equal(
            20 * Math.Min(0.3, blur * 0.3),
            WordLyricsPresenter.GetEmphasisGlowRadius(20, blur),
            6);
        Assert.Equal(
            20 * Math.Min(0.3, 0.8 * 0.3),
            WordLyricsPresenter.GetEmphasisGlowRadius(20, 0.8),
            6);
        Assert.Equal(0, WordLyricsPresenter.GetEmphasisGlowRadius(20, 0), 6);
    }

    [Fact]
    public void WordEmphasisScale_KeepsTheWordCenterFixed()
    {
        var center = new Point(20, 10);
        var matrix = WordLyricsPresenter.CreateScaleAround(1.096, center);
        var transformed = matrix.Transform(new Point(30, 10));

        Assert.Equal(center, matrix.Transform(center));
        Assert.Equal(30.96, transformed.X, 6);
        Assert.Equal(10, transformed.Y, 6);
    }

    [Fact]
    public void BlurBgra8888_SpreadsOpaqueCenterIntoNeighbors()
    {
        // 3x3，中心不透明白，其余透明。
        var source = new byte[3 * 3 * 4];
        var center = (1 * 3 + 1) * 4;
        source[center + 0] = 255;
        source[center + 1] = 255;
        source[center + 2] = 255;
        source[center + 3] = 255;

        var blurred = WordLyricsPresenter.BlurBgra8888(source, 3, 3, 1);

        Assert.True(blurred[center + 3] > 0);
        // 邻像素应被 alpha 柔边点亮
        Assert.True(blurred[((0 * 3) + 1) * 4 + 3] > 0);
        Assert.True(blurred[((1 * 3) + 0) * 4 + 3] > 0);
    }

}
