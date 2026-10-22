# Office Toolkit (working title)

Windows desktop app with three tabs. **Redact**: finds SSNs, phone numbers, emails, card numbers, dates of birth, street addresses, EINs, bank account and routing numbers, driver's license and passport numbers, plus any words you list (read from the page by OCR); zoom with the on-page controls, Ctrl + mouse wheel, or Ctrl +/-/0, lets you review every hit and draw your own boxes, then saves a copy where the approved areas are burned into the page image so the underlying text no longer exists, and re-reads the saved file to verify. **Documents**: batch PDF tools for law-firm workflows (merge, split by page range or every N pages, Bates numbering, watermark, bulk rename; originals are never modified). **Receipts**: drop in receipts and invoices (PNG, JPG, PDF), it reads them with Windows' built-in
offline OCR, pre-fills vendor / date / total / tax / category, you confirm, and it keeps a searchable list
you can export to CSV for Excel or your accountant. Nothing leaves the PC.

## Run it
1. Install **Visual Studio 2022 Community** with the ".NET desktop development" workload (or the .NET 8 SDK).
2. Open `ReceiptCapture.sln`, set **ReceiptCapture** as the startup project, press **F5**.
3. Drag a receipt onto the window.

## Make a shareable .exe
Double-click `publish.bat`. It produces `publish\ReceiptCapture.exe`, a single file that runs on any
Windows 10/11 PC without installing .NET.

## Where your data lives
`%LocalAppData%\ReceiptCapture\`  (receipts.json, a `receipts` folder with copies of the originals, error.log)

## Layout
| Project | What's in it |
|---|---|
| `ReceiptCapture.Core` | Plain C#, no UI: `Receipt` model, `ReceiptParser` (OCR text -> fields), JSON storage, CSV export |
| `ReceiptCapture` | The WPF app. `MainWindow` hosts a tab per tool: `DocumentsView` + `DocumentsViewModel` + `PdfService` (PDFsharp), and `ReceiptsView` + `MainViewModel` + `OcrService` (Windows OCR) |
| `ReceiptCapture.Tests` | Dependency-free tests: `dotnet run --project ReceiptCapture.Tests` |

## Where to poke around
- Redaction logic (finder, box mapping, verifier) and the image-only PDF writer: `ReceiptCapture.Core/Redaction/` and `ReceiptCapture.Core/Pdf/` (unit-tested)
- Bates / page-range / rename logic: `ReceiptCapture.Core/Documents/` (unit-tested)
- PDF operations: `ReceiptCapture/Services/PdfService.cs` (the only file that touches PDFsharp)
- Parsing rules: `ReceiptCapture.Core/Parsing/ReceiptParser.cs` (the regexes at the top are the tunable part)
- Storage is behind `IReceiptStore`, so SQLite can replace the JSON file by adding one class
- Categories: `ReceiptCapture.Core/Categories.cs`

## Automatic builds (GitHub Actions)
`.github/workflows/build.yml` builds, tests, and publishes the exe on every push and pull request (the exe appears
under the run's **Artifacts**). Push a tag like `v0.1.0` and it also attaches `ReceiptCapture.exe` to a GitHub Release.

## Look and feel
Light and dark themes with the Sora typeface. The **Theme** button (top right) cycles Auto (follows Windows), Light and Dark,
and your choice is remembered (`%LocalAppData%\ReceiptCapture\settings.json`). Colours live in `ReceiptCapture/Themes/Light.xaml`
and `Dark.xaml` (identical keys); shared control styles are in `Themes/Shared.xaml`; the switching code is `AppTheme.cs`.
Standard controls (combo boxes, date pickers, grids, scroll bars) get their modern look from ModernWpfUI.
Windows message boxes are drawn by Windows itself, so they stay light in dark mode.

Third-party licences are listed in `THIRD-PARTY-NOTICES.md`.
