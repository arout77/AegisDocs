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

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
