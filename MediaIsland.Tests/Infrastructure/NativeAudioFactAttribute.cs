using MediaIsland.Services.Audio.Playback.Native;
using Xunit;

namespace MediaIsland.Tests.Infrastructure;

/// <summary>
/// 需要 MediaIsland.Audio 原生库在场的测试——经 FFI 读 native 常量的判据一族。
/// 库缺失或 ABI 失配时清晰跳过并写明原因。此前判据类在静态初始化器里直接
/// P/Invoke，缺库环境整类死于 TypeInitializationException，读结果的人看到的是
/// 类型初始化失败的堆栈，而不是「缺什么、怎么补」。
///
/// 与 RealAudioFact 不同层：那个门的是「有没有真实端点在放声」，由人设环境变量
/// 声明；这个门的是「构建产物在不在」，进程自己就能探，不需要人声明。
///
/// 判定与读取分开成纯函数，理由同 ExternalConditionGate：attribute 构造读的是
/// 进程级探测结果，测试里改它会与并行跑的其他测试竞态；纯函数那一半不碰进程状态。
/// </summary>
public sealed class NativeAudioFactAttribute : FactAttribute
{
    public NativeAudioFactAttribute()
    {
        Skip = SkipReasonFor(AudioRenderNative.IsAvailable, AudioRenderNative.FailureReason);
    }

    /// <summary>
    /// 判是否该跳过。可用返回 null 即真跑；不可用返回带探测原因的跳过文案，
    /// 该文案会显示在测试结果里。
    /// </summary>
    internal static string? SkipReasonFor(bool isAvailable, string? failureReason)
    {
        if (isAvailable)
        {
            return null;
        }

        return $"需 MediaIsland.Audio 原生库（{failureReason ?? "原因未知"}）。dotnet build 会一并产出它";
    }
}
