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
