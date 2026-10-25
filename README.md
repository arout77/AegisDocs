# Aegis Docs

Windows desktop app with five tabs. **Redact**: finds SSNs, phone numbers, emails, card numbers, dates of birth, street addresses, EINs, bank account and routing numbers, driver's license and passport numbers, plus any words you list (read from the page by OCR); zoom with the on-page controls, Ctrl + mouse wheel, or Ctrl +/-/0, lets you review every hit and draw your own boxes, then saves a copy where the approved areas are burned into the page image so the underlying text no longer exists (optionally adding back the *uncovered* words as an invisible text layer, so the copy stays searchable), and re-reads the saved file to verify. Opens PDFs (including password-protected ones), Word documents (via Word or LibreOffice) and pictures (JPG, PNG, TIFF…). **Compliance**: checks PDFs, Word documents and pictures, one file or a whole folder (up to 2,000 files, with subfolders) at a time, against a profile (HIPAA Safe Harbor identifiers, PCI DSS cardholder data, financial GLBA-style personal information, general personal information) plus your own words. Shows each item found with its page and line (or the hidden place it was in), severity and a partly hidden value; says plainly what the profile cannot find; also looks for hidden content (author and other properties, comments, tracked changes and deleted text, hidden text, attachments, scripts, page previews, GPS and camera data, earlier saved versions) and runs the same checks over text found there; makes clean copies (Word files and JPEG/PNG pictures are edited at the file-structure level, PDFs are rebuilt from page pictures) with a re-check and a cleanup log; and saves a report as a web page or CSV with a SHA-256 fingerprint of each file. It is worded as a first-pass check, never as certification. **Documents**: batch PDF tools for law-firm workflows (merge, split by page range or every N pages, Bates numbering, watermark, bulk rename; originals are never modified). **Receipts**: drop in receipts and invoices (PNG, JPG, PDF), it reads them with Windows' built-in
offline OCR, pre-fills vendor / date / total / tax / category, you confirm, and it keeps a searchable list
you can export to CSV for Excel or your accountant. **Meetings**: records the microphone and (optionally) the computer's sound, keeps notes, and makes an offline transcript with Whisper. Nothing leaves the PC.

## Run it
1. Install **Visual Studio 2022 Community** with the ".NET desktop development" workload (or the .NET 8 SDK).
2. Open `ReceiptCapture.sln`, set **ReceiptCapture** as the startup project, press **F5**.
3. Drag a receipt onto the window.

## Make an .exe
Double-click `publish.bat`. It produces `publish\AegisDocs.exe`, a single file that runs on any
Windows 10/11 PC without installing .NET.

## Where your data lives
`%LocalAppData%\ReceiptCapture\`  (receipts.json, a `receipts` folder with copies of the originals, error.log)

## Layout
| Project | What's in it |
|---|---|
| `ReceiptCapture.Core` | Plain C#, no UI: `Receipt` model, `ReceiptParser` (OCR text -> fields), JSON storage, CSV export |
| `ReceiptCapture` | The WPF app. `MainWindow` hosts a tab per tool: `ComplianceView` + `ComplianceViewModel` (reuses the Redact tab's offline reader), `DocumentsView` + `DocumentsViewModel` + `PdfService` (PDFsharp), and `ReceiptsView` + `MainViewModel` + `OcrService` (Windows OCR) |
| `ReceiptCapture.Tests` | Dependency-free tests: `dotnet run --project ReceiptCapture.Tests` |

## Where to poke around
- Redaction logic (finder, box mapping, verifier) and the image-only PDF writer: `ReceiptCapture.Core/Redaction/` and `ReceiptCapture.Core/Pdf/` (unit-tested)
- Compliance scanner: `ReceiptCapture.Core/Compliance/` (profiles = the tunable part, scanner, value masking, folder enumeration, HTML/CSV report writer; unit-tested). Hidden content: `ReceiptCapture.Core/HiddenContent/` (`DocxInspector`/`DocxCleaner` work on the zip of XML, `PdfInspector` reads the PDF's own structure including compressed object streams with no PDF library, `ImageMetadata` + `ExifReader` strip JPEG/PNG without re-encoding, `HiddenContentCleaner` names and writes clean copies and the cleanup log). Test fixtures made by other tools (reportlab, pypdf, qpdf, Pillow) are in `ReceiptCapture.Tests/HiddenFixtures.cs`.
- Redaction log: `ReceiptCapture.Core/Redaction/RedactionLog.cs` (kinds and counts only, never the redacted values), written by the Redact tab next to the saved copy The detectors it uses are in `SensitiveDataFinder.cs`, switched on per profile; the Redact tab leaves the new ones off. UI: `ComplianceView` + `ViewModels/ComplianceViewModel.cs`
- Bates / page-range / rename logic: `ReceiptCapture.Core/Documents/` (unit-tested)
- PDF operations: `ReceiptCapture/Services/PdfService.cs` (the only file that touches PDFsharp)
- Parsing rules: `ReceiptCapture.Core/Parsing/ReceiptParser.cs` (the regexes at the top are the tunable part)
- Storage is behind `IReceiptStore`, so SQLite can replace the JSON file by adding one class
- Categories: `ReceiptCapture.Core/Categories.cs`

## Automatic builds (GitHub Actions)
`.github/workflows/build.yml` builds, tests, and publishes the app on every push and pull request, then wraps it in
an installer (`installer/AegisDocs.iss`, made with Inno Setup). Two things appear under the run's **Artifacts**:

- `AegisDocs-Setup`: the installer. This is the file to give to clients. Push a tag like `v0.1.0` and it is also attached to a GitHub Release.
- `AegisDocs-portable`: the same app as a plain folder (unzip it and run `AegisDocs.exe`) for quick testing without installing.
  Keep the folder together: the exe on its own can't make transcripts.

### Meeting transcripts (offline)
Transcripts are made on the PC by Whisper, an open-source speech-to-text model, so audio never leaves the computer.
The model file (`ggml-small.en-q5_1.bin`, about 180 MB, English only) is downloaded by the build, checked, and placed in the
app's `models` folder, so the installer carries it and clients download nothing extra. The speech engine files sit in `runtimes`; `tools/add-speech-engine.ps1` copies them there after publishing and stops the build if any are missing.
To try a different model, change `ModelFileName` in `Services/TranscriptionService.cs` and the model name in `build.yml`.

## Look and feel
Light and dark themes. The **Theme** button (top right) cycles Auto (follows Windows), Light and Dark,
and your choice is remembered. 
Windows message boxes are drawn by Windows itself, so they stay light in dark mode.

Third-party licences are listed in `THIRD-PARTY-NOTICES.md`.
