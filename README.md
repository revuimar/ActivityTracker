# Activity Tracker — Docker MVP

Blazor SSR + ASP.NET Core 8 + EF Core + PostgreSQL 17. Responsive weekly leaderboard, scoring engine, Strava OAuth account connection, and database schema.

## Deployment

Automatic build, scoring checks, PostgreSQL smoke tests, and GHCR image publishing are configured in `.github/workflows/deploy.yml`. SSH deployment is opt-in until VPS credentials and repository variables are configured. See [DEPLOYMENT.md](DEPLOYMENT.md) for the Ubuntu/Mikrus setup, secure authentication, first deployment, and rollback.

## Run

```bash
cp .env.example .env
# Edit .env and set a strong POSTGRES_PASSWORD
# Optional: set STRAVA_CLIENT_ID, STRAVA_CLIENT_SECRET and STRAVA_REDIRECT_URI
docker compose up -d --build
```

Open http://localhost:8080. Health endpoint: http://localhost:8080/health.

For Strava OAuth, register your app at https://www.strava.com/settings/api and configure its callback domain. The redirect URI in `.env` must exactly match your deployed callback URL. On a VPS, put Caddy/another HTTPS reverse proxy in front of the app and change STRAVA_REDIRECT_URI to `https://your-domain/auth/strava/callback`. Do not expose the PostgreSQL port.

## Leaderboard access

- Every leaderboard is public. Any signed-in user can create or edit its name, description, dates and workout rules.
- All accounts, including email/password test accounts without activities, appear on every board automatically with zero points initially.
- Only the app administrator can remove or restore a participant per board. Removal persists across subsequent logins and does not delete the account or activities.
- Strava login uses OAuth. Email/password registration and login are available in local Development; enable `TEST_LOGIN_ENABLED=true` for HTTPS production testing. No shared/default password exists. Test accounts have no email verification or password recovery; they remain separate from Strava accounts.
- After logging in, open `/login` to see your numeric account ID. Set `ADMIN_PARTICIPANT_ID` to your own ID in the **server-only** `.env`, then redeploy. No administrator is assigned automatically.

## Implemented

- Strava OAuth authorization-code flow, CSRF state verification, token exchange and participant upsert.
- Participant, Activity and Challenge EF Core entities with uniqueness constraints.
- Weekly score: best workout per morning, 5 points at 15 min, 6 at 30 min, 7 at 45 min; +5/+10/+15 for 5/6/7 active days.
- Mobile-friendly Blazor leaderboard (empty until participants join and activities are imported).
- Docker Compose with PostgreSQL health check and persistent volume.

## Important limitations / next steps

Strava connection queues an initial import of the last 90 days (configurable with `STRAVA_IMPORT_DAYS`, 7–365). `/strava` shows private sync status and a CSRF-protected **Sync now** button. A PostgreSQL queue survives restarts and coalesces repeated events. The background worker paginates activity imports, persists rotated encrypted refresh tokens, and respects Strava rate-limit reset times with retries. Recent history is reconciled daily. Scores update on the next leaderboard page load.

After deploying, register `/webhooks/strava` using the server-side helper described in DEPLOYMENT.md. GET verifies Strava's challenge; POST durably queues work and promptly acknowledges it. Create/update/delete notifications trigger an authenticated API fetch before changing stored data. Only You activities are excluded/removed; confirmed deauthorization clears imported workouts and tokens. Existing accounts must reconnect once to confirm activity permission. Without a webhook subscription, initial/manual/daily imports still work, but new activities do not arrive immediately. Changes to old activities outside the history window rely on webhook delivery; this is not an unlimited historical archive.

**Security:** Strava tokens are encrypted with ASP.NET Core Data Protection. Production Compose persists login/token keys in a dedicated volume. Protect and back up that volume with the database. Production OAuth requires HTTPS through an explicitly trusted proxy; the deployment guide explains configuration. Add consent/revocation behavior and reviewed EF migrations before evolving production data. Initial schema bootstrap still uses `EnsureCreated`.

**Scoring assumptions:** A workout must start and finish on the same local calendar day, finish by the board's cutoff, and meet its minimum moving duration. `StartLocal` is taken from Strava local clock data (not server timezone). Each board applies its own dates, cutoff and minimum. Previous/next week links browse stored history via `?week=YYYY-MM-DD`, bounded by the leaderboard dates and the current Warsaw week. Ended leaderboards default to their final week. Historical scores use current rules and participant exclusions; they are not frozen snapshots. Browsing history makes no Strava API requests, and weeks without imported activities show zero scores. Sport-type filtering is not implemented.

**Validation:** GitHub Actions builds with .NET 8, runs scoring checks, then checks the container homepage and database health with PostgreSQL before publishing. Deployment is gated on successful verification. Check the Actions run for actual results. Local Docker verification was unavailable during preparation.

Monthly rankings appear directly below weekly rankings. They use calendar months clipped to the challenge dates, summing the same best-per-day workout points and existing weekly consistency bonuses. Bonus counts include only qualifying days inside that month and challenge, so an overlapping week cannot pull points or bonus days from another month. There is no new monthly bonus rule. Previous/next month links browse available months without entering the future; `?month=YYYY-MM` can be combined with `?week=YYYY-MM-DD`. On initial entry the current (or final challenge) month is shown; week navigation selects a corresponding month, while month navigation keeps the weekly selection. Both tables use current participant exclusions/rules and already-imported activities, with no extra Strava requests.
