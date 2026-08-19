using Xunit;

namespace MediaIsland.Tests.Infrastructure;

/// <summary>
/// 需要本机 HTTP 服务在跑的测试。默认跳过，设 MEDIAISLAND_LIVE_LYRICS=1 后启用。
///
/// 与 <see cref="RealAudioFactAttribute"/> 分成两个变量而不合并成一个带参数的：
/// 两者门的是不同种类的外部条件，一个是音频端点、一个是 HTTP 服务。
/// 合成一个变量会让「我只想跑音频验证」连带拉起歌词服务的要求。
/// </summary>
public sealed class LiveServiceFactAttribute : FactAttribute
{
    public const string Variable = "MEDIAISLAND_LIVE_LYRICS";

    public LiveServiceFactAttribute()
    {
        Skip = ExternalConditionGate.SkipReasonFor(
            Variable,
            Environment.GetEnvironmentVariable(Variable),
            "本机歌词 API 服务在跑");
    }
}
