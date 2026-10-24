// A tiny dependency-free test runner:  dotnet run --project ReceiptCapture.Tests
using ReceiptCapture.Core;
using ReceiptCapture.Core.Documents;
using ReceiptCapture.Core.Export;
using ReceiptCapture.Core.Meetings;
using ReceiptCapture.Core.Parsing;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Core.Storage;

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

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
