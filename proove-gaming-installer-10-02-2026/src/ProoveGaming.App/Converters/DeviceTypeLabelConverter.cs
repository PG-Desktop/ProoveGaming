using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public class DeviceTypeLabelConverter : IValueConverter
{
    public static readonly DeviceTypeLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return value;

        var key = s.Trim() switch
        {
            "Wired Mouse" => "DeviceType_WiredMouse",
            "Wireless Mouse" => "DeviceType_WirelessMouse",
            "Wired Keyboard" => "DeviceType_WiredKeyboard",
            "Wireless Keyboard" => "DeviceType_WirelessKeyboard",
            "Wireless Headset" => "DeviceType_WirelessHeadset",
            _ => null
        };

        return key is null ? s : LocalizationManager.GetString(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
