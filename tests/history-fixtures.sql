-- Disposable CI database only. Current/previous-week and previous-month workouts.
WITH weeks AS (
    SELECT date_trunc('week', CURRENT_TIMESTAMP AT TIME ZONE 'Europe/Warsaw') AS monday
), workouts AS (
    SELECT 990001::bigint AS strava_id, monday + interval '8 hours' AS local_start, 1800 AS seconds FROM weeks
    UNION ALL
    SELECT 990002::bigint, monday - interval '7 days' + interval '8 hours', 2700 FROM weeks
    UNION ALL
    SELECT 990003::bigint, date_trunc('month', CURRENT_TIMESTAMP AT TIME ZONE 'Europe/Warsaw') - interval '1 month' + interval '8 hours', 900
)
INSERT INTO "Activities" ("StravaActivityId", "ParticipantId", "StartUtc", "StartLocal", "ElapsedSeconds", "MovingSeconds", "SportType")
SELECT strava_id, 1, local_start AT TIME ZONE 'Europe/Warsaw', local_start, seconds, seconds, 'Run'
FROM workouts;
