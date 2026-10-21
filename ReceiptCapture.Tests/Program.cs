// A tiny dependency-free test runner:  dotnet run --project ReceiptCapture.Tests
using ReceiptCapture.Core;
using ReceiptCapture.Core.Export;
using ReceiptCapture.Core.Parsing;
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

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
