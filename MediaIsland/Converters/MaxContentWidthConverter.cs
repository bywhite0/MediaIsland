using System.Globalization;
using Avalonia.Data.Converters;

namespace MediaIsland.Converters
{
    /// <summary>
    /// 把「0 表示不限制」的配置值翻译成 Avalonia 的 MaxWidth 语义（无限制即 PositiveInfinity）。
    /// </summary>
    public class MaxContentWidthConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not double width || !double.IsFinite(width) || width <= 0)
            {
                return double.PositiveInfinity;
            }

            return width;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
