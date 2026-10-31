using ActivityTracker.Models;
namespace ActivityTracker.Services;
public record ScoreRow(string Name, int ActiveDays, int WorkoutPoints, int Bonus, int Total);
public static class Scoring
{
    // Week starts Monday; only the best eligible activity on each morning counts.
    public static List<ScoreRow> Weekly(IEnumerable<Participant> people, DateOnly monday, TimeOnly cutoff, int minimumMinutes = 15, DateOnly? startsOn = null, DateOnly? endsOn = null)
    {
        return Rank(people.Select(p => {
            var daily = DailyScores(p, monday, monday.AddDays(6), cutoff, minimumMinutes, startsOn, endsOn);
            var workout = daily.Sum(x => x.Points);
            var bonus = ConsistencyBonus(daily.Count);
            return new ScoreRow(p.DisplayName, daily.Count, workout, bonus, workout + bonus);
        }));
    }

    public static List<ScoreRow> Monthly(IEnumerable<Participant> people, DateOnly month, TimeOnly cutoff, int minimumMinutes = 15, DateOnly? startsOn = null, DateOnly? endsOn = null)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
        return Rank(people.Select(p => {
            var daily = DailyScores(p, first, last, cutoff, minimumMinutes, startsOn, endsOn);
            var workout = daily.Sum(x => x.Points);
            // Retain the existing weekly bonus rule. Boundary weeks count only days
            // inside this month AND the challenge, never days from adjacent months.
            var bonus = daily.GroupBy(x => LeaderboardWeeks.MondayOf(x.Day))
                .Sum(week => ConsistencyBonus(week.Count()));
            return new ScoreRow(p.DisplayName, daily.Count, workout, bonus, workout + bonus);
        }));
    }

    private static int ConsistencyBonus(int days) => days >= 7 ? 15 : days == 6 ? 10 : days == 5 ? 5 : 0;

    private static List<(DateOnly Day, int Points)> DailyScores(Participant person, DateOnly first, DateOnly last,
        TimeOnly cutoff, int minimumMinutes, DateOnly? startsOn, DateOnly? endsOn) => person.Activities
        .Where(a => {
            var day = DateOnly.FromDateTime(a.StartLocal);
            var finish = a.StartLocal.AddSeconds(a.ElapsedSeconds);
            return day >= first && day <= last &&
                   (!startsOn.HasValue || day >= startsOn.Value) && (!endsOn.HasValue || day <= endsOn.Value) &&
                   TimeOnly.FromDateTime(finish) <= cutoff && a.StartLocal.Date == finish.Date &&
                   a.MovingSeconds >= minimumMinutes * 60;
        })
        .GroupBy(a => DateOnly.FromDateTime(a.StartLocal))
        .Select(g => (g.Key, g.Max(a => a.MovingSeconds >= 45 * 60 ? 7 : a.MovingSeconds >= 30 * 60 ? 6 : 5)))
        .ToList();

    private static List<ScoreRow> Rank(IEnumerable<ScoreRow> rows) => rows.OrderByDescending(x => x.Total)
        .ThenByDescending(x => x.ActiveDays).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
