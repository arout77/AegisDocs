# Aegis Docs

### The All-in-One Desktop Productivity & Compliance Suite for Privacy-Conscious Professionals

Protect client confidentiality, streamline document management, and automate administrative tasks—without your data ever leaving your computer. Designed specifically for legal, medical, and financial teams that demand enterprise-grade privacy and speed.

| Feature Module | Core Capability | Key Business Benefit |
| :--- | :--- | :--- |
| **Smart Redaction** | Auto-detects 10+ PII/PHI categories; permanently burns out text; verifies output with post-export OCR reads. | **Eliminate HIPAA & Legal Compliance Risks:** Ensures sensitive data is unrecoverable without ruining document searchability. |
| **Document Batching** | Split, merge, watermark, bulk rename, and apply Bates numbering across mass PDF volumes. | **Accelerate Case Prep & Discovery:** Saves hours of manual PDF editing for paralegals and administrative staff. |
| **Receipt Processing** | Auto-extract vendor, date, tax, and totals using offline OCR; export directly to CSV. | **Streamline Accounting:** Fast, accurate expense tracking without uploading financial receipts to cloud servers. |
| **Private Transcription** | Local meeting recording & Whisper AI transcription for system sound and microphone. | **Protect Client Privilege:** Record and transcribe sensitive consultations offline without third-party AI exposure. |

** A signed installer will be provided soon. If you wish to install an unsigned version, or run a portable version that requires no install, click on "Actions" in the menu above, then click on the most recent deployment in the Workflow (usually the very first one listed), and scroll to the bottom of the page. Download "AegisDocs-Setup" for the installer (you'll need this in order to receive updates), or "AegisDocs-portable" to skip install and test it out. Otherwise, follow the instructions below to run the program.

#### Note: Either way, Windows will issue a warning about using software downloaded from the internet. This is because the application is not yet signed and that automatically triggers the Windows protection. You can safely ignore the warning and run the program by clicking "More info" and then "Run anyway" button in the Windows alert box.

## Run it
1. Install **Visual Studio 2022 Community** with the ".NET desktop development" workload (or the .NET 8 SDK).
2. Open `ReceiptCapture.sln`, set **ReceiptCapture** as the startup project, press **F5**.
3. Drag a receipt onto the window.

## Make a shareable .exe
Double-click `publish.bat`. It produces `publish\AegisDocs.exe`, a single file that runs on any
Windows 10/11 PC without installing .NET.

## Where your data lives
`%LocalAppData%\ReceiptCapture\`  (receipts.json, a `receipts` folder with copies of the originals, error.log)

## Look and feel
Light and dark themes. The **Theme** button (top right) cycles Auto (follows Windows), Light and Dark,
and your choice is remembered. 
Windows message boxes are drawn by Windows itself, so they stay light in dark mode.

Third-party licences are listed in `THIRD-PARTY-NOTICES.md`.
