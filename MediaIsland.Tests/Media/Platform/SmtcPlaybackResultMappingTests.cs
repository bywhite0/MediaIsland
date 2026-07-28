using MediaIsland.Services.Media.Platform;
using Xunit;

namespace MediaIsland.Tests.Media.Platform;

/// <summary>
/// Pure mapping contract for SMTC playback results (no WinRT types in shared tests).
/// Manual / Windows runtime checklist for WindowsSmtcMediaPlaybackController:
/// 1. No focused session → NoSession
/// 2. Try*Async returns false → Failed (or NotSupported when Controls flag is off)
/// 3. Successful play/pause/next/previous → Succeeded
/// 4. Must not throw WinRT exceptions to callers
/// </summary>
public class SmtcPlaybackResultMappingTests
{
    [Theory]
    [InlineData(true, MediaPlaybackCommandStatus.Succeeded)]
    [InlineData(false, MediaPlaybackCommandStatus.Failed)]
    public void MapTryResult(bool tryOk, MediaPlaybackCommandStatus expected)
    {
        var status = ToCommandStatus(tryOk);
        Assert.Equal(expected, status);
    }

    [Fact]
    public void MapNoSession_ReturnsNoSession()
    {
        var result = MapNoSession();
        Assert.Equal(MediaPlaybackCommandStatus.NoSession, result.Status);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void MapNotSupported_WhenControlDisabled()
    {
        var result = MapNotSupported("Play");
        Assert.Equal(MediaPlaybackCommandStatus.NotSupported, result.Status);
    }

    [Fact]
    public void MapFailed_FromExceptionMessage()
    {
        var result = MapFailed("RPC server unavailable");
        Assert.Equal(MediaPlaybackCommandStatus.Failed, result.Status);
        Assert.Equal("RPC server unavailable", result.Message);
    }

    // Mirrors WindowsSmtcMediaPlaybackController status mapping without WinRT.
    private static MediaPlaybackCommandStatus ToCommandStatus(bool tryOk) =>
        tryOk
            ? MediaPlaybackCommandStatus.Succeeded
            : MediaPlaybackCommandStatus.Failed;

    private static MediaPlaybackCommandResult MapNoSession() =>
        new(MediaPlaybackCommandStatus.NoSession, "no focused SMTC session");

    private static MediaPlaybackCommandResult MapNotSupported(string command) =>
        new(MediaPlaybackCommandStatus.NotSupported, $"SMTC control does not enable {command}");

    private static MediaPlaybackCommandResult MapFailed(string message) =>
        new(MediaPlaybackCommandStatus.Failed, message);
}
