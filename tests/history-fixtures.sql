-- Disposable CI database only. One current-week and one previous-week workout.
WITH weeks AS (
    SELECT date_trunc('week', CURRENT_TIMESTAMP AT TIME ZONE 'Europe/Warsaw') AS monday
), workouts AS (
    SELECT 990001::bigint AS strava_id, monday + interval '8 hours' AS local_start, 1800 AS seconds FROM weeks
    UNION ALL
    SELECT 990002::bigint, monday - interval '7 days' + interval '8 hours', 2700 FROM weeks
)
INSERT INTO "Activities" ("StravaActivityId", "ParticipantId", "StartUtc", "StartLocal", "ElapsedSeconds", "MovingSeconds", "SportType")
SELECT strava_id, 1, local_start AT TIME ZONE 'Europe/Warsaw', local_start, seconds, seconds, 'Run'
FROM workouts;
