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
/// <param name="TargetMsCurrent">
/// 抖动缓冲目标深度的当前值，毫秒。外环在区间内挪它；贴住区间端点说明外环已把
/// 这个旋钮拧到头，是饱和判定的输入之一。未起播时为零。
/// </param>
/// <param name="PlayTimeErrorUs">
/// 外环误差，微秒，有符号。贴边且误差仍超预算才算饱和——贴边但误差已收进预算
/// 只是恰好收敛到边界。未起播时为零。
/// </param>
public readonly record struct AudioAlignmentFacts(
    bool HasStarted,
    double DeviceLatencyMs,
    double DeviceBufferMs,
    int MinTargetMs,
    int TargetMsCurrent,
    long PlayTimeErrorUs);
