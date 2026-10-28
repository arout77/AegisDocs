// A tiny dependency-free test runner:  dotnet run --project ReceiptCapture.Tests
using ReceiptCapture.Core;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Core.Documents;
using ReceiptCapture.Core.Export;
using ReceiptCapture.Core.HiddenContent;
using ReceiptCapture.Core.Meetings;
using ReceiptCapture.Core.Parsing;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Core.Updates;
using ReceiptCapture.Tests;

int passed = 0, failed = 0;
var today = new DateTime(2025, 12, 31);

void Check<T>(string name, T actual, T expected)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) { passed++; return; }
    failed++;
    Console.WriteLine($"FAIL  {name}\n      expected: {expected}\n      actual:   {actual}");
}

// ---------- 1. Grocery receipt ----------
var grocery = ReceiptParser.Parse("""
    FRESH MART
    123 Main Street
    Springfield, IL 62704
    Tel: 555-123-4567
    03/14/2025 02:31 PM
    Milk 2% 1 gal        3.49
    Bread                2.99
    SUBTOTAL             6.48
    TAX 8.25%            0.53
    TOTAL                7.01
    VISA ****1234        7.01
    """, today);
Check("grocery vendor", grocery.Vendor, "FRESH MART");
Check("grocery date", grocery.Date, new DateTime(2025, 3, 14));
Check("grocery total", grocery.Total, 7.01m);
Check("grocery tax", grocery.Tax, 0.53m);

// ---------- 2. Invoice with due date and balance due ----------
var invoice = ReceiptParser.Parse("""
    ACME Consulting LLC
    INVOICE
    Invoice #: 10234
    Invoice Date: January 15, 2025
    Due Date: February 14, 2025
    Description        Qty   Rate     Amount
    Website design      10  $150.00  $1,500.00
    Hosting setup        1  $200.00    $200.00
    Subtotal                         $1,700.00
    Tax (8%)                           $136.00
    Total                            $1,836.00
    Balance Due                      $1,836.00
    """, today);
Check("invoice vendor", invoice.Vendor, "ACME Consulting LLC");
Check("invoice date (not the due date)", invoice.Date, new DateTime(2025, 1, 15));
Check("invoice total", invoice.Total, 1836.00m);
Check("invoice tax", invoice.Tax, 136.00m);
Check("invoice category (vendor wins)", invoice.Category, "Professional Services");

// ---------- 3. Cafe: total on the line after the label ----------
var cafe = ReceiptParser.Parse("""
    Blue Door Cafe
    Order #45
    2025-06-02 08:15
    Latte 4.50
    Muffin 3.25
    Total
    $7.75
    """, today);
Check("cafe vendor", cafe.Vendor, "Blue Door Cafe");
Check("cafe date", cafe.Date, new DateTime(2025, 6, 2));
Check("cafe total on next line", cafe.Total, 7.75m);
Check("cafe category", cafe.Category, "Meals & Entertainment");

// ---------- 4. Gas station ----------
var gas = ReceiptParser.Parse("SHELL\n456 Highway 9\nUnleaded 10.2 gal\nTotal Sale  $45.67\n", today);
Check("gas total", gas.Total, 45.67m);
Check("gas category", gas.Category, "Fuel");

// ---------- 5. Date formats ----------
DateTime? D(string s) => ReceiptParser.Parse("Store\nDate: " + s + "\nTotal 1.00", today).Date;
Check("date 14 Mar 2025", D("14 Mar 2025"), new DateTime(2025, 3, 14));
Check("date Mar 14, 2025", D("Mar 14, 2025"), new DateTime(2025, 3, 14));
Check("date 14/03/2025 (day first because 14 > 12)", D("14/03/2025"), new DateTime(2025, 3, 14));
Check("date 3/4/25 (month first)", D("3/4/25"), new DateTime(2025, 3, 4));
Check("date in the future is rejected", D("01/01/2031"), (DateTime?)null);

// ---------- 6. Fallbacks ----------
Check("fallback to largest amount", ReceiptParser.Parse("Corner Store\nitem 1.00\nitem 12.99\nthanks", today).Total, 12.99m);
Check("no amounts -> null", ReceiptParser.Parse("Corner Store\nthanks for visiting", today).Total, (decimal?)null);
Check("empty text -> nulls", ReceiptParser.Parse("", today).Vendor, (string?)null);
Check("thousands separator", ReceiptParser.AmountsIn("Total $12,345.67")[0], 12345.67m);
Check("percent is not an amount", ReceiptParser.AmountsIn("Tax 8.25%").Count, 0);

// ---------- 7. CSV ----------
var csv = CsvExporter.Build(new[]
{
    new Receipt { Vendor = "=HYPERLINK(\"x\")", Date = new DateTime(2025, 1, 2), Total = 5m, Category = "A, B", Notes = "line1\nline2" },
});
Check("csv header", csv.StartsWith("Date,Vendor,Category,Tax,Total,Notes,File\r\n"), true);
Check("csv formula defused", csv.Contains("\"'=HYPERLINK(\"\"x\"\")\""), true);
Check("csv quoting", csv.Contains("\"A, B\""), true);
Check("csv invariant decimals", csv.Contains(",5.00,"), true);

// ---------- 8. JSON store ----------
var dir = Path.Combine(Path.GetTempPath(), "rc-test-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(dir, "receipts.json");
var store = new JsonReceiptStore(path);
Check("missing file loads empty", store.Load().Count, 0);

var original = new Receipt { Vendor = "Shop", Total = 9.99m, Tax = 0.80m, Date = new DateTime(2025, 5, 5), Category = "Fuel", NeedsReview = true, FileHash = "abc" };
store.Save(new[] { original });
var loaded = store.Load();
Check("round trip count", loaded.Count, 1);
Check("round trip vendor", loaded[0].Vendor, "Shop");
Check("round trip total", loaded[0].Total, 9.99m);
Check("round trip tax", loaded[0].Tax, 0.80m);
Check("round trip id", loaded[0].Id, original.Id);
Check("round trip needs review", loaded[0].NeedsReview, true);

File.WriteAllText(path, "{ this is not json");
Check("corrupt file loads empty", store.Load().Count, 0);
Check("corrupt file is kept as a backup", Directory.GetFiles(dir, "receipts.json.corrupt-*").Length, 1);
Directory.Delete(dir, true);

// ---------- 9. Receipt model ----------
var a = new Receipt { Vendor = "X", Total = 3m, Category = "  " };
Check("blank category becomes Uncategorized", a.Category, Categories.Uncategorized);
var b = a.Clone();
b.Vendor = "Y";
Check("clone is independent", a.Vendor, "X");
Check("clone keeps id", b.Id, a.Id);

// ---------- 10. Bates numbering ----------
Check("bates basic", new BatesFormatter("ACME-", "", 6).Label(1), "ACME-000001");
Check("bates suffix", new BatesFormatter("", "-CONF", 4).Label(42), "0042-CONF");
Check("bates grows past digit width", new BatesFormatter("X", "", 3).Label(12345), "X12345");
Check("bates default digits", new BatesFormatter().Label(7), "000007");
Check("bates negative throws", ThrowsOutOfRange(() => new BatesFormatter().Label(-1)), true);

static bool ThrowsOutOfRange(Action a)
{
    try { a(); return false; } catch (ArgumentOutOfRangeException) { return true; }
}

// ---------- 11. Page ranges ----------
string Show(List<List<int>> g) => string.Join("|", g.Select(x => string.Join(",", x)));

Check("ranges parse ok", PageRanges.TryParseGroups("1-3, 5, 8-", 10, out var g1, out var e1), true);
Check("ranges groups", Show(g1), "1,2,3|5|8,9,10");
Check("ranges single page", PageRanges.TryParseGroups(" 4 ", 4, out var g2, out _), true);
Check("ranges single page value", Show(g2), "4");
Check("ranges reject zero", PageRanges.TryParseGroups("0", 10, out _, out var e2), false);
Check("ranges reject past end", PageRanges.TryParseGroups("11", 10, out _, out var e3), false);
Check("ranges past-end message", e3, "Page 11 doesn't exist (the document has 10 pages).");
Check("ranges reject backwards", PageRanges.TryParseGroups("5-3", 10, out _, out var e4), false);
Check("ranges backwards message", e4.Contains("backwards"), true);
Check("ranges reject text", PageRanges.TryParseGroups("abc", 10, out _, out _), false);
Check("ranges reject empty", PageRanges.TryParseGroups("  ", 10, out _, out _), false);
Check("ranges reject negative-looking", PageRanges.TryParseGroups("-3", 10, out _, out _), false);
Check("ranges every 3 of 7", Show(PageRanges.EveryN(3, 7)), "1,2,3|4,5,6|7");
Check("ranges every 5 of 5", Show(PageRanges.EveryN(5, 5)), "1,2,3,4,5");

// ---------- 12. Rename planner ----------
var when = new DateTime(2025, 3, 4);
var plan = RenamePlanner.Plan(new[] { Path.Combine("in", "scan1.pdf"), Path.Combine("in", "scan2.PDF") }, "{n:000}_{name}", 1, when);
Check("rename numbered 1", plan[0].NewName, "001_scan1.pdf");
Check("rename numbered 2 keeps extension case", plan[1].NewName, "002_scan2.PDF");
Check("rename start offset", RenamePlanner.Apply("{n}", "a.pdf", 41, when), "41.pdf");
Check("rename date default", RenamePlanner.Apply("{date}_{name}", "a.pdf", 1, when), "2025-03-04_a.pdf");
Check("rename date format", RenamePlanner.Apply("{date:yyyyMMdd}", "a.pdf", 1, when), "20250304.pdf");
Check("rename bad chars", RenamePlanner.Apply("a:b?c", "x.pdf", 1, when), "a_b_c.pdf");
Check("rename empty pattern", RenamePlanner.Apply("", "x.pdf", 1, when), "file.pdf");
Check("rename trailing dot trimmed", RenamePlanner.Apply("memo. ", "x.pdf", 1, when), "memo.pdf");
var dupes = RenamePlanner.Plan(new[] { "a.pdf", "b.pdf", "c.pdf" }, "same", 1, when);
Check("rename duplicate 1", dupes[0].NewName, "same.pdf");
Check("rename duplicate 2", dupes[1].NewName, "same (2).pdf");
Check("rename duplicate 3", dupes[2].NewName, "same (3).pdf");
Check("rename case-insensitive duplicate", RenamePlanner.Plan(new[] { "a.pdf", "b.pdf" }, "X", 1, when)[1].NewName, "X (2).pdf");

// ---------- 13. Sensitive data finder ----------
var allOn = new FindOptions();
List<(SensitiveKind Kind, string Text)> Found(string text, FindOptions? o = null)
    => SensitiveDataFinder.Find(text, o ?? allOn).Select(m => (m.Kind, text.Substring(m.Start, m.Length))).ToList();

Check("ssn found", Found("SSN 123-45-6789 on file")[0], (SensitiveKind.Ssn, "123-45-6789"));
Check("ssn with spaces", Found("123 45 6789")[0].Kind, SensitiveKind.Ssn);
foreach (var bad in new[] { "000-12-3456", "666-12-3456", "912-34-5678", "123-00-4567", "123-45-0000" })
    Check("ssn rejects " + bad, Found(bad).Count(f => f.Kind == SensitiveKind.Ssn), 0);

foreach (var phone in new[] { "(555) 123-4567", "555-123-4567", "555.123.4567", "+1 555 123 4567", "5551234567" })
    Check("phone " + phone, Found("Call " + phone + " now").Any(f => f.Kind == SensitiveKind.Phone && f.Text.Contains("4567")), true);
Check("short number is not a phone", Found("Invoice 12345").Count, 0);
Check("ssn is not a phone", Found("123-45-6789").Count(f => f.Kind == SensitiveKind.Phone), 0);

Check("email", Found("write to jane.doe+law@firm-co.com today")[0], (SensitiveKind.Email, "jane.doe+law@firm-co.com"));

var card = Found("Card 4111 1111 1111 1111 exp 12/27");
Check("valid card found", card.Count(f => f.Kind == SensitiveKind.CreditCard), 1);
Check("valid card not also a phone", card.Count(f => f.Kind == SensitiveKind.Phone), 0);
Check("invalid card (fails Luhn) ignored", Found("Card 4111 1111 1111 1112").Count(f => f.Kind == SensitiveKind.CreditCard), 0);

Check("dob numeric (label excluded)", Found("DOB: 03/14/1985")[0], (SensitiveKind.DateOfBirth, "03/14/1985"));
Check("dob long form", Found("Date of Birth March 14, 1985")[0].Text, "March 14, 1985");
Check("dob 'born on'", Found("born on 14 March 1985")[0].Text, "14 March 1985");
Check("unlabelled date is not a dob", Found("Filed 03/14/2025").Count, 0);

var custom = new FindOptions { Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false,
    Ein = false, BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false,
    CustomTerms = new[] { "acme  corp", "Bob" } };
Check("custom term, case and spacing flexible", Found("ACME Corp sued Acme Corp.", custom).Count, 2);
Check("custom term respects word boundaries", Found("Bobby and Bob", custom).Count, 1);
Check("custom term across a line wrap", Found("Acme\nCorp", custom).Count, 1);
Check("option off means not found", Found("555-123-4567", new FindOptions { Phone = false }).Count, 0);

// ---------- 14. Mapping OCR words to boxes ----------
var words = new List<WordBox>
{
    new("Call", 100, 50, 40, 20, 0), new("me", 150, 50, 30, 20, 0), new("at", 190, 50, 20, 20, 0),
    new("(555)", 220, 50, 60, 20, 0), new("123-4567", 290, 50, 90, 20, 0),
    new("Thanks", 100, 90, 70, 20, 1),
};
var regions = RedactionMapper.FindRegions(2, words, allOn);
Check("one phone region", regions.Count, 1);
Check("region kind", regions[0].Kind, (SensitiveKind?)SensitiveKind.Phone);
Check("region page", regions[0].Page, 2);
Check("region text", regions[0].MatchedText, "(555) 123-4567");
Check("region left (padded)", regions[0].X, 217.0);
Check("region top (padded)", regions[0].Y, 47.0);
Check("region width covers both words", regions[0].Width, 380.0 + 3 - 217.0);
Check("region height", regions[0].Height, 26.0);
Check("regions start approved", regions[0].Approved, true);
Check("label mentions page and kind", regions[0].Label, "p3 · Phone · (555) 123-4567");

var nearEdge = RedactionMapper.FindRegions(0, new List<WordBox> { new("a@b.co", 1, 1, 50, 10, 0) }, allOn);
Check("padding never goes off the page", nearEdge[0].X >= 0 && nearEdge[0].Y >= 0, true);

var wrapped = RedactionMapper.FindRegions(0, new List<WordBox>
    { new("Acme", 10, 10, 40, 12, 0), new("Corp", 10, 30, 40, 12, 1) }, custom);
Check("match over a line break gets one box per line", wrapped.Count, 2);

// ---------- 15. Verifier ----------
Check("leak found despite formatting", RedactionVerifier.FindLeaks("ssn 123 45 6789 here", new[] { "123-45-6789" }).Count, 1);
Check("no leak on a clean page", RedactionVerifier.FindLeaks("nothing to see", new[] { "123-45-6789", "Acme Corp" }).Count, 0);
Check("very short terms are ignored", RedactionVerifier.FindLeaks("xx", new[] { "xx" }).Count, 0);
Check("leak check is case-insensitive", RedactionVerifier.FindLeaks("ACME CORP", new[] { "acme corp" }).Count, 1);

// ---------- 16. Image-only PDF writer ----------
var jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAgABgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDgKKKK+eP2EKKKKACiiigAooooA//Z");
var pdfStream = new MemoryStream();
ImagePdfWriter.Write(pdfStream, new[]
{
    new PdfPageImage(jpeg, 24, 32, 612, 792),
    new PdfPageImage(jpeg, 24, 32, 595.28, 841.89),
});
var pdfBytes = pdfStream.ToArray();
var pdfText = System.Text.Encoding.Latin1.GetString(pdfBytes);
Check("pdf header", pdfText.StartsWith("%PDF-1.4"), true);
Check("pdf trailer", pdfText.EndsWith("%%EOF\n"), true);
Check("pdf page count", pdfText.Contains("/Count 2"), true);
Check("pdf has no text operators or fonts", pdfText.Contains("/Font") || pdfText.Contains("BT\n"), false);
Check("pdf has no metadata dictionary", pdfText.Contains("/Info") || pdfText.Contains("/Producer"), false);

int sx = pdfText.LastIndexOf("startxref\n", StringComparison.Ordinal);
long xrefPos = long.Parse(pdfText.Substring(sx + 10, pdfText.IndexOf('\n', sx + 10) - (sx + 10)));
Check("startxref points at xref", pdfText.Substring((int)xrefPos, 4), "xref");
int entriesStart = pdfText.IndexOf("\n0 ", (int)xrefPos, StringComparison.Ordinal) + 1;
int headerEnd = pdfText.IndexOf('\n', entriesStart) + 1;
int objectTotal = int.Parse(pdfText.Substring(entriesStart + 2, headerEnd - entriesStart - 3));
Check("xref object count", objectTotal, 9);
bool xrefOk = true;
for (int i = 1; i < objectTotal; i++)
{
    var entry = pdfText.Substring(headerEnd + 20 * i, 20);
    int off = int.Parse(entry[..10]);
    if (!pdfText.Substring(off).StartsWith($"{i} 0 obj")) xrefOk = false;
}
Check("every xref offset lands on its object", xrefOk, true);

var keep = Environment.GetEnvironmentVariable("RC_WRITE_PDF");
if (!string.IsNullOrEmpty(keep)) File.WriteAllBytes(keep, pdfBytes);

Check("writer rejects zero pages", ThrowsArg(() => ImagePdfWriter.Write(new MemoryStream(), Array.Empty<PdfPageImage>())), true);
Check("writer rejects an empty image", ThrowsArg(() => ImagePdfWriter.Write(new MemoryStream(), new[] { new PdfPageImage(Array.Empty<byte>(), 1, 1, 10, 10) })), true);

static bool ThrowsArg(Action a)
{
    try { a(); return false; } catch (ArgumentException) { return true; }
}

// ---------- 17. More detectors ----------
(SensitiveKind Kind, string Text)? First(string text, SensitiveKind kind) =>
    Found(text).Where(f => f.Kind == kind).Select(f => ((SensitiveKind, string)?)f).FirstOrDefault();
bool Has(string text, SensitiveKind kind) => Found(text).Any(f => f.Kind == kind);

// EIN
Check("ein unlabelled", First("Filed under 12-3456789 for 2024", SensitiveKind.Ein)?.Text, "12-3456789");
Check("ein labelled, hyphenated", First("EIN: 12-3456789", SensitiveKind.Ein)?.Text, "12-3456789");
Check("ein labelled, no hyphen", First("Employer Identification Number 123456789", SensitiveKind.Ein)?.Text, "123456789");
Check("ein 'Federal Tax ID'", First("Federal Tax ID # 98-7654321", SensitiveKind.Ein)?.Text, "98-7654321");
Check("ein impossible prefix ignored when unlabelled", Has("Ref 00-1234567", SensitiveKind.Ein), false);
Check("ein is not an ssn", Has("12-3456789", SensitiveKind.Ssn), false);

// routing numbers
Check("routing labelled", First("Routing number: 021000021", SensitiveKind.RoutingNumber)?.Text, "021000021");
Check("routing via ABA", First("ABA 026009593", SensitiveKind.RoutingNumber)?.Text, "026009593");
Check("routing via RTN with #", First("RTN # 111000025", SensitiveKind.RoutingNumber)?.Text, "111000025");
Check("bare 9 digits are not a routing number", Has("Order 021000021", SensitiveKind.RoutingNumber), false);
Check("too-short routing number ignored", Has("Routing: 12345", SensitiveKind.RoutingNumber), false);

// bank accounts
Check("account number", First("Account Number: 1234567890", SensitiveKind.BankAccount)?.Text, "1234567890");
Check("acct with spaces", First("Acct #: 0012 3456 7890", SensitiveKind.BankAccount)?.Text, "0012 3456 7890");
Check("savings account", First("Savings Account 987654321", SensitiveKind.BankAccount)?.Text, "987654321");
Check("short 'account' number ignored", Has("Account 123", SensitiveKind.BankAccount), false);
Check("word 'account' alone is fine", Has("on account of weather", SensitiveKind.BankAccount), false);
Check("card number after 'account' stays a card",
      Found("Account Number: 4111 1111 1111 1111").Select(f => f.Kind).Single(), SensitiveKind.CreditCard);
Check("routing and account on one line",
      Found("Routing 021000021 Account 123456789").Select(f => f.Kind).OrderBy(k => k).ToList().SequenceEqual(
          new[] { SensitiveKind.RoutingNumber, SensitiveKind.BankAccount }), true);

// driver's licence
Check("license letter+digits", First("Driver's License: D1234567", SensitiveKind.DriversLicense)?.Text, "D1234567");
Check("license DL#", First("DL# 123456789", SensitiveKind.DriversLicense)?.Text, "123456789");
Check("license dashed", First("DLN: A123-456-78-901-2", SensitiveKind.DriversLicense)?.Text, "A123-456-78-901-2");
Check("license with state prefix", First("Drivers License No. PA 12345678", SensitiveKind.DriversLicense)?.Text, "12345678");
Check("license agreement is not a licence number", Has("License Agreement dated June 2024", SensitiveKind.DriversLicense), false);

// passports
Check("passport 9 digits", First("Passport No: 123456789", SensitiveKind.Passport)?.Text, "123456789");
Check("passport letter+8 digits", First("Passport #: C12345678", SensitiveKind.Passport)?.Text, "C12345678");
Check("passport mention without a number", Has("A passport number is required", SensitiveKind.Passport), false);

// street addresses
Check("address simple", First("Lives at 123 Main Street today", SensitiveKind.Address)?.Text, "123 Main Street");
Check("address abbreviated", First("Send to 45 Oak Ave.", SensitiveKind.Address)?.Text, "45 Oak Ave.");
Check("address with direction, unit, city, state, zip",
      First("456 N. Oak Ave Apt 4B, Springfield, IL 62704", SensitiveKind.Address)?.Text, "456 N. Oak Ave Apt 4B, Springfield, IL 62704");
Check("address trailing direction", First("1600 Pennsylvania Avenue NW", SensitiveKind.Address)?.Text, "1600 Pennsylvania Avenue NW");
Check("address all caps", First("789 ELM ST", SensitiveKind.Address)?.Text, "789 ELM ST");
Check("address city on next line",
      First("123 Main Street\nSpringfield, IL 62704", SensitiveKind.Address)?.Text, "123 Main Street\nSpringfield, IL 62704");
Check("po box", First("Mail to P.O. Box 1234", SensitiveKind.Address)?.Text, "P.O. Box 1234");
Check("ordinary sentence is not an address", Has("See item 5 of the Court order", SensitiveKind.Address), false);
Check("number + words without a street type is not an address", Has("Page 12 Smith Report", SensitiveKind.Address), false);

// switches
var noNew = new FindOptions { Phone = false, Ein = false, BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
Check("new detectors can be switched off",
      Found("EIN 12-3456789 Acct 1234567890 123 Main Street Passport C12345678", noNew).Count, 0);

// address over two OCR lines becomes two boxes
var addressWords = new List<WordBox>
{
    new("123", 10, 10, 30, 14, 0), new("Main", 50, 10, 40, 14, 0), new("Street", 100, 10, 55, 14, 0),
    new("Springfield,", 10, 30, 90, 14, 1), new("IL", 110, 30, 20, 14, 1), new("62704", 140, 30, 50, 14, 1),
};
var addressRegions = RedactionMapper.FindRegions(0, addressWords, allOn);
Check("two-line address: two boxes", addressRegions.Count(r => r.Kind == SensitiveKind.Address), 2);
Check("address description", addressRegions[0].Description, "Address");

// ---------- Meetings ----------
Check("duration under an hour", MeetingFormat.Duration(TimeSpan.FromSeconds(725)), "12:05");
Check("duration over an hour", MeetingFormat.Duration(TimeSpan.FromSeconds(3725)), "1:02:05");
Check("duration zero", MeetingFormat.Duration(TimeSpan.Zero), "0:00");
Check("default title", MeetingFormat.DefaultTitle(new DateTime(2026, 10, 22, 15, 5, 0)), "Meeting 2026-10-22 15:05");

static byte[] MakeWav(int dataBytes, bool finalizeHeader)
{
    // 16 kHz, mono, 16-bit = 32000 bytes per second
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write("RIFF"u8.ToArray()); w.Write(36 + (finalizeHeader ? dataBytes : 0)); w.Write("WAVE"u8.ToArray());
    w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
    w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8.ToArray()); w.Write(finalizeHeader ? dataBytes : 0);
    w.Write(new byte[dataBytes]);
    return ms.ToArray();
}

Check("wav duration (finished file)", WavInfo.GetDuration(new MemoryStream(MakeWav(64000, true)))?.TotalSeconds, (double?)2.0);
Check("wav duration (unfinished header)", WavInfo.GetDuration(new MemoryStream(MakeWav(96000, false)))?.TotalSeconds, (double?)3.0);
Check("not a wav", WavInfo.GetDuration(new MemoryStream(new byte[100])), (TimeSpan?)null);

var meetingRoot = Path.Combine(Path.GetTempPath(), "meetings-test-" + Guid.NewGuid().ToString("N"));
var meetingStore = new JsonMeetingStore(meetingRoot);
Check("no meetings folder loads empty", meetingStore.LoadAll().Count, 0);

var done = Meeting.StartNew(new DateTime(2026, 10, 1, 9, 0, 0), true);
done.Status = MeetingStatus.Completed; done.DurationSeconds = 90; done.Notes = "Client asked about the deadline";
meetingStore.Save(done);

var crashed = Meeting.StartNew(new DateTime(2026, 10, 2, 9, 0, 0), false);   // stays "Recording": simulates a crash
meetingStore.Save(crashed);
File.WriteAllBytes(meetingStore.AudioPathFor(crashed), MakeWav(160000, false));   // 5 seconds on disk

var loadedMeetings = meetingStore.LoadAll();
Check("two meetings loaded", loadedMeetings.Count, 2);
Check("newest first", loadedMeetings[0].Id, crashed.Id);
Check("crashed meeting is marked recovered", loadedMeetings[0].Status, MeetingStatus.Recovered);
Check("crashed meeting keeps its audio length", loadedMeetings[0].DurationSeconds, 5.0);
Check("notes survive a save and load", loadedMeetings[1].Notes, "Client asked about the deadline");
Check("completed meeting stays completed", loadedMeetings[1].Status, MeetingStatus.Completed);
Check("recovery is saved to disk", meetingStore.LoadAll()[0].Status, MeetingStatus.Recovered);

meetingStore.Delete(done);
Check("delete removes the whole meeting", meetingStore.LoadAll().Count, 1);
Directory.Delete(meetingRoot, recursive: true);

// ---------- Transcripts ----------
var rawSegments = new List<TranscriptSegment>
{
    new(0, 4, "  Good morning, everyone. "),
    new(4, 9, " [BLANK_AUDIO]"),
    new(9, 12, "(silence)"),
    new(12, 15, "[inaudible]"),
    new(15, 16, "   "),
    new(16, 20, "We start with the budget."),
};
var cleanedSegments = TranscriptFormatter.Clean(rawSegments);
Check("noise and empty segments are dropped", cleanedSegments.Count, 3);
Check("text is trimmed", cleanedSegments[0].Text, "Good morning, everyone.");
Check("[inaudible] is kept", cleanedSegments[1].Text, "[inaudible]");
Check("transcript text has timestamps",
    TranscriptFormatter.ToText(cleanedSegments),
    "[0:00] Good morning, everyone." + Environment.NewLine + "[0:12] [inaudible]" + Environment.NewLine + "[0:16] We start with the budget.");
Check("a long recording shows hours", TranscriptFormatter.ToText(new[] { new TranscriptSegment(3725, 3730, "Hi") }), "[1:02:05] Hi");

var transcriptRoot = Path.Combine(Path.GetTempPath(), "transcripts-test-" + Guid.NewGuid().ToString("N"));
var transcriptStore = new JsonMeetingStore(transcriptRoot);
var talked = Meeting.StartNew(new DateTime(2026, 10, 3, 10, 0, 0), false);
talked.Status = MeetingStatus.Completed;
transcriptStore.Save(talked);
Check("no transcript yet", transcriptStore.LoadTranscript(talked) == null, true);
transcriptStore.SaveTranscript(talked, new Transcript { Segments = cleanedSegments, Model = "test-model" });
talked.HasTranscript = true;
transcriptStore.Save(talked);
var reloadedTranscript = transcriptStore.LoadTranscript(talked);
Check("transcript round-trips", reloadedTranscript?.Segments.Count, (int?)3);
Check("transcript keeps the model name", reloadedTranscript?.Model, "test-model");
Check("transcript keeps timings", reloadedTranscript?.Segments[2].StartSeconds, (double?)16.0);
Check("HasTranscript persists", transcriptStore.LoadAll()[0].HasTranscript, true);
Check("NoTranscript mirrors it", transcriptStore.LoadAll()[0].NoTranscript, false);
Directory.Delete(transcriptRoot, recursive: true);

// ---------- Password-protected PDFs: spotting them ----------
bool Encrypted(byte[] bytes) => PdfEncryptionProbe.IsEncrypted(new MemoryStream(bytes));
byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

Check("probe: trailer with /Encrypt reference", Encrypted(Ascii("trailer\n<< /Size 9 /Root 1 0 R /Encrypt 8 0 R /ID [<aa><bb>] >>")), true);
Check("probe: inline /Encrypt dictionary", Encrypted(Ascii("<< /Root 1 0 R /Encrypt<< /Filter /Standard /V 2 >> >>")), true);
Check("probe: /EncryptMetadata alone is not enough", Encrypted(Ascii("<< /EncryptMetadata false >>")), false);
Check("probe: plain PDF", Encrypted(Ascii("%PDF-1.4\ntrailer\n<< /Size 3 /Root 1 0 R >>\n%%EOF")), false);
{
    // The token sits across the boundary between two 1 MB reads.
    var filler = new byte[(1 << 20) - 3];
    Array.Fill(filler, (byte)' ');
    var tail = Ascii("/Encrypt 7 0 R");
    var all = new byte[filler.Length + tail.Length];
    filler.CopyTo(all, 0);
    tail.CopyTo(all, filler.Length);
    Check("probe: token split across chunks", Encrypted(all), true);
}
{
    var plain = new MemoryStream();
    ImagePdfWriter.Write(plain, new[] { new PdfPageImage(jpeg, 24, 32, 612, 792) });
    Check("probe: our own redacted PDFs are not encrypted", Encrypted(plain.ToArray()), false);
}
{
    var ex = new PdfPasswordException(Path.Combine("cases", "Smith.pdf"), wrongPassword: false);
    Check("password exception names the file", ex.Message, "Smith.pdf is password-protected.");
    Check("password exception, wrong password", new PdfPasswordException("x.pdf", true).WrongPassword, true);
}

// ---------- Redact tab inputs ----------
Check("input: pdf", InputFile.Classify(@"C:\a\Contract.PDF"), InputKind.Pdf);
Check("input: docx", InputFile.Classify("letter.docx"), InputKind.Word);
Check("input: legacy doc", InputFile.Classify("letter.doc"), InputKind.Word);
Check("input: jpeg", InputFile.Classify("scan.JPG"), InputKind.Image);
Check("input: tiff", InputFile.Classify("scan.tiff"), InputKind.Image);
Check("input: spreadsheet is not supported", InputFile.Classify("sheet.xlsx"), InputKind.Unsupported);
Check("input: no extension", InputFile.Classify("README"), InputKind.Unsupported);
{
    var probeDir = Path.Combine(Path.GetTempPath(), "aegis-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(probeDir);
    var zipLike = Path.Combine(probeDir, "ok.docx");
    File.WriteAllBytes(zipLike, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 });
    var lockedDocx = Path.Combine(probeDir, "locked.docx");
    File.WriteAllBytes(lockedDocx, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1 });
    var oldDoc = Path.Combine(probeDir, "old.doc");
    File.WriteAllBytes(oldDoc, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1 });
    Check("docx: normal file is fine", InputFile.IsEncryptedDocx(zipLike), false);
    Check("docx: password-protected file is spotted", InputFile.IsEncryptedDocx(lockedDocx), true);
    Check("docx: .doc files share that header, so only .docx is judged", InputFile.IsEncryptedDocx(oldDoc), false);
    Directory.Delete(probeDir, recursive: true);
}

// ---------- Searchable redacted PDFs: the invisible text layer ----------
{
    // A 1000 x 2000 px page shown at 500 x 1000 pt, so every pixel is half a point.
    var layerWords = new List<WordBox>
    {
        new("Patient:", 100, 100, 160, 40, 0),
        new("123-45-6789", 300, 100, 300, 40, 0),
        new("Diagnosis", 100, 200, 200, 40, 1),
        new("(see)", 320, 200, 100, 40, 1),
    };
    var page = new ScannedPage(Array.Empty<byte>(), 1000, 2000, 500, 1000, layerWords);
    var ssnBox = new RedactionRegion(0, SensitiveKind.Ssn, "123-45-6789", 296, 96, 308, 48);

    var layer = OcrTextLayer.Build(page, new[] { ssnBox });
    Check("layer: redacted word is left out", layer.Any(w => w.Text.Contains("6789")), false);
    Check("layer: other layerWords stay", layer.Count, 3);
    Check("layer: x scaled to points", layer[0].Left, 50.0);
    Check("layer: y scaled to points", layer[0].Top, 50.0);
    Check("layer: width scaled to points", layer[0].Width, 80.0);

    var nearMiss = new RedactionRegion(0, null, "", 255, 90, 50, 60);   // clips the end of "Patient:" and the start of the SSN
    var clipped = OcrTextLayer.Build(page, new[] { nearMiss });
    Check("layer: a word merely touched by a box is dropped too", clipped.Any(w => w.Text == "Patient:"), false);

    Check("layer: no boxes keeps every word", OcrTextLayer.Build(page, Array.Empty<RedactionRegion>()).Count, 4);
    Check("layer text passes the leak check", RedactionVerifier.FindLeaks(OcrTextLayer.PlainText(layer), new[] { "123-45-6789" }).Count, 0);

    var searchable = new MemoryStream();
    ImagePdfWriter.Write(searchable, new[] { new PdfPageImage(jpeg, 24, 32, 500, 1000, 3, layer) });
    var layerPdfText = System.Text.Encoding.Latin1.GetString(searchable.ToArray());
    Check("writer: font is included when there is text", layerPdfText.Contains("/BaseFont /Courier"), true);
    Check("writer: layerWords are written", layerPdfText.Contains("(Patient:) Tj"), true);
    Check("writer: parentheses are escaped", layerPdfText.Contains(@"(\(see\)) Tj"), true);
    Check("writer: text is invisible (render mode 3)", layerPdfText.Contains("BT 3 Tr"), true);
    Check("writer: redacted text is not in the file", layerPdfText.Contains("123-45-6789") || layerPdfText.Contains("(123"), false);
    // "Patient:" top = 50 pt, height 20 pt: baseline = 1000 - (50 + 16) = 934
    Check("writer: text is placed with PDF's bottom-left origin", layerPdfText.Contains(" 50 934 Tm ("), true);

    var plainPdf = new MemoryStream();
    ImagePdfWriter.Write(plainPdf, new[] { new PdfPageImage(jpeg, 24, 32, 500, 1000) });
    Check("writer: no font when there is no text", System.Text.Encoding.Latin1.GetString(plainPdf.ToArray()).Contains("/Font"), false);

    var accented = new List<PdfTextWord> { new("Café", 10, 10, 40, 12), new("日本", 60, 10, 20, 12) };
    var accentedPdf = new MemoryStream();
    ImagePdfWriter.Write(accentedPdf, new[] { new PdfPageImage(jpeg, 24, 32, 200, 200, 3, accented) });
    var accentedText = System.Text.Encoding.Latin1.GetString(accentedPdf.ToArray());
    Check("writer: accented letters use WinAnsi octal codes", accentedText.Contains(@"(Caf\351) Tj"), true);
    Check("writer: letters WinAnsi can't hold become ?", accentedText.Contains("(??) Tj"), true);
}

// ---------- Pictures as pages ----------
{
    var (letterW, letterH) = ImagePageSizing.PageSize(1700, 2200, 200);          // a 200 dpi scan of a Letter page
    Check("scan: width", Math.Round(letterW, 1), 612.0);
    Check("scan: height", Math.Round(letterH, 1), 792.0);

    var (photoW, photoH) = ImagePageSizing.PageSize(3000, 4000, 72);             // phone photo that claims 72 dpi
    Check("photo: shrunk to fit a Letter-height page", Math.Round(photoH, 1), 792.0);
    Check("photo: keeps its shape", Math.Round(photoW / photoH, 3), 0.75);

    var (oddW, _) = ImagePageSizing.PageSize(1500, 1000, 0);                     // no resolution given: assume 150 dpi
    Check("unknown dpi assumes 150", Math.Round(oddW, 1), 720.0);

    var (bigW, bigH) = ImagePageSizing.PixelSize(4000, 3000, 2400);
    Check("big picture read at the OCR limit", bigW, 2400);
    Check("big picture keeps its shape", bigH, 1800);
    var (smallW, smallH) = ImagePageSizing.PixelSize(800, 600, 2400);
    Check("small picture is not enlarged", (smallW, smallH), (800, 600));
}

// ---------- 17. Compliance scanner: extra detectors ----------
{
    // Each helper turns on exactly one new detector so a test shows that detector alone.
    List<(SensitiveKind Kind, string Text)> Hits(string text, FindOptions o) => Found(text, o);
    var none = new FindOptions { Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false, Ein = false,
        BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };

    Check("new detectors are off by default", Found("MRN: 12345678 fax: 555-123-4567 192.168.1.20 www.example.com", allOn).Count(f =>
        f.Kind is SensitiveKind.Mrn or SensitiveKind.Fax or SensitiveKind.IpAddress or SensitiveKind.Url), 0);

    // medical record numbers
    var mrn = new FindOptions { Mrn = true, Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false, Ein = false,
        BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    Check("mrn label", Hits("MRN: 00481516", mrn).FirstOrDefault(), (SensitiveKind.Mrn, "00481516"));
    Check("mrn long label", Hits("Medical Record Number 4815162342", mrn)[0].Text, "4815162342");
    Check("mrn patient id", Hits("Patient ID: A123-456", mrn)[0].Text, "A123-456");
    Check("mrn needs a digit", Hits("MRN: pending", mrn).Count, 0);
    Check("mrn too short ignored", Hits("MRN 12", mrn).Count, 0);

    // health plan ids
    var planOpts = new FindOptions { HealthPlanId = true, Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false, Ein = false,
        BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    Check("member id", Hits("Member ID: XYZ123456789", planOpts)[0].Text, "XYZ123456789");
    Check("policy number", Hits("Policy No. 998877665", planOpts)[0].Text, "998877665");
    Check("subscriber id", Hits("Subscriber ID W1234567", planOpts)[0].Text, "W1234567");
    Check("medicare MBI found without a label", Hits("Medicare 1EG4-TE5-MK73 on file", planOpts).Any(h => h.Text == "1EG4-TE5-MK73"), true);
    Check("plain word is not an MBI", Hits("Unremarkable", planOpts).Count, 0);

    // card extras
    var cardx = new FindOptions { CardCvv = true, CardExpiry = true, CardTrackData = true, CreditCard = true, Ssn = false, Phone = false, Email = false,
        DateOfBirth = false, Ein = false, BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    Check("cvv labelled", Hits("CVV: 123", cardx)[0], (SensitiveKind.CardCvv, "123"));
    Check("cvc2 four digits", Hits("CVC2 1234", cardx)[0].Text, "1234");
    Check("security code", Hits("Security code 987", cardx)[0].Text, "987");
    Check("a bare 3-digit number is not a cvv", Hits("Room 123", cardx).Count, 0);
    Check("expiry labelled", Hits("Exp: 12/27", cardx)[0], (SensitiveKind.CardExpiry, "12/27"));
    Check("expiry 'valid thru'", Hits("valid thru 03/2028", cardx)[0].Text, "03/2028");
    var cardLine = Hits("Visa 4111 1111 1111 1111  12/27", cardx);
    Check("expiry right after a card number, no label", cardLine.Any(h => h.Kind == SensitiveKind.CardExpiry && h.Text == "12/27"), true);
    Check("...and the card itself still found", cardLine.Any(h => h.Kind == SensitiveKind.CreditCard), true);
    Check("a date far from any card is not an expiry", Hits("Filed 12/27 by the clerk", cardx).Count, 0);
    var track2 = Hits("read ;4111111111111111=27121010000000000000? ok", cardx);
    Check("track 2 found as one item", track2.Count, 1);
    Check("track 2 kind", track2[0].Kind, SensitiveKind.CardTrackData);
    var track1 = Hits("%B4111111111111111^DOE/JOHN^2712101000000000000?", cardx);
    Check("track 1 swallows the card number", track1.Count, 1);
    Check("track 1 kind", track1[0].Kind, SensitiveKind.CardTrackData);

    // fax, urls, ips
    var net = new FindOptions { Fax = true, Url = true, IpAddress = true, Phone = true, Ssn = false, Email = true, CreditCard = false, DateOfBirth = false,
        Ein = false, BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    var fax = Hits("Tel (555) 123-4567  Fax: (555) 987-6543", net);
    Check("fax found", fax.Any(h => h.Kind == SensitiveKind.Fax && h.Text == "(555) 987-6543"), true);
    Check("tel stays a phone", fax.Any(h => h.Kind == SensitiveKind.Phone && h.Text == "(555) 123-4567"), true);
    Check("fax number is not also a phone", fax.Count(h => h.Text == "(555) 987-6543"), 1);
    Check("F: shorthand counts as fax", Hits("T: 555-111-2222  F: 555-333-4444", net).Any(h => h.Kind == SensitiveKind.Fax && h.Text == "555-333-4444"), true);
    Check("'Staff:' is not a fax label", Hits("Staff: 555-333-4444", net).Count(h => h.Kind == SensitiveKind.Fax), 0);
    Check("url with path", Hits("See https://portal.example.com/patients/42?x=1.", net)[0], (SensitiveKind.Url, "https://portal.example.com/patients/42?x=1"));
    Check("www url", Hits("visit www.example.org today", net)[0].Text, "www.example.org");
    Check("email domain is not a url", Hits("jane@www.example.org", net).Count(h => h.Kind == SensitiveKind.Url), 0);
    Check("ipv4", Hits("from 192.168.10.25 at noon", net)[0], (SensitiveKind.IpAddress, "192.168.10.25"));
    Check("ipv4 with out-of-range octet ignored", Hits("version 300.1.1.1", net).Count(h => h.Kind == SensitiveKind.IpAddress), 0);
    Check("three numbers are not an ip", Hits("10.10.10", net).Count, 0);
    Check("ipv6", Hits("addr 2001:0db8:85a3:0000:0000:8a2e:0370:7334 ok", net)[0].Kind, SensitiveKind.IpAddress);

    // ages over 89
    var age = new FindOptions { AgeOver89 = true, Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false, Ein = false,
        BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    Check("95 years old", Hits("a 95 years old patient", age)[0], (SensitiveKind.AgeOver89, "95"));
    Check("102-year-old", Hits("a 102-year-old woman", age)[0].Text, "102");
    Check("y/o", Hits("92 y/o male", age)[0].Text, "92");
    Check("age label", Hits("Age: 91", age)[0].Text, "91");
    Check("aged", Hits("aged 97 at death", age)[0].Text, "97");
    Check("89 is not over 89", Hits("89 years old", age).Count, 0);
    Check("a company's 95 years is ignored", Hits("in business for 95 years", age).Count, 0);
    Check("'page 95' is not an age", Hits("see page 95", age).Count, 0);
    Check("money is not an age", Hits("$95 years old", age).Count, 0);

    // every date
    var dates = new FindOptions { AllDates = true, Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false, Ein = false,
        BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    foreach (var d in new[] { "03/14/2025", "3/4/25", "14.03.2025", "2025-03-14", "March 14, 2025", "Mar 14", "14 March 2025", "14th March", "March 2025", "Sept. 3, 2024" })
        Check("date " + d, Hits("Seen on " + d + " ok", dates).Any(h => h.Kind == SensitiveKind.Date && h.Text.Contains(d.Split(' ')[0].TrimEnd(',', '.'))), true);
    Check("date whole text", Hits("Seen on March 14, 2025 ok", dates)[0].Text, "March 14, 2025");
    Check("month 13 is not a date", Hits("13/13/2025", dates).Count, 0);
    Check("phone is not a date", Hits("555-123-4567", dates).Count, 0);
    Check("ssn is not a date", Hits("123-45-6789", dates).Count, 0);
    Check("ip address is not a date", Hits("10.10.10.10", dates).Count, 0);
    Check("lower-case 'may' is not a date", Hits("you may 5 times", dates).Count, 0);
    Check("labelled dob wins over generic date", Found("DOB: 03/14/1985", new FindOptions { AllDates = true })[0].Kind, SensitiveKind.DateOfBirth);

    // zip codes
    var zip = new FindOptions { ZipCode = true, Ssn = false, Phone = false, Email = false, CreditCard = false, DateOfBirth = false, Ein = false,
        BankAccount = false, RoutingNumber = false, DriversLicense = false, Passport = false, Address = false };
    Check("zip after state", Hits("Springfield, IL 62704", zip)[0], (SensitiveKind.ZipCode, "62704"));
    Check("zip+4", Hits("Austin TX 78701-1234", zip)[0].Text, "78701-1234");
    Check("labelled zip", Hits("ZIP code: 90210", zip)[0].Text, "90210");
    Check("bare five digits are not a zip", Hits("Invoice 62704 total", zip).Count, 0);
    Check("zip inside a street address belongs to the address", Found("123 Main Street, Springfield, IL 62704", new FindOptions { ZipCode = true })
        .Count(f => f.Kind == SensitiveKind.ZipCode), 0);
}

// ---------- 18. Card brands ----------
Check("visa", CardBrand.Detect("4111111111111111"), "Visa");
Check("mastercard 51", CardBrand.Detect("5555555555554444"), "Mastercard");
Check("mastercard 2-series", CardBrand.Detect("2223003122003222"), "Mastercard");
Check("amex", CardBrand.Detect("378282246310005"), "American Express");
Check("discover", CardBrand.Detect("6011111111111117"), "Discover");
Check("diners", CardBrand.Detect("30569309025904"), "Diners Club");
Check("jcb", CardBrand.Detect("3530111333300000"), "JCB");
Check("unknown brand", CardBrand.Detect("9999999999999995"), (string?)null);
Check("too short", CardBrand.Detect("4111"), (string?)null);

// ---------- 19. Masking ----------
Check("mask ssn", MatchMasker.Mask(SensitiveKind.Ssn, "123-45-6789"), "•••-••-6789");
Check("mask card", MatchMasker.Mask(SensitiveKind.CreditCard, "4111 1111 1111 1111"), "•••• •••• •••• 1111");
Check("mask phone", MatchMasker.Mask(SensitiveKind.Phone, "(555) 123-4567"), "(•••) •••-4567");
Check("mask short number hides everything", MatchMasker.Mask(SensitiveKind.Mrn, "12345"), "•••••");
Check("mask email", MatchMasker.Mask(SensitiveKind.Email, "jane.doe@firm.com"), "j•••@firm.com");
Check("mask date keeps only the shape", MatchMasker.Mask(SensitiveKind.Date, "03/14/1985"), "••/••/••••");
Check("mask word date", MatchMasker.Mask(SensitiveKind.DateOfBirth, "March 14, 1985"), "••••• ••, ••••");
Check("mask cvv", MatchMasker.Mask(SensitiveKind.CardCvv, "123"), "•••");
Check("mask track data", MatchMasker.Mask(SensitiveKind.CardTrackData, ";4111111111111111=2712?"), "[track data hidden]");
Check("mask address", MatchMasker.Mask(SensitiveKind.Address, "123 Main Street\nSpringfield, IL 62704"), "[address hidden]");
Check("mask url keeps the host only", MatchMasker.Mask(SensitiveKind.Url, "https://portal.example.com/patients/42"), "https://portal.example.com/…");
Check("mask ip", MatchMasker.Mask(SensitiveKind.IpAddress, "192.168.10.25"), "192.168.•••.•••");
Check("mask custom term", MatchMasker.Mask(SensitiveKind.CustomTerm, "Jane Doe"), "[your term]");
Check("mask never leaks the digits", MatchMasker.Mask(SensitiveKind.Ssn, "123-45-6789").Contains("123-45"), false);

// ---------- 20. Profiles ----------
Check("four profiles", ComplianceProfiles.All.Count, 4);
Check("profile ids are unique", ComplianceProfiles.All.Select(p => p.Id).Distinct().Count(), 4);
Check("lookup by id", ComplianceProfiles.ById("PCI")?.Name, "PCI DSS cardholder data");
Check("every profile says what it can't find", ComplianceProfiles.All.All(p => p.NotChecked.Count > 0), true);
Check("every check has a plain-language explanation", ComplianceProfiles.All.All(p => p.Checks.All(c => c.Explanation.Length > 5)), true);
Check("no profile lists a kind twice", ComplianceProfiles.All.All(p => p.Checks.Select(c => c.Kind).Distinct().Count() == p.Checks.Count), true);
{
    var pci = ComplianceProfiles.Pci.BuildOptions();
    Check("pci: card on", pci.CreditCard, true);
    Check("pci: cvv on", pci.CardCvv, true);
    Check("pci: ssn off", pci.Ssn, false);
    Check("pci: phone off", pci.Phone, false);
    Check("pci: dates off", pci.AllDates, false);
    var hip = ComplianceProfiles.Hipaa.BuildOptions(new[] { "Jane Doe" });
    Check("hipaa: mrn on", hip.Mrn, true);
    Check("hipaa: all dates on", hip.AllDates, true);
    Check("hipaa: ein off", hip.Ein, false);
    Check("hipaa: custom terms carried", hip.CustomTerms.Count, 1);
    Check("hipaa covers the key identifiers", new[] { SensitiveKind.Ssn, SensitiveKind.Mrn, SensitiveKind.HealthPlanId, SensitiveKind.Phone, SensitiveKind.Fax,
        SensitiveKind.Email, SensitiveKind.Url, SensitiveKind.IpAddress, SensitiveKind.ZipCode, SensitiveKind.AgeOver89, SensitiveKind.Date }
        .All(k => ComplianceProfiles.Hipaa.Checks.Any(c => c.Kind == k)), true);
    Check("pci: cvv is high severity", ComplianceProfiles.Pci.SeverityOf(SensitiveKind.CardCvv), FindingSeverity.High);
    Check("hipaa: plain dates are low severity", ComplianceProfiles.Hipaa.SeverityOf(SensitiveKind.Date), FindingSeverity.Low);
}

// ---------- 21. Scanning page text ----------
{
    var chart = "Patient: Jane Doe\nMRN: 00481516  DOB: 03/14/1941\nSSN 123-45-6789\nPhone (555) 123-4567  Fax: (555) 987-6543\n" +
                "Seen 2025-03-14, a 84 y/o woman and her 96 years old mother.\nBill to 100 Main Street, Springfield, IL 62704";
    var page2 = "Insurance Member ID: ABC12345678\nCard 4111 1111 1111 1111 exp 12/27 CVV 123\nEmail jane@example.com";
    var result = ComplianceScanner.ScanPageTexts(@"C:\docs\chart.pdf", "abc123", new[] { chart, "", page2 }, ComplianceProfiles.Hipaa, new[] { "Jane Doe" });

    var kinds1 = result.Findings.Where(f => f.Page == 1).Select(f => f.Kind).ToHashSet();
    foreach (var k in new[] { SensitiveKind.Mrn, SensitiveKind.DateOfBirth, SensitiveKind.Ssn, SensitiveKind.Phone, SensitiveKind.Fax, SensitiveKind.Date,
                              SensitiveKind.AgeOver89, SensitiveKind.Address, SensitiveKind.CustomTerm })
        Check("page 1 has " + k, kinds1.Contains(k), true);
    var kinds3 = result.Findings.Where(f => f.Page == 3).Select(f => f.Kind).ToHashSet();
    foreach (var k in new[] { SensitiveKind.HealthPlanId, SensitiveKind.CreditCard, SensitiveKind.Email })
        Check("page 3 has " + k, kinds3.Contains(k), true);
    Check("hipaa profile does not look for card cvv", kinds3.Contains(SensitiveKind.CardCvv), false);
    Check("hipaa profile does not look for card expiry", kinds3.Contains(SensitiveKind.CardExpiry), false);
    Check("the 84 y/o is not flagged", result.Findings.Any(f => f.Kind == SensitiveKind.AgeOver89 && f.MaskedValue == "••" && f.Page == 1), true);
    Check("only one age over 89", result.Findings.Count(f => f.Kind == SensitiveKind.AgeOver89), 1);
    Check("page count", result.PageCount, 3);
    Check("blank page counted", result.PagesWithoutText, 1);
    Check("blank page noted", result.Note, "1 page had no readable text, so anything on it was not checked");
    Check("card brand shown", result.Findings.Any(f => f.Description == "Card number (Visa)"), true);
    Check("high severity first", result.Findings[0].Severity, FindingSeverity.High);
    Check("severities are in order", result.Findings.Select(f => (int)f.Severity).SequenceEqual(result.Findings.Select(f => (int)f.Severity).OrderBy(x => x)), true);
    Check("line numbers", result.Findings.First(f => f.Kind == SensitiveKind.Ssn).Line, 3);
    Check("status never says passed", result.StatusText.Contains("pass", StringComparison.OrdinalIgnoreCase) || result.StatusText.Contains("compliant", StringComparison.OrdinalIgnoreCase), false);
    Check("no raw values in findings", result.Findings.Any(f => f.MaskedValue.Contains("123-45-6789") || f.MaskedValue.Contains("4111 1111 1111 1111")), false);

    var pciResult = ComplianceScanner.ScanPageTexts("card.pdf", null, new[] { page2 }, ComplianceProfiles.Pci);
    var pciKinds = pciResult.Findings.Select(f => f.Kind).ToHashSet();
    Check("pci finds card, cvv and expiry", pciKinds.SetEquals(new[] { SensitiveKind.CreditCard, SensitiveKind.CardCvv, SensitiveKind.CardExpiry }), true);
    Check("pci does not report the email", pciKinds.Contains(SensitiveKind.Email), false);

    var emptyResult = ComplianceScanner.ScanPageTexts("empty.pdf", null, new[] { "just a letter about lunch" }, ComplianceProfiles.GeneralPii);
    Check("nothing found wording", emptyResult.StatusText, "Nothing found by this scan");
    var allBlank = ComplianceScanner.ScanPageTexts("blank.pdf", null, new[] { "", "  " }, ComplianceProfiles.GeneralPii);
    Check("all-blank note", allBlank.Note, "no readable text was found on any page");
    Check("one item wording", ComplianceScanner.ScanPageTexts("x.pdf", null, new[] { "SSN 123-45-6789" }, ComplianceProfiles.GeneralPii).StatusText, "1 item to review");
    Check("skipped wording", ComplianceScanner.Skipped("locked.pdf", "needs a password").StatusText, "Not scanned: needs a password");

    // from OCR words
    var ocr = new ScannedPage(Array.Empty<byte>(), 100, 100, 100, 100, new List<WordBox>
    {
        new("SSN", 0, 0, 10, 5, 0), new("123-45-6789", 12, 0, 40, 5, 0), new("Thanks", 0, 10, 20, 5, 1),
    });
    var fromWords = ComplianceScanner.ScanPages("ocr.png", null, new[] { ocr }, ComplianceProfiles.GeneralPii);
    Check("scan from OCR words", fromWords.Findings.Count, 1);
    Check("scan from OCR words kind", fromWords.Findings[0].Kind, SensitiveKind.Ssn);
}

// ---------- 22. Report writer ----------
{
    var doc = ComplianceScanner.ScanPageTexts("C:/clients/=cmd<script>.pdf", "deadbeef",
        new[] { "SSN 123-45-6789\nCard 4111 1111 1111 1111" }, ComplianceProfiles.GeneralPii);
    var skipped = ComplianceScanner.Skipped("C:/clients/locked.pdf", "needs a password");
    var clean = ComplianceScanner.ScanPageTexts("C:/clients/fine.pdf", "cafe", new[] { "lunch on friday" }, ComplianceProfiles.GeneralPii);
    var report = new ScanReport(ComplianceProfiles.GeneralPii, new[] { doc, skipped, clean },
        new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero), "0.1.0", 2);

    Check("report totals: findings", report.TotalFindings, 2);
    Check("report totals: scanned", report.FilesScanned, 2);
    Check("report totals: skipped", report.FilesSkipped, 1);

    var html = ScanReportWriter.ToHtml(report);
    Check("html names the profile", html.Contains("General personal information"), true);
    Check("html carries the disclaimer", html.Contains("not a guarantee"), true);
    Check("html says values are hidden", html.Contains("partly hidden"), true);
    Check("html shows masked ssn", html.Contains("•••-••-6789"), true);
    Check("html never contains the raw ssn", html.Contains("123-45-6789"), false);
    Check("html never contains the raw card", html.Contains("4111 1111 1111 1111"), false);
    Check("html escapes file names", html.Contains("<script>"), false);
    Check("html lists what it can't find", html.Contains("What this check cannot find"), true);
    Check("html shows the file hash", html.Contains("deadbeef"), true);
    Check("html says the skipped file was not checked", html.Contains("not</strong> checked"), true);
    Check("html mentions the user's own words", html.Contains("2 word(s) or name(s) you added"), true);
    Check("html avoids 'compliant' as a verdict", html.Contains("is compliant") || html.Contains("passed") || html.Contains("ensures compliance"), false);

    var reportCsv = ScanReportWriter.ToCsv(report);
    var csvLines = reportCsv.TrimEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    Check("csv header + 2 findings + skipped + clean", csvLines.Length, 1 + 2 + 1 + 1);
    Check("csv never contains the raw ssn", reportCsv.Contains("123-45-6789"), false);
    Check("csv guards against formulas in file names", reportCsv.Contains("\"'=cmd"), true);
    Check("csv quotes embedded quotes", ScanReportWriter.Cell("a\"b"), "\"a\"\"b\"");
    Check("csv skipped row", reportCsv.Contains("Not scanned: needs a password"), true);
}

// ======================================================================
// Slice 2: hidden content, folder scanning, clean copies, redaction log
// ======================================================================
byte[] MakeZip(params (string Name, string Content)[] parts)
{
    using var ms = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        foreach (var (name, content) in parts)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false));
            w.Write(content);
        }
    return ms.ToArray();
}

byte[] BuildDocx()
{
    const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:v=\"urn:schemas-microsoft-com:vml\"";
    const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    const string Rt = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    return MakeZip(
        ("[Content_Types].xml", Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"bin\" ContentType=\"application/vnd.openxmlformats-officedocument.oleObject\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/>" +
            "<Override PartName=\"/word/people.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.people+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "<Override PartName=\"/docProps/custom.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.custom-properties+xml\"/>" +
            "<Override PartName=\"/customXml/itemProps1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.customXmlProperties+xml\"/></Types>"),
        ("_rels/.rels", Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rt}officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{Rt}extended-properties\" Target=\"docProps/app.xml\"/>" +
            "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"docProps/thumbnail.jpeg\"/>" +
            $"<Relationship Id=\"rId5\" Type=\"{Rt}custom-properties\" Target=\"docProps/custom.xml\"/></Relationships>"),
        ("word/_rels/document.xml.rels", Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rIdC\" Type=\"{Rt}comments\" Target=\"comments.xml\"/>" +
            "<Relationship Id=\"rIdP\" Type=\"http://schemas.microsoft.com/office/2011/relationships/people\" Target=\"people.xml\"/>" +
            $"<Relationship Id=\"rIdOle\" Type=\"{Rt}oleObject\" Target=\"embeddings/oleObject1.bin\"/>" +
            $"<Relationship Id=\"rIdLink\" Type=\"{Rt}hyperlink\" Target=\"https://example.com/\" TargetMode=\"External\"/>" +
            $"<Relationship Id=\"rIdX\" Type=\"{Rt}customXml\" Target=\"../customXml/item1.xml\"/></Relationships>"),
        ("customXml/item1.xml", "<matter xmlns=\"urn:dms\"><client>Acme</client><matterNo>77-123</matterNo></matter>"),
        ("customXml/itemProps1.xml", "<ds:datastoreItem xmlns:ds=\"http://schemas.openxmlformats.org/officeDocument/2006/customXml\" ds:itemID=\"{ABC}\"/>"),
        ("customXml/_rels/item1.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXmlProps\" Target=\"itemProps1.xml\"/></Relationships>"),
        ("word/document.xml", Decl + $"<w:document {W}><w:body>" +
            "<w:p><w:r><w:t xml:space=\"preserve\">Visible start. </w:t></w:r>" +
            "<w:commentRangeStart w:id=\"0\"/><w:r><w:t>Commented.</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r>" +
            "<w:ins w:id=\"1\" w:author=\"Bob Lee\"><w:r><w:t xml:space=\"preserve\"> Added.</w:t></w:r></w:ins>" +
            "<w:del w:id=\"2\" w:author=\"Bob Lee\"><w:r><w:delText xml:space=\"preserve\"> Deleted secret 987-65-4321.</w:delText></w:r></w:del>" +
            "<w:r><w:rPr><w:vanish/></w:rPr><w:t>HIDDENBIT</w:t></w:r>" +
            "<w:r><w:rPr><w:vanish w:val=\"0\"/></w:rPr><w:t xml:space=\"preserve\"> Not hidden.</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:rPr><w:ins w:id=\"3\" w:author=\"Bob Lee\"/></w:rPr></w:pPr><w:r><w:rPr><w:b/><w:rPrChange w:id=\"4\" w:author=\"Bob Lee\"><w:rPr/></w:rPrChange></w:rPr><w:t>Second paragraph.</w:t></w:r></w:p>" +
            "<w:tbl><w:tr><w:trPr><w:del w:id=\"5\" w:author=\"Bob Lee\"/></w:trPr><w:tc><w:p><w:r><w:t>ROWGONE</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>ROWKEPT</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
            "<w:sdt><w:sdtPr><w:dataBinding w:storeItemID=\"{ABC}\" w:xpath=\"/matter/client\"/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>BOUNDTEXT</w:t></w:r></w:p></w:sdtContent></w:sdt>" +
            "<w:p><w:r><w:object><v:shape id=\"s1\"/><o:OLEObject Type=\"Embed\" ProgID=\"Package\" ShapeID=\"s1\" r:id=\"rIdOle\"/></w:object></w:r></w:p>" +
            "</w:body></w:document>"),
        ("word/comments.xml", Decl + $"<w:comments {W}><w:comment w:id=\"0\" w:author=\"Mary Smith\" w:initials=\"MS\"><w:p><w:r><w:t>Card 4111 1111 1111 1111</w:t></w:r></w:p></w:comment></w:comments>"),
        ("word/people.xml", Decl + "<w15:people xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\"><w15:person w15:author=\"Mary Smith\"/></w15:people>"),
        ("word/embeddings/oleObject1.bin", "OLEOLEOLE"),
        ("docProps/thumbnail.jpeg", "not really a jpeg"),
        ("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<dc:title>Smith settlement</dc:title><dc:subject>Client SSN 123-45-6789</dc:subject><dc:creator>Jane Doe</dc:creator><cp:keywords>confidential</cp:keywords>" +
            "<dc:description>internal draft</dc:description><cp:lastModifiedBy>Bob Lee</cp:lastModifiedBy><dcterms:created xsi:type=\"dcterms:W3CDTF\">2025-01-01T00:00:00Z</dcterms:created></cp:coreProperties>"),
        ("docProps/app.xml", Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Microsoft Office Word</Application><Company>Acme Legal LLP</Company><Manager>Pat Boss</Manager></Properties>"),
        ("docProps/custom.xml", Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/custom-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><property fmtid=\"{D5CDD505-2E9C-101B-9397-08002B2CF9AE}\" pid=\"2\" name=\"ClientId\"><vt:lpwstr>ACME-77123</vt:lpwstr></property></Properties>"));
}

byte[] ReadPart(byte[] zipBytes, string name)
{
    using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes), System.IO.Compression.ZipArchiveMode.Read);
    var e = zip.GetEntry(name);
    if (e == null) return Array.Empty<byte>();
    using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
}
List<string> PartNames(byte[] zipBytes)
{
    using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes), System.IO.Compression.ZipArchiveMode.Read);
    return zip.Entries.Select(e => e.FullName).ToList();
}
string PartText(byte[] zipBytes, string name) => System.Text.Encoding.UTF8.GetString(ReadPart(zipBytes, name));

// ---------- 23. Names: masking ----------
Check("mask name", MatchMasker.MaskName("Jane Doe"), "J••• D••");
Check("mask name keeps one-letter words", MatchMasker.MaskName("J Smith"), "J S••••");
Check("mask file name keeps the extension", MatchMasker.MaskFileName("client-list.xlsx"), "c••••••.xlsx");
Check("mask long text is shortened", MatchMasker.MaskName(new string('a', 200)).Length <= 61, true);

// ---------- 24. Word documents ----------
{
    var docx = BuildDocx();
    var found = DocxInspector.Inspect(new MemoryStream(docx));
    bool HasHid(HiddenCategory c, FindingSeverity? s = null) => found.Items.Any(i => i.Category == c && (s == null || i.Severity == s));

    Check("docx: author found", found.Items.Any(i => i.What == "Author: J••• D••"), true);
    Check("docx: last saved by found", found.Items.Any(i => i.What == "Last saved by: B•• L••"), true);
    Check("docx: company found", found.Items.Any(i => i.What == "Company: A••• L•••• L••"), true);
    Check("docx: custom property", found.Items.Any(i => i.What.StartsWith("1 custom propert")), true);
    Check("docx: comment found", found.Items.Any(i => i.Category == HiddenCategory.Comments && i.What.StartsWith("1 comment by M••• S••••")), true);
    Check("docx: tracked deletion is high severity", HasHid(HiddenCategory.TrackedChanges, FindingSeverity.High), true);
    Check("docx: tracked insertion", found.Items.Any(i => i.What.StartsWith("2 tracked insertions")), true);
    Check("docx: formatting change", found.Items.Any(i => i.What.StartsWith("1 tracked formatting change")), true);
    Check("docx: hidden text counted once (vanish=0 is not hidden)", found.Items.Any(i => i.What.StartsWith("1 piece of text formatted as hidden")), true);
    Check("docx: embedded object is high severity", HasHid(HiddenCategory.Attachments, FindingSeverity.High), true);
    Check("docx: preview picture", HasHid(HiddenCategory.PreviewImage), true);
    Check("docx: custom xml noticed", found.Items.Any(i => i.What.StartsWith("Custom XML data")), true);

    string TextOf(string area) => string.Join("|", found.Texts.Where(t => t.Area == area).Select(t => t.Text));
    Check("docx: deleted text is handed on for checking", TextOf("Deleted text (tracked changes)").Contains("987-65-4321"), true);
    Check("docx: comment text is handed on", TextOf("Comments").Contains("4111 1111 1111 1111"), true);
    Check("docx: hidden text is handed on", TextOf("Hidden text"), "HIDDENBIT");
    Check("docx: subject is handed on", TextOf("Document properties").Contains("123-45-6789"), true);
    Check("docx: no raw names in item text", found.Items.Any(i => i.What.Contains("Jane") || i.What.Contains("Mary") || i.What.Contains("Bob")), false);

    // cleaning
    using var cleanOut = new MemoryStream();
    var result = DocxCleaner.Clean(new MemoryStream(docx), cleanOut, new CleanOptions());
    var cleaned = cleanOut.ToArray();
    var after = DocxInspector.Inspect(new MemoryStream(cleaned));
    Check("docx clean: nothing left to find", after.Items.Count, 0);
    Check("docx clean: summary lists what was done", result.Removed.Count >= 5, true);

    var body = PartText(cleaned, "word/document.xml");
    Check("docx clean: visible text kept", body.Contains("Visible start.") && body.Contains("Commented.") && body.Contains("Second paragraph.") && body.Contains("ROWKEPT"), true);
    Check("docx clean: inserted text kept", body.Contains("Added."), true);
    Check("docx clean: not-hidden run kept", body.Contains("Not hidden."), true);
    Check("docx clean: deleted text gone", body.Contains("Deleted secret") || body.Contains("w:delText") || body.Contains("<w:del "), false);
    Check("docx clean: deleted row gone", body.Contains("ROWGONE"), false);
    Check("docx clean: hidden text gone", body.Contains("HIDDENBIT"), false);
    Check("docx clean: comment marks gone", body.Contains("commentRange") || body.Contains("commentReference"), false);
    Check("docx clean: tracked markers gone", body.Contains("<w:ins ") || body.Contains("rPrChange"), false);
    Check("docx clean: embedded object markup gone", body.Contains("w:object") || body.Contains("rIdOle"), false);

    var names = PartNames(cleaned);
    Check("docx clean: comments part gone", names.Any(n => n.Contains("comments")), false);
    Check("docx clean: people part gone", names.Contains("word/people.xml"), false);
    Check("docx clean: embedding gone", names.Any(n => n.StartsWith("word/embeddings/")), false);
    Check("docx clean: thumbnail gone", names.Any(n => n.StartsWith("docProps/thumbnail")), false);
    Check("docx clean: custom props gone", names.Contains("docProps/custom.xml"), false);
    Check("docx clean: custom xml gone", names.Any(n => n.StartsWith("customXml/")), false);
    Check("docx clean: custom xml relationship gone", PartText(cleaned, "word/_rels/document.xml.rels").Contains("customXml"), false);
    Check("docx clean: custom xml content type gone", PartText(cleaned, "[Content_Types].xml").Contains("customXml"), false);
    Check("docx clean: bound control keeps its text", PartText(cleaned, "word/document.xml").Contains("BOUNDTEXT"), true);
    Check("docx clean: binding to the removed data is dropped", PartText(cleaned, "word/document.xml").Contains("dataBinding"), false);
    Check("docx clean: summary mentions custom xml", result.Removed.Any(r => r.Contains("custom XML")), true);
    Check("docx clean: content types first", names[0], "[Content_Types].xml");

    var core = PartText(cleaned, "docProps/core.xml");
    Check("docx clean: author cleared", core.Contains("Jane Doe") || core.Contains("Bob Lee") || core.Contains("Smith settlement") || core.Contains("123-45"), false);
    Check("docx clean: created date kept", core.Contains("2025-01-01"), true);
    var app = PartText(cleaned, "docProps/app.xml");
    Check("docx clean: company cleared", app.Contains("Acme") || app.Contains("Pat Boss"), false);
    Check("docx clean: application name kept", app.Contains("Microsoft Office Word"), true);

    // the package must still hang together, or Word calls the file damaged
    var contentTypes = PartText(cleaned, "[Content_Types].xml");
    foreach (var m in System.Text.RegularExpressions.Regex.Matches(contentTypes, "PartName=\"/([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>())
        Check("docx clean: content type points at a real part: " + m.Groups[1].Value, names.Contains(m.Groups[1].Value), true);
    string Resolve(string folder, string target) => target.StartsWith("/") ? target[1..] : (folder.Length == 0 ? target : folder + "/" + target);
    foreach (var (rels, folder) in new[] { ("_rels/.rels", ""), ("word/_rels/document.xml.rels", "word") })
        foreach (var rel in System.Xml.Linq.XDocument.Parse(PartText(cleaned, rels)).Root!.Elements())
        {
            if ((string?)rel.Attribute("TargetMode") == "External") continue;
            Check($"docx clean: {rels} -> {rel.Attribute("Target")!.Value} exists", names.Contains(Resolve(folder, (string)rel.Attribute("Target")!)), true);
        }
    Check("docx clean: external link kept", PartText(cleaned, "word/_rels/document.xml.rels").Contains("https://example.com/"), true);
    foreach (var n in names.Where(n => n.EndsWith(".xml") || n.EndsWith(".rels")))
        Check("docx clean: well-formed " + n, ThrowsNothing(() => System.Xml.Linq.XDocument.Parse(PartText(cleaned, n))), true);

    // options: leave comments alone
    using var partial = new MemoryStream();
    DocxCleaner.Clean(new MemoryStream(docx), partial, new CleanOptions { Comments = false, DocumentProperties = false, Attachments = false });
    var partialBytes = partial.ToArray();
    Check("docx options: comments kept when asked", PartNames(partialBytes).Contains("word/comments.xml"), true);
    Check("docx options: comment marks kept", PartText(partialBytes, "word/document.xml").Contains("commentReference"), true);
    Check("docx options: properties kept", PartText(partialBytes, "docProps/core.xml").Contains("Jane Doe"), true);
    Check("docx options: embedding kept", PartNames(partialBytes).Contains("word/embeddings/oleObject1.bin"), true);
    Check("docx options: custom xml kept with properties", PartNames(partialBytes).Contains("customXml/item1.xml"), true);
    Check("docx options: binding kept", PartText(partialBytes, "word/document.xml").Contains("dataBinding"), true);
    Check("docx options: tracked changes still accepted", PartText(partialBytes, "word/document.xml").Contains("Deleted secret"), false);
    var partialAfter = DocxInspector.Inspect(new MemoryStream(partialBytes));
    Check("docx options: what was left shows up on inspection", partialAfter.Items.Any(i => i.Category == HiddenCategory.Comments), true);

    // cleaning twice changes nothing more
    using var twice = new MemoryStream();
    DocxCleaner.Clean(new MemoryStream(cleaned), twice, new CleanOptions());
    Check("docx clean: second pass finds nothing either", DocxInspector.Inspect(new MemoryStream(twice.ToArray())).Items.Count, 0);

    // a plain document comes back with nothing to report
    var plain = MakeZip(("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body></w:document>"));
    Check("docx: plain document has nothing hidden", DocxInspector.Inspect(new MemoryStream(plain)).Items.Count, 0);
}

bool ThrowsNothing(Action a) { try { a(); return true; } catch { return false; } }

// ---------- 25. PDFs ----------
{
    var meta = PdfInspector.Inspect(HiddenFixtures.MetaPdf);
    Check("pdf meta: checked", meta.WasChecked, true);
    Check("pdf meta: author", meta.Items.Any(i => i.What == "Author: J••• D••"), true);
    Check("pdf meta: no attachments", meta.Items.Any(i => i.Category == HiddenCategory.Attachments), false);
    Check("pdf meta: subject text handed on", meta.Texts.Any(t => t.Text.Contains("123-45-6789")), true);
    Check("pdf meta: title handed on", meta.Texts.Any(t => t.Text == "Smith v. Jones settlement"), true);
    Check("pdf meta: no raw author in items", meta.Items.Any(i => i.What.Contains("Jane")), false);

    var rich = PdfInspector.Inspect(HiddenFixtures.RichPdf);
    Check("pdf rich: attachment is high severity", rich.Items.Any(i => i.Category == HiddenCategory.Attachments && i.Severity == FindingSeverity.High), true);
    Check("pdf rich: attachment name masked", rich.Items.Any(i => i.What.Contains("c••••••.xlsx")), true);
    Check("pdf rich: comment by author", rich.Items.Any(i => i.Category == HiddenCategory.Comments && i.What.Contains("by M••• S••••")), true);
    Check("pdf rich: comment text handed on", rich.Texts.Any(t => t.Area == "Comments and markup" && t.Text.Contains("555-123-4567")), true);
    Check("pdf rich: javascript", rich.Items.Any(i => i.Category == HiddenCategory.Scripts), true);
    Check("pdf rich: layer switched off", rich.Items.Any(i => i.Category == HiddenCategory.HiddenText && i.Where == "Layers"), true);
    Check("pdf rich: one save only", rich.Items.Any(i => i.Category == HiddenCategory.SavedVersions), false);

    // The same content packed into compressed object streams: the details live inside them.
    var packedText = System.Text.Encoding.Latin1.GetString(HiddenFixtures.PackedPdf);
    Check("pdf packed: really uses object streams", packedText.Contains("/ObjStm"), true);
    Check("pdf packed: author no longer readable as plain text", packedText.Contains("/Author"), false);
    var packed = PdfInspector.Inspect(HiddenFixtures.PackedPdf);
    string Cats(HiddenInspection r) => string.Join(",", r.Items.Select(i => i.Category.ToString() + ":" + i.Severity).Distinct().OrderBy(x => x));
    Check("pdf packed: same findings as the plain file", Cats(packed), Cats(rich));
    Check("pdf packed: comment text found inside object streams", packed.Texts.Any(t => t.Text.Contains("555-123-4567")), true);

    var inc = PdfInspector.Inspect(HiddenFixtures.IncrementalPdf);
    Check("pdf incremental: earlier versions flagged", inc.Items.Any(i => i.Category == HiddenCategory.SavedVersions), true);
    Check("pdf incremental: later keyword found", inc.Items.Any(i => i.What.StartsWith("Keywords:")), true);

    var locked = PdfInspector.Inspect(HiddenFixtures.LockedPdf);
    Check("pdf locked: not checked", locked.WasChecked, false);
    Check("pdf locked: says why", locked.Note!.Contains("password"), true);

    Check("pdf: not a pdf", PdfInspector.Inspect(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }).WasChecked, false);
    Check("pdf: header but no structure", PdfInspector.Inspect(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nnothing else here at all")).WasChecked, false);

    // A page-picture PDF built by our own writer carries none of it: that is why it is the way PDFs get cleaned.
    using var rebuilt = new MemoryStream();
    ImagePdfWriter.Write(rebuilt, new[] { new PdfPageImage(jpeg, 24, 32, 612, 792), new PdfPageImage(jpeg, 24, 32, 612, 792) });
    var rebuiltResult = PdfInspector.Inspect(rebuilt.ToArray());
    Check("pdf rebuilt from pictures: checked", rebuiltResult.WasChecked, true);
    Check("pdf rebuilt from pictures: nothing hidden", rebuiltResult.Items.Count, 0);
}

// ---------- 26. Pictures ----------
{
    // A JPEG with an EXIF block (camera, GPS) and a text comment added in front of a known clean one.
    byte[] Tiff()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(new byte[] { (byte)'I', (byte)'I', 42, 0 }); w.Write(8u);
        w.Write((ushort)2);
        w.Write((ushort)0x010F); w.Write((ushort)2); w.Write(5u); w.Write(38u);               // Make -> "ACME" at 38
        w.Write((ushort)0x8825); w.Write((ushort)4); w.Write(1u); w.Write(44u);               // GPS IFD at 44
        w.Write(0u);
        w.Write(new byte[] { (byte)'A', (byte)'C', (byte)'M', (byte)'E', 0, 0 });             // 38..43
        w.Write((ushort)3);                                                                    // GPS IFD at 44
        w.Write((ushort)1); w.Write((ushort)2); w.Write(2u); w.Write(new byte[] { (byte)'N', 0, 0, 0 });
        w.Write((ushort)2); w.Write((ushort)5); w.Write(3u); w.Write(200u);
        w.Write((ushort)4); w.Write((ushort)5); w.Write(3u); w.Write(224u);
        w.Write(0u);
        return ms.ToArray();
    }
    byte[] Segment(byte marker, byte[] payload)
    {
        var seg = new byte[4 + payload.Length];
        seg[0] = 0xFF; seg[1] = marker; seg[2] = (byte)((payload.Length + 2) >> 8); seg[3] = (byte)((payload.Length + 2) & 0xFF);
        payload.CopyTo(seg, 4);
        return seg;
    }
    var exifPayload = System.Text.Encoding.ASCII.GetBytes("Exif\0\0").Concat(Tiff()).ToArray();
    var withMeta = jpeg.Take(2).Concat(Segment(0xE1, exifPayload)).Concat(Segment(0xFE, System.Text.Encoding.UTF8.GetBytes("note: SSN 123-45-6789")))
        .Concat(jpeg.Skip(2)).ToArray();

    var jpgFound = ImageMetadata.Inspect(withMeta, ".jpg");
    Check("jpeg: checked", jpgFound.WasChecked, true);
    Check("jpeg: gps found, high severity", jpgFound.Items.Any(i => i.Category == HiddenCategory.Location && i.Severity == FindingSeverity.High), true);
    Check("jpeg: device found", jpgFound.Items.Any(i => i.What.StartsWith("The make and model")), true);
    Check("jpeg: comment found", jpgFound.Items.Any(i => i.What.StartsWith("A text comment")), true);
    Check("jpeg: comment text handed on", jpgFound.Texts.Any(t => t.Text.Contains("123-45-6789")), true);
    Check("jpeg: plain picture has nothing", ImageMetadata.Inspect(jpeg, ".jpg").Items.Count, 0);

    var (jpgClean, jpgRemoved) = ImageMetadata.Strip(withMeta, ".jpg");
    Check("jpeg strip: back to exactly the original bytes", jpgClean.SequenceEqual(jpeg), true);
    Check("jpeg strip: says what it removed", jpgRemoved.Any(r => r.Contains("EXIF")) && jpgRemoved.Any(r => r.Contains("comment")), true);
    Check("jpeg strip: inspection is clean", ImageMetadata.Inspect(jpgClean, ".jpg").Items.Count, 0);
    Check("jpeg strip: nothing to strip changes nothing", ImageMetadata.Strip(jpeg, ".jpg").Data.SequenceEqual(jpeg), true);
    Check("jpeg: not a jpeg", ImageMetadata.Inspect(new byte[] { 1, 2, 3, 4, 5 }, ".jpg").WasChecked, false);

    // Real files made by Pillow: a JPEG with camera, GPS and a thumbnail, and a PNG with text notes and EXIF.
    var pillowJpg = ImageMetadata.Inspect(HiddenFixtures.PhotoJpg, ".jpg");
    Check("pillow jpeg: gps", pillowJpg.Items.Any(i => i.Category == HiddenCategory.Location), true);
    Check("pillow jpeg: serial number", pillowJpg.Items.Any(i => i.What.Contains("serial number")), true);
    Check("pillow jpeg: author text", pillowJpg.Items.Any(i => i.What == "Author text is stored: J••• D••"), true);
    var pillowJpgClean = ImageMetadata.Strip(HiddenFixtures.PhotoJpg, ".jpg").Data;
    Check("pillow jpeg strip: clean", ImageMetadata.Inspect(pillowJpgClean, ".jpg").Items.Count, 0);
    Check("pillow jpeg strip: still a jpeg", pillowJpgClean[0] == 0xFF && pillowJpgClean[1] == 0xD8 && pillowJpgClean[^2] == 0xFF && pillowJpgClean[^1] == 0xD9, true);
    Check("pillow jpeg strip: smaller", pillowJpgClean.Length < HiddenFixtures.PhotoJpg.Length, true);

    var png = ImageMetadata.Inspect(HiddenFixtures.PhotoPng, ".png");
    Check("png: gps from the EXIF chunk", png.Items.Any(i => i.Category == HiddenCategory.Location), true);
    Check("png: text notes named", png.Items.Any(i => i.What.Contains("Author") && i.What.Contains("Comment")), true);
    Check("png: note text handed on", png.Texts.Any(t => t.Text.Contains("MRN 0048151")), true);
    var (pngClean, pngRemoved) = ImageMetadata.Strip(HiddenFixtures.PhotoPng, ".png");
    Check("png strip: clean", ImageMetadata.Inspect(pngClean, ".png").Items.Count, 0);
    Check("png strip: says what it removed", pngRemoved.Count >= 2, true);
    var chunkTypes = new List<string>();
    for (int i = 8; i + 12 <= pngClean.Length;)
    {
        int len = pngClean[i] << 24 | pngClean[i + 1] << 16 | pngClean[i + 2] << 8 | pngClean[i + 3];
        chunkTypes.Add(System.Text.Encoding.ASCII.GetString(pngClean, i + 4, 4));
        i += 12 + len;
    }
    Check("png strip: picture chunks kept, metadata chunks gone", chunkTypes.Contains("IHDR") && chunkTypes.Contains("IDAT") && chunkTypes[^1] == "IEND"
        && !chunkTypes.Contains("tEXt") && !chunkTypes.Contains("eXIf"), true);
}

// ---------- 27. Dispatcher and clean copies on disk ----------
{
    var hdir = Path.Combine(Path.GetTempPath(), "aegis-hidden-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(hdir);
    try
    {
        var docxPath = Path.Combine(hdir, "memo.docx"); File.WriteAllBytes(docxPath, BuildDocx());
        var pdfPath = Path.Combine(hdir, "memo.pdf"); File.WriteAllBytes(pdfPath, HiddenFixtures.RichPdf);
        var jpgPath = Path.Combine(hdir, "photo.jpg"); File.WriteAllBytes(jpgPath, HiddenFixtures.PhotoJpg);
        var docPath = Path.Combine(hdir, "old.doc"); File.WriteAllBytes(docPath, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3 });
        var tifPath = Path.Combine(hdir, "scan.tif"); File.WriteAllBytes(tifPath, new byte[] { 1, 2, 3 });
        var lockedDocx = Path.Combine(hdir, "locked.docx"); File.WriteAllBytes(lockedDocx, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3 });
        var brokenDocx = Path.Combine(hdir, "broken.docx"); File.WriteAllText(brokenDocx, "this is not a zip file");

        Check("dispatch docx", HiddenContentInspector.Inspect(docxPath).Items.Count > 5, true);
        Check("dispatch pdf", HiddenContentInspector.Inspect(pdfPath).Items.Any(i => i.Category == HiddenCategory.Attachments), true);
        Check("dispatch jpg", HiddenContentInspector.Inspect(jpgPath).Items.Any(i => i.Category == HiddenCategory.Location), true);
        Check("dispatch .doc is not checked", HiddenContentInspector.Inspect(docPath).WasChecked, false);
        Check("dispatch .doc says to save as .docx", HiddenContentInspector.Inspect(docPath).Note!.Contains(".docx"), true);
        Check("dispatch .tif is not checked", HiddenContentInspector.Inspect(tifPath).WasChecked, false);
        Check("dispatch protected docx is not checked", HiddenContentInspector.Inspect(lockedDocx).Note!.Contains("password"), true);
        Check("dispatch broken docx is not checked", HiddenContentInspector.Inspect(brokenDocx).WasChecked, false);
        Check("dispatch missing file is not checked", HiddenContentInspector.Inspect(Path.Combine(hdir, "gone.docx")).WasChecked, false);

        Check("can clean docx", HiddenContentCleaner.CanClean(docxPath), true);
        Check("can clean jpg", HiddenContentCleaner.CanClean(jpgPath), true);
        Check("pdf is cleaned another way", HiddenContentCleaner.CanClean(pdfPath), false);

        // naming
        var outDir = Path.Combine(hdir, "clean");
        Check("copy name", Path.GetFileName(HiddenContentCleaner.CleanCopyPath(outDir, "memo.docx")), "memo_CLEAN.docx");
        Check("copy keeps folders", HiddenContentCleaner.CleanCopyPath(outDir, Path.Combine("Client A", "memo.docx")),
              Path.Combine(Path.GetFullPath(outDir), "Client A", "memo_CLEAN.docx"));
        Check("copy can change extension", Path.GetFileName(HiddenContentCleaner.CleanCopyPath(outDir, "memo.pdf", ".pdf")), "memo_CLEAN.pdf");
        Check("copy path can't climb out of the output folder",
              Path.GetDirectoryName(HiddenContentCleaner.CleanCopyPath(outDir, Path.Combine("..", "..", "evil", "x.docx"))), Path.GetFullPath(outDir));

        // cleaning the files
        var hashBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(docxPath)));
        var docxOut = HiddenContentCleaner.CleanCopyPath(outDir, "memo.docx");
        var res = HiddenContentCleaner.CleanFile(docxPath, docxOut, new CleanOptions());
        Check("clean file: copy exists", File.Exists(docxOut), true);
        Check("clean file: original untouched", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(docxPath))), hashBefore);
        Check("clean file: copy is clean", HiddenContentInspector.Inspect(docxOut).Items.Count, 0);
        Check("clean file: summary", res.Removed.Count > 3, true);
        Check("second copy gets a new name", Path.GetFileName(HiddenContentCleaner.CleanCopyPath(outDir, "memo.docx")), "memo_CLEAN (2).docx");

        var jpgOut = HiddenContentCleaner.CleanCopyPath(outDir, "photo.jpg");
        HiddenContentCleaner.CleanFile(jpgPath, jpgOut, new CleanOptions());
        Check("clean picture: copy is clean", HiddenContentInspector.Inspect(jpgOut).Items.Count, 0);
        Check("clean picture: original keeps its data", HiddenContentInspector.Inspect(jpgPath).Items.Count > 0, true);

        var keepPictureData = HiddenContentCleaner.CleanCopyPath(outDir, "photo.jpg");
        HiddenContentCleaner.CleanFile(jpgPath, keepPictureData, new CleanOptions { PictureMetadata = false });
        Check("clean picture: option off leaves it alone", File.ReadAllBytes(keepPictureData).SequenceEqual(File.ReadAllBytes(jpgPath)), true);

        var badOut = Path.Combine(outDir, "broken_CLEAN.docx");
        Check("clean file: a damaged file throws", ThrowsNothing(() => HiddenContentCleaner.CleanFile(brokenDocx, badOut, new CleanOptions())), false);
        Check("clean file: no half-written copy left behind", File.Exists(badOut), false);

        // the cleanup log
        var cleanLogCsv = CleanupLogWriter.ToCsv(new[]
        {
            new CleanupLogEntry("memo.docx", "clean/memo_CLEAN.docx", "AAA", "BBB", new[] { "Removed all comments", "Cleared properties" }, Array.Empty<string>(), "Clean copy made"),
            new CleanupLogEntry("=evil.docx", null, null, null, Array.Empty<string>(), Array.Empty<string>(), "Not cleaned: the file is damaged"),
        }, new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero), "0.1.0");
        var cleanLogLines = cleanLogCsv.TrimEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        Check("cleanup log: header + 2 rows", cleanLogLines.Length, 3);
        Check("cleanup log: hashes recorded", cleanLogLines[1].Contains("\"AAA\"") && cleanLogLines[1].Contains("\"BBB\""), true);
        Check("cleanup log: removed list joined", cleanLogLines[1].Contains("Removed all comments; Cleared properties"), true);
        Check("cleanup log: formula guard", cleanLogLines[2].StartsWith("\"'=evil.docx\""), true);
    }
    finally { Directory.Delete(hdir, true); }
}

// ---------- 28. Scanning folders ----------
{
    var root = Path.Combine(Path.GetTempPath(), "aegis-folder-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
    void Touch(string rel) => File.WriteAllText(Path.Combine(root, rel), "x");
    try
    {
        foreach (var f in new[] { "a.pdf", "B.DOCX", "notes.txt", "~$lock.docx", Path.Combine("sub", "c.png"), Path.Combine("sub", "deep", "d.jpg"), Path.Combine("sub", "data.cleanLogCsv") })
            Touch(f);

        var all = FolderScan.Enumerate(root, includeSubfolders: true);
        Check("folder: finds readable files in every folder", all.Files.Count, 4);
        Check("folder: relative paths",
              string.Join("|", all.Files.Select(f => f.RelativePath.Replace('\\', '/'))), "a.pdf|B.DOCX|sub/c.png|sub/deep/d.jpg");
        Check("folder: counts the unreadable ones", all.UnsupportedFiles, 2);          // notes.txt and data.cleanLogCsv (the ~$ lock file is ignored outright)
        Check("folder: full paths", all.Files.All(f => Path.IsPathRooted(f.FullPath) && File.Exists(f.FullPath)), true);

        var top = FolderScan.Enumerate(root, includeSubfolders: false);
        Check("folder: top level only", top.Files.Count, 2);

        var capped = FolderScan.Enumerate(root, includeSubfolders: true, maxFiles: 3);
        Check("folder: cap", capped.Files.Count, 3);
        Check("folder: cap flagged", capped.WasCapped, true);
        Check("folder: no cap flag when everything fits", all.WasCapped, false);

        var empty = Path.Combine(root, "sub", "deep", "nothing"); Directory.CreateDirectory(empty);
        Check("folder: empty folder", FolderScan.Enumerate(empty, true).Files.Count, 0);
    }
    finally { Directory.Delete(root, true); }
}

// ---------- 29. Scanner with hidden content ----------
{
    var hiddenFound = new HiddenInspection(
        new[]
        {
            new HiddenItem(HiddenCategory.Comments, FindingSeverity.Medium, "1 comment by M••• S••••", "Comments"),
            new HiddenItem(HiddenCategory.TrackedChanges, FindingSeverity.High, "1 tracked deletion: the deleted text can still be read", "Tracked changes"),
        },
        new[] { new HiddenText("Comments", "Call back, SSN 123-45-6789"), new HiddenText("Document properties", "Quarterly memo") });

    var withHidden = ComplianceScanner.ScanPageTexts("C:/docs/memo.docx", "abc", new[] { "Nothing sensitive on the page." },
        ComplianceProfiles.GeneralPii, null, hiddenFound, displayName: "Client A/memo.docx");
    Check("hidden: finding from a comment", withHidden.Findings.Count, 1);
    var f1 = withHidden.Findings[0];
    Check("hidden: finding kind", f1.Kind, SensitiveKind.Ssn);
    Check("hidden: finding is not on a page", f1.Page, 0);
    Check("hidden: finding says where", f1.Location, "Comments");
    Check("hidden: finding is masked", f1.MaskedValue, "•••-••-6789");
    Check("hidden: items carried, worst first", withHidden.HiddenItems[0].Severity, FindingSeverity.High);
    Check("hidden: status counts both", withHidden.StatusText, "1 item to review · 2 hidden-content items");
    Check("hidden: display name", withHidden.DisplayName, "Client A/memo.docx");
    Check("display name defaults to the file name", ComplianceScanner.ScanPageTexts("C:/docs/x.pdf", null, new[] { "hi" }, ComplianceProfiles.GeneralPii).DisplayName, "x.pdf");

    var onlyHidden = ComplianceScanner.ScanPageTexts("m.docx", null, new[] { "Plain." }, ComplianceProfiles.GeneralPii, null,
        new HiddenInspection(new[] { new HiddenItem(HiddenCategory.Comments, FindingSeverity.Medium, "1 comment", "Comments") }, Array.Empty<HiddenText>()));
    Check("hidden: only hidden items", onlyHidden.StatusText, "1 hidden-content item");

    var notChecked = ComplianceScanner.ScanPageTexts("old.doc", null, new[] { "Plain." }, ComplianceProfiles.GeneralPii, null,
        HiddenInspection.NotChecked("older Word formats (.doc, .rtf) can't be checked for hidden content: save the file as .docx first"));
    Check("hidden: an uncheckable file is not called clean without a caution", notChecked.Note!.Contains("save the file as .docx"), true);

    var customInComment = ComplianceScanner.ScanPageTexts("m.docx", null, new[] { "Plain." }, ComplianceProfiles.GeneralPii, new[] { "Project Falcon" },
        new HiddenInspection(Array.Empty<HiddenItem>(), new[] { new HiddenText("Hidden text", "see project falcon notes") }));
    Check("hidden: your own words are found in hidden text too", customInComment.Findings.Any(f => f.Kind == SensitiveKind.CustomTerm && f.Area == "Hidden text"), true);

    // report
    var reportWithHidden = new ScanReport(ComplianceProfiles.GeneralPii,
        new[] { withHidden, ComplianceScanner.Skipped("C:/docs/x.pdf", "stopped before this file", "Client A/x.pdf") },
        new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero), "0.1.0", 0, stoppedEarly: true, checkedHiddenContent: true);
    Check("report: hidden total", reportWithHidden.TotalHiddenItems, 2);
    var html2 = ScanReportWriter.ToHtml(reportWithHidden);
    Check("report: stopped notice", html2.Contains("stopped before it finished"), true);
    Check("report: hidden section", html2.Contains("Hidden content") && html2.Contains("1 tracked deletion"), true);
    Check("report: where column", html2.Contains("<th>Where</th>") && html2.Contains(">Comments<"), true);
    Check("report: display name used", html2.Contains("Client A/memo.docx"), true);
    Check("report: hidden check described", html2.Contains("earlier saved versions"), true);
    Check("report: hidden check limits stated", html2.Contains("older Word files (.doc, .rtf)"), true);
    Check("report: raw ssn absent", html2.Contains("123-45-6789"), false);
    var csv2 = ScanReportWriter.ToCsv(reportWithHidden);
    var csv2Lines = csv2.TrimEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    Check("report cleanLogCsv: header + 1 finding + 2 hidden + 1 skipped", csv2Lines.Length, 5);
    Check("report cleanLogCsv: hidden rows labelled", csv2.Contains("\"Hidden content\"") && csv2.Contains("Tracked changes"), true);
    Check("report cleanLogCsv: raw ssn absent", csv2.Contains("123-45-6789"), false);
    var noHiddenHtml = ScanReportWriter.ToHtml(new ScanReport(ComplianceProfiles.GeneralPii, new[] { withHidden }, DateTimeOffset.Now, "0.1.0", 0));
    Check("report: no hidden-check claims when it wasn't run", noHiddenHtml.Contains("earlier saved versions"), false);
    Check("report: no stopped notice on a finished run", noHiddenHtml.Contains("stopped before it finished"), false);
}

// ---------- 30. Redaction log ----------
{
    RedactionRegion R(int page, SensitiveKind? kind, string text, bool approved = true) => new(page, kind, text, 0, 0, 10, 10) { Approved = approved };
    var logRegions = new[]
    {
        R(0, SensitiveKind.Ssn, "123-45-6789"), R(0, SensitiveKind.Ssn, "987-65-4321"), R(1, SensitiveKind.Ssn, "111-22-3333", approved: false),
        R(1, SensitiveKind.Email, "jane@example.com"), R(1, null, ""),
    };
    var log = RedactionLog.Create("C:/in/Smith file.pdf", "AAA111", "C:/out/Smith file_REDACTED.pdf", "BBB222",
        new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero), "0.1.0", "andrew", 2, logRegions, searchableCopy: true, sourceWasProtected: false,
        itemsChecked: 3, itemsStillReadable: 0);

    Check("log: redacted total", log.TotalRedacted, 4);
    Check("log: left visible total", log.TotalLeftVisible, 1);
    Check("log: hand-drawn boxes", log.HandDrawnBoxes, 1);
    Check("log: ssn row", log.Kinds.First(k => k.Item == "SSN"), new RedactionLogKindRow("SSN", 2, 1));
    Check("log: email row", log.Kinds.First(k => k.Item == "Email"), new RedactionLogKindRow("Email", 1, 0));
    Check("log: page 2 row", log.Pages.First(p => p.Page == 2), new RedactionLogPageRow(2, 1, 1, 1));
    Check("log: names", log.SourceName + "|" + log.OutputName, "Smith file.pdf|Smith file_REDACTED.pdf");
    Check("log: path next to the copy", Path.GetFileName(RedactionLog.LogPathFor("C:/out/Smith file_REDACTED.pdf")), "Smith file_REDACTED - redaction log.html");

    var logHtml = RedactionLogWriter.ToHtml(log);
    Check("log html: fingerprints", logHtml.Contains("AAA111") && logHtml.Contains("BBB222"), true);
    Check("log html: who", logHtml.Contains("by andrew"), true);
    Check("log html: never the redacted values", logHtml.Contains("123-45-6789") || logHtml.Contains("987-65-4321") || logHtml.Contains("jane@example.com"), false);
    Check("log html: kinds listed", logHtml.Contains("<td>SSN</td>") && logHtml.Contains("<td>Email</td>"), true);
    Check("log html: verification statement", logHtml.Contains("None could be read in it"), true);
    Check("log html: hand-drawn caution", logHtml.Contains("Hand-drawn boxes can't be checked"), true);
    Check("log html: not a certificate", logHtml.Contains("not a certificate"), true);
    Check("log html: searchable noted", logHtml.Contains("It is searchable"), true);

    var leaky = RedactionLog.Create("a.pdf", null, "a_REDACTED.pdf", null, DateTimeOffset.Now, "0.1.0", null, 1, logRegions, false, true, 3, 2);
    var leakyHtml = RedactionLogWriter.ToHtml(leaky);
    Check("log html: leak warning", leakyHtml.Contains("2 of the 3 redacted item(s) could still be read"), true);
    Check("log html: protected original noted", leakyHtml.Contains("password-protected"), true);
    Check("log html: missing hashes say so", leakyHtml.Contains("not recorded"), true);
    Check("log html: not searchable noted", leakyHtml.Contains("It is not searchable"), true);
    var leakyMeta = leakyHtml.Substring(leakyHtml.IndexOf("Saved "), leakyHtml.IndexOf("with Aegis Docs") - leakyHtml.IndexOf("Saved "));
    Check("log html: no user named when unknown", leakyMeta.Contains(" by "), false);

    var nothing = RedactionLog.Create("a.pdf", null, "b.pdf", null, DateTimeOffset.Now, "0.1.0", null, 1, Array.Empty<RedactionRegion>(), false, false, 0, 0);
    Check("log html: nothing redacted", RedactionLogWriter.ToHtml(nothing).Contains("nothing to check for"), true);
}

// ======================================================================
// Values under their label (ID cards, forms, statements)
// ======================================================================
{
    // The layout of a Pennsylvania ID camera card, with word positions measured from a photo and made-up values.
    // lineByCell: some OCR engines return every table cell as its own line instead of one line per row.
    List<WordBox> CardWords(bool lineByCell = false, double tilt = 0.004, bool reverseOrder = false)
    {
        var list = new List<WordBox>();
        int nextLine = 0; var lineOf = new Dictionary<(int Row, int Cell), int>();
        void Add(int row, int cell, string text, double x, double w, double y, double h)
        {
            if (!lineOf.TryGetValue(lineByCell ? (row, cell) : (row, 0), out var line)) lineOf[lineByCell ? (row, cell) : (row, 0)] = line = nextLine++;
            list.Add(new WordBox(text, x, y + x * tilt, w, h, line));
        }
        // header row 1
        Add(0, 0, "SEX", 40, 50, 290, 22); Add(0, 1, "HEIGHT", 130, 85, 290, 22); Add(0, 2, "EYES", 280, 60, 290, 22);
        Add(0, 3, "DRIVER", 400, 75, 290, 22); Add(0, 3, "/", 485, 10, 290, 22); Add(0, 3, "ID", 505, 25, 290, 22); Add(0, 3, "NUMBER", 540, 75, 290, 22);
        Add(0, 4, "EXPIRATION", 720, 105, 290, 22); Add(0, 4, "DATE", 835, 60, 290, 22);
        Add(0, 5, "BIRTH", 1008, 60, 290, 22); Add(0, 5, "DATE", 1078, 55, 290, 22);
        Add(0, 6, "DUPS", 1190, 55, 290, 22);
        // value row 1
        Add(1, 0, "M", 65, 25, 335, 32); Add(1, 1, "5", 135, 22, 335, 32); Add(1, 1, "09", 180, 38, 335, 32); Add(1, 2, "BRO", 275, 75, 335, 32);
        Add(1, 3, "12", 392, 45, 335, 32); Add(1, 3, "345", 468, 70, 335, 32); Add(1, 3, "678", 568, 70, 335, 32);
        Add(1, 4, "02", 720, 45, 335, 32); Add(1, 4, "28", 790, 45, 335, 32); Add(1, 4, "30", 865, 45, 335, 32);
        Add(1, 5, "07", 995, 45, 335, 32); Add(1, 5, "15", 1065, 45, 335, 32); Add(1, 5, "85", 1135, 45, 335, 32);
        Add(1, 6, "00", 1195, 45, 335, 32);
        // header row 2
        Add(2, 0, "CLASSES", 320, 95, 385, 22); Add(2, 1, "ENDORSEMENTS", 460, 170, 385, 22);
        Add(2, 2, "ISSUE", 690, 55, 385, 22); Add(2, 2, "DATE", 755, 50, 385, 22);
        Add(2, 3, "COMMERCIAL", 840, 120, 385, 22); Add(2, 3, "/", 970, 10, 385, 22); Add(2, 3, "MEDICAL", 990, 85, 385, 22); Add(2, 3, "RESTRICTIONS", 1085, 150, 385, 22);
        // value row 2
        Add(3, 0, "--", 340, 45, 430, 32); Add(3, 1, "-----", 475, 110, 430, 32); Add(3, 2, "021026", 675, 110, 430, 32); Add(3, 3, "*****************", 850, 395, 430, 32);
        if (reverseOrder) list.Reverse();
        return list;
    }

    var everything = new FindOptions();

    foreach (var (name, cardWordList) in new[]
    {
        ("one OCR line per row", CardWords()),
        ("one OCR line per cell", CardWords(lineByCell: true)),
        ("no tilt", CardWords(tilt: 0)),
        ("stronger tilt", CardWords(tilt: 0.01)),
        ("cardWordList in any order", CardWords(reverseOrder: true)),
    })
    {
        var cardRegions = RedactionMapper.FindRegions(0, cardWordList, everything);
        Check($"card ({name}): driver/ID number found", cardRegions.Any(r => r.Kind == SensitiveKind.DriversLicense && r.MatchedText == "12 345 678"), true);
        Check($"card ({name}): birth date found", cardRegions.Any(r => r.Kind == SensitiveKind.DateOfBirth && r.MatchedText == "07 15 85"), true);
        Check($"card ({name}): nothing else is marked", cardRegions.Count, 2);
    }

    var cardFound = RedactionMapper.FindRegions(0, CardWords(), everything);
    var dl = cardFound.First(r => r.Kind == SensitiveKind.DriversLicense);
    Check("card: box starts at the first digit group", dl.X < 392 && dl.X > 380, true);
    Check("card: box reaches the last digit group", dl.X + dl.Width > 638, true);
    Check("card: box does not run into the next column", dl.X + dl.Width < 700, true);
    Check("card: box is on the value row, not the label row", dl.Y > 320, true);
    var dob = cardFound.First(r => r.Kind == SensitiveKind.DateOfBirth);
    Check("card: birth date box stays in its column", dob.X > 980 && dob.X + dob.Width < 1190, true);

    // Why positions matter: read as plain text, the header row and the value row are two separate lines of unrelated words.
    Check("card: the text patterns alone can't link these values to their labels",
          Found(PageTextBuilder.Build(CardWords()).Text, everything).Any(h => h.Kind is SensitiveKind.DriversLicense or SensitiveKind.DateOfBirth), false);

    // other dates on the card are only picked up when asked for
    var datesToo = RedactionMapper.FindRegions(0, CardWords(), new FindOptions { AllDates = true });
    Check("card, all dates: birth date still reads as a birth date", datesToo.Count(r => r.Kind == SensitiveKind.DateOfBirth), 1);
    Check("card, all dates: expiration date found", datesToo.Any(r => r.Kind == SensitiveKind.Date && r.MatchedText == "02 28 30"), true);
    Check("card, all dates: issue date found", datesToo.Any(r => r.Kind == SensitiveKind.Date && r.MatchedText == "021026"), true);
    Check("card, all dates: birth date not also reported as a plain date", datesToo.Count(r => r.MatchedText == "07 15 85"), 1);

    // options are respected
    Check("card: licence off", RedactionMapper.FindRegions(0, CardWords(), new FindOptions { DriversLicense = false }).Any(r => r.Kind == SensitiveKind.DriversLicense), false);
    Check("card: birth date off", RedactionMapper.FindRegions(0, CardWords(), new FindOptions { DateOfBirth = false }).Any(r => r.Kind == SensitiveKind.DateOfBirth), false);

    // a stray word that OCR glued onto the value is left out
    var stray = CardWords(); stray.Add(new WordBox("X", 372, 335 + 372 * 0.004, 14, 32, 1));
    Check("card: a stray word beside the value doesn't stop it being found", RedactionMapper.FindRegions(0, stray, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), true);

    // OCR noise: cell borders read as separate "|" words or stuck to the numbers, and O read for 0
    var bordered = CardWords();
    bordered.Add(new WordBox("|", 380, 335 + 380 * 0.004, 8, 32, 1)); bordered.Add(new WordBox("|", 655, 335 + 655 * 0.004, 8, 32, 1));
    Check("card: borders read as separate words don't stop it", RedactionMapper.FindRegions(0, bordered, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), true);
    var stuck = CardWords().Select(w => w.Text == "12" ? w with { Text = "|12" } : w.Text == "678" ? w with { Text = "678]" } : w).ToList();
    Check("card: borders stuck to the numbers don't stop it", RedactionMapper.FindRegions(0, stuck, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), true);
    var misread = CardWords().Select(w => w.Text == "07" ? w with { Text = "O7" } : w.Text == "15" ? w with { Text = "l5" } : w).ToList();
    Check("card: O for 0 and l for 1 in the birth date still reads as a date", RedactionMapper.FindRegions(0, misread, everything).Any(r => r.Kind == SensitiveKind.DateOfBirth), true);
    var barsBothEnds = CardWords().Select(w => w.Text == "12" ? w with { Text = "|", Width = 8 } : w).ToList();
    barsBothEnds.Add(new WordBox("12", 392, 335 + 392 * 0.004, 40, 32, 1));
    Check("card: a border word before the value is left out of the box", RedactionMapper.FindRegions(0, barsBothEnds, everything).First(r => r.Kind == SensitiveKind.DriversLicense).MatchedText.StartsWith("|"), false);

    // what must NOT match
    var noValue = CardWords().Where(w => !(new[] { "12", "345", "678" }.Contains(w.Text))).ToList();
    Check("no value under the label: nothing marked for it", RedactionMapper.FindRegions(0, noValue, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), false);
    var wordValue = CardWords().Select(w => w.Text is "12" or "345" or "678" ? w with { Text = w.Text == "12" ? "SIGNATURE" : "HERE" } : w).Where(w => w.Text != "HERE" || true).ToList();
    Check("cardWordList under the label are not a number", RedactionMapper.FindRegions(0, wordValue, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), false);
    var notADate = CardWords().Select(w => w.Text is "07" or "15" or "85" ? w with { Text = w.Text == "07" ? "7771" : w.Text == "15" ? "5555" : "9" } : w).ToList();
    Check("a number that isn't a date under 'birth date' is not marked", RedactionMapper.FindRegions(0, notADate, everything).Any(r => r.Kind == SensitiveKind.DateOfBirth), false);
    var tooFar = CardWords().Select(w => w.Y > 330 && w.Y < 380 ? w with { Y = w.Y + 150 } : w).ToList();
    Check("a value far below the label is not taken for it", RedactionMapper.FindRegions(0, tooFar, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), false);
    var onlyNeighbour = CardWords().Where(w => !(new[] { "12", "345", "678" }.Contains(w.Text))).ToList();
    onlyNeighbour.Add(new WordBox("98765432", 1210, 335 + 1210 * 0.004, 120, 32, 1));       // a number under DUPS, not under the label
    Check("a number under a different heading is not taken for it", RedactionMapper.FindRegions(0, onlyNeighbour, everything).Any(r => r.Kind == SensitiveKind.DriversLicense), false);

    // the scanner uses it too (and shows the value partly hidden)
    var cardPage = new ScannedPage(Array.Empty<byte>(), 2016, 1512, 612, 792, CardWords());
    var cardScan = ComplianceScanner.ScanPages("card.jpg", null, new[] { cardPage }, ComplianceProfiles.GeneralPii);
    Check("card scan: driver/ID number reported", cardScan.Findings.Any(f => f.Kind == SensitiveKind.DriversLicense), true);
    Check("card scan: birth date reported", cardScan.Findings.Any(f => f.Kind == SensitiveKind.DateOfBirth), true);
    Check("card scan: values are partly hidden", cardScan.Findings.Any(f => f.MaskedValue.Contains("12 345") || f.MaskedValue.Contains("07 15")), false);
    Check("card scan: licence shows only its last digits", cardScan.Findings.First(f => f.Kind == SensitiveKind.DriversLicense).MaskedValue.EndsWith("678"), true);
    Check("card scan: has a line number", cardScan.Findings.All(f => f.Line >= 1), true);
    var hipaaCard = ComplianceScanner.ScanPages("card.jpg", null, new[] { cardPage }, ComplianceProfiles.Hipaa);
    Check("card scan, HIPAA: expiration and issue dates are reported too", hipaaCard.Findings.Count(f => f.Kind == SensitiveKind.Date), 2);

    // forms: a label above a left-aligned value, with nothing beside it
    List<WordBox> Form(string label, string value, double labelWidth = 160, double valueWidth = 230)
    {
        var l = label.Split(' ').Select((t, i) => new WordBox(t, 100 + i * (labelWidth / label.Split(' ').Length), 200, labelWidth / label.Split(' ').Length - 8, 20, 0));
        var v = value.Split(' ').Select((t, i) => new WordBox(t, 100 + i * (valueWidth / value.Split(' ').Length), 232, valueWidth / value.Split(' ').Length - 10, 22, 1));
        return l.Concat(v).ToList();
    }
    Check("form: date of birth above a written-out date", RedactionMapper.FindRegions(0, Form("Date of Birth", "February 14, 1977"), everything)
        .Any(r => r.Kind == SensitiveKind.DateOfBirth && r.MatchedText == "February 14, 1977"), true);
    Check("form: DOB above a slashed date", RedactionMapper.FindRegions(0, Form("DOB", "02/14/1977", 40, 150), everything)
        .Any(r => r.Kind == SensitiveKind.DateOfBirth), true);
    Check("form: account number above a number", RedactionMapper.FindRegions(0, Form("Account Number", "4400 1234 5678"), everything)
        .Any(r => r.Kind == SensitiveKind.BankAccount), true);
    Check("form: routing number above nine digits", RedactionMapper.FindRegions(0, Form("Routing Number", "021000021"), everything)
        .Any(r => r.Kind == SensitiveKind.RoutingNumber && r.MatchedText == "021000021"), true);
    Check("form: passport above a passport number", RedactionMapper.FindRegions(0, Form("Passport No.", "C01234567"), everything)
        .Any(r => r.Kind == SensitiveKind.Passport), true);
    Check("form: driver license number above a number", RedactionMapper.FindRegions(0, Form("Driver License Number", "D123 4567 8901"), everything)
        .Any(r => r.Kind == SensitiveKind.DriversLicense), true);
    Check("form: ID number above a number", RedactionMapper.FindRegions(0, Form("ID Number", "12345678"), everything)
        .Any(r => r.Kind == SensitiveKind.DriversLicense), true);
    var ssnForm = RedactionMapper.FindRegions(0, Form("Social Security Number", "123-45-6789"), everything);
    Check("form: ssn found once, not twice", ssnForm.Count(r => r.Kind == SensitiveKind.Ssn), 1);
    Check("form: ein above an ein", RedactionMapper.FindRegions(0, Form("EIN", "12-3456789", 40, 150), everything).Any(r => r.Kind == SensitiveKind.Ein), true);
    var hipaaOnly = new FindOptions { Mrn = true, HealthPlanId = true };
    Check("form: medical record number above a number", RedactionMapper.FindRegions(0, Form("Medical Record Number", "00481516"), hipaaOnly).Any(r => r.Kind == SensitiveKind.Mrn), true);
    Check("form: member id above an id", RedactionMapper.FindRegions(0, Form("Member ID", "XYZ123456789"), hipaaOnly).Any(r => r.Kind == SensitiveKind.HealthPlanId), true);
    Check("form: a label with ordinary text under it marks nothing", RedactionMapper.FindRegions(0, Form("Date of Birth", "Please see the attached notes"), everything).Count, 0);
    Check("form: unrelated cardWordList marked nothing", RedactionMapper.FindRegions(0, Form("Favourite colour", "Blue"), everything).Count, 0);
}

// ---------- text patterns: the label names and spaced dates the old patterns missed ----------
{
    var text = new FindOptions();
    List<(SensitiveKind Kind, string Text)> Hit(string s) => Found(s, text);
    Check("text: birth date with spaces", Hit("DOB 02 04 77").Any(h => h.Kind == SensitiveKind.DateOfBirth && h.Text == "02 04 77"), true);
    Check("text: 'birth date' label, spaced date", Hit("BIRTH DATE: 02 04 1977").Any(h => h.Kind == SensitiveKind.DateOfBirth && h.Text == "02 04 1977"), true);
    Check("text: slashed date still works", Hit("DOB: 02/04/1977").Any(h => h.Kind == SensitiveKind.DateOfBirth && h.Text == "02/04/1977"), true);
    Check("text: 'Driver / ID Number'", Hit("Driver / ID Number: 25 960 454").Any(h => h.Kind == SensitiveKind.DriversLicense && h.Text == "25 960 454"), true);
    Check("text: 'Driver/ID No.'", Hit("Driver/ID No. 25960454").Any(h => h.Kind == SensitiveKind.DriversLicense), true);
    Check("text: \"Driver's ID\"", Hit("Driver's ID 25960454").Any(h => h.Kind == SensitiveKind.DriversLicense), true);
    Check("text: 'ID Number'", Hit("ID Number 12345678").Any(h => h.Kind == SensitiveKind.DriversLicense), true);
    Check("text: 'ID No.'", Hit("ID No. 12345678").Any(h => h.Kind == SensitiveKind.DriversLicense), true);
    Check("text: 'State ID'", Hit("State ID 12345678").Any(h => h.Kind == SensitiveKind.DriversLicense), true);
    Check("text: older 'driver license' wording still works", Hit("Driver License No: D1234567").Any(h => h.Kind == SensitiveKind.DriversLicense), true);
    Check("text: 'ID' alone is not a label", Hit("ID 12345678").Any(h => h.Kind == SensitiveKind.DriversLicense), false);
    Check("text: 'ID Number' with no number after it", Hit("Please bring your ID number tomorrow").Any(h => h.Kind == SensitiveKind.DriversLicense), false);
}

var everythingOn = new FindOptions();
List<WordBox> FormField(string label, string value, double labelWidth = 160, double valueWidth = 230)
{
    var l = label.Split(' ').Select((t, i) => new WordBox(t, 100 + i * (labelWidth / label.Split(' ').Length), 200, labelWidth / label.Split(' ').Length - 8, 20, 0));
    var v = value.Split(' ').Select((t, i) => new WordBox(t, 100 + i * (valueWidth / value.Split(' ').Length), 232, valueWidth / value.Split(' ').Length - 10, 22, 1));
    return l.Concat(v).ToList();
}
// ======================================================================
// Social Security cards, medical record and prescription numbers
// ======================================================================
{
    var ssnOnly = new FindOptions();
    List<(SensitiveKind Kind, string Text)> Hit(string s, FindOptions? o = null) => Found(s, o ?? ssnOnly);
    bool IsSsn(string s) => Hit(s).Any(h => h.Kind == SensitiveKind.Ssn);
    string SsnText(string s) => Hit(s).First(h => h.Kind == SensitiveKind.Ssn).Text;

    // the many ways a card's number comes back from OCR
    foreach (var good in new[]
    {
        "123-45-6789", "123 - 45 - 6789", "123 -45- 6789", "123–45–6789", "123—45—6789", "123−45−6789", "123‐45‐6789",
        "1 2 3 - 4 5 - 6 7 8 9", "l23-45-6789", "I23-45-6789", "O23-45-6789", "123-4S-6789", "123-45-67B9", "123-45-6789 ",
    })
        Check("ssn variant: " + good, IsSsn("Number " + good + " issued"), true);
    Check("ssn: a stray dash before it is left out of the match", SsnText("-123-45-6789"), "123-45-6789");
    Check("ssn: a stray dash after it is left out of the match", SsnText("123-45-6789-"), "123-45-6789");
    Check("ssn: spaced digits are all covered", SsnText("x 1 2 3 - 4 5 - 6 7 8 9 y"), "1 2 3 - 4 5 - 6 7 8 9");

    foreach (var bad in new[] { "SSS-SS-SSSS", "BOB-12-3456", "1-123-45-6789", "123-45-67890", "1123-45-6789", "ABC-DE-FGHI", "OOO-OO-OOOO" })
        Check("ssn variant rejected: " + bad, IsSsn("Number " + bad + " issued"), false);

    // ITINs (9xx with a middle block in the ITIN ranges) are tax IDs too; other 9xx numbers are not
    Check("itin range accepted", IsSsn("987-65-4321"), true);
    Check("itin 70s accepted", IsSsn("912-72-3456"), true);
    Check("9xx outside itin ranges rejected", IsSsn("987-69-4321"), false);
    Check("9xx with spaces only is not trusted", IsSsn("987 65 4321"), false);

    // placeholders are passed over, unless the page talks about Social Security
    Check("placeholder alone is passed over", IsSsn("000-00-0000"), false);
    Check("placeholder under a Social Security heading is found", IsSsn("SOCIAL SECURITY\n000-00-0000"), true);
    Check("placeholder after 'SSN' is found", IsSsn("SSN: 666-12-3456"), true);
    Check("9xx spaced number on a Social Security page is found", IsSsn("Social Security\n912 34 5678"), true);
    Check("heading words may be in any case", IsSsn("social security card 000-12-3456"), true);

    // nine digits with no separators: only next to the words
    Check("bare digits after SSN", IsSsn("SSN 123456789"), true);
    Check("bare digits after 'Social Security Number:'", IsSsn("Social Security Number: 123456789"), true);
    Check("bare digits under a heading", IsSsn("SOCIAL SECURITY\n123456789"), true);
    Check("bare digits with a stray space", IsSsn("SSN: 123 456789"), true);
    Check("bare digits alone are not an SSN", IsSsn("Reference 123456789 attached"), false);
    Check("bare digits far from the words are not an SSN", IsSsn("Social Security " + new string('x', 120) + " 123456789"), false);
    Check("bare nine digits are not an SSN just because of a routing label", IsSsn("Routing 021000021"), false);

    // as the scanner sees it
    var scanText = "Applicants\nSocial Security card number 000-00-0000";
    Check("scan: placeholder with context reported", ComplianceScanner.ScanPageTexts("a.pdf", null, new[] { scanText }, ComplianceProfiles.GeneralPii).Findings.Any(f => f.Kind == SensitiveKind.Ssn), true);

    // a Social Security card laid out as OCR returns it: heading, number, small print, name
    List<WordBox> SsCard(string number, double numberY = 170, int firstLine = 0)
    {
        var w = new List<WordBox>
        {
            new("SOCIAL", 70, 60, 230, 54, firstLine), new("SECURITY", 320, 60, 260, 54, firstLine),
        };
        double x = 90; int line = firstLine + 1;
        foreach (var part in number.Split(' ')) { w.Add(new WordBox(part, x, numberY, 40 + part.Length * 40, 78, line)); x += 60 + part.Length * 40; }
        line++;
        foreach (var (t, tx) in new[] { ("THIS", 90.0), ("NUMBER", 170.0), ("HAS", 300.0), ("BEEN", 370.0), ("ESTABLISHED", 460.0), ("FOR", 680.0) })
            w.Add(new WordBox(t, tx, 300, t.Length * 14, 24, line));
        line++;
        w.Add(new WordBox("ANDREW", 90, 350, 190, 44, line)); w.Add(new WordBox("D", 300, 350, 30, 44, line)); w.Add(new WordBox("ROUT", 350, 350, 120, 44, line));
        return w;
    }
    foreach (var (label, number) in new[]
    {
        ("normal", "123-45-6789"), ("placeholder", "000-00-0000"), ("itin", "987-65-4321"), ("spaced around dashes", "123 - 45 - 6789"),
        ("stray dash", "-123-45-6789"), ("o for zero", "O23-45-6789"), ("spaces", "123 45 6789"), ("no separators", "123456789"),
    })
    {
        var cardRegions = RedactionMapper.FindRegions(0, SsCard(number), everythingOn);
        Check($"social security card ({label}): number marked", cardRegions.Any(r => r.Kind == SensitiveKind.Ssn), true);
        Check($"social security card ({label}): name and small print left alone", cardRegions.Count(r => r.Kind != SensitiveKind.Ssn), 0);
    }
    var cardOnce = RedactionMapper.FindRegions(0, SsCard("123-45-6789"), everythingOn);
    Check("social security card: marked once, not once per method", cardOnce.Count(r => r.Kind == SensitiveKind.Ssn), 1);
    Check("social security card: the box covers the number", cardOnce.First(r => r.Kind == SensitiveKind.Ssn).Width > 300, true);
    var numberFarBelow = SsCard("123-45-6789", numberY: 330);      // number several rows under the heading
    Check("social security card: number not directly under the heading is still found by its shape", RedactionMapper.FindRegions(0, numberFarBelow, everythingOn).Any(r => r.Kind == SensitiveKind.Ssn), true);
    Check("social security card: placeholder not directly under the heading is found because the page says so",
          RedactionMapper.FindRegions(0, SsCard("000-00-0000", numberY: 330), everythingOn).Any(r => r.Kind == SensitiveKind.Ssn), true);

    // form: SSN label over a value in odd forms
    Check("form: SSN label over a number with a stray dash", RedactionMapper.FindRegions(0, FormField("SSN", "-123-45-6789", 40, 170), everythingOn).Any(r => r.Kind == SensitiveKind.Ssn), true);
}

{
    // ---- medical record numbers: the form that the Redact tab missed ----
    var health = new FindOptions { Mrn = true, HealthPlanId = true, RxNumber = true };
    const string header = "Name: Jane Q Public | DOB: 2/4/1977 | MRN: 933278038 | PCP: Roger L Palisoc, DO | Legal Name: Jane Q Public";
    var withHealth = Found(header, health);
    Check("form header: MRN found", withHealth.Any(h => h.Kind == SensitiveKind.Mrn && h.Text == "933278038"), true);
    Check("form header: birth date found", withHealth.Any(h => h.Kind == SensitiveKind.DateOfBirth && h.Text == "2/4/1977"), true);
    Check("MRN is not looked for unless asked (the Redact tab turns it on itself)", Found(header, new FindOptions()).Any(h => h.Kind == SensitiveKind.Mrn), false);
    Check("form header: the label is not marked, only the number", withHealth.First(h => h.Kind == SensitiveKind.Mrn).Text.Contains("MRN"), false);
    Check("MRN in the form's other layouts", Found("Medical Record Number: 00481516", health).Any(h => h.Kind == SensitiveKind.Mrn), true);
    Check("MRN written 'MRN #'", Found("MRN # A-1234567", health).Any(h => h.Kind == SensitiveKind.Mrn), true);
    Check("MRN word with nothing after it", Found("The MRN is required here", health).Any(h => h.Kind == SensitiveKind.Mrn), false);

    // ---- prescription numbers ----
    Check("rx: 'Prescription number'", Found("Prescription number 1006599", health).Any(h => h.Kind == SensitiveKind.RxNumber && h.Text == "1006599"), true);
    Check("rx: 'Rx #'", Found("Rx #: 4455667", health).Any(h => h.Kind == SensitiveKind.RxNumber && h.Text == "4455667"), true);
    Check("rx: 'Rx No.'", Found("Rx No. 99887766", health).Any(h => h.Kind == SensitiveKind.RxNumber), true);
    Check("rx: a dose is not a prescription number", Found("Rx 5 mg twice daily", health).Any(h => h.Kind == SensitiveKind.RxNumber), false);
    Check("rx: 'prescription' with no number word", Found("Prescription details 1006599", health).Any(h => h.Kind == SensitiveKind.RxNumber), false);
    Check("rx: off unless asked", Found("Prescription number 1006599", new FindOptions()).Any(h => h.Kind == SensitiveKind.RxNumber), false);
    Check("rx: shown by its own name", SensitiveKindInfo.DisplayName(SensitiveKind.RxNumber), "Prescription number");
    Check("rx: partly hidden", MatchMasker.Mask(SensitiveKind.RxNumber, "1006599"), "•••6599");
    Check("rx: found under its label on a form", RedactionMapper.FindRegions(0, FormField("Prescription Number", "1006599"), health).Any(r => r.Kind == SensitiveKind.RxNumber), true);
    Check("rx: HIPAA profile looks for it", ComplianceProfiles.Hipaa.Checks.Any(c => c.Kind == SensitiveKind.RxNumber), true);
    Check("rx: HIPAA options turn it on", ComplianceProfiles.Hipaa.BuildOptions().RxNumber, true);
    Check("rx: PCI options leave it off", ComplianceProfiles.Pci.BuildOptions().RxNumber, false);
    var rxScan = ComplianceScanner.ScanPageTexts("meds.pdf", null, new[] { header + "\nPrescription number 1006599" }, ComplianceProfiles.Hipaa);
    Check("rx: reported by a HIPAA scan", rxScan.Findings.Any(f => f.Kind == SensitiveKind.RxNumber && f.MaskedValue == "•••6599"), true);
    Check("MRN: reported by a HIPAA scan", rxScan.Findings.Any(f => f.Kind == SensitiveKind.Mrn), true);
}

// ---------- Encryption at rest ----------
{
    byte[] KeyOf(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();
    var vault = new ReceiptCapture.Core.Security.Vault(new ReceiptCapture.Core.Security.StaticKeyProvider(KeyOf(1)));
    var otherVault = new ReceiptCapture.Core.Security.Vault(new ReceiptCapture.Core.Security.StaticKeyProvider(KeyOf(99)));
    var rng = new Random(7);
    byte[] Random(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }
    bool Throws(Action a) { try { a(); return false; } catch (ReceiptCapture.Core.Security.VaultException) { return true; } }

    foreach (var size in new[] { 0, 1, 100, (1 << 20) - 1, 1 << 20, (1 << 20) + 1, 2 * (1 << 20), 2_600_000 })
    {
        var data = Random(size);
        var sealedData = vault.Encrypt(data);
        Check($"vault round trip, {size} bytes", vault.Decrypt(sealedData).AsSpan().SequenceEqual(data), true);
        Check($"vault output is marked, {size} bytes", ReceiptCapture.Core.Security.Vault.IsEncrypted(sealedData), true);
    }

    var secret = System.Text.Encoding.UTF8.GetBytes("Vendor: Jones & Partners, Total 1,234.56");
    var locked = vault.Encrypt(secret);
    Check("vault hides the text", System.Text.Encoding.UTF8.GetString(locked).Contains("Jones"), false);
    Check("same data locks differently each time", locked.AsSpan().SequenceEqual(vault.Encrypt(secret)), false);
    Check("plain data isn't marked as locked", ReceiptCapture.Core.Security.Vault.IsEncrypted(secret), false);
    Check("wrong key is refused", Throws(() => otherVault.Decrypt(locked)), true);

    var tampered = (byte[])locked.Clone();
    tampered[tampered.Length / 2] ^= 0x01;
    Check("a changed byte is detected", Throws(() => vault.Decrypt(tampered)), true);

    var big = vault.Encrypt(Random(2_600_000));
    var cutAtChunk = big.AsSpan(0, 20 + 2 * ((1 << 20) + 16)).ToArray();   // header + exactly two whole chunks
    Check("a file cut at a chunk boundary is detected", Throws(() => vault.Decrypt(cutAtChunk)), true);
    Check("a file cut mid-chunk is detected", Throws(() => vault.Decrypt(big.AsSpan(0, big.Length - 5).ToArray())), true);
    Check("header only is refused", Throws(() => vault.Decrypt(locked.AsSpan(0, 20).ToArray())), true);
    Check("plain bytes are refused", Throws(() => vault.Decrypt(secret)), true);

    var swapped = (byte[])big.Clone();
    for (int i = 0; i < (1 << 20) + 16; i++)
        (swapped[20 + i], swapped[20 + (1 << 20) + 16 + i]) = (swapped[20 + (1 << 20) + 16 + i], swapped[20 + i]);
    Check("swapped chunks are detected", Throws(() => vault.Decrypt(swapped)), true);

    var vaultRoot = Path.Combine(Path.GetTempPath(), "vault-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vaultRoot);

    // files
    var plainFile = Path.Combine(vaultRoot, "a.bin");
    var bigOriginal = Random(3_000_000);
    File.WriteAllBytes(plainFile, bigOriginal);
    var sealedFile = Path.Combine(vaultRoot, "a.bin" + ReceiptCapture.Core.Security.Vault.SealedSuffix);
    vault.EncryptFile(plainFile, sealedFile);
    Check("sealed file is marked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(sealedFile), true);
    Check("plain file isn't marked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(plainFile), false);
    Check("missing file isn't marked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(Path.Combine(vaultRoot, "nope")), false);
    Check("the original is left alone", File.ReadAllBytes(plainFile).AsSpan().SequenceEqual(bigOriginal), true);
    vault.Verify(sealedFile);
    Check("verify passes on a good file", true, true);
    var tempCopy = vault.DecryptToTempFile(sealedFile, Path.Combine(vaultRoot, "tmp"), "pdf");
    Check("temp copy has the right extension", Path.GetExtension(tempCopy), ".pdf");
    Check("temp copy matches the original", File.ReadAllBytes(tempCopy).AsSpan().SequenceEqual(bigOriginal), true);
    ReceiptCapture.Core.Security.Vault.ClearFolder(Path.Combine(vaultRoot, "tmp"));
    Check("clearing the temp folder removes the copy", File.Exists(tempCopy), false);
    Check("a wrong key can't unlock a file", Throws(() => otherVault.DecryptToTempFile(sealedFile, Path.Combine(vaultRoot, "tmp"), "bin")), true);
    Check("a failed unlock leaves nothing behind", Directory.GetFiles(Path.Combine(vaultRoot, "tmp")).Length, 0);
    Check("sealed names are recognised", ReceiptCapture.Core.Security.Vault.IsSealedPath("x/abc.pdf.AEGIS"), true);
    Check("plain names are not", ReceiptCapture.Core.Security.Vault.IsSealedPath("x/abc.pdf"), false);
    Check("logical extension of a sealed name", ReceiptCapture.Core.Security.Vault.LogicalExtension("abc.pdf.aegis"), ".pdf");
    Check("logical extension of a plain name", ReceiptCapture.Core.Security.Vault.LogicalExtension("abc.jpg"), ".jpg");

    // text files
    var textFile = Path.Combine(vaultRoot, "t.json");
    ReceiptCapture.Core.Security.Vault.WriteText(vault, textFile, "{\"a\":\"héllo wörld\"}");
    Check("text file is locked on disk", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(textFile), true);
    Check("text file reads back", ReceiptCapture.Core.Security.Vault.ReadText(vault, textFile, out var enc1), "{\"a\":\"héllo wörld\"}");
    Check("reading reports it was locked", enc1, true);
    File.WriteAllText(textFile, "{\"old\":true}", new System.Text.UTF8Encoding(true));
    Check("an older plain file (with a BOM) still reads", ReceiptCapture.Core.Security.Vault.ReadText(vault, textFile, out var enc2), "{\"old\":true}");
    Check("reading reports it was plain", enc2, false);
    ReceiptCapture.Core.Security.Vault.WriteText(null, textFile, "plain again");
    Check("no vault writes plain text", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(textFile), false);

    // receipts
    var receiptFile = Path.Combine(vaultRoot, "receipts.json");
    var lockedStore = new JsonReceiptStore(receiptFile, vault);
    lockedStore.Save(new[] { new Receipt { Vendor = "Jones & Partners", Total = 12.5m } });
    Check("receipts file is locked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(receiptFile), true);
    Check("receipts file hides the vendor", File.ReadAllText(receiptFile).Contains("Jones"), false);
    var reloadedReceipts = new JsonReceiptStore(receiptFile, vault).Load();
    Check("receipts read back", reloadedReceipts.Count == 1 && reloadedReceipts[0].Vendor == "Jones & Partners" && reloadedReceipts[0].Total == 12.5m, true);

    new JsonReceiptStore(receiptFile).Save(new[] { new Receipt { Vendor = "Old Plain Vendor" } });
    var upgradeStore = new JsonReceiptStore(receiptFile, vault);
    Check("an older plain receipts file loads", upgradeStore.Load()[0].Vendor, "Old Plain Vendor");
    Check("and is locked by loading it", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(receiptFile), true);
    Check("with no problem reported", upgradeStore.LoadProblem, (string?)null);

    var wrongKeyStore = new JsonReceiptStore(receiptFile, otherVault);
    Check("a wrong key loads nothing", wrongKeyStore.Load().Count, 0);
    Check("and says why", wrongKeyStore.LoadProblem?.Contains("unlocked") == true, true);
    Check("the unreadable file is kept, not deleted", Directory.GetFiles(vaultRoot, "receipts.json.unreadable-*").Length, 1);
    Check("the unreadable file was moved out of the way", File.Exists(receiptFile), false);

    // meetings
    var meetingsRoot = Path.Combine(vaultRoot, "meetings");
    var lockedMeetings = new JsonMeetingStore(meetingsRoot, vault);
    var m1 = Meeting.StartNew(new DateTime(2026, 10, 5, 9, 0, 0), true);
    m1.Status = MeetingStatus.Completed; m1.DurationSeconds = 2; m1.Title = "Smith intake"; m1.Notes = "Client mentioned the diagnosis";
    lockedMeetings.Save(m1);
    Check("meeting.json is locked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(Path.Combine(lockedMeetings.FolderFor(m1), "meeting.json")), true);
    Check("meeting.json hides the notes", File.ReadAllText(Path.Combine(lockedMeetings.FolderFor(m1), "meeting.json")).Contains("diagnosis"), false);

    lockedMeetings.SaveTranscript(m1, new Transcript { Segments = { new TranscriptSegment(0, 2, "Her blood pressure is high", "Dr. Lee") }, Speakers = { "Dr. Lee" } });
    Check("transcript.json is locked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(Path.Combine(lockedMeetings.FolderFor(m1), "transcript.json")), true);
    var tr = lockedMeetings.LoadTranscript(m1);
    Check("locked transcript reads back with its speaker", tr?.Segments[0].Speaker, "Dr. Lee");

    var act = new SpeakerActivity(); act.Add(0.5f, 0f); act.Add(0f, 0.4f);
    lockedMeetings.SaveActivity(m1, act);
    Check("activity.json is locked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(Path.Combine(lockedMeetings.FolderFor(m1), "activity.json")), true);
    Check("activity reads back", lockedMeetings.LoadActivity(m1)?.Count, (int?)2);
    Check("activity is known to exist", lockedMeetings.HasActivity(m1), true);

    var wavBytes = MakeWav(64000, true);
    File.WriteAllBytes(lockedMeetings.AudioPathFor(m1), wavBytes);
    Check("audio exists while plain", lockedMeetings.AudioExists(m1), true);
    Check("sealing locks the recording", lockedMeetings.SealAudio(m1), true);
    Check("the plain recording is gone", File.Exists(lockedMeetings.AudioPathFor(m1)), false);
    Check("the locked recording is there", File.Exists(lockedMeetings.SealedAudioPathFor(m1)), true);
    Check("audio still counts as existing", lockedMeetings.AudioExists(m1), true);
    Check("the locked recording has no wav header", File.ReadAllBytes(lockedMeetings.SealedAudioPathFor(m1)).AsSpan(0, 4).SequenceEqual("RIFF"u8), false);
    Check("sealing twice does nothing", lockedMeetings.SealAudio(m1), false);

    var tempAudio = lockedMeetings.OpenAudioForUse(m1, Path.Combine(vaultRoot, "tmp2"), out var isTemp);
    Check("opening a locked recording makes a temp copy", isTemp, true);
    Check("the temp copy is the original recording", File.ReadAllBytes(tempAudio).AsSpan().SequenceEqual(wavBytes), true);
    Check("the temp copy is a playable wav", WavInfo.GetDuration(tempAudio)?.TotalSeconds, (double?)2.0);
    JsonMeetingStore.ReleaseAudio(tempAudio, isTemp);
    Check("releasing deletes the temp copy", File.Exists(tempAudio), false);

    var exported = Path.Combine(vaultRoot, "exported.wav");
    lockedMeetings.ExportAudio(m1, exported);
    Check("export gives back the recording", File.ReadAllBytes(exported).AsSpan().SequenceEqual(wavBytes), true);

    // an older plain meeting is locked on load, and a recording left plain is found
    var plainMeetings = new JsonMeetingStore(Path.Combine(vaultRoot, "meetings2"));
    var old = Meeting.StartNew(new DateTime(2026, 9, 1, 9, 0, 0), false);
    old.Status = MeetingStatus.Completed; old.Notes = "from an older version";
    plainMeetings.Save(old);
    plainMeetings.SaveTranscript(old, new Transcript { Segments = { new TranscriptSegment(0, 1, "old words") } });
    File.WriteAllBytes(plainMeetings.AudioPathFor(old), wavBytes);
    var migrating = new JsonMeetingStore(Path.Combine(vaultRoot, "meetings2"), vault);
    Check("an older meeting loads", migrating.LoadAll()[0].Notes, "from an older version");
    Check("and its file is now locked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(Path.Combine(migrating.FolderFor(old), "meeting.json")), true);
    Check("an older transcript loads", migrating.LoadTranscript(old)?.Segments[0].Text, "old words");
    Check("and is now locked", ReceiptCapture.Core.Security.Vault.IsEncryptedFile(Path.Combine(migrating.FolderFor(old), "transcript.json")), true);
    Check("an older recording is opened in place", migrating.OpenAudioForUse(old, Path.Combine(vaultRoot, "tmp3"), out var isTemp2) == migrating.AudioPathFor(old) && !isTemp2, true);
    Check("sealing an older recording works", migrating.SealAudio(old) && !File.Exists(migrating.AudioPathFor(old)), true);

    // a crashed recording is still recovered, and can then be locked
    var crashStore = new JsonMeetingStore(Path.Combine(vaultRoot, "meetings3"), vault);
    var crash = Meeting.StartNew(new DateTime(2026, 10, 6, 9, 0, 0), false);
    crashStore.Save(crash);
    File.WriteAllBytes(crashStore.AudioPathFor(crash), MakeWav(160000, false));
    var recovered = crashStore.LoadAll()[0];
    Check("a crashed locked-store recording is recovered", recovered.Status, MeetingStatus.Recovered);
    Check("with its length", recovered.DurationSeconds, 5.0);
    Check("and can be locked afterwards", crashStore.SealAudio(recovered), true);

    // another key: counted, not deleted
    var strangerView = new JsonMeetingStore(meetingsRoot, otherVault);
    Check("meetings locked by another key aren't loaded", strangerView.LoadAll().Count, 0);
    Check("they are counted", strangerView.UnreadableCount, 1);
    Check("and the folder is untouched", Directory.Exists(lockedMeetings.FolderFor(m1)), true);
    Check("the right key still loads it", new JsonMeetingStore(meetingsRoot, vault).LoadAll().Count, 1);
    Check("a transcript can't be read with the wrong key", strangerView.LoadTranscript(m1) == null, true);

    Directory.Delete(vaultRoot, recursive: true);
}

// ---------- Speaker labels ----------
{
    Transcript Fresh() => new()
    {
        Segments =
        {
            new TranscriptSegment(0, 4, "Good morning."),
            new TranscriptSegment(4, 8, "Thanks for coming in."),
            new TranscriptSegment(8, 12, "Happy to be here."),
            new TranscriptSegment(12, 15, "Let's begin."),
        },
    };

    Check("normalize trims and collapses", SpeakerLabels.Normalize("  Dr.   Lee \t"), "Dr. Lee");
    Check("normalize blank is null", SpeakerLabels.Normalize("   "), (string?)null);
    Check("normalize null is null", SpeakerLabels.Normalize(null), (string?)null);
    Check("normalize drops control characters", SpeakerLabels.Normalize("Ann\u0007e"), "Anne");
    Check("normalize caps the length", SpeakerLabels.Normalize(new string('x', 200))!.Length, SpeakerLabels.MaxNameLength);

    var t = Fresh();
    Check("assign changes the chosen lines", SpeakerLabels.Assign(t, new[] { 0, 1 }, "Attorney"), 2);
    Check("assign sets the speaker", t.Segments[1].Speaker, "Attorney");
    Check("other lines stay unlabelled", t.Segments[2].Speaker, (string?)null);
    Check("assign remembers the name", t.Speakers.SequenceEqual(new[] { "Attorney" }), true);
    Check("assigning the same again changes nothing", SpeakerLabels.Assign(t, new[] { 0, 1 }, "Attorney"), 0);
    Check("assign ignores out-of-range lines", SpeakerLabels.Assign(t, new[] { -1, 99 }, "Attorney"), 0);
    Check("assign ignores duplicates", SpeakerLabels.Assign(t, new[] { 2, 2, 2 }, "Client"), 1);
    Check("a different capitalisation reuses the existing name", SpeakerLabels.Assign(t, new[] { 3 }, "  attorney "), 1);
    Check("... with the original spelling", t.Segments[3].Speaker, "Attorney");
    Check("names list has each once", string.Join("|", SpeakerLabels.Names(t)), "Attorney|Client");
    Check("unlabelled count", SpeakerLabels.UnlabeledCount(t), 0);
    Check("a blank name clears the label", SpeakerLabels.Assign(t, new[] { 3 }, ""), 1);
    Check("... leaving it unlabelled", t.Segments[3].Speaker, (string?)null);
    Check("unlabelled count after clearing", SpeakerLabels.UnlabeledCount(t), 1);

    // names used on lines but missing from the saved list still show up
    var sneaky = Fresh();
    sneaky.Segments[0] = sneaky.Segments[0] with { Speaker = "Visitor" };
    Check("names include speakers used on lines", string.Join("|", SpeakerLabels.Names(sneaky)), "Visitor");

    // rename and merge
    var r = Fresh();
    SpeakerLabels.Assign(r, new[] { 0, 2 }, "Cleint");
    SpeakerLabels.Assign(r, new[] { 1 }, "Attorney");
    Check("rename fixes a typo everywhere", SpeakerLabels.Rename(r, "Cleint", "Client"), 2);
    Check("... on the lines", r.Segments[2].Speaker, "Client");
    Check("... and in the list", string.Join("|", SpeakerLabels.Names(r)), "Client|Attorney");
    Check("rename into an existing name merges", SpeakerLabels.Rename(r, "Attorney", "client"), 1);
    Check("... keeping the existing spelling", r.Segments[1].Speaker, "Client");
    Check("... with one name left", string.Join("|", SpeakerLabels.Names(r)), "Client");
    Check("renaming a name that isn't there does nothing", SpeakerLabels.Rename(r, "Nobody", "Somebody"), 0);
    Check("... and adds nothing", string.Join("|", SpeakerLabels.Names(r)), "Client");
    Check("rename to a blank name does nothing", SpeakerLabels.Rename(r, "Client", " "), 0);
    Check("renaming only the capitals works", SpeakerLabels.Rename(r, "Client", "CLIENT") > 0 && r.Segments[0].Speaker == "CLIENT", true);

    var c = Fresh();
    SpeakerLabels.Assign(c, new[] { 0, 1, 2 }, "Dana");
    Check("clear removes a speaker from every line", SpeakerLabels.Clear(c, "dana"), 3);
    Check("... and from the list", SpeakerLabels.Names(c).Count, 0);
    SpeakerLabels.Assign(c, new[] { 0 }, "Dana");
    SpeakerLabels.Clear(c, "Dana", keepName: true);
    Check("clear can keep the name for quick picking", string.Join("|", SpeakerLabels.Names(c)), "Dana");

    // saving and loading
    var storeRoot = Path.Combine(Path.GetTempPath(), "speaker-test-" + Guid.NewGuid().ToString("N"));
    var speakerStore = new JsonMeetingStore(storeRoot);
    var sm = Meeting.StartNew(new DateTime(2026, 10, 7, 9, 0, 0), false);
    speakerStore.Save(sm);
    var labelled = Fresh();
    SpeakerLabels.Assign(labelled, new[] { 0, 1 }, "Dr. Lee");
    speakerStore.SaveTranscript(sm, labelled);
    var back = speakerStore.LoadTranscript(sm)!;
    Check("speakers survive a save", back.Segments[0].Speaker, "Dr. Lee");
    Check("the list survives a save", string.Join("|", back.Speakers), "Dr. Lee");
    Check("unlabelled stay unlabelled", back.Segments[3].Speaker, (string?)null);

    // a transcript saved before speakers existed
    File.WriteAllText(Path.Combine(speakerStore.FolderFor(sm), "transcript.json"),
        "{\"Segments\":[{\"StartSeconds\":1,\"EndSeconds\":2,\"Text\":\"older words\"}],\"Model\":\"m\",\"CreatedAt\":\"2026-10-01T09:00:00\"}");
    var older = speakerStore.LoadTranscript(sm);
    Check("an older transcript still loads", older?.Segments[0].Text, "older words");
    Check("with no speaker", older?.Segments[0].Speaker, (string?)null);
    Check("and an empty speaker list", older?.Speakers.Count, (int?)0);
    Directory.Delete(storeRoot, recursive: true);

    // text output
    var f = Fresh();
    SpeakerLabels.Assign(f, new[] { 0 }, "Dana");
    SpeakerLabels.Assign(f, new[] { 1, 2 }, "Sam");
    Check("the text names the speaker", TranscriptFormatter.ToText(f.Segments).Split(Environment.NewLine)[0], "[0:00] Dana: Good morning.");
    Check("neighbouring lines by one speaker become one paragraph", TranscriptFormatter.ToText(f.Segments).Split(Environment.NewLine)[1], "[0:04] Sam: Thanks for coming in. Happy to be here.");
    Check("an unlabelled line keeps its own time only", TranscriptFormatter.ToText(f.Segments).Split(Environment.NewLine)[2], "[0:12] Let's begin.");
    Check("three paragraphs in all", TranscriptFormatter.ToTurns(f.Segments).Count, 3);
    Check("unlabelled neighbours are not merged", TranscriptFormatter.ToTurns(Fresh().Segments).Count, 4);
    var exportTest = Meeting.StartNew(new DateTime(2026, 10, 7, 9, 0, 0), false);
    exportTest.Title = "Intake";
    Check("the export lists the speakers", TranscriptFormatter.ToExportText(exportTest, f).Contains("Speakers Dana, Sam"), true);
    Check("the export has no speaker line when nobody is named", TranscriptFormatter.ToExportText(exportTest, Fresh()).Contains("Speakers"), false);
}

// ---------- Suggested speakers from the recording ----------
{
    var activity = new SpeakerActivity();
    // seconds 0-4: only the microphone; 4-8: only computer sound; 8-12: both, mic far louder; 12-16: both, similar;
    // 16-20: both, computer far louder; 20-24: silence
    void Windows(int count, float mic, float computer) { for (int i = 0; i < count; i++) activity.Add(mic, computer); }
    Windows(8, 0.30f, 0f);
    Windows(8, 0f, 0.30f);
    Windows(8, 0.40f, 0.04f);
    Windows(8, 0.20f, 0.20f);
    Windows(8, 0.04f, 0.40f);
    Windows(8, 0f, 0f);

    var transcript = new Transcript
    {
        Segments =
        {
            new TranscriptSegment(0.2, 3.8, "mic only"),
            new TranscriptSegment(4.2, 7.8, "computer only"),
            new TranscriptSegment(8, 12, "both, mic louder"),
            new TranscriptSegment(12, 16, "both, same"),
            new TranscriptSegment(16, 20, "both, computer louder"),
            new TranscriptSegment(20, 24, "silence"),
            new TranscriptSegment(500, 510, "past the end of the activity"),
        },
    };

    var result = SpeakerActivityLabeler.Apply(transcript, activity);
    Check("mic only is You", transcript.Segments[0].Speaker, SpeakerActivityLabeler.YouLabel);
    Check("computer only is Other participants", transcript.Segments[1].Speaker, SpeakerActivityLabeler.OthersLabel);
    Check("both with the mic far louder is You", transcript.Segments[2].Speaker, SpeakerActivityLabeler.YouLabel);
    Check("both about equal is left blank", transcript.Segments[3].Speaker, (string?)null);
    Check("both with the computer far louder is Other participants", transcript.Segments[4].Speaker, SpeakerActivityLabeler.OthersLabel);
    Check("silence is left blank", transcript.Segments[5].Speaker, (string?)null);
    Check("a line beyond the recorded activity is left blank", transcript.Segments[6].Speaker, (string?)null);
    Check("labelled count", result.Labeled, 4);
    Check("unclear count", result.Unclear, 3);
    Check("the two names are remembered", string.Join("|", transcript.Speakers), "You|Other participants");

    // hand-made labels are respected unless asked otherwise
    var guarded = new Transcript { Segments = { new TranscriptSegment(0.2, 3.8, "mic only", "Dana") } };
    Check("existing labels are kept", SpeakerActivityLabeler.Apply(guarded, activity).Labeled, 0);
    Check("... and unchanged", guarded.Segments[0].Speaker, "Dana");
    Check("they can be overwritten on request", SpeakerActivityLabeler.Apply(guarded, activity, overwrite: true).Labeled, 1);
    Check("no activity means no labels", SpeakerActivityLabeler.Apply(new Transcript { Segments = { new TranscriptSegment(0, 1, "x") } }, new SpeakerActivity()).Labeled, 0);

    Check("quantize silence", SpeakerActivity.Quantize(0f), (byte)0);
    Check("quantize full scale", SpeakerActivity.Quantize(1f), (byte)255);
    Check("quantize clamps above one", SpeakerActivity.Quantize(5f), (byte)255);
    Check("a level survives quantizing closely", Math.Abs(SpeakerActivity.Level(SpeakerActivity.Quantize(0.25f)) - 0.25) < 0.01, true);
}

// ---------- Word export ----------
{
    var meeting = Meeting.StartNew(new DateTime(2026, 10, 8, 14, 30, 0), false);
    meeting.Title = "Smith & Sons <intake>";
    meeting.DurationSeconds = 725;
    meeting.Notes = "Bring the \"signed\" form\nSecond line";

    var transcript = new Transcript
    {
        Segments =
        {
            new TranscriptSegment(0, 4, "Good morning.", "Dana"),
            new TranscriptSegment(4, 8, "Let's start with the budget.", "Dana"),
            new TranscriptSegment(8, 12, "It's 5 < 6 & more\u0007.", "Sam"),
            new TranscriptSegment(12, 15, "Unlabelled line."),
        },
        Speakers = { "Dana", "Sam" },
    };

    var invariant = System.Globalization.CultureInfo.InvariantCulture;
    var bytes = TranscriptDocx.Build(meeting, transcript, includeNotes: false, invariant);
    using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
    string Part(string name) { using var s = new StreamReader(zip.GetEntry(name)!.Open()); return s.ReadToEnd(); }

    Check("the file is a zip with the Word parts", new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels" }.All(n => zip.GetEntry(n) != null), true);
    Check("content types come first", zip.Entries[0].FullName, "[Content_Types].xml");
    Check("no personal properties are written", zip.Entries.Any(e => e.FullName.StartsWith("docProps")), false);

    foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels" })
    {
        bool wellFormed = true;
        try { new System.Xml.XmlDocument().LoadXml(Part(name)); } catch (System.Xml.XmlException) { wellFormed = false; }
        Check($"{name} is well-formed XML", wellFormed, true);
    }

    var xml = new System.Xml.XmlDocument();
    xml.LoadXml(Part("word/document.xml"));
    var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
    ns.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
    var paragraphs = xml.SelectNodes("//w:body/w:p", ns)!.Cast<System.Xml.XmlNode>().Select(p => p.InnerText).ToList();

    Check("the title is the meeting title, special characters intact", paragraphs[0], "Smith & Sons <intake>");
    Check("the date line", paragraphs[1], "Recorded: " + meeting.StartedAt.ToString("f", invariant));
    Check("the length line", paragraphs[2], "Length: 12:05");
    Check("the speakers line", paragraphs[3], "Speakers: Dana, Sam");
    Check("notes are left out when not asked for", paragraphs.Any(p => p.Contains("signed")), false);
    Check("a Transcript heading", paragraphs.Contains("Transcript"), true);
    Check("one speaker's neighbouring lines share a paragraph", paragraphs.Contains("[0:00] Dana: Good morning. Let's start with the budget."), true);
    Check("stray control characters are dropped and symbols escaped", paragraphs.Contains("[0:08] Sam: It's 5 < 6 & more."), true);
    Check("an unlabelled line has only its time", paragraphs.Contains("[0:12] Unlabelled line."), true);
    Check("the last paragraph warns about mistakes", paragraphs[^1], TranscriptDocx.Disclaimer);
    Check("the raw document has no unescaped ampersand", Part("word/document.xml").Contains("Smith & Sons"), false);
    Check("speaker names are in bold runs", Part("word/document.xml").Contains("<w:b/></w:rPr><w:t xml:space=\"preserve\">Dana: </w:t>"), true);

    var withNotes = TranscriptDocx.Build(meeting, transcript, includeNotes: true, invariant);
    using var zip2 = new System.IO.Compression.ZipArchive(new MemoryStream(withNotes), System.IO.Compression.ZipArchiveMode.Read);
    var xml2 = new System.Xml.XmlDocument();
    using (var s = new StreamReader(zip2.GetEntry("word/document.xml")!.Open())) xml2.LoadXml(s.ReadToEnd());
    var paragraphs2 = xml2.SelectNodes("//w:body/w:p", ns)!.Cast<System.Xml.XmlNode>().Select(p => p.InnerText).ToList();
    Check("notes are included when asked for", paragraphs2.Contains("Bring the \"signed\" form") && paragraphs2.Contains("Second line"), true);
    Check("under a Notes heading", paragraphs2.Contains("Notes"), true);

    var blankNotes = Meeting.StartNew(new DateTime(2026, 10, 8, 14, 30, 0), false);
    blankNotes.Title = "  ";
    var bareBytes = TranscriptDocx.Build(blankNotes, new Transcript { Segments = { new TranscriptSegment(0, 1, "Hi") } }, includeNotes: true, invariant);
    using var zip3 = new System.IO.Compression.ZipArchive(new MemoryStream(bareBytes), System.IO.Compression.ZipArchiveMode.Read);
    string bare; using (var s = new StreamReader(zip3.GetEntry("word/document.xml")!.Open())) bare = s.ReadToEnd();
    Check("a blank title gets a stand-in", bare.Contains("Meeting transcript"), true);
    Check("no Notes heading when there are no notes", bare.Contains(">Notes<"), false);
    Check("no Speakers line when nobody is named", bare.Contains("Speakers:"), false);

    var docxPath = Path.Combine(Path.GetTempPath(), "transcript-test-" + Guid.NewGuid().ToString("N") + ".docx");
    TranscriptDocx.Write(docxPath, meeting, transcript, includeNotes: false, invariant);
    Check("writing to a path makes the file", File.Exists(docxPath) && new FileInfo(docxPath).Length > 500, true);
    Check("... without leaving a temp file", File.Exists(docxPath + ".tmp"), false);
    File.Delete(docxPath);
    Check("Escape drops control characters", TranscriptDocx.Escape("a\u0000b\u000bc\td\ne"), "abc\td\ne");
}

// ---------- Auto-update: versions ----------
{
    Check("a plain version parses", AppVersion.TryParse("0.2.0", out var v1) && v1 == new Version(0, 2, 0, 0), true);
    Check("a leading v is allowed", AppVersion.TryParse("v1.4.2", out var v2) && v2 == new Version(1, 4, 2, 0), true);
    Check("four parts parse", AppVersion.TryParse("1.2.3.4", out var v3) && v3 == new Version(1, 2, 3, 4), true);
    Check("two parts parse", AppVersion.TryParse("2.0", out var v4) && v4 == new Version(2, 0, 0, 0), true);
    foreach (var bad in new[] { "", "  ", "1", "1.2.3.4.5", "1.2.x", "1.2.0-beta", "1..2", "-1.2", "1.2.99999999", "v", "1.2 .3" })
        Check($"'{bad}' is rejected", AppVersion.TryParse(bad, out _), false);
    Check("a higher version is newer", AppVersion.IsNewer("0.2.0", "0.1.0"), true);
    Check("the same version is not newer", AppVersion.IsNewer("0.1.0", "0.1.0"), false);
    Check("1.2 and 1.2.0 are the same", AppVersion.IsNewer("1.2.0", "1.2"), false);
    Check("an older version is not newer", AppVersion.IsNewer("0.1.0", "0.2.0"), false);
    Check("10 is higher than 9 (numbers, not text)", AppVersion.IsNewer("0.10.0", "0.9.0"), true);
    Check("an unreadable version is never newer", AppVersion.IsNewer("banana", "0.1.0"), false);
}

// ---------- Auto-update: signing and manifests ----------
{
    var (publicKey, privateKey) = UpdateSigning.GenerateKeyPair();
    var (otherPublic, otherPrivate) = UpdateSigning.GenerateKeyPair();
    var goodHash = new string('a', 64);

    UpdateManifest Good() => new("0.2.0", "https://updates.example.com/AegisDocs-Setup-0.2.0.exe", goodHash, 123456, "Faster scans.", false, new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    bool Fails(Action a) { try { a(); return false; } catch (UpdateException) { return true; } }

    var data = new byte[] { 1, 2, 3, 4, 5 };
    var sig = UpdateSigning.Sign(data, privateKey);
    Check("a signature checks out", UpdateSigning.Verify(data, sig, publicKey), true);
    Check("a changed message fails", UpdateSigning.Verify(new byte[] { 1, 2, 3, 4, 6 }, sig, publicKey), false);
    Check("another key fails", UpdateSigning.Verify(data, sig, otherPublic), false);
    Check("a damaged signature fails", UpdateSigning.Verify(data, sig.Take(sig.Length - 1).ToArray(), publicKey), false);
    Check("a junk public key fails quietly", UpdateSigning.Verify(data, sig, "not a key"), false);
    Check("an empty public key fails quietly", UpdateSigning.Verify(data, sig, ""), false);
    Check("signatures are 64 bytes", sig.Length, 64);

    var file = SignedManifest.Create(Good(), privateKey);
    var opened = SignedManifest.Open(file, publicKey);
    Check("a signed manifest opens", opened.Version, "0.2.0");
    Check("with its address", opened.InstallerUrl, Good().InstallerUrl);
    Check("with its fingerprint", opened.Sha256, goodHash);
    Check("with its size", opened.SizeBytes, 123456L);
    Check("with its notes", opened.Notes, "Faster scans.");
    Check("with its date", opened.ReleasedAt, (DateTimeOffset?)new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    Check("another key's manifest is refused", Fails(() => SignedManifest.Open(file, otherPublic)), true);
    Check("no public key set is refused", Fails(() => SignedManifest.Open(file, "")), true);
    Check("signed by the wrong key is refused", Fails(() => SignedManifest.Open(SignedManifest.Create(Good(), otherPrivate), publicKey)), true);

    // tamper with the signed payload: change the download address
    var envelope = System.Text.Json.JsonDocument.Parse(file).RootElement;
    var payloadBytes = Convert.FromBase64String(envelope.GetProperty("payload").GetString()!);
    var evil = System.Text.Encoding.UTF8.GetString(payloadBytes).Replace("updates.example.com", "evil.example.com");
    var tampered = "{\"payload\":\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(evil)) + "\",\"signature\":\"" + envelope.GetProperty("signature").GetString() + "\"}";
    Check("a changed download address is refused", Fails(() => SignedManifest.Open(tampered, publicKey)), true);

    Check("garbage is refused", Fails(() => SignedManifest.Open("hello", publicKey)), true);
    Check("an empty file is refused", Fails(() => SignedManifest.Open("", publicKey)), true);
    Check("a missing signature is refused", Fails(() => SignedManifest.Open("{\"payload\":\"AAAA\"}", publicKey)), true);
    Check("bad base64 is refused", Fails(() => SignedManifest.Open("{\"payload\":\"!!!\",\"signature\":\"!!!\"}", publicKey)), true);
    Check("an unsigned manifest is refused", Fails(() => SignedManifest.Open(System.Text.Json.JsonSerializer.Serialize(Good()), publicKey)), true);

    // validation: nothing unsafe gets signed, and nothing unsafe is accepted
    Check("http addresses are refused", Fails(() => SignedManifest.Create(Good() with { InstallerUrl = "http://updates.example.com/a.exe" }, privateKey)), true);
    Check("file addresses are refused", Fails(() => SignedManifest.Create(Good() with { InstallerUrl = "file:///C:/a.exe" }, privateKey)), true);
    Check("relative addresses are refused", Fails(() => SignedManifest.Create(Good() with { InstallerUrl = "/a.exe" }, privateKey)), true);
    Check("a short fingerprint is refused", Fails(() => SignedManifest.Create(Good() with { Sha256 = "abc" }, privateKey)), true);
    Check("a non-hex fingerprint is refused", Fails(() => SignedManifest.Create(Good() with { Sha256 = new string('z', 64) }, privateKey)), true);
    Check("a zero size is refused", Fails(() => SignedManifest.Create(Good() with { SizeBytes = 0 }, privateKey)), true);
    Check("a huge size is refused", Fails(() => SignedManifest.Create(Good() with { SizeBytes = 3L * 1024 * 1024 * 1024 }, privateKey)), true);
    Check("a bad version is refused", Fails(() => SignedManifest.Create(Good() with { Version = "latest" }, privateKey)), true);
    Check("an uppercase fingerprint is lowercased", SignedManifest.Open(SignedManifest.Create(Good() with { Sha256 = new string('A', 64) }, privateKey), publicKey).Sha256, new string('a', 64));
    Check("blank notes become none", SignedManifest.Open(SignedManifest.Create(Good() with { Notes = "   " }, privateKey), publicKey).Notes, (string?)null);
    Check("very long notes are cut", SignedManifest.Open(SignedManifest.Create(Good() with { Notes = new string('x', 9000) }, privateKey), publicKey).Notes!.Length, UpdateManifest.MaxNotesLength);
    Check("the installer file name comes from the version", Good().InstallerFileName, "AegisDocs-Setup-0.2.0.0.exe");

    // ---------- checking for an update ----------
    var key = publicKey;
    async Task<UpdateCheckResult> CheckWith(Func<HttpRequestMessage, HttpResponseMessage> answer, string current = "0.1.0", string url = "https://updates.example.com/latest.json", string? pub = null)
    {
        using var http = new HttpClient(new FakeHandler(answer));
        return await UpdateChecker.CheckAsync(http, url, pub ?? key, current);
    }

    var available = await CheckWith(_ => FakeHandler.Ok(file));
    Check("a newer version is available", available.Status, UpdateCheckStatus.Available);
    Check("and carries the manifest", available.Manifest?.Version, "0.2.0");
    Check("the same version is up to date", (await CheckWith(_ => FakeHandler.Ok(file), "0.2.0")).Status, UpdateCheckStatus.UpToDate);
    Check("a newer installed version is up to date", (await CheckWith(_ => FakeHandler.Ok(file), "0.3.0")).Status, UpdateCheckStatus.UpToDate);
    Check("an old manifest can't downgrade", (await CheckWith(_ => FakeHandler.Ok(file), "1.0.0")).Manifest?.Version, "0.2.0");

    var notFound = await CheckWith(_ => FakeHandler.Status(System.Net.HttpStatusCode.NotFound));
    Check("a 404 is a failure", notFound.Status, UpdateCheckStatus.Failed);
    Check("and says what the server answered", notFound.Error!.Contains("404"), true);
    Check("a server error is a failure", (await CheckWith(_ => FakeHandler.Status(System.Net.HttpStatusCode.InternalServerError))).Status, UpdateCheckStatus.Failed);
    Check("a bad signature is a failure", (await CheckWith(_ => FakeHandler.Ok(file), pub: otherPublic)).Status, UpdateCheckStatus.Failed);
    Check("garbage is a failure", (await CheckWith(_ => FakeHandler.Ok("<html>captive portal</html>"))).Status, UpdateCheckStatus.Failed);
    Check("a network error is a failure", (await CheckWith(_ => throw new HttpRequestException("down"))).Error, "The update server couldn't be reached.");
    Check("an oversize answer is refused", (await CheckWith(_ => FakeHandler.Ok(new byte[UpdateChecker.MaxManifestBytes + 1]))).Status, UpdateCheckStatus.Failed);
    Check("an http address is refused without asking", (await CheckWith(_ => FakeHandler.Ok(file), url: "http://updates.example.com/latest.json")).Status, UpdateCheckStatus.Failed);
    Check("no address is refused", (await CheckWith(_ => FakeHandler.Ok(file), url: "")).Error, "Updates aren't set up in this build.");
    Check("an unreadable own version is refused", (await CheckWith(_ => FakeHandler.Ok(file), current: "dev")).Status, UpdateCheckStatus.Failed);

    var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    bool threw = false;
    try { using var http = new HttpClient(new FakeHandler(_ => FakeHandler.Ok(file))); await UpdateChecker.CheckAsync(http, "https://updates.example.com/latest.json", key, "0.1.0", cancelled.Token); }
    catch (OperationCanceledException) { threw = true; }
    Check("cancelling is passed on, not reported as a failure", threw, true);

    // ---------- downloading ----------
    var folder = Path.Combine(Path.GetTempPath(), "update-test-" + Guid.NewGuid().ToString("N"));
    var installer = new byte[700_000]; new Random(3).NextBytes(installer);
    string HashOf(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
    UpdateManifest For(byte[] b) => Good() with { Sha256 = HashOf(b), SizeBytes = b.Length };

    var seen = new List<double>();
    var requests = new FakeHandler(_ => FakeHandler.Ok(installer));
    using (var http = new HttpClient(requests))
    {
        var savedPath = await UpdateChecker.DownloadAsync(http, For(installer), folder, new Progress<double>(seen.Add));
        await Task.Delay(50);
        Check("the download is saved under the safe name", Path.GetFileName(savedPath), "AegisDocs-Setup-0.2.0.0.exe");
        Check("with the right contents", File.ReadAllBytes(savedPath).AsSpan().SequenceEqual(installer), true);
        Check("progress reaches 100%", seen.Count > 0 && Math.Abs(seen.Max() - 1.0) < 1e-9, true);
        Check("progress never goes backwards", seen.Zip(seen.Skip(1), (a, b) => b >= a).All(x => x), true);
        Check("no partial file is left", File.Exists(savedPath + ".part"), false);

        var again = await UpdateChecker.DownloadAsync(http, For(installer), folder);
        Check("a complete matching download is reused", requests.Requests.Count, 1);
        Check("and returned", again, savedPath);
    }

    using (var http = new HttpClient(new FakeHandler(_ => FakeHandler.Ok(installer.Select(b => (byte)(b ^ 1)).ToArray()))))
    {
        var other = Path.Combine(folder, "wrong-hash");
        Check("a wrong fingerprint is refused", await ThrowsUpdate(() => UpdateChecker.DownloadAsync(http, For(installer), other)), true);
        Check("nothing is kept after a wrong fingerprint", Directory.GetFiles(other).Length, 0);
    }

    using (var http = new HttpClient(new FakeHandler(_ => FakeHandler.Ok(installer.Concat(new byte[10]).ToArray()))))
    {
        var other = Path.Combine(folder, "too-big");
        Check("a download bigger than promised is refused", await ThrowsUpdate(() => UpdateChecker.DownloadAsync(http, For(installer), other)), true);
        Check("nothing is kept after a download that was too big", Directory.GetFiles(other).Length, 0);
    }

    using (var http = new HttpClient(new FakeHandler(_ => FakeHandler.Ok(installer.Take(installer.Length - 1).ToArray()))))
    {
        var other = Path.Combine(folder, "too-small");
        Check("a download smaller than promised is refused", await ThrowsUpdate(() => UpdateChecker.DownloadAsync(http, For(installer), other)), true);
        Check("nothing is kept after a short download", Directory.GetFiles(other).Length, 0);
    }

    using (var http = new HttpClient(new FakeHandler(_ => FakeHandler.Status(System.Net.HttpStatusCode.Forbidden))))
        Check("a refused download is reported", await ThrowsUpdate(() => UpdateChecker.DownloadAsync(http, For(installer), Path.Combine(folder, "refused"))), true);

    using (var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("down"))))
        Check("a network failure is reported", await ThrowsUpdate(() => UpdateChecker.DownloadAsync(http, For(installer), Path.Combine(folder, "offline"))), true);

    // a damaged earlier download is replaced, not trusted
    var repair = Path.Combine(folder, "repair");
    Directory.CreateDirectory(repair);
    File.WriteAllBytes(Path.Combine(repair, "AegisDocs-Setup-0.2.0.0.exe"), new byte[installer.Length]);
    using (var http = new HttpClient(new FakeHandler(_ => FakeHandler.Ok(installer))))
    {
        var fixedPath = await UpdateChecker.DownloadAsync(http, For(installer), repair);
        Check("a damaged earlier download is replaced", File.ReadAllBytes(fixedPath).AsSpan().SequenceEqual(installer), true);
    }

    // an unsafe manifest never reaches the network
    var calls = new FakeHandler(_ => FakeHandler.Ok(installer));
    using (var http = new HttpClient(calls))
    {
        Check("an unsafe manifest is refused before downloading", await ThrowsUpdate(() => UpdateChecker.DownloadAsync(http, For(installer) with { InstallerUrl = "http://x/y.exe" }, Path.Combine(folder, "unsafe"))), true);
        Check("and nothing was requested", calls.Requests.Count, 0);
    }

    Directory.Delete(folder, recursive: true);

    async Task<bool> ThrowsUpdate(Func<Task> action)
    {
        try { await action(); return false; }
        catch (UpdateException) { return true; }
    }
}

// ---------- Auto-update: settings ----------
{
    var settingsPath = Path.Combine(Path.GetTempPath(), "upd-settings-" + Guid.NewGuid().ToString("N"), "updates.json");
    var fresh = UpdateSettings.Load(settingsPath);
    Check("settings default to checking automatically", fresh.AutoCheck, true);
    Check("with no previous check", fresh.LastCheck, (DateTimeOffset?)null);
    Check("a first check is due", fresh.IsCheckDue(DateTimeOffset.Now, TimeSpan.FromHours(24)), true);

    var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    fresh.AutoCheck = false; fresh.LastCheck = now; fresh.SkippedVersion = "0.2.0";
    fresh.Save(settingsPath);
    var back = UpdateSettings.Load(settingsPath);
    Check("settings round trip: auto check", back.AutoCheck, false);
    Check("settings round trip: last check", back.LastCheck, (DateTimeOffset?)now);
    Check("settings round trip: skipped version", back.SkippedVersion, "0.2.0");
    Check("a recent check isn't due", back.IsCheckDue(now.AddHours(5), TimeSpan.FromHours(24)), false);
    Check("an old check is due", back.IsCheckDue(now.AddHours(25), TimeSpan.FromHours(24)), true);
    Check("a check dated in the future is treated as due", back.IsCheckDue(now.AddDays(-2), TimeSpan.FromHours(24)), true);

    File.WriteAllText(settingsPath, "{ not json");
    Check("a damaged settings file falls back to the defaults", UpdateSettings.Load(settingsPath).AutoCheck, true);
    Directory.Delete(Path.GetDirectoryName(settingsPath)!, recursive: true);
}

// >>> shredder
// ---------- Secure file shredder ----------
{
    var work = Path.Combine(Path.GetTempPath(), "shred-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);

    // Overwriting keeps the size but replaces the content.
    var big = Path.Combine(work, "big.bin");
    var origBytes = new byte[3 * 1024 * 1024 + 123];
    for (int i = 0; i < origBytes.Length; i++) origBytes[i] = (byte)(i * 31 + 7);
    File.WriteAllBytes(big, origBytes);
    ReceiptCapture.Core.Security.FileShredder.OverwriteInPlace(big, 2);
    var after = File.ReadAllBytes(big);
    Check("overwrite keeps the file size", after.Length, origBytes.Length);
    int same = 0;
    for (int i = 0; i < after.Length; i++) if (after[i] == origBytes[i]) same++;
    Check("overwrite replaces (nearly) every byte", same < origBytes.Length / 50, true);
    Check("overwrite leaves the file in place", File.Exists(big), true);

    // A normal file, counted by bytes overwritten.
    var doc = Path.Combine(work, "secret.txt");
    File.WriteAllText(doc, "client SSN 123-45-6789");
    var one = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { doc }, new ReceiptCapture.Core.Security.ShredOptions { Passes = 3 });
    Check("a shredded file is gone", File.Exists(doc), false);
    Check("it is counted", one.FilesShredded, 1);
    Check("bytes overwritten = size x passes", one.BytesOverwritten, 22L * 3);
    Check("no failures", one.Failures.Count, 0);
    Check("the run counts as succeeded", one.Succeeded, true);
    Check("nothing is left behind in the folder under any name", Directory.GetFileSystemEntries(work).Length, 1);   // only big.bin

    // Read-only and empty files.
    var locked = Path.Combine(work, "readonly.txt");
    File.WriteAllText(locked, "x");
    File.SetAttributes(locked, FileAttributes.ReadOnly);
    var empty = Path.Combine(work, "empty.txt");
    File.WriteAllBytes(empty, Array.Empty<byte>());
    var both = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { locked, empty });
    Check("a read-only file is shredded", File.Exists(locked), false);
    Check("an empty file is shredded", File.Exists(empty), false);
    Check("both counted", both.FilesShredded, 2);

    // A whole folder tree.
    var tree = Path.Combine(work, "case");
    Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
    File.WriteAllText(Path.Combine(tree, "one.txt"), "1");
    File.WriteAllText(Path.Combine(tree, "a", "two.txt"), "22");
    File.WriteAllText(Path.Combine(tree, "a", "b", "three.txt"), "333");
    var folderRun = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { tree });
    Check("a folder is removed with everything inside", Directory.Exists(tree), false);
    Check("its files are counted", folderRun.FilesShredded, 3);
    Check("its folders are counted", folderRun.FoldersRemoved, 3);

    // Links are removed but what they point at is not touched.
    var targetFolder = Path.Combine(work, "precious");
    Directory.CreateDirectory(targetFolder);
    var precious = Path.Combine(targetFolder, "keep.txt");
    File.WriteAllText(precious, "keep me");
    var link = Path.Combine(work, "link-to-keep.txt");
    bool linked = false;
    try { File.CreateSymbolicLink(link, precious); linked = true; } catch (Exception) { /* not allowed on this PC: skip */ }
    if (linked)
    {
        ReceiptCapture.Core.Security.FileShredder.Shred(new[] { link });
        Check("a file link is removed", File.Exists(link) || new FileInfo(link).Exists, false);
        Check("what the link pointed at is untouched", File.ReadAllText(precious), "keep me");
    }
    var dirLink = Path.Combine(work, "link-to-folder");
    bool dirLinked = false;
    try { Directory.CreateSymbolicLink(dirLink, targetFolder); dirLinked = true; } catch (Exception) { }
    if (dirLinked)
    {
        var linkRun = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { dirLink });
        Check("a folder link is removed", Directory.Exists(dirLink), false);
        Check("the folder it pointed at is untouched", File.ReadAllText(precious), "keep me");
        Check("and nothing inside it was shredded", linkRun.FilesShredded, 0);
    }

    // A folder that contains a link must not shred the link's target either.
    var holder = Path.Combine(work, "holder");
    Directory.CreateDirectory(holder);
    File.WriteAllText(Path.Combine(holder, "own.txt"), "own");
    bool inner = false;
    try { Directory.CreateSymbolicLink(Path.Combine(holder, "inner-link"), targetFolder); inner = true; } catch (Exception) { }
    if (inner)
    {
        ReceiptCapture.Core.Security.FileShredder.Shred(new[] { holder });
        Check("a folder holding a link is removed", Directory.Exists(holder), false);
        Check("the link's target survives", File.ReadAllText(precious), "keep me");
    }

    // Problems are reported, not thrown.
    var missing = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { Path.Combine(work, "nope.txt") });
    Check("a missing path is a failure", missing.Failures.Count, 1);
    Check("it says it doesn't exist", missing.Failures[0].Reason.Contains("doesn't exist"), true);

    var stuck = Path.Combine(work, "stuck.txt");
    File.WriteAllText(stuck, "in use");
    using (var hold = new FileStream(stuck, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var stuckRun = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { stuck });
        Check("a file in use is a failure, not a crash", stuckRun.Failures.Count, 1);
        Check("and it is not counted as shredded", stuckRun.FilesShredded, 0);
    }
    Check("the file in use is still there", File.Exists(stuck), true);

    // Stopping happens between files; nothing is half done.
    var stopA = Path.Combine(work, "stopA.txt");
    File.WriteAllText(stopA, "keep");
    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        var stoppedRun = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { stopA }, cancel: cts.Token);
        Check("a stopped run says so", stoppedRun.Stopped, true);
        Check("and shredded nothing", stoppedRun.FilesShredded, 0);
        Check("a file not reached is untouched", File.ReadAllText(stopA), "keep");
    }

    // The number of passes is checked.
    bool badPasses = false;
    try { ReceiptCapture.Core.Security.FileShredder.Shred(new[] { stopA }, new ReceiptCapture.Core.Security.ShredOptions { Passes = 0 }); }
    catch (ArgumentOutOfRangeException) { badPasses = true; }
    Check("zero passes is refused", badPasses, true);
    Check("and the file is untouched", File.ReadAllText(stopA), "keep");

    // The guard.
    var defaultGuard = ReceiptCapture.Core.Security.ShredGuard.Default;
    var rootPath = Path.GetPathRoot(work)!;
    Check("a drive root is refused", defaultGuard.Check(rootPath) != null, true);
    var guard = new ReceiptCapture.Core.Security.ShredGuard(
        new[] { Path.Combine(work, "system") },
        new[] { Path.Combine(work, "home") });
    Check("inside a protected tree is refused", guard.Check(Path.Combine(work, "system", "x", "y.dll")) != null, true);
    Check("the protected tree itself is refused", guard.Check(Path.Combine(work, "system")) != null, true);
    Check("a folder holding a protected tree is refused", guard.Check(work) != null, true);
    Check("a main folder itself is refused", guard.Check(Path.Combine(work, "home")) != null, true);
    Check("a trailing slash doesn't fool the guard", guard.Check(Path.Combine(work, "home") + Path.DirectorySeparatorChar) != null, true);
    Check("a file inside a main folder is allowed", guard.Check(Path.Combine(work, "home", "old.docx")), (string?)null);
    Check("a similarly named folder is not confused with a protected one", guard.Check(Path.Combine(work, "system2", "a.txt")), (string?)null);

    var guarded = ReceiptCapture.Core.Security.FileShredder.Shred(new[] { Path.Combine(work, "system", "a.dll") }, guard: guard);
    Check("the guard stops the shredder", guarded.Failures.Count, 1);

    Directory.Delete(work, recursive: true);
}
// <<< shredder

// >>> compare
// ---------- Document comparison ----------
{
    // Reads a segment list back: both texts must be rebuilt exactly, and the edit count must be the minimum possible.
    bool Valid(string[] a, string[] b, ReceiptCapture.Core.Comparison.DiffResult r, out int lcsMissing)
    {
        var rebuiltA = new List<string>(); var rebuiltB = new List<string>();
        bool ok = true;
        foreach (var s in r.Segments)
        {
            switch (s.Kind)
            {
                case ReceiptCapture.Core.Comparison.DiffKind.Equal:
                    for (int i = 0; i < s.OldCount; i++) { if (a[s.OldStart + i] != b[s.NewStart + i]) ok = false; rebuiltA.Add(a[s.OldStart + i]); rebuiltB.Add(b[s.NewStart + i]); }
                    break;
                case ReceiptCapture.Core.Comparison.DiffKind.Delete:
                    for (int i = 0; i < s.OldCount; i++) rebuiltA.Add(a[s.OldStart + i]);
                    break;
                default:
                    for (int i = 0; i < s.NewCount; i++) rebuiltB.Add(b[s.NewStart + i]);
                    break;
            }
        }
        if (!rebuiltA.SequenceEqual(a) || !rebuiltB.SequenceEqual(b)) ok = false;

        // Brute-force longest common subsequence.
        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        int lcs = table[0, 0];
        lcsMissing = (a.Length - lcs - r.Removed) + (b.Length - lcs - r.Added);
        return ok;
    }

    var rng = new Random(12345);
    int bad = 0, notMinimal = 0;
    for (int trial = 0; trial < 400; trial++)
    {
        var seqA = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => "w" + rng.Next(0, 5)).ToArray();
        var seqB = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => "w" + rng.Next(0, 5)).ToArray();
        var r = ReceiptCapture.Core.Comparison.TextDiff.Compare(seqA, seqB);
        if (!Valid(seqA, seqB, r, out int miss)) bad++;
        if (miss != 0) notMinimal++;
    }
    Check("400 random diffs rebuild both texts exactly", bad, 0);
    Check("and every one uses the fewest possible edits", notMinimal, 0);

    var same = ReceiptCapture.Core.Comparison.TextDiff.Compare(new[] { "a", "b" }, new[] { "a", "b" });
    Check("identical texts have no changes", same.Removed + same.Added, 0);
    Check("and everything is unchanged", same.Unchanged, 2);
    var empty = ReceiptCapture.Core.Comparison.TextDiff.Compare(Array.Empty<string>(), Array.Empty<string>());
    Check("two empty texts are identical", empty.Segments.Count, 0);
    var allNew = ReceiptCapture.Core.Comparison.TextDiff.Compare(Array.Empty<string>(), new[] { "x", "y" });
    Check("a new text is all added", allNew.Added, 2);

    // A big document with a few dozen scattered edits.
    var bigWords = Enumerable.Range(0, 60000).Select(i => "word" + (i * 7919 % 5003)).ToList();
    var edited = new List<string>(bigWords);
    int expectedAdded = 0, expectedRemoved = 0;
    var editRng = new Random(99);
    for (int e = 0; e < 40; e++)
    {
        int at = editRng.Next(100, edited.Count - 100);
        if (e % 3 == 0) { edited.RemoveAt(at); expectedRemoved++; }
        else if (e % 3 == 1) { edited.Insert(at, "NEW" + e); expectedAdded++; }
        else { edited[at] = "CHANGED" + e; expectedRemoved++; expectedAdded++; }
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var bigDiff = ReceiptCapture.Core.Comparison.TextDiff.Compare(bigWords, edited);
    sw.Stop();
    Check("a 60,000-word document with 40 edits finds them", bigDiff.Added == expectedAdded && bigDiff.Removed == expectedRemoved, true);
    Check("and does it in a few seconds", sw.Elapsed.TotalSeconds < 10, true);

    // Texts that share almost nothing give up politely.
    var xs = Enumerable.Range(0, 300).Select(i => "a" + i).ToArray();
    var ys = Enumerable.Range(0, 300).Select(i => "b" + i).ToArray();
    var tooMuch = ReceiptCapture.Core.Comparison.TextDiff.Compare(xs, ys, maxChanges: 50);
    Check("very different texts are flagged", tooMuch.TooDifferent, true);
    Check("and shown as wholly replaced", tooMuch.Removed == 300 && tooMuch.Added == 300, true);
    var tooMuchKeepsEnds = ReceiptCapture.Core.Comparison.TextDiff.Compare(
        new[] { "start" }.Concat(xs).Concat(new[] { "end" }).ToArray(),
        new[] { "start" }.Concat(ys).Concat(new[] { "end" }).ToArray(), maxChanges: 50);
    Check("the shared start and end are still recognised", tooMuchKeepsEnds.Unchanged, 2);

    // Words and keys.
    var toks = ReceiptCapture.Core.Comparison.DocumentText.FromParagraphs(new[] { "The  quick fox", "", "jumps." });
    Check("four words", toks.Count, 4);
    Check("the paragraph end is marked", toks[2].ParagraphEnd && toks[3].ParagraphEnd && !toks[0].ParagraphEnd, true);
    var paged = ReceiptCapture.Core.Comparison.DocumentText.FromPages(new[] { "one two", "three" });
    Check("pages are numbered from 1", paged[2].Page, (int?)2);
    Check("a page ends a paragraph", paged[1].ParagraphEnd, true);
    Check("curly quotes match straight ones", ReceiptCapture.Core.Comparison.DocumentText.Key("don\u2019t", false), "don't");
    Check("long dashes match hyphens", ReceiptCapture.Core.Comparison.DocumentText.Key("2020\u20132021", false), "2020-2021");
    Check("case can be ignored", ReceiptCapture.Core.Comparison.DocumentText.Key("Hello", true), "hello");
    Check("case is kept by default", ReceiptCapture.Core.Comparison.DocumentText.Key("Hello", false), "Hello");
    Check("soft hyphens vanish", ReceiptCapture.Core.Comparison.DocumentText.Key("co\u00ADoperate", false), "cooperate");

    // Word documents.
    const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";
    var xml = $@"<w:document {W}><w:body>
        <w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t xml:space=""preserve""> world</w:t></w:r></w:p>
        <w:p><w:r><w:t>Keep</w:t></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del><w:ins><w:r><w:t> added</w:t></w:r></w:ins></w:p>
        <w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t></w:r></w:p>
        <w:p><w:r><w:t>   </w:t></w:r></w:p>
        <w:tbl><w:tr><w:tc><w:p><w:r><w:t>cell one</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>cell two</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
        <w:p><w:r><mc:AlternateContent><mc:Choice><w:txbxContent><w:p><w:r><w:t>in a box</w:t></w:r></w:p></w:txbxContent></mc:Choice><mc:Fallback><w:txbxContent><w:p><w:r><w:t>in a box</w:t></w:r></w:p></w:txbxContent></mc:Fallback></mc:AlternateContent></w:r></w:p>
        </w:body></w:document>";
    var paras = ReceiptCapture.Core.Comparison.DocxText.ParagraphsFromXml(xml);
    Check("a Word paragraph's runs are joined", paras[0], "Hello world");
    Check("tracked deletions are left out and insertions kept", paras[1], "Keep added");
    Check("a tab becomes a space", paras[2], "a b");
    Check("a blank paragraph is dropped, table cells and the text box counted once", string.Join("|", paras.Skip(3)), "cell one|cell two|in a box");

    var notesXml = $@"<w:footnotes {W}><w:footnote w:type=""separator"" w:id=""-1""><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
        <w:footnote w:id=""1""><w:p><w:r><w:t>See page 4.</w:t></w:r></w:p></w:footnote></w:footnotes>";
    Check("footnote separators are ignored but footnotes are read",
        string.Join("|", ReceiptCapture.Core.Comparison.DocxText.ParagraphsFromXml(notesXml, skipSeparators: true)), "See page 4.");

    // A real .docx file (a zip), and things that are not one.
    var folder = Path.Combine(Path.GetTempPath(), "cmp-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var docx = Path.Combine(folder, "sample.docx");
    using (var zip = System.IO.Compression.ZipFile.Open(docx, System.IO.Compression.ZipArchiveMode.Create))
    {
        using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open())) w.Write($@"<w:document {W}><w:body><w:p><w:r><w:t>Body text</w:t></w:r></w:p></w:body></w:document>");
        using (var w = new StreamWriter(zip.CreateEntry("word/footnotes.xml").Open())) w.Write(notesXml);
    }
    Check("a .docx file is read: body then footnotes", string.Join("|", ReceiptCapture.Core.Comparison.DocxText.ReadParagraphs(docx)), "Body text|See page 4.");

    var notZip = Path.Combine(folder, "old.docx");
    File.WriteAllBytes(notZip, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3 });
    string? friendly = null;
    try { ReceiptCapture.Core.Comparison.DocxText.ReadParagraphs(notZip); } catch (InvalidDataException ex) { friendly = ex.Message; }
    Check("something that isn't a .docx gets a plain-language error", friendly != null && friendly.Contains("old.docx") && friendly.Contains("save a copy"), true);

    var noBody = Path.Combine(folder, "nobody.docx");
    using (var zip = System.IO.Compression.ZipFile.Open(noBody, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("other.txt").Open())) w.Write("x");
    string? noBodyMessage = null;
    try { ReceiptCapture.Core.Comparison.DocxText.ReadParagraphs(noBody); } catch (InvalidDataException ex) { noBodyMessage = ex.Message; }
    Check("a zip with no Word body is explained", noBodyMessage != null && noBodyMessage.Contains("Word document"), true);
    Directory.Delete(folder, recursive: true);

    // The report.
    var left = ReceiptCapture.Core.Comparison.DocumentText.FromParagraphs(new[]
    {
        "Intro paragraph one stays", "Second paragraph stays too", "The fee is 100 dollars", "Fourth paragraph unchanged", "Fifth paragraph unchanged", "Sixth paragraph unchanged",
    });
    var right = ReceiptCapture.Core.Comparison.DocumentText.FromParagraphs(new[]
    {
        "Intro paragraph one stays", "Second paragraph stays too", "The fee is 250 <b>dollars</b>", "Fourth paragraph unchanged", "Fifth paragraph unchanged", "Sixth paragraph unchanged",
    });
    var diff = ReceiptCapture.Core.Comparison.TextDiff.Compare(
        left.Select(t => ReceiptCapture.Core.Comparison.DocumentText.Key(t.Text, false)).ToList(),
        right.Select(t => ReceiptCapture.Core.Comparison.DocumentText.Key(t.Text, false)).ToList());
    var info = new ReceiptCapture.Core.Comparison.ComparisonInfo("old.docx", "AAA", 1000, "new.docx", "BBB", 2000,
        new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), "1.0", new[] { "A note." });
    var summary = ReceiptCapture.Core.Comparison.ComparisonReport.Summarize(left, right, diff);
    Check("one paragraph changed", summary.ParagraphsChanged, 1);
    Check("two words added", summary.WordsAdded, 2);
    Check("two words removed", summary.WordsRemoved, 2);
    Check("not identical", summary.Identical, false);

    var full = ReceiptCapture.Core.Comparison.ComparisonReport.ToHtml(info, left, right, diff, changesOnly: false);
    Check("removed words are struck through", full.Contains("<del>100 dollars</del>"), true);
    Check("added words are marked", full.Contains("<ins>250 &lt;b&gt;dollars&lt;/b&gt;</ins>"), true);
    Check("markup in the documents can't run inside the report", full.Contains("<b>dollars"), false);
    Check("the file names and fingerprints are listed", full.Contains("old.docx") && full.Contains("AAA") && full.Contains("new.docx") && full.Contains("BBB"), true);
    Check("notes appear", full.Contains("A note."), true);
    Check("the full report keeps unchanged paragraphs", full.Contains("Sixth paragraph unchanged"), true);

    var changesOnly = ReceiptCapture.Core.Comparison.ComparisonReport.ToHtml(info, left, right, diff, changesOnly: true);
    Check("changes-only keeps the change", changesOnly.Contains("<del>100 dollars</del>"), true);
    Check("changes-only keeps one paragraph of context", changesOnly.Contains("Second paragraph stays too") && changesOnly.Contains("Fourth paragraph unchanged"), true);
    Check("changes-only drops the distant paragraphs", changesOnly.Contains("Intro paragraph one stays") || changesOnly.Contains("Sixth paragraph unchanged"), false);
    Check("and marks the gap", changesOnly.Contains("class=\"gap\""), true);

    var identicalDiff = ReceiptCapture.Core.Comparison.TextDiff.Compare(new[] { "x" }, new[] { "x" });
    var one = ReceiptCapture.Core.Comparison.DocumentText.FromParagraphs(new[] { "x" });
    var identicalHtml = ReceiptCapture.Core.Comparison.ComparisonReport.ToHtml(info, one, one, identicalDiff, changesOnly: true);
    Check("identical documents say so", identicalHtml.Contains("No differences"), true);

    var pagedLeft = ReceiptCapture.Core.Comparison.DocumentText.FromPages(new[] { "same page", "old words" });
    var pagedRight = ReceiptCapture.Core.Comparison.DocumentText.FromPages(new[] { "same page", "new words" });
    var pagedDiff = ReceiptCapture.Core.Comparison.TextDiff.Compare(pagedLeft.Select(t => t.Text).ToList(), pagedRight.Select(t => t.Text).ToList());
    var pagedHtml = ReceiptCapture.Core.Comparison.ComparisonReport.ToHtml(info, pagedLeft, pagedRight, pagedDiff, changesOnly: false);
    Check("changes on a PDF show their page", pagedHtml.Contains("p. 2"), true);

    var different = ReceiptCapture.Core.Comparison.ComparisonReport.ToHtml(info,
        ReceiptCapture.Core.Comparison.DocumentText.FromParagraphs(new[] { string.Join(" ", xs) }),
        ReceiptCapture.Core.Comparison.DocumentText.FromParagraphs(new[] { string.Join(" ", ys) }),
        tooMuch, changesOnly: false);
    Check("a too-different pair is explained in the report", different.Contains("very different"), true);
}
// <<< compare

// >>> integrity
// ---------- Integrity and chain of custody ----------
{
    var root = Path.Combine(Path.GetTempPath(), "integ-" + Guid.NewGuid().ToString("N"));
    var evidence = Path.Combine(root, "Evidence");
    Directory.CreateDirectory(Path.Combine(evidence, "emails"));
    File.WriteAllText(Path.Combine(evidence, "contract.txt"), "The contract text.");
    File.WriteAllText(Path.Combine(evidence, "emails", "one.txt"), "first email");
    File.WriteAllText(Path.Combine(evidence, "emails", "two.txt"), "second email");
    var stamp0 = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(-4));

    var made = ReceiptCapture.Core.Integrity.IntegrityChecker.Create(evidence, "Aegis Docs 1.0", "andrew@PC", stamp0,
        matter: "Smith v. Jones", preparedBy: "A. Out", notes: "Initial collection");
    Check("three files are recorded", made.FileCount, 3);
    Check("paths use forward slashes", made.Entries.Any(e => e.Path == "emails/one.txt"), true);
    Check("sizes are recorded", made.Entries.First(e => e.Path == "contract.txt").Size, 18L);
    Check("a fingerprint is stored", made.Fingerprint.Length, 64);
    Check("the stored fingerprint is intact", made.FingerprintIsIntact, true);
    Check("the folder name is the folder, not the path", made.FolderName, "Evidence");
    Check("the first custody entry says Created", made.Events[0].Action, "Created");
    Check("the SHA-256 of a known text is right", made.Entries.First(e => e.Path == "contract.txt").Sha256,
        ReceiptCapture.Core.Storage.FileHasher.Sha256(Path.Combine(evidence, "contract.txt")));

    // Same files, same fingerprint, whatever the order or time.
    var again = ReceiptCapture.Core.Integrity.IntegrityChecker.Create(evidence, "x", "someone else", stamp0.AddDays(5));
    Check("the list fingerprint depends only on the files", again.ComputeFingerprint(), made.ComputeFingerprint());

    // Save and load.
    var jsonPath = Path.Combine(root, "manifest.json");
    made.Save(jsonPath);
    var loadedManifest = ReceiptCapture.Core.Integrity.IntegrityManifest.Load(jsonPath);
    Check("a saved manifest loads back with the same entries", loadedManifest.Entries.Count, 3);
    Check("and the same matter", loadedManifest.Matter, "Smith v. Jones");
    Check("and the same time", loadedManifest.CreatedAt, stamp0);
    Check("and the fingerprint is still intact", loadedManifest.FingerprintIsIntact, true);
    Check("and the custody log", loadedManifest.Events.Count, 1);

    // Everything unchanged.
    var clean = ReceiptCapture.Core.Integrity.IntegrityChecker.Verify(loadedManifest, evidence);
    Check("an untouched folder passes", clean.AllGood, true);
    Check("all three match", clean.Matching, 3);

    // The manifest saved inside the folder is not counted as a new file stamp0 it is excluded.
    var inside = Path.Combine(evidence, "manifest-inside.json");
    made.Save(inside);
    Check("a manifest inside the folder shows as new if not excluded",
        ReceiptCapture.Core.Integrity.IntegrityChecker.Verify(made, evidence).Count(ReceiptCapture.Core.Integrity.VerifyStatus.Added), 1);
    Check("and doesn't stamp0 excluded",
        ReceiptCapture.Core.Integrity.IntegrityChecker.Verify(made, evidence, new[] { inside }).AllGood, true);
    File.Delete(inside);

    // Same-size change, size change, removal, addition.
    File.WriteAllText(Path.Combine(evidence, "emails", "one.txt"), "FIRST email");        // same length, different content
    File.WriteAllText(Path.Combine(evidence, "contract.txt"), "The contract text, edited.");
    File.Delete(Path.Combine(evidence, "emails", "two.txt"));
    File.WriteAllText(Path.Combine(evidence, "extra.txt"), "new");
    var dirty = ReceiptCapture.Core.Integrity.IntegrityChecker.Verify(loadedManifest, evidence);
    Check("a tampered folder fails", dirty.AllGood, false);
    Check("a same-size edit is caught", dirty.Items.First(i => i.Path == "emails/one.txt").Status, ReceiptCapture.Core.Integrity.VerifyStatus.Changed);
    Check("a same-size edit shows both fingerprints", dirty.Items.First(i => i.Path == "emails/one.txt").ActualSha256 != null, true);
    Check("a size change is caught", dirty.Items.First(i => i.Path == "contract.txt").Status, ReceiptCapture.Core.Integrity.VerifyStatus.Changed);
    Check("and says so", dirty.Items.First(i => i.Path == "contract.txt").Detail.Contains("size changed"), true);
    Check("a removed file is caught", dirty.Items.First(i => i.Path == "emails/two.txt").Status, ReceiptCapture.Core.Integrity.VerifyStatus.Missing);
    Check("an added file is caught", dirty.Items.First(i => i.Path == "extra.txt").Status, ReceiptCapture.Core.Integrity.VerifyStatus.Added);
    Check("nothing else is flagged", dirty.Items.Count, 4);

    // Editing the list itself is detected.
    var forged = ReceiptCapture.Core.Integrity.IntegrityManifest.Load(jsonPath);
    forged.Entries[0] = forged.Entries[0] with { Sha256 = new string('0', 64) };
    Check("a hand-edited list no longer matches its fingerprint", forged.FingerprintIsIntact, false);
    var forgedText = File.ReadAllText(jsonPath).Replace(made.Entries.OrderBy(e => e.Path).First().Sha256, new string('a', 64));
    var forgedPath = Path.Combine(root, "forged.json");
    File.WriteAllText(forgedPath, forgedText);
    Check("and so is a list changed in its file", ReceiptCapture.Core.Integrity.IntegrityManifest.Load(forgedPath).FingerprintIsIntact, false);

    // Recording a check doesn't change the fingerprint.
    var before = loadedManifest.ComputeFingerprint();
    loadedManifest.AddEvent(stamp0.AddDays(1), "andrew@PC", "Checked", "Differences found.");
    Check("a custody entry doesn't change the list fingerprint", loadedManifest.ComputeFingerprint(), before);
    Check("and is kept", loadedManifest.Events.Count, 2);

    // Bad input.
    var junk = Path.Combine(root, "junk.json");
    File.WriteAllText(junk, "this is not json");
    string? junkMessage = null;
    try { ReceiptCapture.Core.Integrity.IntegrityManifest.Load(junk); } catch (InvalidDataException ex) { junkMessage = ex.Message; }
    Check("a file that isn't a manifest is explained", junkMessage != null && junkMessage.Contains("isn't an Aegis Docs integrity manifest"), true);

    var future = Path.Combine(root, "future.json");
    File.WriteAllText(future, "{\"format\": 99, \"entries\": []}");
    string? futureMessage = null;
    try { ReceiptCapture.Core.Integrity.IntegrityManifest.Load(future); } catch (InvalidDataException ex) { futureMessage = ex.Message; }
    Check("a newer format asks for an update", futureMessage != null && futureMessage.Contains("newer version"), true);

    bool missingFolder = false;
    try { ReceiptCapture.Core.Integrity.IntegrityChecker.Create(Path.Combine(root, "nope"), "t", "u", stamp0); } catch (DirectoryNotFoundException) { missingFolder = true; }
    Check("a missing folder is refused", missingFolder, true);

    // Stopping gives nothing half-made.
    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        bool cancelled = false;
        try { ReceiptCapture.Core.Integrity.IntegrityChecker.Create(evidence, "t", "u", stamp0, cancel: cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check("a stopped run throws instead of returning a partial list", cancelled, true);
    }

    // Links are not followed.
    var outside = Path.Combine(root, "outside");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside the folder");
    bool linked = false;
    try { Directory.CreateSymbolicLink(Path.Combine(evidence, "link"), outside); linked = true; } catch (Exception) { }
    if (linked)
    {
        var withLink = ReceiptCapture.Core.Integrity.IntegrityChecker.Create(evidence, "t", "u", stamp0);
        Check("a linked folder's contents are not recorded", withLink.Entries.Any(e => e.Path.Contains("secret")), false);
    }

    // The reports.
    var html = ReceiptCapture.Core.Integrity.IntegrityReport.ManifestHtml(made);
    Check("the manifest page names the matter", html.Contains("Smith v. Jones"), true);
    Check("it lists the files and fingerprints", html.Contains("emails/one.txt") && html.Contains(made.Entries[0].Sha256), true);
    Check("it shows the list fingerprint", html.Contains(made.ComputeFingerprint()), true);
    Check("it is honest about what it can't prove", html.Contains("does not on its own prove when"), true);
    Check("it has a custody log", html.Contains("Custody log") && html.Contains("Created"), true);

    var nasty = new ReceiptCapture.Core.Integrity.IntegrityManifest { FolderName = "<script>alert(1)</script>", Tool = "t", CreatedBy = "u" };
    nasty.Entries.Add(new ReceiptCapture.Core.Integrity.ManifestEntry("<img src=x>.txt", 1, "ab", stamp0));
    Check("markup in names can't run in the page", ReceiptCapture.Core.Integrity.IntegrityReport.ManifestHtml(nasty).Contains("<script>alert"), false);

    var manifestCsv = ReceiptCapture.Core.Integrity.IntegrityReport.ManifestCsv(made);
    Check("the CSV has a header and a row per file", manifestCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, 4);
    var formula = new ReceiptCapture.Core.Integrity.IntegrityManifest();
    formula.Entries.Add(new ReceiptCapture.Core.Integrity.ManifestEntry("=cmd|calc.txt", 1, "ab", stamp0));
    Check("a file name that looks like a formula is defused in the CSV", ReceiptCapture.Core.Integrity.IntegrityReport.ManifestCsv(formula).Contains("\n'=cmd"), true);
    var commaName = new ReceiptCapture.Core.Integrity.IntegrityManifest();
    commaName.Entries.Add(new ReceiptCapture.Core.Integrity.ManifestEntry("a,b \"c\".txt", 1, "ab", stamp0));
    Check("commas and quotes are escaped in the CSV", ReceiptCapture.Core.Integrity.IntegrityReport.ManifestCsv(commaName).Contains("\"a,b \"\"c\"\".txt\""), true);

    var goodPage = ReceiptCapture.Core.Integrity.IntegrityReport.VerifyHtml(made, clean, evidence, stamp0, "andrew@PC", "Aegis Docs 1.0");
    Check("a clean check says everything matches", goodPage.Contains("Everything matches"), true);
    var badPage = ReceiptCapture.Core.Integrity.IntegrityReport.VerifyHtml(made, dirty, evidence, stamp0, "andrew@PC", "Aegis Docs 1.0");
    Check("a failed check says so", badPage.Contains("Differences found"), true);
    Check("and lists what changed", badPage.Contains("contract.txt") && badPage.Contains("extra.txt") && badPage.Contains("two.txt"), true);
    var forgedPage = ReceiptCapture.Core.Integrity.IntegrityReport.VerifyHtml(forged, clean, evidence, stamp0, "u", "t");
    Check("an edited list is called out", forgedPage.Contains("The list itself was edited"), true);
    Check("the check CSV has a row per file", ReceiptCapture.Core.Integrity.IntegrityReport.VerifyCsv(dirty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, 5);

    Directory.Delete(root, recursive: true);
}
// <<< integrity

// >>> pdftools
// ---------- PDF toolbox: page lists and compression decisions ----------
{
    bool Order(string? spec, int count, out List<int> pages, out string error)
        => ReceiptCapture.Core.Documents.PageSelection.TryParseOrder(spec, count, out pages, out error);
    bool Set(string? spec, int count, bool all, out SortedSet<int> pages, out string error)
        => ReceiptCapture.Core.Documents.PageSelection.TryParseSet(spec, count, all, out pages, out error);

    Check("a new order is read as typed", Order("3, 1, 2", 5, out var ord, out _) ? string.Join(",", ord) : "fail", "3,1,2");
    Check("ranges expand in place", Order("1, 4-6, 2", 8, out ord, out _) ? string.Join(",", ord) : "fail", "1,4,5,6,2");
    Check("a range can run backwards for a new order", Order("5-3", 5, out ord, out _) ? string.Join(",", ord) : "fail", "5,4,3");
    Check("an open range runs to the end", Order("3-", 5, out ord, out _) ? string.Join(",", ord) : "fail", "3,4,5");
    Check("pages may repeat in a new order", Order("1, 1, 2", 3, out ord, out _) ? string.Join(",", ord) : "fail", "1,1,2");
    Check("semicolons work like commas", Order("2; 1", 3, out ord, out _) ? string.Join(",", ord) : "fail", "2,1");
    Check("a page past the end is refused", Order("9", 5, out _, out var err), false);
    Check("and says which page", err.Contains("Page 9 doesn't exist") && err.Contains("5 pages"), true);
    Check("page zero is refused", Order("0", 5, out _, out _), false);
    Check("words are refused", Order("two", 5, out _, out err), false);
    Check("and quoted back", err.Contains("\"two\""), true);
    Check("an empty order is refused", Order("  ", 5, out _, out _), false);
    Check("a negative-looking range is refused", Order("-3", 5, out _, out _), false);
    Check("a document with no pages is refused", Order("1", 0, out _, out _), false);

    Check("a set is sorted and de-duplicated", Set("5, 2, 2, 3-4", 6, false, out var set, out _) ? string.Join(",", set) : "fail", "2,3,4,5");
    Check("a blank set can mean every page", Set("", 3, true, out set, out _) ? string.Join(",", set) : "fail", "1,2,3");
    Check("or be refused", Set("", 3, false, out _, out _), false);
    Check("a backwards range is refused in a set", Set("4-2", 6, false, out _, out err), false);
    Check("with a reason", err.Contains("backwards"), true);
    Check("a set page past the end is refused", Set("7", 6, false, out _, out _), false);

    Set("2, 4", 5, false, out var toDelete, out _);
    Check("deleting leaves the rest in order", ReceiptCapture.Core.Documents.PageSelection.TryKeepAfterDelete(toDelete, 5, out var keptPages, out _) ? string.Join(",", keptPages) : "fail", "1,3,5");
    Set("1-3", 3, false, out var everything, out _);
    Check("deleting every page is refused", ReceiptCapture.Core.Documents.PageSelection.TryKeepAfterDelete(everything, 3, out _, out err), false);
    Check("with a reason", err.Contains("every page"), true);

    Check("reverse", string.Join(",", ReceiptCapture.Core.Documents.PageSelection.Reverse(4)), "4,3,2,1");
    Check("reverse of nothing", ReceiptCapture.Core.Documents.PageSelection.Reverse(0).Count, 0);

    Check("a page turned 90 more is 90", ReceiptCapture.Core.Documents.PageSelection.Rotated(0, 90), 90);
    Check("rotation wraps past 360", ReceiptCapture.Core.Documents.PageSelection.Rotated(270, 180), 90);
    Check("turning left from 0 gives 270", ReceiptCapture.Core.Documents.PageSelection.Rotated(0, -90), 270);
    Check("a full circle is back to 0", ReceiptCapture.Core.Documents.PageSelection.Rotated(180, 180), 0);

    var smaller = ReceiptCapture.Core.Documents.ImageShrink.For(ReceiptCapture.Core.Documents.CompressLevel.Smaller);
    var smallest = ReceiptCapture.Core.Documents.ImageShrink.For(ReceiptCapture.Core.Documents.CompressLevel.Smallest);
    Check("smallest is tighter than smaller", smallest.MaxSide < smaller.MaxSide && smallest.JpegQuality < smaller.JpegQuality, true);
    Check("a big picture is scaled down, keeping its shape", ReceiptCapture.Core.Documents.ImageShrink.ScaledSize(3300, 2550, 1650), (1650, 1275));
    Check("a tall picture is limited by its height", ReceiptCapture.Core.Documents.ImageShrink.ScaledSize(1000, 4000, 1000), (250, 1000));
    Check("a small picture is never enlarged", ReceiptCapture.Core.Documents.ImageShrink.ScaledSize(800, 600, 1650), (800, 600));
    Check("a thin picture keeps at least a pixel", ReceiptCapture.Core.Documents.ImageShrink.ScaledSize(10000, 1, 100), (100, 1));
    Check("a barely smaller result isn't worth keeping", ReceiptCapture.Core.Documents.ImageShrink.WorthKeeping(1000, 950), false);
    Check("a clearly smaller one is", ReceiptCapture.Core.Documents.ImageShrink.WorthKeeping(1000, 600), true);
    Check("an empty result is never kept", ReceiptCapture.Core.Documents.ImageShrink.WorthKeeping(1000, 0), false);
    Check("a larger result is never kept", ReceiptCapture.Core.Documents.ImageShrink.WorthKeeping(1000, 1200), false);
    Check("savings are described", ReceiptCapture.Core.Documents.ImageShrink.DescribeSaving(10 * 1024 * 1024, 4 * 1024 * 1024).StartsWith("Saved 6.0 MB (60%)"), true);
    Check("no saving is described plainly", ReceiptCapture.Core.Documents.ImageShrink.DescribeSaving(1000, 1000), "No saving");
}
// <<< pdftools

// >>> walker
// ---------- Folder walker (used by Integrity and the Shredder) ----------
{
    var walkRoot = Path.Combine(Path.GetTempPath(), "walk-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(walkRoot, "sub", "deeper"));
    File.WriteAllText(Path.Combine(walkRoot, "a.txt"), "a");
    File.WriteAllText(Path.Combine(walkRoot, "sub", "b.txt"), "b");
    File.WriteAllText(Path.Combine(walkRoot, "sub", "deeper", "c.txt"), "c");
    var hiddenFile = Path.Combine(walkRoot, ".hidden.txt");
    File.WriteAllText(hiddenFile, "h");
    try { File.SetAttributes(hiddenFile, FileAttributes.Hidden); } catch (Exception) { }
    var systemFile = Path.Combine(walkRoot, "sub", "system.dat");
    File.WriteAllText(systemFile, "s");
    try { File.SetAttributes(systemFile, FileAttributes.Hidden | FileAttributes.System); } catch (Exception) { }

    var walked = ReceiptCapture.Core.Storage.FolderWalker.EnumerateFiles(walkRoot).Select(f => Path.GetRelativePath(walkRoot, f).Replace('\\', '/')).OrderBy(f => f).ToList();
    Check("every file is listed, including hidden and system ones", string.Join("|", walked), ".hidden.txt|a.txt|sub/b.txt|sub/deeper/c.txt|sub/system.dat");

    var walkOutside = Path.Combine(walkRoot, "outside-target");
    Directory.CreateDirectory(walkOutside);
    File.WriteAllText(Path.Combine(walkOutside, "far.txt"), "far");
    var walkLinkFolder = Path.Combine(walkRoot, "sub", "loop");
    bool walkLinked = false;
    try { Directory.CreateSymbolicLink(walkLinkFolder, walkRoot); walkLinked = true; } catch (Exception) { }
    if (walkLinked)
    {
        var withLink = ReceiptCapture.Core.Storage.FolderWalker.EnumerateFiles(walkRoot).Count(f => !f.Contains("outside-target"));
        Check("a link back to the folder doesn't loop or double count", withLink, 5);
    }
    Check("a missing folder lists nothing instead of crashing", ReceiptCapture.Core.Storage.FolderWalker.EnumerateFiles(Path.Combine(walkRoot, "nope")).Count(), 0);

    // The integrity manifest includes them too.
    var walkManifest = ReceiptCapture.Core.Integrity.IntegrityChecker.Create(walkRoot, "t", "u", DateTimeOffset.UtcNow);
    Check("a manifest records hidden files", walkManifest.Entries.Any(e => e.Path == ".hidden.txt"), true);
    Check("and system files", walkManifest.Entries.Any(e => e.Path == "sub/system.dat"), true);
    File.WriteAllText(hiddenFile, "H");
    try { File.SetAttributes(hiddenFile, FileAttributes.Hidden); } catch (Exception) { }
    var walkCheck = ReceiptCapture.Core.Integrity.IntegrityChecker.Verify(walkManifest, walkRoot);
    Check("a change to a hidden file is caught", walkCheck.Items.First(i => i.Path == ".hidden.txt").Status, ReceiptCapture.Core.Integrity.VerifyStatus.Changed);

    Directory.Delete(walkRoot, recursive: true);
}
// <<< walker

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
