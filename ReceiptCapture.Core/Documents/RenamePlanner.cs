using System.Globalization;
using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Documents;

public sealed record RenameItem(string SourcePath, string NewName);

/// <summary>
/// Plans bulk renames from a pattern such as "{n:000}_{name}".
/// Tokens: {name} original name without extension, {n} or {n:000} running number, {date} or {date:yyyyMMdd}.
/// The original extension is always kept.
/// </summary>
public static class RenamePlanner
{
    private const int MaxBaseLength = 150;

    private static readonly Regex TokenRx = new(
        @"\{(?<tok>name|n|date)(?::(?<fmt>[^}]*))?\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Apply(string pattern, string fileName, long number, DateTime today)
    {
        var originalBase = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        var built = TokenRx.Replace(pattern, m =>
        {
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            switch (m.Groups["tok"].Value.ToLowerInvariant())
            {
                case "name":
                    return originalBase;
                case "n":
                    return Format(number, fmt);
                default: // date
                    return Format(today, string.IsNullOrEmpty(fmt) ? "yyyy-MM-dd" : fmt);
            }
        });

        return Sanitize(built) + extension;
    }

    /// <summary>One new name per source file, numbered from <paramref name="start"/>, with duplicates made unique.</summary>
    public static List<RenameItem> Plan(IReadOnlyList<string> sourcePaths, string pattern, long start, DateTime today)
    {
        var result = new List<RenameItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            var source = sourcePaths[i];
            var name = Apply(pattern, Path.GetFileName(source), start + i, today);
            var unique = name;

            for (int copy = 2; !used.Add(unique); copy++)
                unique = Path.GetFileNameWithoutExtension(name) + $" ({copy})" + Path.GetExtension(name);

            result.Add(new RenameItem(source, unique));
        }
        return result;
    }

    private static string Format(long number, string? format)
    {
        try { return number.ToString(string.IsNullOrEmpty(format) ? "0" : format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return number.ToString(CultureInfo.InvariantCulture); }
    }

    private static string Format(DateTime date, string format)
    {
        try { return date.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
    }

    // Always block Windows' forbidden characters, whatever OS the code happens to run on.
    private static readonly char[] WindowsInvalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static bool IsInvalid(char c) => c < 32 || WindowsInvalid.Contains(c) || Path.GetInvalidFileNameChars().Contains(c);

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => IsInvalid(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (cleaned.Length > MaxBaseLength) cleaned = cleaned[..MaxBaseLength].TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "file" : cleaned;
    }
}
