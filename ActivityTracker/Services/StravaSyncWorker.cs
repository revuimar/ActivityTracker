using System.Security.Cryptography;
using System.Text.Json;
using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Services;

public sealed class StravaSyncWorker(IDbContextFactory<TrackerDb> factory, StravaApi api,
    StravaRateGate gate, IConfiguration config, ILogger<StravaSyncWorker> logger) : BackgroundService
{
    private DateTimeOffset nextSeed;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextSeed) {
                    await StravaQueue.SeedAsync(db, stoppingToken);
                    nextSeed = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                if (gate.BlockedUntil <= DateTimeOffset.UtcNow && await ProcessOneAsync(db, stoppingToken)) continue;
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) {
                // Do not log response bodies, credentials, or URLs containing authorization codes.
                logger.LogWarning("Strava worker will retry after {ErrorType}.", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static async Task<bool> AdvisoryLockAsync(TrackerDb db, bool acquire, CancellationToken ct)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = acquire ? "SELECT pg_try_advisory_lock(33520261028)" : "SELECT pg_advisory_unlock(33520261028)";
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> ProcessOneAsync(TrackerDb db, CancellationToken ct)
    {
        // Session lock serializes workers across app instances without holding a transaction
        // over API calls. Always unlock before returning the connection to its pool.
        await db.Database.OpenConnectionAsync(ct);
        if (!await AdvisoryLockAsync(db, true, ct)) return false;
        try {
            var now = DateTimeOffset.UtcNow;
            var job = await db.StravaSyncJobs.AsNoTracking().Where(j => j.NextAttemptAt <= now)
                .OrderBy(j => j.NextAttemptAt).ThenBy(j => j.ParticipantId).FirstOrDefaultAsync(ct);
            if (job is null) return false;
            var person = await db.Participants.SingleOrDefaultAsync(p => p.Id == job.ParticipantId, ct);
            if (person is null) return true; // FK cascade also removes the job.
            try {
                if (person.AccessToken is not null && person.StravaActivityReadGranted) {
                    person.StravaSyncStatus = "syncing";
                    await db.SaveChangesAsync(ct);
                    await SyncAsync(db, person, job.ActivityId, ct);
                    person.LastStravaSyncAt = DateTimeOffset.UtcNow;
                    if (job.ActivityId == 0) person.LastStravaFullSyncAt = person.LastStravaSyncAt;
                    person.StravaSyncStatus = "ready";
                    await db.SaveChangesAsync(ct);
                }
                await JobQuery(db, job).ExecuteDeleteAsync(ct);
            } catch (StravaReconnectException) {
                await DisconnectAsync(db, person, ct);
            } catch (CryptographicException) {
                await DisconnectAsync(db, person, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) {
                db.ChangeTracker.Clear();
                var backoff = TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(job.Attempts, 8))));
                if (error is StravaRetryException retry && retry.Delay > backoff) backoff = retry.Delay;
                await JobQuery(db, job).ExecuteUpdateAsync(set => set
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.NextAttemptAt, DateTimeOffset.UtcNow.Add(backoff)), ct);
                await db.Participants.Where(p => p.Id == person.Id && p.AccessToken != null)
                    .ExecuteUpdateAsync(set => set.SetProperty(p => p.StravaSyncStatus, "retrying"), ct);
                logger.LogWarning("Strava sync for account {ParticipantId} will retry after {ErrorType}.", person.Id, error.GetType().Name);
            }
            return true;
        } finally {
            await AdvisoryLockAsync(db, false, CancellationToken.None);
        }
    }

    private static IQueryable<StravaSyncJob> JobQuery(TrackerDb db, StravaSyncJob job) =>
        db.StravaSyncJobs.Where(j => j.ParticipantId == job.ParticipantId && j.ActivityId == job.ActivityId && j.Version == job.Version);

    private static async Task DisconnectAsync(TrackerDb db, Participant person, CancellationToken ct)
    {
        // A concurrent OAuth reconnect changes RefreshToken and wins the concurrency check.
        // Only purge after successfully recording the revoked connection in the same transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        person.AccessToken = null;
        person.RefreshToken = null;
        person.TokenExpiresAt = null;
        person.StravaActivityReadGranted = false;
        person.StravaSyncStatus = "reconnect";
        await db.SaveChangesAsync(ct);
        await db.Activities.Where(a => a.ParticipantId == person.Id).ExecuteDeleteAsync(ct);
        await db.StravaSyncJobs.Where(j => j.ParticipantId == person.Id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task SyncAsync(TrackerDb db, Participant person, long activityId, CancellationToken ct)
    {
        Task SaveTokens() => db.SaveChangesAsync(ct);
        if (activityId == -1) {
            // Webhook payloads are notifications, never authority to revoke or delete data.
            using var athlete = await api.GetAsync(person, "athlete", SaveTokens, ct);
            if (athlete is null) throw new StravaReconnectException();
            return;
        }
        if (activityId > 0) {
            await FetchActivityAsync(db, person, activityId, ct);
            return;
        }
        var days = Math.Clamp(config.GetValue<int?>("Strava:ImportDays") ?? 90, 7, 365);
        var until = DateTimeOffset.UtcNow;
        var since = until.AddDays(-days);
        var seen = new HashSet<long>();
        for (var page = 1; ; page++) {
            if (page > 500) throw new InvalidOperationException("Activity pagination exceeded the safety limit.");
            using var document = await api.GetAsync(person,
                $"athlete/activities?after={since.ToUnixTimeSeconds()}&before={until.ToUnixTimeSeconds()}&per_page=200&page={page}", SaveTokens, ct);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid activity page.");
            foreach (var item in document.RootElement.EnumerateArray()) {
                seen.Add(item.GetProperty("id").GetInt64());
                await UpsertAsync(db, person, item, ct);
            }
            await db.SaveChangesAsync(ct);
            // Continue until EMPTY: Strava may return fewer than per_page before the last page.
            if (document.RootElement.GetArrayLength() == 0) break;
        }
        var missing = await db.Activities.Where(a => a.ParticipantId == person.Id && a.StartUtc >= since && a.StartUtc < until)
            .Select(a => a.StravaActivityId).ToListAsync(ct);
        // Verify missing records individually, avoiding false deletions if pagination shifts.
        foreach (var id in missing.Where(id => !seen.Contains(id))) await FetchActivityAsync(db, person, id, ct);
    }

    private async Task FetchActivityAsync(TrackerDb db, Participant person, long id, CancellationToken ct)
    {
        using var document = await api.GetAsync(person, $"activities/{id}", () => db.SaveChangesAsync(ct), ct);
        if (document is null) {
            await db.Activities.Where(a => a.ParticipantId == person.Id && a.StravaActivityId == id).ExecuteDeleteAsync(ct);
            return;
        }
        if (document.RootElement.GetProperty("id").GetInt64() != id) throw new JsonException("Activity ID mismatch.");
        await UpsertAsync(db, person, document.RootElement, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task UpsertAsync(TrackerDb db, Participant person, JsonElement item, CancellationToken ct)
    {
        if (item.TryGetProperty("athlete", out var athlete) && athlete.GetProperty("id").GetInt64() != person.StravaAthleteId)
            throw new JsonException("Activity owner mismatch.");
        var id = item.GetProperty("id").GetInt64();
        if (StravaApi.IsPrivate(item)) {
            await db.Activities.Where(a => a.ParticipantId == person.Id && a.StravaActivityId == id).ExecuteDeleteAsync(ct);
            return;
        }
        var incoming = StravaApi.ParseActivity(item, person.Id);
        var existing = await db.Activities.SingleOrDefaultAsync(a => a.StravaActivityId == id, ct);
        if (existing is null) db.Activities.Add(incoming);
        else {
            if (existing.ParticipantId != person.Id) throw new JsonException("Stored activity owner mismatch.");
            db.Entry(existing).CurrentValues.SetValues(new {
                incoming.StartUtc, incoming.StartLocal, incoming.ElapsedSeconds, incoming.MovingSeconds, incoming.SportType
            });
        }
    }
}
