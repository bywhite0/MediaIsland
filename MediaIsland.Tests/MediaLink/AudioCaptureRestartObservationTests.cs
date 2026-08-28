using MediaIsland.Services.MediaLink;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 采集重启是 fire-and-forget（播放重启不等采集），faulted task 没人 await 就会带着
/// 未观察异常进终结器。RestartCaptureAsync 内部已吞常规异常，这里钉的是「内部吞漏了」
/// 时的兜底：Warning 恰一条、异常就地观察不再上抛。判据直接驱动 internal static
/// 帮手——hub 是具体类且随服务端私有重建，经公开面无缝注入 faulted 的重启。
/// </summary>
public class AudioCaptureRestartObservationTests
{
    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }

    [Fact]
    public async Task AFaultedRestart_LogsExactlyOneWarning_AndNeverRethrows()
    {
        // 观察任务成功完成即证明异常被 await 到并吞掉——终结器上不再有未观察异常。
        var logger = new CountingLogger();
        var faulted = Task.FromException(new InvalidOperationException("内部吞漏"));

        var observed = MediaLinkHostedService.ObserveCaptureRestartFaults(faulted, logger);
        await observed;

        Assert.True(observed.IsCompletedSuccessfully);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public async Task ASuccessfulRestart_LogsNothing()
    {
        // 兜底只对 faulted 出声：常规成功路径零日志，不给每次设备变化添噪声。
        var logger = new CountingLogger();

        await MediaLinkHostedService.ObserveCaptureRestartFaults(Task.CompletedTask, logger);

        Assert.Equal(0, logger.Warnings);
    }
}
