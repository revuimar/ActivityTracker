using Microsoft.EntityFrameworkCore;
namespace ActivityTracker.Data;

public static class SchemaUpgrade
{
    // Additive upgrade for databases initialized by the original EnsureCreated starter.
    // Retains existing participants, tokens, scores and challenge rows; safe to rerun.
    public static async Task ApplyAsync(TrackerDb db)
    {
        await db.Database.EnsureCreatedAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(33520261027)");
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Activities'
                           AND column_name = 'StartLocal' AND data_type = 'timestamp with time zone') THEN
                    ALTER TABLE "Activities" ALTER COLUMN "StartLocal" TYPE timestamp without time zone USING "StartLocal" AT TIME ZONE 'UTC';
                END IF;
            END $$;
            ALTER TABLE "Participants" ALTER COLUMN "StravaAthleteId" DROP NOT NULL;
            ALTER TABLE "Participants" ADD COLUMN IF NOT EXISTS "NormalizedEmail" character varying(254);
            ALTER TABLE "Participants" ADD COLUMN IF NOT EXISTS "PasswordHash" text;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Participants_NormalizedEmail" ON "Participants" ("NormalizedEmail");
            ALTER TABLE "Challenges" ADD COLUMN IF NOT EXISTS "Description" character varying(1000) NOT NULL DEFAULT '';
            CREATE TABLE IF NOT EXISTS "LeaderboardExclusions" (
                "ChallengeId" integer NOT NULL REFERENCES "Challenges" ("Id") ON DELETE CASCADE,
                "ParticipantId" integer NOT NULL REFERENCES "Participants" ("Id") ON DELETE CASCADE,
                PRIMARY KEY ("ChallengeId", "ParticipantId")
            );
            INSERT INTO "Challenges" ("Name", "Description", "StartsOn", "EndsOn", "Cutoff", "MinimumMinutes")
            SELECT 'Morning Challenge', 'Move every morning. Build the habit.', DATE '2020-01-01', DATE '2099-12-31', TIME '11:00', 15
            WHERE NOT EXISTS (SELECT 1 FROM "Challenges");
            """);
        await transaction.CommitAsync();
    }
}
