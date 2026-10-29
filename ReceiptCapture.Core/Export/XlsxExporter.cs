using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReceiptCapture.Core.Meetings;

namespace ReceiptCapture.Core.Export;

/// <summary>
/// Writes the receipt list as a real Excel workbook (.xlsx), with no extra software needed. The workbook is built from
/// scratch from the receipts, so it holds only the columns below: no author name, no hidden sheets, no comments.
/// Dates and amounts are real dates and numbers (they sort and add up), and text is stored as text, never as a formula,
/// so something odd read off a receipt can't run when the file is opened.
/// </summary>
public static class XlsxExporter
{
    private const int MaxCellLength = 32000;   // Excel's own limit is 32,767 characters per cell

    private static readonly string[] Headers = { "Date", "Vendor", "Category", "Tax", "Total", "Notes", "File" };
    private static readonly int[] Widths = { 12, 30, 18, 10, 12, 40, 50 };

    // Cell styles (see Styles below): 0 plain, 1 header, 2 date, 3 money
    private const int Plain = 0, Header = 1, DateStyle = 2, Money = 3;

    private static readonly DateTime ExcelEpoch = new(1899, 12, 30);

    public static void Export(IEnumerable<Receipt> receipts, string path)
    {
        var bytes = Build(receipts);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public static byte[] Build(IEnumerable<Receipt> receipts)
    {
        var rows = receipts.ToList();

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypes);
            AddEntry(zip, "_rels/.rels", RootRelationships);
            AddEntry(zip, "xl/workbook.xml", Workbook);
            AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
            AddEntry(zip, "xl/styles.xml", Styles);
            AddEntry(zip, "xl/worksheets/sheet1.xml", BuildSheet(rows));
        }
        return memory.ToArray();
    }

    private static string BuildSheet(List<Receipt> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        // Keep the header row in view while scrolling
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");

        sb.Append("<cols>");
        for (int i = 0; i < Widths.Length; i++)
            sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Widths[i]}\" customWidth=\"1\"/>");
        sb.Append("</cols>");

        sb.Append("<sheetData>");
        sb.Append("<row r=\"1\">");
        for (int c = 0; c < Headers.Length; c++) sb.Append(Text(c, 1, Headers[c], Header));
        sb.Append("</row>");

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int n = i + 2;
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{n}\">");
            sb.Append(Number(0, n, (r.Date.Date - ExcelEpoch).TotalDays, DateStyle));
            sb.Append(Text(1, n, r.Vendor, Plain));
            sb.Append(Text(2, n, r.Category, Plain));
            if (r.Tax is { } tax) sb.Append(Number(3, n, (double)tax, Money));
            sb.Append(Number(4, n, (double)r.Total, Money));
            sb.Append(Text(5, n, r.Notes, Plain));
            sb.Append(Text(6, n, r.FilePath ?? "", Plain));
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        // Filter arrows on the header row
        sb.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:G{Math.Max(1, rows.Count + 1)}\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Cells
    // ------------------------------------------------------------------
    private static string Text(int column, int row, string value, int style)
    {
        if (string.IsNullOrEmpty(value)) return "";                       // an empty cell is just left out
        if (value.Length > MaxCellLength) value = value[..MaxCellLength];

        // Inline strings are always text: Excel never evaluates them, even if they start with = + - or @.
        return string.Create(CultureInfo.InvariantCulture,
            $"<c r=\"{Ref(column, row)}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{TranscriptDocx.Escape(value)}</t></is></c>");
    }

    private static string Number(int column, int row, double value, int style)
        => string.Create(CultureInfo.InvariantCulture, $"<c r=\"{Ref(column, row)}\" s=\"{style}\"><v>{value.ToString("0.############", CultureInfo.InvariantCulture)}</v></c>");

    private static string Ref(int column, int row) => $"{(char)('A' + column)}{row}";

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    // ------------------------------------------------------------------
    // The fixed parts of the file
    // ------------------------------------------------------------------
    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string Workbook =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Receipts\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    // 0 plain, 1 bold header on light grey, 2 date (yyyy-mm-dd), 3 money (#,##0.00)
    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\"/></numFmts>" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE7E9EE\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"4\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
