using ReceiptCapture.Core.Compliance;

namespace ReceiptCapture.Core.HiddenContent;

public enum HiddenCategory
{
    DocumentProperties, Comments, TrackedChanges, HiddenText, Attachments, Scripts, Location, PreviewImage, SavedVersions, Other,
}

public static class HiddenCategoryInfo
{
    public static string DisplayName(HiddenCategory category) => category switch
    {
        HiddenCategory.DocumentProperties => "Document properties",
        HiddenCategory.Comments => "Comments",
        HiddenCategory.TrackedChanges => "Tracked changes",
        HiddenCategory.HiddenText => "Hidden text",
        HiddenCategory.Attachments => "Attachments",
        HiddenCategory.Scripts => "Scripts",
        HiddenCategory.Location => "Location data",
        HiddenCategory.PreviewImage => "Preview image",
        HiddenCategory.SavedVersions => "Earlier versions",
        _ => "Other",
    };
}

/// <summary>One piece of hidden content found in a file. <see cref="What"/> never contains a full value (names are partly hidden).</summary>
public sealed record HiddenItem(HiddenCategory Category, FindingSeverity Severity, string What, string Where)
{
    /// <summary>The kind in words, for lists.</summary>
    public string CategoryName => HiddenCategoryInfo.DisplayName(Category);
}

/// <summary>Text that is stored in a file but not shown on its pages (a comment, deleted text, a property...). It is run through the same sensitive-data checks as page text.</summary>
public sealed record HiddenText(string Area, string Text);

/// <summary>What looking inside a file turned up.</summary>
public sealed class HiddenInspection
{
    public HiddenInspection(IReadOnlyList<HiddenItem> items, IReadOnlyList<HiddenText> texts, string? note = null, bool wasChecked = true)
    {
        Items = items;
        Texts = texts;
        Note = note;
        WasChecked = wasChecked;
    }

    /// <summary>A file of a kind that can't be looked inside; the note says so.</summary>
    public static HiddenInspection NotChecked(string note)
        => new(Array.Empty<HiddenItem>(), Array.Empty<HiddenText>(), note, wasChecked: false);

    /// <summary>False when this kind of file can't be inspected (the result must not be read as "nothing hidden").</summary>
    public bool WasChecked { get; }
    public IReadOnlyList<HiddenItem> Items { get; }
    public IReadOnlyList<HiddenText> Texts { get; }
    public string? Note { get; }
}

/// <summary>What a clean copy should remove. Everything is on by default.</summary>
public sealed class CleanOptions
{
    public bool DocumentProperties { get; init; } = true;
    public bool Comments { get; init; } = true;
    /// <summary>Accepts every tracked change: deleted text is removed for good and inserted text stays. This changes the document's text.</summary>
    public bool TrackedChanges { get; init; } = true;
    /// <summary>Deletes text formatted as hidden. This changes the document's text.</summary>
    public bool HiddenText { get; init; } = true;
    public bool Attachments { get; init; } = true;
    public bool PreviewImages { get; init; } = true;
    public bool PictureMetadata { get; init; } = true;

    public bool Includes(HiddenCategory category) => category switch
    {
        HiddenCategory.DocumentProperties => DocumentProperties,
        HiddenCategory.Comments => Comments,
        HiddenCategory.TrackedChanges => TrackedChanges,
        HiddenCategory.HiddenText => HiddenText,
        HiddenCategory.Attachments => Attachments,
        HiddenCategory.PreviewImage => PreviewImages,
        HiddenCategory.Location => PictureMetadata,
        _ => true,
    };
}

/// <summary>What a cleaning run did to one file.</summary>
public sealed record CleanResult(IReadOnlyList<string> Removed, string? Note = null);

internal static class MarkupText
{
    private static readonly System.Text.RegularExpressions.Regex Tags = new("<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Spaces = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The words in a piece of XML (such as an XMP metadata block), without the tags and attributes around them.</summary>
    public static string Strip(string xml)
        => Spaces.Replace(System.Net.WebUtility.HtmlDecode(Tags.Replace(xml, " ")), " ").Trim();
}
