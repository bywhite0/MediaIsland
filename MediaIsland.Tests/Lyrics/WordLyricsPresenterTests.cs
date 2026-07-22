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
    public void WordEmphasisScale_KeepsTheWordCenterFixed()
    {
        var center = new Point(20, 10);
        var matrix = WordLyricsPresenter.CreateScaleAround(1.096, center);
        var transformed = matrix.Transform(new Point(30, 10));

        Assert.Equal(center, matrix.Transform(center));
        Assert.Equal(30.96, transformed.X, 6);
        Assert.Equal(10, transformed.Y, 6);
    }
}
