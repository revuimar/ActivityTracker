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

Activity OnDate(DateOnly day, int minutes = 15, int hour = 8) => new() {
    StartLocal = day.ToDateTime(new TimeOnly(hour, 0)), MovingSeconds = minutes * 60, ElapsedSeconds = minutes * 60
};
var october = new DateOnly(2026, 10, 1);
var fullMonthPerson = new Participant {
    DisplayName = "Consistent runner",
    Activities = Enumerable.Range(0, 31).Select(d => OnDate(october.AddDays(d))).ToList()
};
var monthly = Scoring.Monthly([fullMonthPerson], october, new TimeOnly(11, 0)).Single();
WeekCheck(monthly.ActiveDays == 31 && monthly.WorkoutPoints == 155 && monthly.Bonus == 55 && monthly.Total == 210,
    "monthly totals sum daily points and separate weekly bonuses");
monthly = Scoring.Monthly([fullMonthPerson], october, new TimeOnly(11, 0), 15, october.AddDays(9), october.AddDays(19)).Single();
WeekCheck(monthly.ActiveDays == 11 && monthly.WorkoutPoints == 55 && monthly.Bonus == 15 && monthly.Total == 70,
    "partial challenge month clips both points and weekly bonuses");
var crossingMonth = new Participant { Activities = Enumerable.Range(0, 7).Select(d => OnDate(new DateOnly(2026, 9, 28).AddDays(d))).ToList() };
monthly = Scoring.Monthly([crossingMonth], october, new TimeOnly(11, 0)).Single();
WeekCheck(monthly.ActiveDays == 4 && monthly.Total == 20 && monthly.Bonus == 0,
    "days in the previous month cannot inflate a boundary week's monthly bonus");
var monthlyLimits = new Participant { Activities = [OnDate(october, 15), OnDate(october, 45), OnDate(october.AddDays(1), 14),
    OnDate(october.AddDays(2), 60, 10), OnDate(october.AddDays(3), 61, 10), OnDate(october.AddDays(4), 120, 23),
    OnDate(october.AddDays(-1), 45), OnDate(october.AddMonths(1), 45)] };
monthly = Scoring.Monthly([monthlyLimits], october, new TimeOnly(11, 0)).Single();
WeekCheck(monthly.ActiveDays == 2 && monthly.WorkoutPoints == 14 && monthly.Bonus == 0,
    "monthly scoring preserves best-per-day, duration, cutoff and calendar boundaries");
WeekCheck(Scoring.Monthly([new Participant { DisplayName = "No workouts" }], october, new TimeOnly(11, 0)).Single().Total == 0,
    "monthly table retains participants without workouts");
var months = LeaderboardMonths.Select(october.AddDays(9), october.AddDays(19), today, today, null)!;
WeekCheck(months.PeriodStart == october.AddDays(9) && months.PeriodEnd == october.AddDays(19) && months.EligibleDays == 11 &&
    !months.HasPrevious && !months.HasNext, "monthly range clips to challenge dates");
months = LeaderboardMonths.Select(october, new(2099, 12, 31), today, today, "2099-12")!;
WeekCheck(months.SelectedMonth == october && !months.HasNext, "monthly navigation cannot enter future months");
months = LeaderboardMonths.Select(october, new(2099, 12, 31), today, new(2026, 9, 28), null)!;
WeekCheck(months.SelectedMonth == october, "first partial week chooses a month inside the challenge");
months = LeaderboardMonths.Select(new(2028, 1, 1), new(2028, 3, 31), new(2028, 3, 1), new(2028, 3, 1), "2028-02")!;
WeekCheck(months.EligibleDays == 29 && months.PeriodEnd == new DateOnly(2028, 2, 29), "calendar months handle leap years");
months = LeaderboardMonths.Select(new(2026, 1, 1), new(2027, 12, 31), new(2027, 1, 1), new(2027, 1, 1), "2026-12")!;
WeekCheck(months.SelectedMonth == new DateOnly(2026, 12, 1) && months.PeriodEnd == new DateOnly(2026, 12, 31), "month navigation crosses year boundaries");
WeekCheck(LeaderboardMonths.Select(october, new(2099, 12, 31), today, today, "bad-month")?.SelectedMonth == october,
    "invalid month falls back safely");
WeekCheck(LeaderboardMonths.Select(october, new(2099, 12, 31), today, today, "0001-01") is { HasPrevious: false },
    "month navigation stops at challenge start");
WeekCheck(LeaderboardMonths.Select(new(2026, 11, 1), new(2026, 12, 31), today, today, null) is null,
    "future challenge has no monthly rankings yet");
