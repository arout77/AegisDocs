namespace ReceiptCapture.Workflows;

/// <summary>
/// A saved recipe: "when a file lands in this folder, check it against this profile, make clean copies of the ones with
/// hidden content, and put the copies, a report and a log in that folder." Plain data so it saves as JSON.
/// </summary>
public sealed class Workflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New workflow";

    // Where
    public string WatchFolder { get; set; } = "";
    public bool IncludeSubfolders { get; set; } = true;
    public string OutputFolder { get; set; } = "";

    /// <summary>True while the folder is being watched (only while Aegis Docs is open).</summary>
    public bool WatchEnabled { get; set; }

    // What to check
    public string ProfileName { get; set; } = "";
    public string CustomTerms { get; set; } = "";
    public bool CheckHiddenContent { get; set; } = true;

    // What to produce
    public bool MakeCleanCopies { get; set; } = true;
    public bool SaveReports { get; set; } = true;

    // What a clean copy removes (same choices as the Compliance tab)
    public bool CleanProperties { get; set; } = true;
    public bool CleanComments { get; set; } = true;
    public bool CleanTrackedChanges { get; set; } = true;
    public bool CleanHiddenText { get; set; } = true;
    public bool CleanAttachments { get; set; } = true;
    public bool CleanPreviews { get; set; } = true;
    public bool CleanPictures { get; set; } = true;
    public bool KeepPdfSearchable { get; set; } = true;

    public Workflow Copy(string newName)
    {
        var copy = (Workflow)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = newName;
        copy.WatchEnabled = false;      // a copy never starts watching until the person turns it on
        return copy;
    }

    public string[] Terms() =>
        CustomTerms.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Why this workflow can't be switched on yet, or null if it can.</summary>
    public string? ProblemWithFolders()
    {
        if (string.IsNullOrWhiteSpace(WatchFolder)) return "Choose the folder to watch first.";
        if (!Directory.Exists(WatchFolder)) return "The folder to watch isn't there any more:\n" + WatchFolder;
        if (string.IsNullOrWhiteSpace(OutputFolder)) return "Choose where the results should go first.";

        var watch = Normalize(WatchFolder);
        var output = Normalize(OutputFolder);
        if (string.Equals(watch, output, StringComparison.OrdinalIgnoreCase))
            return "The results folder can't be the same as the watched folder, or every result would be checked again.";
        if (watch.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "The watched folder can't be inside the results folder.";
        return null;
    }

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
