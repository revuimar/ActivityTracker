using ActivityTracker.Models;
namespace ActivityTracker.Services;
public record ScoreRow(string Name, int ActiveDays, int WorkoutPoints, int Bonus, int Total);
public static class Scoring
{
    // Week starts Monday; only the best eligible activity on each morning counts.
    public static List<ScoreRow> Weekly(IEnumerable<Participant> people, DateOnly monday, TimeOnly cutoff, int minimumMinutes = 15, DateOnly? startsOn = null, DateOnly? endsOn = null)
    {
        var end = monday.AddDays(7);
        return people.Select(p => {
            var daily = p.Activities
                .Where(a => {
                    var day = DateOnly.FromDateTime(a.StartLocal);
                    return day >= monday && day < end &&
                           (!startsOn.HasValue || day >= startsOn.Value) &&
                           (!endsOn.HasValue || day <= endsOn.Value) &&
                           TimeOnly.FromDateTime(a.StartLocal.AddSeconds(a.ElapsedSeconds)) <= cutoff &&
                           a.StartLocal.Date == a.StartLocal.AddSeconds(a.ElapsedSeconds).Date &&
                           a.MovingSeconds >= minimumMinutes * 60;
                })
                .GroupBy(a => DateOnly.FromDateTime(a.StartLocal))
                .Select(g => g.Max(a => a.MovingSeconds >= 45*60 ? 7 : a.MovingSeconds >= 30*60 ? 6 : 5))
                .ToList();
            int n = daily.Count;
            int bonus = n >= 7 ? 15 : n == 6 ? 10 : n == 5 ? 5 : 0;
            return new ScoreRow(p.DisplayName, n, daily.Sum(), bonus, daily.Sum()+bonus);
        }).OrderByDescending(x => x.Total).ThenByDescending(x => x.ActiveDays).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
