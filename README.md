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
* **Built-in Verification:** Automatically re-reads the saved file and tells you if any of the redacted text can still be read.
* **Wide Compatibility:** Open and redact PDFs (even password-protected ones), Word documents, and photos (JPG, PNG, TIFF).
* **Reads Forms & ID Cards:** Finds values printed *under* their label, such as the license number and birth date in an ID card's table, or a field name above its box on a form. Recognizes Social Security numbers written many ways (with dashes or spaces, or with a digit the text reader slipped on), ITINs, and labels like "Driver / ID Number".
* **Medical Details:** Spots medical record numbers (MRN), health plan and member IDs, and prescription numbers.
* **Redaction Log:** Optionally saves a record next to each redacted copy: who saved it and when, a fingerprint (SHA-256) of the original and of the copy, how many items of each kind were redacted or left visible, and what the re-check found. It never contains the redacted text.
* **Barcode Reminder:** An ID card's barcode repeats the details printed on it and can't be found automatically, so the program reminds you to draw a box over it.
* **See What It Read:** "Copy page text" shows exactly what the text reader saw on a page. Handy when something wasn't found: a number the reader got wrong can't be matched.

### 2. Compliance & Deep Cleaning
Scan your files and uncover hidden risks before sharing them.
* **Comprehensive Scans:** Check individual files or entire folders (up to 2,000 files with subfolders) against industry privacy guidelines like medical record standards, credit card security, or financial data.
* **Uncover Hidden Data:** Finds sneaky hidden elements like old comments, track changes, hidden text, author names, attachments, and even camera GPS data from photos.
* **Safe, Clean Copies:** Generates scrubbed, safe copies of your files while keeping your originals untouched, complete with a detailed cleanup report and secure file fingerprints. 
* **Choose What to Check Against:** Four profiles (HIPAA Safe Harbor identifiers, PCI DSS cardholder data, financial GLBA-style personal information, and general personal information), plus your own words or names. Each one lists what it looks for and what it can't find.
* **Clear, Printable Reports:** Save results as a web page (print it to PDF) or a CSV spreadsheet. Found values are partly hidden in the report, every file gets a SHA-256 fingerprint, and the report states what the scan cannot catch.
* **You Choose What a Clean Copy Removes:** Author and other properties, comments, tracked changes (accepting them removes deleted text for good), hidden text, attached and embedded files, preview pictures, and picture metadata. PDFs are cleaned by rebuilding each page as a picture, optionally keeping the text searchable. Every copy is checked again, and a `Cleanup log.csv` records what was done.
* **Stop Anytime:** On a large folder, Stop finishes the file in progress and lists the rest as not scanned, and the report says so.
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
* **Speaker Names:** Select lines in the transcript and give them a name ("Dr. Lee", "Client"). Rename a speaker everywhere in one step. For calls recorded with computer sound, one click suggests which lines were you and which were the other people.
* **Save as Word or Text:** Export the transcript as a Word (.docx) file or a text file, with speaker names, times, and (if you choose) your notes.
* **Locked on Your PC:** Receipts, meeting notes, transcripts, and recordings are stored encrypted, so a copied folder or a lost laptop doesn't give them away.

### 6. Good to Know
Aegis Docs is a careful first pass, not a guarantee. Please keep these in mind:
* **Review before you share.** It can miss things and it can flag harmless ones. Look over every redacted or cleaned file yourself.
* **Names, signatures, photographs, handwriting, and barcodes can't be found automatically.** Add names under "Also redact these words or names," and draw a box over the rest.
* **It can only find what the text reader can read.** Blurry, tilted, or glary photos can be misread, and a number read wrongly can't be matched. Flat, well-lit, in-focus photos work best.
* **Hand-drawn boxes can't be re-checked automatically,** so look at the pages they cover.
* **Hidden-content checks cover Word (.docx), PDF, JPEG, and PNG files.** Older Word files (.doc, .rtf), password-protected PDFs, and other picture types can't be looked inside, and the results say so. Save older Word files as .docx first.
* **Cleaning changes the files it makes, not your originals.** Accepting tracked changes and deleting hidden text change a Word document's wording, and a PDF rebuilt from page pictures is larger than the original.
* **Up to 2,000 files** are kept in one run.
* **Saved data is locked to your Windows sign-in.** Receipts, meetings, transcripts, and recordings are encrypted (AES-256) with a key only your Windows account can open. That protects against a copied folder, a stolen drive, or another Windows user. It does not protect against someone signed in as you, or malware running as you.
* **Locked data can't be moved to another PC or recovered if the Windows account is deleted or reset.** Export what you need to keep (transcripts, the receipts CSV, audio copies) before reinstalling Windows or changing computers.
* **A recording is written unlocked while it is being made** (so it survives a crash) and is locked when you press Stop. Opening a locked receipt or recording makes a temporary readable copy that is removed when the program closes.
* **Files from older versions are locked the first time you open the program.** The old plain copies are deleted, but Windows doesn't wipe the freed disk space. Turn on BitLocker (or "Reset this PC" with a full wipe) if that matters to you.
* **Files you save are not locked.** Redacted documents, reports, exported transcripts, Word files, and audio copies go where you put them, so store them somewhere safe.
* **Suggested speakers are guesses.** They work from whether your microphone or the computer's sound was active, so they can't separate several people on the same side, and they don't work for an in-person meeting. Lines where both sides were talking are left blank. Check them before you rely on them.
* **Transcripts can contain mistakes.** The Word export says so at the bottom.
* **Treat reports and logs as confidential.** Found values are partly hidden in them, but they still list your file names and where things were found.

---
**🔒 Private & Local:** Your documents, receipts, and recordings never leave your PC. The only internet use is the optional update check (one small file, nothing from your documents), which can be turned off in the Updates window.
---

<div id="install">

** A signed installer will be provided soon. If you wish to install an unsigned version, or run a portable version that requires no install, click on "Actions" in the menu above, then click on the most recent deployment in the Workflow (usually the very first one listed), and scroll to the bottom of the page. Download "AegisDocs-Setup" for the installer (installed copies can then update themselves), or "AegisDocs-portable" to skip install and test it out. Otherwise, follow the instructions below to run the program.

#### Note: Either way, Windows will issue a warning about using software downloaded from the internet. This is because the application is not yet signed and that automatically triggers the Windows protection. You can safely ignore the warning and run the program by clicking "More info" and then "Run anyway" button in the Windows alert box.

## Run it
1. Install **Visual Studio 2022 Community** with the ".NET desktop development" workload (or the .NET 8 SDK).
2. Open `ReceiptCapture.sln`, set **ReceiptCapture** as the startup project, press **F5**.
3. Drag a receipt onto the window.

## Make an .exe
Double-click `publish.bat`. It produces `publish\AegisDocs.exe`, which runs on any
Windows 10/11 PC without installing .NET. Keep the whole `publish` folder together: the `runtimes` and `models`
folders next to the exe are needed for meeting transcripts. The first run downloads the speech model
(about 180 MB), so it needs an internet connection that one time.

</div>

## Updates
The **Updates** button (top right) shows the version and checks for a newer one. On start-up the program also checks, at most once a day, unless the person turns that off in the Updates window. A new version is never installed without the person pressing **Install and restart**.

How it stays safe: the check downloads one small file (`latest.json`). It is only believed if it was signed with your private key (the program holds only the matching public key). The installer it points to is only run if its size and SHA-256 fingerprint match what you signed, so a hacked web server can't push a fake update. Nothing from the person's documents is sent.

### One-time setup
1. On your PC, make the signing keys: `dotnet run --project tools/UpdateSigner -- keygen`. It prints a public key and a private key.
2. In GitHub: Settings, Secrets and variables, Actions, **New repository secret** named `UPDATE_SIGNING_KEY`, with the private key. Also keep a copy in a password manager: if it is lost, installed copies can no longer be updated automatically.
3. Put the public key and the web address of `latest.json` in `ReceiptCapture/Services/UpdateConfig.cs`.
4. In GitHub: Settings, Secrets and variables, Actions, Variables, **New repository variable** named `UPDATE_BASE_URL`: the https folder where you will host installers (for example `https://updates.example.com/aegisdocs`).

Until steps 3 and 4 are done, the Updates window says updates aren't set up.

### Releasing a version
1. Push a tag such as `v0.2.0`. The workflow builds the installer with that version number and a signed `latest.json` (the `AegisDocs-update` download, also attached to the GitHub Release). Builds that aren't from a tag are always numbered 0.1.0, so only tagged builds are for customers.
2. Upload the installer (`AegisDocs-Setup-0.2.0.exe`) to `UPDATE_BASE_URL`, then upload `latest.json` to the address from step 3 of the setup. Upload the installer first and `latest.json` last, so nobody is offered an installer that isn't there yet.
3. Installed copies offer the update within a day, or straight away from the Updates button.

## License
Aegis Docs is proprietary software. Copyright (c) 2026 Aegis Labs. All rights reserved. The license (`LICENSE.txt`) is shown, and must be accepted, when installing. It has three `[[FILL IN: ...]]` blanks (the company's legal name, the governing law, and a contact email); tag builds stop until they are filled in. Have a lawyer read it before you sell the program.

## Run the tests
`dotnet run --project ReceiptCapture.Tests -c Release`

This checks the sensitive-data finders, the compliance scanner, hidden-content inspection and cleaning, and the report and log writers. It needs no extra setup, and it runs on every build in GitHub Actions.

## Where things live
* `ReceiptCapture.Core/Redaction/`: the finders (text patterns in `SensitiveDataFinder`, values under their label in `LabeledFieldFinder`), box mapping, verification, and the redaction log.
* `ReceiptCapture.Core/Compliance/`: profiles, the scanner, folder scanning, value masking, and the report writer.
* `ReceiptCapture.Core/HiddenContent/`: reading and cleaning Word files, PDFs, and pictures.
* `ReceiptCapture/`: the Windows app (one view and view model per tab).

Third-party licences are listed in `THIRD-PARTY-NOTICES.md`.
