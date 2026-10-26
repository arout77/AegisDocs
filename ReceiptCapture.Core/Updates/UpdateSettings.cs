using System.Text.Json;

namespace ReceiptCapture.Core.Updates;

/// <summary>The person's update choices and when the program last looked. Kept in its own small file next to the other settings.</summary>
public sealed class UpdateSettings
{
    /// <summary>Look for a new version when the program starts (at most once a day).</summary>
    public bool AutoCheck { get; set; } = true;

    public DateTimeOffset? LastCheck { get; set; }

    /// <summary>A version the person chose "Not now" for, so the start-up check doesn't ask again until a newer one appears.</summary>
    public string? SkippedVersion { get; set; }

    public bool IsCheckDue(DateTimeOffset now, TimeSpan interval)
        => LastCheck is null || now - LastCheck.Value >= interval || LastCheck.Value > now;

    public static UpdateSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }

        return new UpdateSettings();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a settings file isn't worth an error
    }
}
