# Receipt Capture

Windows desktop app: drop in receipts and invoices (PNG, JPG, PDF), it reads them with Windows' built-in
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
| `ReceiptCapture` | The WPF app: `MainWindow.xaml` (layout), `MainViewModel` (behaviour), `OcrService` (Windows OCR + PDF) |
| `ReceiptCapture.Tests` | Dependency-free tests: `dotnet run --project ReceiptCapture.Tests` |

## Where to poke around
- Parsing rules: `ReceiptCapture.Core/Parsing/ReceiptParser.cs` (the regexes at the top are the tunable part)
- Storage is behind `IReceiptStore`, so SQLite can replace the JSON file by adding one class
- Categories: `ReceiptCapture.Core/Categories.cs`

