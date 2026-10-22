using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace CompUsage.Core;

public sealed record PartialRow(int Row, WorkInterval Interval)
{
    // Later sections of the same day, appended right after a completed last row.
    public IReadOnlyList<WorkInterval> Following { get; init; } = [];
    // False when the row is already complete: Interval is then the first appended section, not a completion.
    public bool CompletesRow { get; init; } = true;
    public IEnumerable<WorkInterval> Appended => CompletesRow ? Following : Following.Prepend(Interval);
}
public sealed record WorkbookPlan(string Path, string Fingerprint, DateOnly? LastDate,
    IReadOnlyList<WorkInterval> Rows, IReadOnlyList<PartialRow> Partials, int LastRow, bool Date1904, WorkbookFormats Formats);

// Update Details and its style/calculation metadata while preserving unrelated workbook parts.
public sealed class WorkbookService(TimeZoneInfo zone, CultureInfo? culture = null)
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static readonly TimeSpan PartialStartTolerance = TimeSpan.FromMinutes(3);

    public WorkbookPlan Preview(string path, IReadOnlyList<WorkReport> days)
    {
        ValidatePath(path);
        var bytes = File.ReadAllBytes(path);
        using var buffer = new MemoryStream(bytes);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var (sheet, _, date1904) = ReadSheet(zip);
        var strings = ReadStrings(zip);
        ValidateHeaders(sheet, strings);
        var dated = SheetRows(sheet).Select(r => (Row: r, Date: ReadDate(Cell(r, "A"), strings, date1904)))
            .Where(r => r.Date is not null).ToArray();
        var lastDate = dated.Select(r => r.Date).Max();
        var lastRow = SheetRows(sheet).Where(r => r.Elements(Ns + "c").Any(c => Column(c) is "A" or "B" or "C" or "F"
            && !string.IsNullOrWhiteSpace(Text(c, strings)))).Select(RowNumber).DefaultIfEmpty(1).Max();
        var formats = ReadFormats(sheet, ReadXml(zip, "xl/styles.xml"), lastRow);
        // Today's ongoing day is included too; its open section is written without an end (see Commit).
        var rows = days.Where(d => !d.Incomplete && (lastDate is null || d.Date > lastDate))
            .OrderBy(d => d.Date).SelectMany(d => d.Sections).ToArray();
        var partials = new List<PartialRow>();
        foreach (var (row, date) in dated.Where(r => r.Date == lastDate))
        {
            var sections = days.Where(d => d.Date == date && !d.Incomplete).SelectMany(d => d.Sections).OrderBy(s => s.From).ToArray();
            var from = ReadTime(Cell(row, "B"), strings);
            if (!string.IsNullOrWhiteSpace(Text(Cell(row, "C"), strings)))
            {
                // A complete last row, for example written during a break, continues with the day's later sections.
                if (RowNumber(row) != lastRow || from is null || ReadTime(Cell(row, "C"), strings) is not { } to || to <= from) continue;
                var later = sections.Where(s => TimeZoneInfo.ConvertTime(s.From, zone).TimeOfDay > to).ToArray();
                if (later.Length > 0) partials.Add(new(RowNumber(row), later[0]) { CompletesRow = false, Following = later[1..] });
                continue;
            }
            if (from is null) continue;
            // A manually typed start matches the closest recorded section start within three minutes, including
            // today's ongoing day. A still-open section has no end to complete yet.
            var candidate = sections.Select(s => (Section: s, Distance: Math.Abs((TimeZoneInfo.ConvertTime(s.From, zone).TimeOfDay - from.Value).Ticks)))
                .Where(c => c.Distance <= PartialStartTolerance.Ticks).OrderBy(c => c.Distance).Select(c => c.Section).FirstOrDefault();
            if (candidate is { Open: false })
            {
                var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(candidate.From, zone).DateTime);
                var existingFrom = WorkDayClock.At(localDate, TimeOnly.FromTimeSpan(from.Value), zone);
                // Later sections can follow only when the completed row is the sheet's last row.
                var following = RowNumber(row) == lastRow ? sections.Where(s => s.From > candidate.From).ToArray() : [];
                if (existingFrom < candidate.To) partials.Add(new(RowNumber(row), candidate with { From = existingFrom }) { Following = following });
            }
        }
        return new(Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)), lastDate, rows, partials, lastRow, date1904, formats);
    }

    public string Commit(WorkbookPlan plan, IReadOnlyCollection<int> selectedPartials)
    {
        ValidatePath(plan.Path);
        if (selectedPartials.Any(n => plan.Partials.All(p => p.Row != n))) throw new InvalidOperationException("Érvénytelen megkezdett sor.");
        if (plan.Rows.Count == 0 && selectedPartials.Count == 0) throw new InvalidOperationException("Nincs mentendő sor.");
        var directory = Path.GetDirectoryName(plan.Path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(plan.Path)}.{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(plan.Path)}.backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.xlsx");
        try
        {
            // Excel or another writer holding the file prevents this exclusive read/write access.
            using (var original = new FileStream(plan.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var memory = new MemoryStream();
                original.CopyTo(memory);
                var bytes = memory.ToArray();
                if (Convert.ToHexString(SHA256.HashData(bytes)) != plan.Fingerprint)
                    throw new IOException("Az Excel-fájl az előnézet óta megváltozott. Kérj új előnézetet.");
                File.WriteAllBytes(temp, bytes);
                using var zip = ZipFile.Open(temp, ZipArchiveMode.Update);
                var (sheet, sheetPath, _) = ReadSheet(zip);
                var data = sheet.Root!.Element(Ns + "sheetData")!;
                var styles = ReadXml(zip, "xl/styles.xml");
                var formats = new Dictionary<(int, string), int>();
                var template = SheetRows(sheet).LastOrDefault(r => RowNumber(r) <= plan.LastRow && RowNumber(r) > 1);
                var next = plan.LastRow;
                // A completed partial row's later sections belong to the last date, before any newer day.
                var appended = plan.Partials.Where(p => selectedPartials.Contains(p.Row)).SelectMany(p => p.Appended).Concat(plan.Rows);
                foreach (var interval in appended)
                {
                    var index = ++next;
                    var row = data.Elements(Ns + "row").FirstOrDefault(r => RowNumber(r) == index);
                    if (row is null)
                    {
                        row = new XElement(Ns + "row", new XAttribute("r", index));
                        var after = data.Elements(Ns + "row").FirstOrDefault(r => RowNumber(r) > index);
                        if (after is null) data.Add(row); else after.AddBeforeSelf(row);
                    }
                    Set(row, "A", index, ExcelDate(interval.Date, plan.Date1904), template);
                    Set(row, "B", index, LocalFraction(interval.From), template);
                    // Only Date/From/To are written: the durations and Details belong to the workbook's own formulas and text.
                    // A still-open section keeps To blank, so a later append completes it as a started row.
                    if (!interval.Open) Set(row, "C", index, LocalFraction(interval.To), template);
                    FormatRow(row, sheet, styles, formats, false, plan.Formats);
                }
                foreach (var partial in plan.Partials.Where(p => p.CompletesRow && selectedPartials.Contains(p.Row)))
                {
                    var row = data.Elements(Ns + "row").Single(r => RowNumber(r) == partial.Row);
                    Set(row, "C", partial.Row, LocalFraction(partial.Interval.To), template);
                    FormatRow(row, sheet, styles, formats, true, plan.Formats);
                }
                var dimension = sheet.Root.Element(Ns + "dimension");
                if (dimension is not null)
                {
                    var oldEnd = ((string?)dimension.Attribute("ref") ?? "A1:F1").Split(':').Last();
                    var oldLast = int.TryParse(new string(oldEnd.Where(char.IsDigit).ToArray()), out var n) ? n : 1;
                    if (next > oldLast) dimension.SetAttributeValue("ref", $"A1:{new string(oldEnd.Where(char.IsLetter).ToArray())}{next}");
                }
                WriteXml(zip, sheetPath, sheet);
                WriteXml(zip, "xl/styles.xml", styles);
                var workbook = ReadXml(zip, "xl/workbook.xml");
                var calc = workbook.Root!.Element(Ns + "calcPr");
                if (calc is null) { calc = new XElement(Ns + "calcPr"); workbook.Root.Add(calc); }
                calc.SetAttributeValue("fullCalcOnLoad", "1"); calc.SetAttributeValue("forceFullCalc", "1");
                WriteXml(zip, "xl/workbook.xml", workbook);
                // A stale calculation chain may refer to replaced duration cells. Excel rebuilds it on open.
                if (zip.GetEntry("xl/calcChain.xml") is not null)
                {
                    zip.GetEntry("xl/calcChain.xml")!.Delete();
                    var relationships = ReadXml(zip, "xl/_rels/workbook.xml.rels");
                    relationships.Root!.Elements().Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true).Remove();
                    WriteXml(zip, "xl/_rels/workbook.xml.rels", relationships);
                    var types = ReadXml(zip, "[Content_Types].xml");
                    types.Root!.Elements().Where(e => (string?)e.Attribute("PartName") == "/xl/calcChain.xml").Remove();
                    WriteXml(zip, "[Content_Types].xml", types);
                }
            }
            // Atomic replacement on Windows. Some SMB servers deny it even with full control; fall back to
            // a verified backup copy and a same-directory rename that replaces the original in one step.
            try { File.Replace(temp, plan.Path, backup); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { ReplaceByRename(temp, plan.Path, backup, plan.Fingerprint); }
            return backup;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void ReplaceByRename(string replacement, string path, string backup, string fingerprint)
    {
        var created = false;
        if (File.Exists(path))
        {
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var memory = new MemoryStream();
            original.CopyTo(memory);
            if (Convert.ToHexString(SHA256.HashData(memory.ToArray())) != fingerprint)
                throw new IOException("Az Excel-fájl az előnézet óta megváltozott. Kérj új előnézetet.");
            using var copy = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            memory.Position = 0; memory.CopyTo(copy); copy.Flush(true);
        }
        // A failed ReplaceFile can already have moved the original to the backup name; then only the rename remains.
        else if (!File.Exists(backup)) throw new FileNotFoundException("Az Excel-fájl nem található.", path);
        try { File.Move(replacement, path, true); }
        catch
        {
            // The original is untouched (for example Excel still holds it), so the copy made here is not a backup of anything.
            if (created) File.Delete(backup);
            throw;
        }
    }

    private double LocalFraction(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, zone).TimeOfDay.TotalDays;
    private static double ExcelDate(DateOnly date, bool mode1904) => date.ToDateTime(TimeOnly.MinValue).ToOADate() - (mode1904 ? 1462 : 0);
    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Teljes .xlsx fájlútvonal szükséges.");
    }
    private static (XDocument Sheet, string Path, bool Date1904) ReadSheet(ZipArchive zip)
    {
        var book = ReadXml(zip, "xl/workbook.xml");
        var sheet = book.Descendants(Ns + "sheet").FirstOrDefault(s => (string?)s.Attribute("name") == "Details")
            ?? throw new InvalidDataException("Nincs Details munkalap a fájlban.");
        var rels = ReadXml(zip, "xl/_rels/workbook.xml.rels");
        var target = rels.Root!.Elements().FirstOrDefault(r => (string?)r.Attribute("Id") == (string?)sheet.Attribute(Rel + "id"));
        if (target is null || (string?)target.Attribute("TargetMode") == "External") throw new InvalidDataException("Érvénytelen Details munkalap.");
        var raw = (string?)target.Attribute("Target") ?? "";
        var path = new Uri(new Uri("https://local/xl/workbook.xml"), raw).AbsolutePath.TrimStart('/');
        var date1904 = (string?)book.Root!.Element(Ns + "workbookPr")?.Attribute("date1904");
        return (ReadXml(zip, path), path, date1904 is "1" or "true");
    }
    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        using var stream = (zip.GetEntry(path) ?? throw new InvalidDataException($"Hiányzó Excel-rész: {path}")).Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }
    private static void WriteXml(ZipArchive zip, string path, XDocument doc)
    {
        zip.GetEntry(path)?.Delete();
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        doc.Save(stream, SaveOptions.DisableFormatting);
    }
    private static string[] ReadStrings(ZipArchive zip) => zip.GetEntry("xl/sharedStrings.xml") is null ? [] :
        ReadXml(zip, "xl/sharedStrings.xml").Descendants(Ns + "si").Select(e => string.Concat(e.Descendants(Ns + "t").Select(t => t.Value))).ToArray();
    private static IEnumerable<XElement> SheetRows(XDocument sheet) => sheet.Root!.Element(Ns + "sheetData")?.Elements(Ns + "row") ?? [];
    private static int RowNumber(XElement row) => (int?)row.Attribute("r") ?? 0;
    private static string Column(XElement cell) => new(((string?)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray());
    private static XElement? Cell(XElement row, string column) => row.Elements(Ns + "c").FirstOrDefault(c => Column(c) == column);
    private static string Text(XElement? cell, string[] strings)
    {
        if (cell is null) return "";
        var value = cell.Element(Ns + "v")?.Value ?? "";
        return (string?)cell.Attribute("t") switch
        {
            "s" => int.TryParse(value, out var index) && index >= 0 && index < strings.Length ? System.Xml.XmlConvert.DecodeName(strings[index]) : "",
            "inlineStr" => System.Xml.XmlConvert.DecodeName(string.Concat(cell.Descendants(Ns + "t").Select(t => t.Value))),
            _ => value
        };
    }
    private static DateOnly? ReadDate(XElement? cell, string[] strings, bool mode1904)
    {
        var text = Text(cell, strings);
        if (double.TryParse(text, NumberStyles.Float, Invariant, out var number) && number > 0 && number < 2957000)
            return DateOnly.FromDateTime(DateTime.FromOADate(number + (mode1904 ? 1462 : 0)));
        return DateOnly.TryParseExact(text.Trim(), ["yyyy.MM.dd", "yyyy.MM.dd.", "yyyy-MM-dd"], Invariant, DateTimeStyles.None, out var date) ? date : null;
    }
    private static TimeSpan? ReadTime(XElement? cell, string[] strings)
    {
        var text = Text(cell, strings);
        if (double.TryParse(text, NumberStyles.Float, Invariant, out var number) && number >= 0)
            return TimeSpan.FromSeconds(Math.Round((number % 1) * 86400, 6));
        return TimeSpan.TryParse(text, Invariant, out var time) ? time : null;
    }
    private static void ValidateHeaders(XDocument sheet, string[] strings)
    {
        var row = SheetRows(sheet).FirstOrDefault(r => RowNumber(r) == 1) ?? throw new InvalidDataException("Hiányzik a Details fejléc.");
        if (!Text(Cell(row, "A"), strings).Trim().Equals("Date", StringComparison.OrdinalIgnoreCase)
            || !Text(Cell(row, "B"), strings).StartsWith("From", StringComparison.OrdinalIgnoreCase)
            || !Text(Cell(row, "C"), strings).StartsWith("To", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A Details A–C fejlécének Date / From / To sorrendűnek kell lennie.");
    }
    private static void Set(XElement row, string column, int index, double value, XElement? template)
    {
        var cell = Cell(row, column);
        if (cell is null)
        {
            cell = new XElement(Ns + "c", new XAttribute("r", $"{column}{index}"));
            var style = Cell(template ?? new XElement("row"), column)?.Attribute("s");
            if (style is not null) cell.SetAttributeValue("s", style.Value);
            var next = row.Elements(Ns + "c").FirstOrDefault(c => Column(c).Length > column.Length
                || Column(c).Length == column.Length && string.CompareOrdinal(Column(c), column) > 0);
            if (next is null) row.Add(cell); else next.AddBeforeSelf(cell);
        }
        cell.SetAttributeValue("t", null);
        cell.Elements(Ns + "f").Remove(); cell.Elements(Ns + "is").Remove(); cell.Elements(Ns + "v").Remove();
        cell.Add(new XElement(Ns + "v", value.ToString("R", Invariant)));
    }

    private static void FormatRow(XElement row, XDocument sheet, XDocument styles, Dictionary<(int, string), int> cache, bool partial, WorkbookFormats fallback)
    {
        var root = styles.Root!;
        var numbers = root.Element(Ns + "numFmts");
        if (numbers is null) { numbers = new XElement(Ns + "numFmts", new XAttribute("count", 0)); root.AddFirst(numbers); }
        var xfs = root.Element(Ns + "cellXfs") ?? throw new InvalidDataException("Hiányoznak az Excel cellastílusai.");
        foreach (var (column, format) in new[] { ("A", fallback.Date), ("B", fallback.Start), ("C", fallback.End) })
        {
            if (partial && column is "A" or "B") continue;
            var cell = Cell(row, column);
            if (cell is null) continue;
            var oldId = EffectiveStyle(sheet, row, cell, column);
            // Keep existing cell/row/column number formats, including built-in regional formats.
            var existing = xfs.Elements(Ns + "xf").ElementAtOrDefault(oldId);
            if (((int?)existing?.Attribute("numFmtId") ?? 0) != 0)
            {
                if (cell.Attribute("s") is null && oldId != 0) cell.SetAttributeValue("s", oldId);
                continue;
            }
            if (!cache.TryGetValue((oldId, format), out var styleId))
            {
                var number = numbers.Elements(Ns + "numFmt").FirstOrDefault(n => (string?)n.Attribute("formatCode") == format);
                if (number is null)
                {
                    var id = Math.Max(163, numbers.Elements().Select(n => (int?)n.Attribute("numFmtId") ?? 0).DefaultIfEmpty(163).Max()) + 1;
                    number = new XElement(Ns + "numFmt", new XAttribute("numFmtId", id), new XAttribute("formatCode", format));
                    numbers.Add(number); numbers.SetAttributeValue("count", numbers.Elements().Count());
                }
                var original = xfs.Elements(Ns + "xf").ElementAtOrDefault(oldId) ?? xfs.Elements(Ns + "xf").First();
                var clone = new XElement(original);
                clone.SetAttributeValue("numFmtId", (string?)number.Attribute("numFmtId")); clone.SetAttributeValue("applyNumberFormat", "1");
                styleId = xfs.Elements().Count(); xfs.Add(clone); xfs.SetAttributeValue("count", styleId + 1); cache[(oldId, format)] = styleId;
            }
            cell.SetAttributeValue("s", styleId);
        }
    }

    private static int EffectiveStyle(XDocument sheet, XElement row, XElement? cell, string column)
    {
        if (cell?.Attribute("s") is { } cellStyle) return (int)cellStyle;
        if ((string?)row.Attribute("customFormat") is "1" or "true" && row.Attribute("s") is { } rowStyle) return (int)rowStyle;
        var index = column.Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
        return (int?)sheet.Root!.Element(Ns + "cols")?.Elements(Ns + "col")
            .LastOrDefault(c => (int?)c.Attribute("min") <= index && (int?)c.Attribute("max") >= index)?.Attribute("style") ?? 0;
    }

    private WorkbookFormats ReadFormats(XDocument sheet, XDocument styles, int lastRow)
    {
        var custom = styles.Root!.Element(Ns + "numFmts")?.Elements(Ns + "numFmt")
            .ToDictionary(n => (int)n.Attribute("numFmtId")!, n => (string)n.Attribute("formatCode")!) ?? [];
        var xfs = styles.Root.Element(Ns + "cellXfs")?.Elements(Ns + "xf").ToArray() ?? [];
        string Find(string column, string fallback)
        {
            var ordered = SheetRows(sheet).Where(r => RowNumber(r) > 1 && RowNumber(r) <= lastRow + 1)
                .OrderByDescending(RowNumber).ToArray();
            foreach (var row in ordered)
            {
                var style = EffectiveStyle(sheet, row, Cell(row, column), column);
                var id = style >= 0 && style < xfs.Length ? (int?)xfs[style].Attribute("numFmtId") ?? 0 : 0;
                if (id == 0) continue;
                if (custom.TryGetValue(id, out var format)) return format;
                return BuiltInFormat(id, fallback);
            }
            // Empty sheets can still carry the intended date/time format in their column styles.
            var empty = new XElement(Ns + "row");
            var colStyle = EffectiveStyle(sheet, empty, null, column);
            var colId = colStyle >= 0 && colStyle < xfs.Length ? (int?)xfs[colStyle].Attribute("numFmtId") ?? 0 : 0;
            return custom.GetValueOrDefault(colId) ?? BuiltInFormat(colId, fallback);
        }
        return new(Find("A", "yyyy/mm/dd"), Find("B", "h:mm"), Find("C", "h:mm"), Find("D", "[h]:mm"), Find("E", "0.00"));
    }

    private string BuiltInFormat(int id, string fallback) => id switch
    {
        14 => (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortDatePattern.Replace('M', 'm'),
        15 => "d-mmm-yy", 16 => "d-mmm", 17 => "mmm-yy", 18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM",
        20 => "h:mm", 21 => "h:mm:ss", 22 => (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortDatePattern.Replace('M', 'm') + " h:mm",
        45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0", 1 => "0", 2 => "0.00", _ => fallback
    };
}
