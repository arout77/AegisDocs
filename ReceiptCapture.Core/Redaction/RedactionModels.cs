namespace ReceiptCapture.Core.Redaction;

/// <summary>One word read by OCR, with its position in pixels on the page image.</summary>
public sealed record WordBox(string Text, double X, double Y, double Width, double Height, int Line);

/// <summary>A page rendered to an image, plus the words OCR found on it.</summary>
public sealed record ScannedPage(
    byte[] Png, int PixelWidth, int PixelHeight, double WidthPoints, double HeightPoints,
    IReadOnlyList<WordBox> Words);

/// <summary>A rectangle (in page-image pixels) that will be blacked out if Approved.</summary>
public sealed class RedactionRegion : ObservableObject
{
    private bool _approved = true;

    public RedactionRegion(int page, SensitiveKind? kind, string matchedText, double x, double y, double width, double height)
    {
        Page = page;
        Kind = kind;
        MatchedText = matchedText;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int Page { get; }                 // 0-based
    public SensitiveKind? Kind { get; }      // null = drawn by hand
    public string MatchedText { get; }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public bool IsManual => Kind == null;

    public bool Approved
    {
        get => _approved;
        set => SetProperty(ref _approved, value);
    }

    public string Description => Kind switch
    {
        null => "Hand-drawn box",
        SensitiveKind.Ssn => "SSN",
        SensitiveKind.Phone => "Phone",
        SensitiveKind.Email => "Email",
        SensitiveKind.CreditCard => "Card number",
        SensitiveKind.DateOfBirth => "Date of birth",
        SensitiveKind.CustomTerm => "Your term",
        _ => "Item",
    };

    public string Label => IsManual
        ? $"p{Page + 1} · Hand-drawn box"
        : $"p{Page + 1} · {Description} · {MatchedText.Replace('\n', ' ')}";
}
