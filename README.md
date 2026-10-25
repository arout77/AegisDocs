# Aegis Docs

### Your All-in-One Privacy & Productivity Workspace
**Designed for law firms, medical practices, and businesses that handle sensitive data.** 
*Best of all? Everything runs 100% locally on your computer. No cloud servers, no subscriptions, and zero risk of data leaks.*

---

### 1. Smart Redaction
Protect client privacy and confidential information in seconds.
* **Instant Detection:** Automatically spots social security numbers, phone numbers, emails, financial details, dates of birth, street addresses, and custom keywords.
* **Total Control:** Zoom in, review every match, or draw your own custom blackout boxes with ease.
* **Permanent Protection:** Permanently burns approved redactions into the document image so sensitive text is gone for good. Optionally keeps the remaining text fully searchable.
* **Built-in Verification:** Automatically re-reads saved files to ensure every sensitive detail has been successfully secured.
* **Wide Compatibility:** Open and redact PDFs (even password-protected ones), Word documents, and photos (JPG, PNG, TIFF).

### 2. Compliance & Deep Cleaning
Scan your files and uncover hidden risks before sharing them.
* **Comprehensive Scans:** Check individual files or entire folders (up to 2,000 files with subfolders) against industry privacy guidelines like medical record standards, credit card security, or financial data.
* **Uncover Hidden Data:** Finds sneaky hidden elements like old comments, track changes, hidden text, author names, attachments, and even camera GPS data from photos.
* **Safe, Clean Copies:** Generates scrubbed, safe copies of your files while keeping your originals untouched, complete with a detailed cleanup report and secure file fingerprints. 
* *Note: Built as a reliable internal first-pass review tool.*

### 3. Document & Case Tools
A streamlined toolkit built for busy legal and professional workflows.
* **Batch Processing:** Easily merge files, split by page ranges or intervals, apply custom watermarks, and bulk-rename documents.
* **Bates Numbering:** Apply sequential Bates stamps to case files and discovery sets instantly.
* **Zero Risk:** Your original source files are never modified—every action creates a pristine new copy.

### 4. Expense & Receipt Manager
Simplify bookkeeping without exposing financial data to the cloud.
* **Drag & Drop Simplicity:** Drop in receipts or vendor invoices (PNG, JPG, PDF).
* **Smart Offline Extraction:** Automatically reads the vendor, date, total, tax, and category for you to review and confirm.
* **Easy Export:** Keep a clean, searchable list of your expenses ready to export to CSV for Excel or your accountant.

### 5. Secure Meetings & Local Transcription
Capture client consultations and internal discussions with complete privacy.
* **Flexible Recording:** Record your microphone and (optionally) your computer's sound during meetings.
* **Offline Transcripts:** Keep organized notes and let local AI generate accurate transcripts completely offline.

---
**🔒 100% Private & Local:** Nothing ever leaves your PC.
---

** A signed installer will be provided soon. If you wish to install an unsigned version, or run a portable version that requires no install, click on "Actions" in the menu above, then click on the most recent deployment in the Workflow (usually the very first one listed), and scroll to the bottom of the page. Download "AegisDocs-Setup" for the installer (you'll need this in order to receive updates), or "AegisDocs-portable" to skip install and test it out. Otherwise, follow the instructions below to run the program.

#### Note: Either way, Windows will issue a warning about using software downloaded from the internet. This is because the application is not yet signed and that automatically triggers the Windows protection. You can safely ignore the warning and run the program by clicking "More info" and then "Run anyway" button in the Windows alert box.

## Run it
1. Install **Visual Studio 2022 Community** with the ".NET desktop development" workload (or the .NET 8 SDK).
2. Open `ReceiptCapture.sln`, set **ReceiptCapture** as the startup project, press **F5**.
3. Drag a receipt onto the window.

## Make an .exe
Double-click `publish.bat`. It produces `publish\AegisDocs.exe`, a single file that runs on any
Windows 10/11 PC without installing .NET.


Third-party licences are listed in `THIRD-PARTY-NOTICES.md`.
