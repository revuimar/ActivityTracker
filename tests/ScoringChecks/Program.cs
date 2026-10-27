using ActivityTracker.Models;
using ActivityTracker.Services;
var monday = new DateOnly(2026, 10, 26);
Activity Workout(int day, int minutes, int hour = 8) => new() {
    StartLocal = monday.ToDateTime(new TimeOnly(hour, 0)).AddDays(day),
    MovingSeconds = minutes * 60, ElapsedSeconds = minutes * 60
};
void Check(string name, IEnumerable<Activity> activities, int days, int workout, int bonus) {
    var person = new Participant { DisplayName = name, Activities = activities.ToList() };
    var row = Scoring.Weekly([person], monday, new TimeOnly(11, 0)).Single();
    if (row.ActiveDays != days || row.WorkoutPoints != workout || row.Bonus != bonus || row.Total != workout + bonus)
        throw new Exception($"Scoring failed: {name}: {row}");
    Console.WriteLine($"PASS {name}");
}
Check("thresholds", [Workout(0,14), Workout(1,15), Workout(2,30), Workout(3,45)], 3,18,0);
Check("best per day", [Workout(0,15), Workout(0,45)],1,7,0);
Check("cutoff inclusive", [Workout(0,60,10), Workout(1,61,10)],1,7,0);
Check("overnight excluded", [Workout(0,120,23)],0,0,0);
Check("week boundaries", [Workout(-1,30), Workout(7,30)],0,0,0);
foreach (var (days, bonus) in new[] { (5,5), (6,10), (7,15) })
    Check($"{days} day bonus", Enumerable.Range(0,days).Select(d=>Workout(d,15)),days,days*5,bonus);
Console.WriteLine("All scoring checks passed.");

var zero = Scoring.Weekly([new Participant { DisplayName = "No workouts" }], monday, new TimeOnly(11,0)).Single();
if (zero.Total != 0 || zero.ActiveDays != 0) throw new Exception("Accounts without workouts must appear at zero.");
var filtered = new Participant { Activities = [Workout(0,15), Workout(1,30), Workout(2,45)] };
var custom = Scoring.Weekly([filtered], monday, new TimeOnly(11,0), 30, monday.AddDays(2), monday.AddDays(6)).Single();
if (custom.ActiveDays != 1 || custom.WorkoutPoints != 7) throw new Exception("Leaderboard dates/minimum must apply.");
Console.WriteLine("PASS zero-score accounts and leaderboard date/duration rules");

var today = new DateOnly(2026, 10, 27);
void WeekCheck(bool condition, string name) {
    if (!condition) throw new Exception("Week navigation failed: " + name);
    Console.WriteLine("PASS " + name);
}
var weeks = LeaderboardWeeks.Select(new(2026, 10, 7), new(2026, 11, 30), today, null)!;
WeekCheck(weeks.SelectedMonday == monday && weeks.FirstMonday == new DateOnly(2026, 10, 5) &&
    weeks.Number == 4 && weeks.Count == 4 && weeks.HasPrevious && !weeks.HasNext, "default current week and partial first week");
weeks = LeaderboardWeeks.Select(new(2026, 10, 7), new(2026, 11, 30), today, "2026-10-18")!;
WeekCheck(weeks.SelectedMonday == new DateOnly(2026, 10, 12) && weeks.HasPrevious && weeks.HasNext,
    "date in a past week resolves to Monday and permits both directions");
WeekCheck(LeaderboardWeeks.Select(new(2026, 10, 7), new(2026, 11, 30), today, "0001-01-01") is { HasPrevious: false, Number: 1 }, "early query clamps to first week");
WeekCheck(LeaderboardWeeks.Select(new(2026, 10, 7), new(2026, 11, 30), today, "9999-12-31")?.SelectedMonday == monday, "future query clamps to latest available week");
WeekCheck(LeaderboardWeeks.Select(new(2026, 10, 7), new(2026, 11, 30), today, "invalid")?.SelectedMonday == monday, "malformed query falls back safely");
weeks = LeaderboardWeeks.Select(new(2026, 9, 30), new(2026, 10, 15), today, null)!;
WeekCheck(weeks.SelectedMonday == new DateOnly(2026, 10, 12) && !weeks.HasNext, "ended leaderboard defaults to final week");
WeekCheck(LeaderboardWeeks.Select(new(2026, 11, 1), new(2026, 11, 30), today, null) is null, "future leaderboard has no browsable weeks");
WeekCheck(LeaderboardWeeks.Select(today, today, today, null) is { Count: 1, HasPrevious: false, HasNext: false }, "single-week leaderboard has no pagination links");
WeekCheck(LeaderboardWeeks.MondayOf(new(2027, 1, 3)) == new DateOnly(2026, 12, 28), "week navigation crosses year boundaries");
var historyPerson = new Participant { DisplayName = "Historical runner", Activities = [Workout(-7, 45), Workout(0, 30)] };
var past = Scoring.Weekly([historyPerson], monday.AddDays(-7), new TimeOnly(11, 0)).Single();
var present = Scoring.Weekly([historyPerson], monday, new TimeOnly(11, 0)).Single();
WeekCheck(past.Total == 7 && present.Total == 6, "past and current weeks score only their own activities");
