using MediaIsland.Services.Audio;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 可计数的假采集源。与 AudioFrameHubTests 里的私有版同构，提成共享类是因为
/// LocalAudioCapture 与服务端解耦用例都要断言「源被启停了几次」。
/// </summary>
internal sealed class FakeAudioFrameSource : IAudioFrameSource, IDisposable
{
    public bool IsAvailable { get; init; } = true;

    public string? FailureReason { get; init; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public event Action<AudioFrame>? FrameAvailable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public Task EmitAsync(AudioFrame frame)
    {
        FrameAvailable?.Invoke(frame);
        return Task.CompletedTask;
    }

    public void Dispose() => IsDisposed = true;
}
