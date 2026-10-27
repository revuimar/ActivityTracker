using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ActivityTracker.Data;
using ActivityTracker.Models;
using ActivityTracker.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

static void Check(bool condition, string name) {
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["Strava:ClientId"] = "123", ["Strava:ClientSecret"] = "test-only", ["Strava:ImportDays"] = "90"
}).Build();
var protection = new EphemeralDataProtectionProvider();
var protector = protection.CreateProtector("StravaTokens.v1");
Participant Person() => new() {
    Id = 1, StravaAthleteId = 77, DisplayName = "Sync athlete", StravaActivityReadGranted = true,
    AccessToken = protector.Protect("old-access"), RefreshToken = protector.Protect("old-refresh"),
    TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
};
var activityJson = """
    {"id":501,"athlete":{"id":77},"start_date":"2026-10-26T07:30:00Z","start_date_local":"2026-10-26T08:30:00Z",
    "moving_time":1800,"elapsed_time":2100,"sport_type":"Run","private":false}
    """;
using (var document = JsonDocument.Parse(activityJson)) {
    var activity = StravaApi.ParseActivity(document.RootElement, 1);
    Check(activity.StartLocal.Hour == 8 && activity.StartLocal.Kind == DateTimeKind.Unspecified && activity.StartUtc.Hour == 7,
        "Strava local clock is preserved separately from UTC");
    Check(activity.MovingSeconds == 1800 && activity.ElapsedSeconds == 2100, "moving and elapsed duration remain distinct");
    Check(Scoring.Weekly([new Participant { DisplayName = "Runner", Activities = [activity] }], new DateOnly(2026, 10, 26), new TimeOnly(11, 0))[0].Total == 6,
        "imported workout contributes the expected leaderboard points");
}
using (var document = JsonDocument.Parse("{\"private\":true}")) Check(StravaApi.IsPrivate(document.RootElement), "Only You workouts excluded");
Check(StravaEndpoints.HasActivityScope("read,activity:read") && StravaEndpoints.HasActivityScope("activity:read_all") &&
    !StravaEndpoints.HasActivityScope("read,activity:write"), "activity permission required explicitly");
var now = new DateTimeOffset(2026, 10, 27, 10, 7, 0, TimeSpan.Zero);
using (var response = Json("{}", HttpStatusCode.TooManyRequests)) {
    response.Headers.Add("X-ReadRateLimit-Limit", "100,1000");
    response.Headers.Add("X-ReadRateLimit-Usage", "100,200");
    Check(StravaRateGate.RateDelay(response, now) == TimeSpan.FromMinutes(8).Add(TimeSpan.FromSeconds(2)), "15-minute rate reset honored");
    response.Headers.Remove("X-ReadRateLimit-Usage");
    response.Headers.Add("X-ReadRateLimit-Usage", "1,1000");
    Check(StravaRateGate.RateDelay(response, now) == now.UtcDateTime.Date.AddDays(1).Subtract(now.UtcDateTime).Add(TimeSpan.FromSeconds(2)),
        "daily rate reset honored");
}
var saved = false;
var person = Person();
person.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
var fake = new FakeHttp(async request => {
    if (request.RequestUri!.AbsolutePath == "/oauth/token") {
        var body = await request.Content!.ReadAsStringAsync();
        Check(body.Contains("refresh_token=old-refresh"), "protected refresh token decrypted only for Strava request");
        return Json($$"""{"access_token":"new-access","refresh_token":"new-refresh","expires_at":{{DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds()}}} """);
    }
    Check(saved && protector.Unprotect(person.RefreshToken!) == "new-refresh", "rotated refresh token saved before fetching data");
    Check(request.Headers.Authorization?.Parameter == "new-access", "refreshed token used for API request");
    return Json("[]");
});
var api = new StravaApi(fake, protection, config, new StravaRateGate());
using (await api.GetAsync(person, "athlete/activities", () => { saved = true; return Task.CompletedTask; }, default)) { }
var calls = 0;
fake = new FakeHttp(_ => Task.FromResult(++calls == 1 ? Json("{}", HttpStatusCode.Unauthorized) :
    calls == 2 ? Json($$"""{"access_token":"fresh","refresh_token":"rotated","expires_at":{{DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds()}}}""") : Json("[]")));
api = new StravaApi(fake, protection, config, new StravaRateGate());
using (await api.GetAsync(Person(), "athlete/activities", () => Task.CompletedTask, default)) { }
Check(calls == 3, "401 triggers one refresh and retries the original request");
fake = new FakeHttp(_ => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
api = new StravaApi(fake, protection, config, new StravaRateGate());
try {
    using var unexpected = await api.GetAsync(Person(), "athlete/activities", () => Task.CompletedTask, default);
    throw new Exception("Expected rate limit retry");
} catch (StravaRetryException error) { Check(error.Delay >= TimeSpan.FromMinutes(15), "429 schedules retry instead of disconnecting"); }

var database = Environment.GetEnvironmentVariable("STRAVA_TEST_DATABASE");
if (string.IsNullOrEmpty(database)) {
    Console.WriteLine("Database checks not run: set STRAVA_TEST_DATABASE (CI runs these against disposable PostgreSQL).");
    return;
}
// Own database keeps this suite separate from account/membership tests and production data.
var connection = new NpgsqlConnectionStringBuilder(database);
Check(connection.Host is "127.0.0.1" or "localhost" && connection.Database == "activitytracker", "database tests restricted to local disposable CI database");
await using (var admin = new NpgsqlConnection(connection.ConnectionString)) {
    await admin.OpenAsync();
    await using var create = new NpgsqlCommand("CREATE DATABASE activitytracker_strava_checks", admin);
    await create.ExecuteNonQueryAsync();
}
connection.Database = "activitytracker_strava_checks";
var factory = new PooledDbContextFactory<TrackerDb>(new DbContextOptionsBuilder<TrackerDb>().UseNpgsql(connection.ConnectionString).Options);
await using (var db = await factory.CreateDbContextAsync()) {
    await SchemaUpgrade.ApplyAsync(db);
    await SchemaUpgrade.ApplyAsync(db);
    person = Person();
    db.Participants.Add(person);
    await db.SaveChangesAsync();
    await StravaQueue.EnqueueAsync(db, person.Id);
    await StravaQueue.EnqueueAsync(db, person.Id);
    Check(await db.StravaSyncJobs.CountAsync() == 1 && (await db.StravaSyncJobs.SingleAsync()).Version == 2,
        "duplicate jobs coalesce durably after repeatable schema upgrade");
}
var missing = false;
var privateActivity = false;
var revoked = false;
var listCalls = 0;
fake = new FakeHttp(_ => {
    var path = _.RequestUri!.AbsolutePath;
    if (revoked) return Task.FromResult(path == "/oauth/token"
        ? Json("""{"errors":[{"resource":"RefreshToken","field":"refresh_token","code":"invalid"}]}""", HttpStatusCode.BadRequest)
        : Json("{}", HttpStatusCode.Unauthorized));
    if (path.EndsWith("/athlete/activities")) {
        listCalls++;
        var pageTwo = _.RequestUri.Query.Contains("&page=2", StringComparison.Ordinal);
        return Task.FromResult(Json(pageTwo ? "[]" : "[" + activityJson + "]"));
    }
    if (path.EndsWith("/athlete")) return Task.FromResult(Json("{\"id\":77}"));
    return Task.FromResult(missing ? Json("{}", HttpStatusCode.NotFound) : Json(privateActivity ? activityJson.Replace("\"private\":false", "\"private\":true") : activityJson));
});
var gate = new StravaRateGate();
api = new StravaApi(fake, protection, config, gate);
using var worker = new StravaSyncWorker(factory, api, gate, config, NullLogger<StravaSyncWorker>.Instance);
await worker.StartAsync(default);
async Task WaitFor(Func<TrackerDb, Task<bool>> predicate, string name) {
    for (var attempt = 0; attempt < 200; attempt++) {
        await using var db = await factory.CreateDbContextAsync();
        if (await predicate(db)) { Check(true, name); return; }
        await Task.Delay(200);
    }
    await using var snapshot = await factory.CreateDbContextAsync();
    var jobs = await snapshot.StravaSyncJobs.AsNoTracking().ToListAsync();
    Console.WriteLine($"Database snapshot: activities={await snapshot.Activities.CountAsync()}, jobs={jobs.Count}");
    foreach (var job in jobs) Console.WriteLine($"Job: activity={job.ActivityId}, attempts={job.Attempts}, version={job.Version}");
    throw new Exception("Timed out: " + name);
}
await WaitFor(async db => await db.Activities.CountAsync() == 1 && !await db.StravaSyncJobs.AnyAsync(), "initial paginated import stores workouts and finishes queued job");
Check(listCalls == 2, "short first page does not terminate pagination prematurely");
async Task Enqueue(long id) {
    await using var db = await factory.CreateDbContextAsync();
    await StravaQueue.EnqueueAsync(db, person.Id, id);
}
await Enqueue(501);
await Enqueue(501);
await WaitFor(async db => await db.Activities.CountAsync() == 1 && !await db.StravaSyncJobs.AnyAsync(), "duplicate webhook updates do not duplicate activities");
privateActivity = true;
await Enqueue(501);
await WaitFor(async db => !await db.Activities.AnyAsync() && !await db.StravaSyncJobs.AnyAsync(), "privacy change removes previously imported activity");
privateActivity = false;
await Enqueue(501);
await WaitFor(async db => await db.Activities.CountAsync() == 1 && !await db.StravaSyncJobs.AnyAsync(), "activity can be restored when accessible again");
missing = true;
await Enqueue(501);
await WaitFor(async db => !await db.Activities.AnyAsync() && !await db.StravaSyncJobs.AnyAsync(), "deletion confirmed through API removes activity");
missing = false;
await Enqueue(501);
await WaitFor(async db => await db.Activities.CountAsync() == 1 && !await db.StravaSyncJobs.AnyAsync(), "activity restored before authorization test");
revoked = true;
await Enqueue(-1);
await WaitFor(async db => !await db.Activities.AnyAsync() && (await db.Participants.SingleAsync()).StravaSyncStatus == "reconnect",
    "confirmed revoked authorization clears tokens and imported activity data");
await worker.StopAsync(default);
Console.WriteLine("All Strava checks passed.");

sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler, IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://www.strava.com/") };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
}
