namespace MediaIsland.Tests.Audio;

/// <summary>
/// 扫一块 s16le 交错 PCM，返回峰值绝对振幅与是否含非零样本。
///
/// 全仓唯一实现处。此前同形逻辑散在 WasapiLoopbackManualCheck 的 StatsSink 与
/// MediaLinkAudioEndToEndManualCheck 的 TryDecodeAndScan 两处，第三个消费者
/// （真机对齐判据的已播出帧扫描）出现时抽出——两份各自为真的副本就是下一次
/// 漂移的起点，与 AudioAlignmentThresholds 的唯一性理由相同。
///
/// short.MinValue 特判：Math.Abs(short.MinValue) 会溢出抛出，而 -32768 的振幅
/// 语义上就是满量程。
/// </summary>
internal static class PcmScan
{
    public static (int Peak, bool HasNonZero) Scan(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        var any = false;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var amplitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (amplitude > peak)
            {
                peak = amplitude;
            }

            if (sample != 0)
            {
                any = true;
            }
        }

        return (peak, any);
    }
}
