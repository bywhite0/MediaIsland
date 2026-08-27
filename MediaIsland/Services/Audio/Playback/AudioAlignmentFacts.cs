namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 对齐可行性判定要吃的本机事实，一次读取成组取得。
///
/// 判定同时要设备延迟、缓冲容量与目标深度下界；分开各读一次的话，读到的可能是
/// 起播前后两个世界各一半——半份事实判出的结论两种世界都不对。
/// </summary>
/// <param name="HasStarted">本句柄起播过。为假时两项设备量恒为零，可行性无从判。</param>
/// <param name="DeviceLatencyMs">设备取走数据之后到出声那段的估计（引擎周期加流延迟）。</param>
/// <param name="DeviceBufferMs">
/// 端点缓冲容量的时长。渲染循环每轮把可写帧全写满，一个采样最坏要等整整一个容量
/// 才被取走，故它计入本机最小可达延迟的下限。
/// </param>
/// <param name="MinTargetMs">抖动缓冲目标深度的下界，取自渲染实现的常量。</param>
public readonly record struct AudioAlignmentFacts(
    bool HasStarted,
    double DeviceLatencyMs,
    double DeviceBufferMs,
    int MinTargetMs);
