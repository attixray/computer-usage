using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelNumberFormat;

namespace CompUsage.Core;

public sealed record WorkbookFormats(string Date = "yyyy/mm/dd", string Start = "h:mm", string End = "h:mm",
    string Duration = "[h]:mm", string Hours = "0.00");

public sealed class ExcelDisplay(WorkbookFormats formats, CultureInfo culture, TimeZoneInfo zone)
{
    public string Date(DateOnly date) => Format(date.ToDateTime(TimeOnly.MinValue), formats.Date);
    public string Time(DateTimeOffset value, bool end = false) => Format(TimeZoneInfo.ConvertTime(value, zone).TimeOfDay.TotalDays, end ? formats.End : formats.Start);
    public string Duration(TimeSpan value) => Format(value, formats.Duration);
    public string CopyRow(WorkInterval row) => string.Join('\t', Date(row.Date),
        Presentation.Time(row.From, zone, true), Presentation.Time(row.To, zone, true));
    public static string ExactDuration(TimeSpan value) => string.Create(CultureInfo.InvariantCulture,
        $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}");
    public string[] ExcelCells(WorkInterval row) => [Date(row.Date), Presentation.Time(row.From, zone, true),
        Presentation.Time(row.To, zone, true), ExactDuration(row.Duration), row.Duration.TotalHours.ToString("G15", culture), row.Comment];
    public string CopyExcelRow(WorkInterval row) => string.Join('\t', ExcelCells(row).Select(TsvCell));
    public static string TsvCell(string value) => value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private string Format(object value, string code)
    {
        var selectedCulture = culture;
        var locale = Regex.Match(code, @"\[\$[^\]]*-([0-9a-fA-F]+)\]");
        if (locale.Success && int.TryParse(locale.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid))
        {
            try { if (lcid is > 0 and < 0xF000) selectedCulture = CultureInfo.GetCultureInfo(lcid); }
            catch (CultureNotFoundException) { }
        }
        var normalized = NormalizeSeparators(code, selectedCulture);
        return new NumberFormat(normalized).Format(value, selectedCulture);
    }

    // Excel's unescaped date/time separators follow Windows regional settings. Quoted and escaped
    // separators remain literal. Preserve the slash in AM/PM and A/P tokens.
    private static string NormalizeSeparators(string code, CultureInfo culture)
    {
        var result = new StringBuilder();
        var quoted = false; var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '\\' && i + 1 < code.Length) { result.Append(c).Append(code[++i]); continue; }
            if (c == '"') quoted = !quoted;
            if (!quoted && c == '[') bracket = true;
            if (!quoted && c == ']') bracket = false;
            var ampm = !quoted && !bracket ? (code.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase) ? 5
                : code.AsSpan(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase) ? 3 : 0) : 0;
            if (ampm > 0) { result.Append(code.AsSpan(i, ampm)); i += ampm - 1; continue; }
            if (!quoted && !bracket && c is '/' or ':')
            {
                // Excel uses the regional separator without the spacing embedded in some ICU defaults.
                var separator = (c == '/' ? culture.DateTimeFormat.DateSeparator : culture.DateTimeFormat.TimeSeparator).Trim();
                result.Append('"').Append(separator.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
            }
            else result.Append(c);
        }
        return result.ToString();
    }
}
