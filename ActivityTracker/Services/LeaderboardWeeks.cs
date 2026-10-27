using System.Globalization;

namespace ActivityTracker.Services;

public sealed record LeaderboardWeeks(DateOnly FirstMonday, DateOnly LatestMonday, DateOnly SelectedMonday)
{
    public bool HasPrevious => SelectedMonday > FirstMonday;
    public bool HasNext => SelectedMonday < LatestMonday;
    public int Number => (SelectedMonday.DayNumber - FirstMonday.DayNumber) / 7 + 1;
    public int Count => (LatestMonday.DayNumber - FirstMonday.DayNumber) / 7 + 1;

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-((int)date.DayOfWeek + 6) % 7);

    public static LeaderboardWeeks? Select(DateOnly startsOn, DateOnly endsOn, DateOnly today, string? requestedWeek)
    {
        if (startsOn > today || endsOn < startsOn) return null;
        var first = MondayOf(startsOn);
        var latest = MondayOf(endsOn < today ? endsOn : today);
        var selected = DateOnly.TryParseExact(requestedWeek, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var requested) ? MondayOf(requested) : latest;
        if (selected < first) selected = first;
        if (selected > latest) selected = latest;
        return new(first, latest, selected);
    }
}
