using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReceiptCapture;

/// <summary>The Help window (button in the top-right corner, or F1). Plain-language instructions for each tab.</summary>
public partial class HelpWindow : Window
{
    private sealed record Topic(string Title, (string Heading, string Body)[] Sections);

    // The help text lives here so it is easy to edit: one entry per topic, each with headed paragraphs.
    private static readonly Topic[] Topics =
    {
        new("Getting started", new[]
        {
            ("What this is",
             "Aegis Docs has eight tabs along the top: Documents (batch PDF tools), Redact (black out private information), " +
             "Compliance (check documents for sensitive information and save a report), " +
             "Compare (see exactly what changed between two versions of a document), " +
             "Integrity (prove a folder of files hasn't been altered), Shredder (destroy files for good), " +
             "Receipts (capture receipts and invoices) and Meetings (record a meeting and keep notes). " +
             "Everything happens on this PC. Nothing is uploaded anywhere."),
            ("Adding files",
             "Every tab accepts files two ways: use the button at the top left of the tab, or simply drag files from " +
             "Windows Explorer and drop them anywhere on the tab."),
            ("Your originals are safe",
             "The toolkit never changes your original files. Every tool saves a new copy."),
            ("Resizing the panels",
             "Each tab has a left and a right panel. Drag the gap between them to make either one wider or narrower."),
            ("Light and dark",
             "The Theme button in the top-right corner cycles between Auto (follows Windows), Light and Dark."),
            ("Help",
             "Press F1 at any time, or click the Help button, to open this window."),
        }),

        new("Documents", new[]
        {
            ("Building the list",
             "Click Add files…, or drop PDFs (or a whole folder of them) onto the tab. Use ↑ and ↓ to put them in the order " +
             "you want, Remove to take out the selected file, and Clear to start over. A password-protected PDF shows \"locked\" " +
             "in the Pages column and you're asked for its password when you use a tool; results made from it are not password-protected."),
            ("Choosing a tool",
             "The panel on the right has one tab per tool. Set it up, then press its button."),
            ("Merge",
             "Joins every PDF in the list into one file, in the order shown. Type a name for the merged file first."),
            ("Split",
             "Works on the PDF selected in the list. \"Extract page ranges\" makes one file per comma: \"1-3, 5, 8-\" makes " +
             "three files, and \"8-\" means page 8 to the end. \"Every N pages\" cuts the PDF into equal chunks."),
            ("Pages",
             "Works on the PDF selected in the list and saves a new copy. Pick what to do: keep only some pages in a new order " +
             "(type them as you want them, for example “3, 1, 2, 5-7”; a page can be repeated), delete pages (“2, 4-6”), " +
             "reverse the page order, or rotate pages (choose right, upside down or left; leave the box empty to turn every page)."),
            ("Shrink",
             "Makes every PDF in the list smaller by reducing the pictures inside it, so it is best for scans and photo-heavy files. " +
             "Text and drawings are left exactly as they are. “Smaller” keeps files good for reading and printing; “Smallest” makes " +
             "pictures noticeably softer. A file is only saved if it comes out clearly smaller. If nothing could be shrunk you'll be told, " +
             "and no file is made."),
            ("Bates",
             "Stamps a running page number at the bottom-right of every page. The numbering carries on from one file to the " +
             "next, in list order. Prefix and suffix are optional text around the number; Digits sets how many digits it has."),
            ("Watermark",
             "Draws light grey diagonal text, such as DRAFT or CONFIDENTIAL, across every page of every PDF in the list."),
            ("Rename",
             "Saves copies with new names. In the pattern, {name} is the original name, {n:000} is a running number " +
             "(000 means three digits) and {date} is today's date. The Preview box shows the result before you commit."),
            ("Where results go",
             "The box at the bottom right shows the output folder. By default it is a folder called Processed next to your " +
             "first file. Change… picks a different folder; Open folder shows you the results."),
        }),

        new("Redact", new[]
        {
            ("What it does",
             "Blacks out private information, such as Social Security numbers, phone numbers, and account numbers. " +
             "It works on scanned and digital PDFs alike, and also on Word documents and pictures (JPG, PNG, TIFF). " +
             "Click Open file… or drop a file on the tab to begin. The redacted copy is always saved as a PDF."),
            ("Passwords, Word files and pictures",
             "A password-protected PDF asks for its password when you open it; the password is used only to read the file and is never saved. " +
             "Word documents (.docx, .doc, .rtf) are first turned into a PDF on this PC using Microsoft Word, or LibreOffice if Word isn't installed; " +
             "remove a Word file's password first. A picture becomes one page (a multi-page TIFF becomes several). " +
             "The redacted copy is not password-protected, so protect it again before sending it if you need to."),
            ("Find automatically",
             "Tick the kinds of information you want found. The program marks every match on the page with a box. " +
             "Account, routing, license and passport numbers, and birth dates, are found by the label printed with them: " +
             "beside the value (\"DOB: 02/04/1977\"), or above it, as in a table header over a row of cells on an ID card or a form's field name over its box. " +
             "To also catch specific words or names, type them in the box underneath, one per line. " +
             "Names, signatures, photographs and barcodes can't be found automatically: an ID card's barcode repeats the details printed on it, " +
             "so draw a box over it by hand."),
            ("Review the boxes",
             "Every box found appears in the Review list on the right. Tick a box to include it or untick it to leave that " +
             "item visible. All and None change every box at once. A black box on the page will be blacked out; an outlined box " +
             "will be left alone."),
            ("Redact by hand",
             "You aren't limited to what the program finds. Press and drag the mouse on the page to draw your own box over " +
             "anything: a signature, a face, a name it missed. Click any box on the page to include or exclude it. " +
             "Right-click a box you drew yourself to delete it."),
            ("Zoom and move around",
             "Use the − and + buttons, or hold Ctrl and turn the mouse wheel, to zoom. Ctrl and + or − also zoom. " +
             "Fit (or Ctrl 0) fits the whole page in the window, and the percentage button returns to actual size. " +
             "Previous and Next change page. Scroll bars appear when the page is larger than the window."),
            ("Saving",
             "Save redacted copy… asks for a new file name; your original is never overwritten. Redacted pages are saved as " +
             "pictures, so whatever was under a box is truly gone. With Keep the copy searchable ticked, the words that were NOT " +
             "covered are added back as an invisible text layer so you can search and copy them; anything touching a box is left out. " +
             "Untick it for a copy that is pictures only. The program then re-checks the saved file, including that hidden text, " +
             "and tells you whether anything still shows."),
            ("The redaction log",
             "With Save a redaction log with the copy ticked, a log is saved next to the copy (\"… - redaction log.html\"). It records the date, who saved it, " +
             "a fingerprint (SHA-256) of the original and of the copy, how many items of each kind were redacted and how many were found but left visible, " +
             "a page-by-page count, and what the re-check of the saved copy found. It never contains the redacted text, so it can be filed with the copy. " +
             "It is a record of the steps taken, not a certificate: look over the redacted copy before you share it."),
            ("Always double-check",
             "Automatic finding is a helpful first pass, not a guarantee. Look through every page before you share the result."),
        }),

        new("Compliance", new[]
        {
            ("What it does",
             "Checks documents for sensitive information and tells you what it found and where, without changing the documents. " +
             "It works on scanned and digital PDFs, Word documents and pictures, one file or a whole folder at a time. " +
             "Use it to look over documents before they are shared or filed, then use the Redact tab to black out what you find."),
            ("Adding files and folders",
             "Click Add files… or Add folder…, or drop files or folders onto the tab. A folder adds the readable files inside it, including its subfolders " +
             "unless you untick Include subfolders. Up to 2,000 files are kept in one run. " +
             "A password-protected PDF asks for its password when it is scanned; a protected Word file has to have its password removed first. " +
             "While a scan runs, Stop finishes the file in progress and lists the rest as not scanned."),
            ("Choosing what to check against",
             "Pick a profile on the right: HIPAA Safe Harbor identifiers, PCI DSS cardholder data, financial (GLBA-style) personal information, " +
             "or general personal information. Under the picker, \"What it looks for\" lists exactly what that profile checks, and " +
             "\"What it can't find\" lists what you still have to check yourself, such as names, photographs and details written in ordinary sentences. " +
             "To catch specific names or words, type them in the box underneath, one per line."),
            ("Reading the results",
             "Press Scan files. Each file shows a result in the list. Click a file to see what was found in it. " +
             "The Items found tab lists each item: how serious it is (High, Medium or Low for that profile), where it is (a page and line, " +
             "or somewhere hidden such as Comments), what kind of item it is, and the value. Values are partly hidden on purpose, for example •••-••-6789. " +
             "\"Nothing found by this scan\" does not mean a document has no sensitive information, only that this check found none. " +
             "If a page had no readable text, the result says so: anything on that page was not checked."),
            ("Hidden content",
             "With Also look for hidden content ticked, each Word (.docx), PDF, JPEG and PNG file is also looked at for what it carries but doesn't show on its pages: " +
             "author names and other properties, comments, tracked changes (including deleted text that can still be read), text formatted as hidden, " +
             "attached and embedded files, scripts, page previews, GPS location and camera details in pictures, and earlier saved versions inside a PDF. " +
             "The Hidden content tab lists them. Any text found there, such as a comment or a deleted sentence, is checked for sensitive information too. " +
             "Older Word files (.doc, .rtf), password-protected PDFs and other picture types can't be looked inside, and the result says so."),
            ("Making clean copies",
             "After a scan, Clean copies… makes a clean copy of every file that has hidden content, in a folder you choose, keeping your folder layout. " +
             "Your originals are never changed. Word files lose the items ticked under What a clean copy removes. Pay attention to two of them: " +
             "accepting tracked changes removes deleted text for good, and removing hidden text deletes it; both change the document's text. " +
             "JPEG and PNG pictures lose their metadata without any change to the picture. " +
             "PDFs are rebuilt from pictures of their pages, which removes everything hidden in them; tick Keep rebuilt PDFs searchable to add the page text back as an invisible layer. " +
             "Each copy is looked at again afterwards and the message tells you if anything still shows. " +
             "A record of the run (file names, what was removed, what was still there, and a fingerprint of each original and copy) is saved as Cleanup log.csv in the same folder."),
            ("Saving a report",
             "Save report… makes a web page (it opens in any browser, and you can print it to PDF) or a CSV spreadsheet. " +
             "It lists the profile, the date, each file with a fingerprint (SHA-256) of the file as it was scanned, every item found, " +
             "the hidden content, and what the check cannot find. If you stopped the scan, it says so. Found values are partly hidden in it too. " +
             "It still names your files, so treat it as confidential."),
            ("Changing the profile",
             "If you switch profiles after a scan, the earlier results are cleared because they belong to the profile they were made with. Scan again."),
            ("A first pass, not a guarantee",
             "This check helps you review documents. It can miss things and it can flag things that are harmless, and it does not certify " +
             "that a document or your organization meets any law or standard. Have a person review the documents too."),
        }),

        new("Compare", new[]
        {
            ("What it does",
             "Shows every word that was added or removed between two versions of a document, for example a contract the other side sent back. " +
             "It writes a report where removed words are struck through in red and added words are underlined in green."),
            ("Choosing the two documents",
             "Drop two files on the tab (the first becomes the original), or use the two Choose… buttons. Swap exchanges them. " +
             "PDF, Word (.docx) and text (.txt) files work. For an older .doc or .rtf file, open it in Word and save a copy as .docx or PDF."),
            ("Reading the result",
             "Press Compare, then Open report to see it in your web browser (nothing is uploaded), or Save report… to keep it. " +
             "“Show only the changes” leaves out the parts that didn't change, apart from a little text around each change."),
            ("PDFs and accuracy",
             "PDFs are read with text recognition (OCR), so a few differences may be reading mistakes rather than real changes, " +
             "and big PDFs take a while. Comparing a PDF with a Word file is the least reliable pairing. " +
             "Only the words are compared, not formatting, images or layout, so treat the report as a reading aid and check anything important against the documents."),
            ("Keeping it private",
             "A saved report contains the full text of both documents. Keep it as confidential as they are."),
        }),

        new("Integrity", new[]
        {
            ("What it does",
             "Makes a list of every file in a folder with a SHA-256 fingerprint for each one. If even one character of a file changes, " +
             "its fingerprint changes completely. Later you check the folder against the list and see at once whether anything was changed, removed or added."),
            ("Make a manifest",
             "On the “Make a manifest” tab choose the folder, add a matter reference and your name if you like, and press Make manifest. " +
             "Save the manifest file somewhere safe. The report button saves a printable page or a spreadsheet of every file and fingerprint."),
            ("Pin down the date",
             "A manifest shows what the files looked like when it was made, but anyone could make a new one later, so by itself it doesn't prove when. " +
             "Press Copy fingerprint and send the list fingerprint to someone independent at the time, for example in an email to yourself or to the other side. " +
             "That fixes the date."),
            ("Check a folder",
             "On the “Check a folder” tab choose the manifest and the folder, and press Check. You'll see how many files are unchanged, changed, missing or new. " +
             "Save report… keeps the result. Record in custody log adds a line about the check to the manifest file; the list of files stays as it was."),
            ("If the manifest was edited",
             "A manifest knows its own fingerprint. If someone edits the list inside it, you'll be warned and shouldn't rely on it."),
        }),

        new("Shredder", new[]
        {
            ("What it does",
             "Destroys files and folders for good. Each file is overwritten with random data, emptied, renamed and then deleted. " +
             "It skips the Recycle Bin, so what you shred can't be brought back with undelete tools."),
            ("Using it",
             "Drop files or folders on the tab, or use Add files… and Add folder…. Nothing happens until you press Shred and confirm. " +
             "Remove and Clear only change the list on screen; they never touch your files. Stop finishes the file being shredded and then stops."),
            ("Overwrite passes",
             "One pass is enough on modern drives. Three is the default, and seven takes longer for no practical gain on most drives."),
            ("What it can't do",
             "On a solid-state drive (most laptops) the drive can keep old copies of data where no program can reach them, so full-disk encryption (BitLocker) " +
             "is the only complete protection there. Shredding also can't reach backups, copies in cloud folders such as OneDrive, or copies you emailed or saved elsewhere."),
            ("Safety",
             "Aegis Docs refuses to shred a whole drive, Windows or program folders, or your main folders (Documents, Desktop and so on) as a whole. " +
             "Files inside those folders can be shredded. Shortcuts and links are removed without touching what they point to."),
        }),

        new("Receipts", new[]
        {
            ("What it does",
             "Keeps a searchable record of your receipts and invoices. The program reads each file and fills in the vendor, " +
             "date and total for you to check."),
            ("Adding receipts",
             "Click Add files… or drop PNG, JPG or PDF files onto the tab. New entry lets you type one in by hand."),
            ("Checking what it found",
             "Click a receipt in the list to see it on the right: a preview on top and the details underneath. " +
             "An orange dot ● marks one that needs a look because the program wasn't sure about something. " +
             "Fix any field, then press Save (or Ctrl+S)."),
            ("Reading the original",
             "\"Text read from the file\" at the bottom of the details shows everything the program could read. " +
             "Open original opens the file itself in its usual program."),
            ("Finding receipts",
             "Type in the search box to look through vendors, notes, and the text on the receipts. You can also filter by " +
             "category and by date range. Clear resets all of the filters."),
            ("Exporting",
             "Export CSV saves the list as a spreadsheet file that opens in Excel. It respects the filters, so filter first " +
             "to export just one month or category."),
            ("Delete",
             "Delete removes the selected entry, and the toolkit's own stored copy of its file, after asking you to confirm. The original file you added stays where it was."),
        }),

        new("Meetings", new[]
        {
            ("What it does",
             "Records a meeting (in person, a phone call or a video call) and keeps the recording together with your notes. " +
             "Recordings are saved on this PC and are never uploaded."),
            ("Before you record",
             "Tell everyone they are being recorded. Many places, including several US states, require everyone's permission. " +
             "The program reminds you the first time you record each session."),
            ("Recording",
             "Choose your microphone, then press Start recording. The big number is how long you have recorded. " +
             "Pause stops the recording until you press Resume, and \"Stop and save\" finishes it. " +
             "The bars show what is being heard: if the Microphone bar never moves when you speak, check that the right " +
             "microphone is chosen and that Windows allows apps to use it (Windows Settings, Privacy & security, Microphone)."),
            ("Video calls",
             "Tick \"Also record the sound from this computer\" to capture the other people on a Zoom, Teams or phone-app call " +
             "along with your own voice. Wear headphones if you can: on speakers, your microphone also hears them, " +
             "which makes the recording sound echoey. Untick it for in-person meetings."),
            ("Transcripts",
             "A transcript is the meeting written out as text, with the time of each line. It is made on this PC, so the " +
             "recording is never sent anywhere. It works best with clear speech in English and takes a while: a long meeting can " +
             "take a good part of its own length, and the bar shows how far along it is. By default one is made automatically when " +
             "you stop recording. Otherwise open the Transcript tab and press Make transcript. You can Copy it, save it as a text " +
             "file, or redo it. Names and unusual terms may need correcting, so read it before you rely on it."),
            ("Notes",
             "Select the meeting (a new recording is selected automatically) and type in the Notes box on the right, even " +
             "while it is still recording. Notes and the title are saved as you go."),
            ("Past meetings",
             "Click a meeting to see it. Play recording opens it in your usual audio player, Show in folder finds the file, " +
             "and Delete permanently removes the recording and its notes."),
            ("If something goes wrong",
             "If the program closes unexpectedly mid-recording, the audio up to that moment is kept and the meeting is " +
             "marked Recovered the next time you open Aegis Docs. Closing the window during a recording asks you first, " +
             "then saves it."),
            ("Where the files are",
             "Each meeting has its own folder under the program's data folder, containing a standard audio file (WAV), " +
             "its notes and its transcript. Use Show in folder to open it."),
        }),

        new("Shortcuts", new[]
        {
            ("Everywhere", "F1: open Help."),
            ("Redact tab",
             "Ctrl + mouse wheel: zoom in and out.\nCtrl and +: zoom in.\nCtrl and −: zoom out.\nCtrl and 0: fit the page to the window.\n" +
             "Drag on the page: draw a box.\nClick a box: include or exclude it.\nRight-click a hand-drawn box: delete it."),
            ("Receipts tab", "Ctrl+S: save the receipt you are editing."),
        }),
    };

    public HelpWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => AppTheme.ApplyTitleBar(this);
        AppTheme.Changed += OnThemeChanged;
        Closed += (_, _) => AppTheme.Changed -= OnThemeChanged;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        TopicList.ItemsSource = Topics.Select(t => t.Title).ToList();
        TopicList.SelectedIndex = 0;
    }

    private void OnThemeChanged() => AppTheme.ApplyTitleBar(this);

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = TopicList.SelectedIndex;
        if (index < 0) return;

        var topic = Topics[index];
        ContentPanel.Children.Clear();

        var title = new TextBlock { Text = topic.Title, FontSize = 22, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        ContentPanel.Children.Add(title);

        foreach (var (heading, body) in topic.Sections)
        {
            var h = new TextBlock
            {
                Text = heading,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 4),
            };
            h.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

            var b = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            b.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");

            ContentPanel.Children.Add(h);
            ContentPanel.Children.Add(b);
        }

        ContentScroller.ScrollToTop();
    }
}
