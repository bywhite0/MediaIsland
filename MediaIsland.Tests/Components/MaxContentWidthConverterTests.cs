using System.Globalization;
using MediaIsland.Converters;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 转换器负责把「0 表示不限制」翻译成 Avalonia 的 MaxWidth 语义。
/// 这里一旦返回 0，组件会被压成零宽，所以边界必须锁死。
/// </summary>
public class MaxContentWidthConverterTests
{
    private static object? Convert(object? value) =>
        new MaxContentWidthConverter().Convert(value, typeof(double), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Zero_MeansUnlimited()
    {
        Assert.Equal(double.PositiveInfinity, Convert(0d));
    }

    [Fact]
    public void NegativeValue_MeansUnlimited()
    {
        Assert.Equal(double.PositiveInfinity, Convert(-120d));
    }

    [Theory]
    [InlineData(40d)]
    [InlineData(240d)]
    [InlineData(1000d)]
    public void PositiveValue_PassesThrough(double value)
    {
        Assert.Equal(value, Convert(value));
    }

    [Fact]
    public void NonFiniteValue_MeansUnlimited()
    {
        Assert.Equal(double.PositiveInfinity, Convert(double.NaN));
        Assert.Equal(double.PositiveInfinity, Convert(double.PositiveInfinity));
    }

    [Fact]
    public void NullOrUnexpectedType_MeansUnlimited()
    {
        Assert.Equal(double.PositiveInfinity, Convert(null));
        Assert.Equal(double.PositiveInfinity, Convert("240"));
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        var result = new MaxContentWidthConverter()
            .ConvertBack(240d, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }
}
