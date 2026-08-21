using System.Diagnostics;
using MediaIsland.Services.Audio;
using Xunit;

namespace MediaIsland.Tests.Audio;

public class MonotonicClockTests
{
    [Fact]
    public void TickUnit_IsTheSameAsTimeSpanTicks()
    {
        // 钉的是「与 TimeSpan.Ticks 同刻度」这条声明，不是某个字面量。
        // 写成 Assert.Equal(10_000_000, MonotonicClock.TicksPerSecond100Ns) 是同义反复：
        // 改常量的同时改判据即可全绿。与 TimeSpan 对照才让那个声明可被证伪。
        Assert.Equal(TimeSpan.TicksPerSecond, MonotonicClock.TicksPerSecond100Ns);
        Assert.Equal(TimeSpan.TicksPerMillisecond, MonotonicClock.TicksPerMs100Ns);
    }

    [Fact]
    public void Now_IsMonotonic()
    {
        var previous = MonotonicClock.Now100Ns();
        for (var i = 0; i < 1_000; i++)
        {
            var current = MonotonicClock.Now100Ns();
            Assert.True(current >= previous, $"第 {i} 次读到 {current}，小于上一次 {previous}");
            previous = current;
        }
    }

    [Fact]
    public void Now_IsScaledTo100Nanoseconds()
    {
        // 这条判据挡的是「把 Stopwatch 的原始刻度当成 100ns 用」——那种错是一个
        // 常数因子，症状为时间轴整体缩放，而不是崩溃或明显的乱序。
        //
        // 忙等而非 Thread.Sleep：Sleep 的实际时长受调度器影响，下界不可靠，
        // 而这里要比的是两个时钟的比例，不是等了多久。
        var stopwatch = Stopwatch.StartNew();
        var start = MonotonicClock.Now100Ns();
        while (stopwatch.ElapsedMilliseconds < 20)
        {
        }

        var measured = MonotonicClock.Now100Ns() - start;
        stopwatch.Stop();

        // 容差取两倍，只为挡住数量级错误；判据的目的不是标定精度。
        // 局限要写明：本机 Stopwatch.Frequency 恰为 10^7 时，「假定刻度相同」这个
        // 写法与正确写法结果一致，故那种变异在本机上无法被这条判据区分。
        // 它挡得住的是漏乘、漏除与单位记错。
        Assert.InRange(measured, stopwatch.Elapsed.Ticks / 2, stopwatch.Elapsed.Ticks * 2);
    }
}
