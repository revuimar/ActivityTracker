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

**This is a runnable foundation, not a finished Strava sync service.** OAuth saves athletes and tokens but does not yet fetch activities. Add token refresh, initial activity import via `GET /api/v3/athlete/activities`, webhook handling, idempotent activity upsert/deletion, retries and rate limiting. Implement consent-aware privacy and Strava API terms compliance.

**Security:** Strava tokens are encrypted with ASP.NET Core Data Protection. Production Compose persists login/token keys in a dedicated volume. Protect and back up that volume with the database. Production OAuth requires HTTPS through an explicitly trusted proxy; the deployment guide explains configuration. Add consent/revocation behavior and reviewed EF migrations before evolving production data. Initial schema bootstrap still uses `EnsureCreated`.

**Scoring assumptions:** A workout must start and finish on the same local calendar day, finish by 11:00, and have >=15 minutes moving time. `StartLocal` is taken from Strava local clock data (not server timezone). Sports filtering, challenges dates, activity imports, and multi-challenge scoring are not yet wired into the UI.

**Validation:** GitHub Actions builds with .NET 8, runs scoring checks, then checks the container homepage and database health with PostgreSQL before publishing. Deployment is gated on successful verification. Check the Actions run for actual results. Local Docker verification was unavailable during preparation.
