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
             "Aegis Docs has four tabs along the top: Documents (batch PDF tools), Redact (black out private information), " +
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
             "Account, routing, license and passport numbers are found by the label printed next to them. " +
             "To also catch specific words or names, type them in the box underneath, one per line."),
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
            ("Always double-check",
             "Automatic finding is a helpful first pass, not a guarantee. Look through every page before you share the result."),
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
