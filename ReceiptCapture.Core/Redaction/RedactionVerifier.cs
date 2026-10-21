namespace ReceiptCapture.Core.Redaction;

/// <summary>After redacting, re-read the output and check the redacted text really is gone.</summary>
public static class RedactionVerifier
{
    /// <summary>Returns each redacted text that can still be found in the text of an output page.</summary>
    public static List<string> FindLeaks(string outputPageText, IEnumerable<string> redactedTexts)
    {
        var haystack = Normalize(outputPageText);
        var leaks = new List<string>();

        foreach (var original in redactedTexts.Distinct())
        {
            var needle = Normalize(original);
            if (needle.Length >= 3 && haystack.Contains(needle, StringComparison.Ordinal))
                leaks.Add(original);
        }
        return leaks;
    }

    /// <summary>Lower-cases and keeps only letters and digits, so OCR spacing and punctuation differences don't matter.</summary>
    public static string Normalize(string text)
        => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
