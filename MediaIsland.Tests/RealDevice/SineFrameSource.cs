using System.Buffers.Binary;
using System.Diagnostics;
using MediaIsland.Services.Audio;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 合成单频帧源。真机验证集的发送侧。
///
/// 为什么注入而不采集：采集与播放取的是同一个默认输出端点
/// （capture 与 render 都用 GetDefaultAudioEndpoint(eRender, eConsole)），
/// 同时工作就是啸叫——那正是音源仲裁互斥要防的事，不是设计洁癖。
/// 注入之后播放侧仍然全真：真的装饰器、真的 native 渲染线程、真的端点。
///
/// 相位按全局样本序号推算，故跨帧连续。这不是讲究：若每帧从零相位重算，
/// 帧接缝处会有一个合法的跳变，真机的爆音判据就永远红。
/// </summary>
internal sealed class SineFrameSource : IAudioFrameSource, IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>每块时长。与 native 采集侧同量级（跟随 WASAPI 事件，约 20ms）。</summary>
    public const int FrameMs = 20;

    private const int FramesPerBlock = SampleRate * FrameMs / 1_000;

    private readonly double _frequencyHz;
    private readonly double _amplitude;
    private readonly CancellationTokenSource _cts = new();

    private Task? _worker;
    private long _framesEmitted;

    /// <param name="frequencyHz">注入频率。</param>
    /// <param name="amplitude">振幅，满量程的比例。半量程留足余量给端点混音的裕度。</param>
    public SineFrameSource(double frequencyHz, double amplitude)
    {
        _frequencyHz = frequencyHz;
        _amplitude = amplitude * short.MaxValue;
    }

    public bool IsAvailable => true;

    public string? FailureReason => null;

    public event Action<AudioFrame>? FrameAvailable;

    /// <summary>已发出的块数。速率判据用它。</summary>
    public long TotalFramesEmitted => Volatile.Read(ref _framesEmitted);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker ??= Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
                // 正常的收尾路径。
            }

            _worker = null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// 按墙钟追赶，而不是每轮固定发一块。
    ///
    /// Windows 的定时器分辨率约 15.6ms，固定节奏会让实际速率偏离 48000Hz 达两成以上，
    /// 而缓冲占用的漂移判据会把「注入太慢」读成控制律失灵——两者的排查方向相反。
    /// 追赶让长期速率由 Stopwatch 决定，与唤醒抖动无关。
    /// </summary>
    private async Task PumpAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        long emitted = 0;

        while (!token.IsCancellationRequested)
        {
            var due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            while (emitted + FramesPerBlock <= due)
            {
                Emit(emitted);
                emitted += FramesPerBlock;
            }

            await Task.Delay(5, token);
        }
    }

    private void Emit(long startFrame)
    {
        var pcm = new byte[FramesPerBlock * Channels * sizeof(short)];
        for (var i = 0; i < FramesPerBlock; i++)
        {
            var value = (short)Math.Round(
                _amplitude * Math.Sin(2 * Math.PI * _frequencyHz * (startFrame + i) / SampleRate));
            var offset = i * Channels * sizeof(short);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset, sizeof(short)), value);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(offset + sizeof(short), sizeof(short)), value);
        }

        Interlocked.Increment(ref _framesEmitted);
        FrameAvailable?.Invoke(new AudioFrame(
            pcm, Stopwatch.GetTimestamp(), SampleRate, Channels, IsSilent: false));
    }
}
