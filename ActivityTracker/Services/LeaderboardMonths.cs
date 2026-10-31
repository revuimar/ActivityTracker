using System.Globalization;

namespace ActivityTracker.Services;

public sealed record LeaderboardMonths(DateOnly FirstMonth, DateOnly LatestMonth, DateOnly SelectedMonth,
    DateOnly ChallengeStart, DateOnly ChallengeEnd)
{
    public bool HasPrevious => SelectedMonth > FirstMonth;
    public bool HasNext => SelectedMonth < LatestMonth;
    public DateOnly PeriodStart => SelectedMonth < ChallengeStart ? ChallengeStart : SelectedMonth;
    public DateOnly CalendarEnd => new(SelectedMonth.Year, SelectedMonth.Month,
        DateTime.DaysInMonth(SelectedMonth.Year, SelectedMonth.Month));
    public DateOnly PeriodEnd => CalendarEnd > ChallengeEnd ? ChallengeEnd : CalendarEnd;
    public int EligibleDays => PeriodEnd.DayNumber - PeriodStart.DayNumber + 1;

    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    public static LeaderboardMonths? Select(DateOnly startsOn, DateOnly endsOn, DateOnly today,
        DateOnly anchor, string? requestedMonth)
    {
        if (startsOn > today || endsOn < startsOn) return null;
        var first = MonthOf(startsOn);
        var latest = MonthOf(endsOn < today ? endsOn : today);
        if (anchor < startsOn) anchor = startsOn;
        if (anchor > endsOn) anchor = endsOn;
        var selected = DateOnly.TryParseExact(requestedMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var requested) ? MonthOf(requested) : MonthOf(anchor);
        if (selected < first) selected = first;
        if (selected > latest) selected = latest;
        return new(first, latest, selected, startsOn, endsOn);
    }
}
