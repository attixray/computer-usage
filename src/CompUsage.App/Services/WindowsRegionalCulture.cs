using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CompUsage.App.Services;

public static class WindowsRegionalCulture
{
    public static CultureInfo Create()
    {
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        // Read explicit Windows overrides: ICU defaults can disagree with Excel about separators.
        if (Read(0x1D) is { } date) culture.DateTimeFormat.DateSeparator = date;
        if (Read(0x1E) is { } time) culture.DateTimeFormat.TimeSeparator = time;
        if (Read(0x1F) is { } pattern) culture.DateTimeFormat.ShortDatePattern = pattern;
        if (Read(0x28) is { } am) culture.DateTimeFormat.AMDesignator = am;
        if (Read(0x29) is { } pm) culture.DateTimeFormat.PMDesignator = pm;
        return culture;
    }

    private static string? Read(uint kind)
    {
        var buffer = new StringBuilder(256);
        return GetLocaleInfoEx(null, kind, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLocaleInfoEx(string? localeName, uint localeType, StringBuilder data, int size);
}
