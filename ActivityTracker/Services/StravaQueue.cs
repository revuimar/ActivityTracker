using ActivityTracker.Data;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Services;

public static class StravaQueue
{
    public static Task SeedAsync(TrackerDb db, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "StravaSyncJobs" ("ParticipantId", "ActivityId", "Version", "Attempts", "NextAttemptAt")
        SELECT "Id", 0, 1, 0, {DateTimeOffset.UtcNow} FROM "Participants"
        WHERE "StravaActivityReadGranted" AND "AccessToken" IS NOT NULL
          AND ("LastStravaFullSyncAt" IS NULL OR "LastStravaFullSyncAt" < {DateTimeOffset.UtcNow.AddDays(-1)})
        ON CONFLICT ("ParticipantId", "ActivityId") DO NOTHING
        """, ct);

    public static Task EnqueueAsync(TrackerDb db, int participantId, long activityId = 0, CancellationToken ct = default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StravaSyncJobs" ("ParticipantId", "ActivityId", "Version", "Attempts", "NextAttemptAt")
            VALUES ({participantId}, {activityId}, 1, 0, {DateTimeOffset.UtcNow})
            ON CONFLICT ("ParticipantId", "ActivityId") DO UPDATE
            SET "Version" = "StravaSyncJobs"."Version" + 1
            """, ct);
}
