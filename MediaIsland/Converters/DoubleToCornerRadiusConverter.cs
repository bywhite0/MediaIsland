using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MediaIsland.Converters
{
    public class DoubleToCornerRadiusConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var v = (double)(value ?? throw new ArgumentNullException(nameof(value)));
            return new CornerRadius(v);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return null;
        }
    }
}