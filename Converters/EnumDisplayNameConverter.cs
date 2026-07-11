using Avalonia.Data.Converters;
using LectureSmith.Models;
using System;
using System.Globalization;

namespace LectureSmith.Converters;

public class EnumDisplayNameConverter<T> : IValueConverter where T : Enum
{
    private readonly Func<T, string> _displayFunc;

    public EnumDisplayNameConverter(Func<T, string> displayFunc)
    {
        _displayFunc = displayFunc;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is T enumValue)
            return _displayFunc(enumValue);
        return value?.ToString() ?? "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
