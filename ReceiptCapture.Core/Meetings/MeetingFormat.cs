using System.Globalization;

namespace ReceiptCapture.Core.Meetings;

public static class MeetingFormat
{
    /// <summary>12:05 for under an hour, 1:02:05 for an hour or more.</summary>
    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        int hours = (int)time.TotalHours;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, time.Minutes, time.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", time.Minutes, time.Seconds);
    }

    /// <summary>"Meeting 2026-10-22 15:15": sorts naturally and tells meetings apart.</summary>
    public static string DefaultTitle(DateTime start)
        => "Meeting " + start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
