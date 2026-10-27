# GitHub → GHCR → Ubuntu VPS

## Enable Strava activity sync after deploying this version

Keep the Strava application's **Authorization Callback Domain** set to your hostname only, e.g. `aktywnosci.byst.re`. OAuth continues to use `https://aktywnosci.byst.re/auth/strava/callback`. The webhook has a separate URL: `https://aktywnosci.byst.re/webhooks/strava`.

1. On the VPS, generate a verification token with `openssl rand -hex 32`. Add it to `/opt/activitytracker/shared/.env` as `STRAVA_WEBHOOK_VERIFY_TOKEN=...`. Set `STRAVA_WEBHOOK_SUBSCRIPTION_ID=0` for initial registration. Keep the existing client ID, client secret, HTTPS redirect URI and trusted proxy settings. Optional: `STRAVA_IMPORT_DAYS=90` controls recent-history imports (7–365 days).
2. Deploy this release, then recreate the app to load those values:

   ```sh
   cd /opt/activitytracker
   export APP_IMAGE="$(cat current/image)"
   docker compose --project-name activitytracker --env-file shared/.env -f current/compose.production.yml config --quiet
   docker compose --project-name activitytracker --env-file shared/.env -f current/compose.production.yml up -d --no-deps --force-recreate --wait app
   ```

3. Run `python3 /opt/activitytracker/current/register-strava-webhook.py` on the VPS. It reads credentials from the running app in memory, checks for an existing subscription, and creates one only if none exists. It never prints credentials or changes an existing different subscription. The public webhook GET must be reachable for Strava verification. If reusing an existing subscription, retain its original verification token.
4. Add the returned `STRAVA_WEBHOOK_SUBSCRIPTION_ID=...` to the shared `.env`, then repeat the recreate command. Event POSTs return 503 until this ID is configured. Retrying **Sync now** covers any activity created during setup. Do not repeatedly recreate the subscription: a single subscription serves all authorized athletes.
5. Open `/strava`, reconnect once and allow activity access, then use **Sync now** and refresh the sync status. Reconnecting automatically queues the first import. Each athlete connects their own Strava account; email test accounts remain separate accounts. Never paste credentials/tokens into chat or Git.

The worker refreshes expiring access tokens, safely saves rotated refresh tokens before fetching activities, retries transient errors, and pauses at Strava rate limits. A daily recent-history reconciliation covers missed notifications within the configured history window. New/updated/deleted activities are fetched by ID before stored data is changed; duplicate notifications cannot duplicate workouts. Only You workouts are excluded. If Strava confirms revoked access, imported activities and tokens are removed and the app asks the athlete to reconnect. A bad or forged event alone cannot delete an activity or revoke an account.

The upgrade adds sync metadata and a queue without deleting existing tables or rows. Back up the database and `protection_keys` volume before deployment. The app still displays only the current week's leaderboard; sync does not add week history navigation. Confirm your app's athlete capacity in Strava API settings before inviting additional users.

Validation: `dotnet run --project tests/StravaChecks` runs API/scoring checks with a fake Strava server, never live credentials. CI additionally sets `STRAVA_TEST_DATABASE` for disposable PostgreSQL tests (repeatable schema upgrade, durable queue, paginated imports, duplicates, privacy, deletion, and revocation). The CI-only Compose override exposes PostgreSQL on localhost port 15432; production does not expose it.

Repository: https://github.com/revuimar/ActivityTracker
Server: sandra335.mikrus.xyz, SSH port 10335, Ubuntu 24.04.

## What happens automatically

Every push to main builds the ASP.NET Core 8 application, runs scoring boundary checks, builds a Linux image, and smoke tests the homepage and database health endpoint with PostgreSQL. Only after verification passes is the image published to GHCR under an immutable commit tag. Deployment uses the image digest, verified SSH host keys, and a server-side lock. Pull requests only verify; they cannot publish or deploy. Third-party actions are pinned to commit SHAs and Dependabot checks for updates.

Deployment stays off until the repository variable DEPLOY_ENABLED is set to true. Database and Strava credentials live only in a protected server file. No .env file should be uploaded to GitHub. The workflow never transfers or prints that file.

## 1. Check Docker and resources on the VPS

Run in your existing root SSH session:

```sh
docker --version
docker compose version
uname -m
free -h
df -h /
```

The build targets linux/amd64 (`x86_64`). Confirm that before enabling deployment. Use Docker Engine with the Compose v2 plugin supporting `up --wait`. If Docker is missing, follow the official Ubuntu instructions: https://docs.docker.com/engine/install/ubuntu/ . No SDK is required on the VPS; GitHub builds the application.

Mikrus can provide shared public addresses and forwarded ports. Confirm the assigned public HTTP port or HTTPS reverse proxy/domain in your panel. SSH port 10335 is NOT an application port. Do not assume you can bind public 80/443. The app initially binds only 127.0.0.1:8080. Arrange HTTPS before using Strava login.

## 2. Create a dedicated deployment account

Run as root on the VPS (only once):

```sh
adduser --disabled-password --gecos '' deploy
usermod -aG docker deploy
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
install -m 600 -o deploy -g deploy /dev/null /home/deploy/.ssh/authorized_keys
install -d -m 700 -o deploy -g deploy /opt/activitytracker
install -d -m 700 -o deploy -g deploy /opt/activitytracker/shared /opt/activitytracker/releases
```

Docker group membership gives root-equivalent control of this VPS. Use a separate key only for deployment. Do not put your root password or personal SSH key into Actions.

## 3. Generate and authorize the deployment key

Run on your Mac, choosing a new filename if this already exists:

```sh
ssh-keygen -t ed25519 -f ~/.ssh/activitytracker_actions -C activitytracker-actions -N ''
cat ~/.ssh/activitytracker_actions.pub
```

Copy that PUBLIC key line into `/home/deploy/.ssh/authorized_keys` on the VPS, prefixed with `restrict ` (a space after restrict). This allows remote commands and file copying while blocking forwarding and PTY access. Keep your existing root SSH session open. Confirm from the Mac:

```sh
ssh -i ~/.ssh/activitytracker_actions -p 10335 deploy@sandra335.mikrus.xyz 'docker compose version'
```

On the VPS, obtain the server host key through your authenticated root session:

```sh
printf '[sandra335.mikrus.xyz]:10335 '
cut -d ' ' -f 1,2 /etc/ssh/ssh_host_ed25519_key.pub
```

The output should be ONE line like `[sandra335.mikrus.xyz]:10335 ssh-ed25519 AAAA...`. This is the SSH_KNOWN_HOSTS value. Do not obtain trust by blindly accepting ssh-keyscan results.

## 4. Server-only configuration

Run on the VPS as root; this creates a secret file without printing its password. It refuses to overwrite an existing file:

```sh
python3 - <<'PY'
from pathlib import Path
import secrets, os, pwd
path = Path('/opt/activitytracker/shared/.env')
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as f:
    f.write('POSTGRES_PASSWORD=' + secrets.token_hex(32) + '\n')
    f.write('ALLOWED_HOSTS=localhost;127.0.0.1\nAPP_BIND_IP=127.0.0.1\nAPP_PORT=8080\n')
    f.write('TRUSTED_PROXY_IP=\nSTRAVA_CLIENT_ID=\nSTRAVA_CLIENT_SECRET=\nSTRAVA_REDIRECT_URI=\n')
user = pwd.getpwnam('deploy')
os.chown(path, user.pw_uid, user.pw_gid)
PY
nano /opt/activitytracker/shared/.env
```

Add your actual domain to ALLOWED_HOSTS (semicolon separated); retain localhost and 127.0.0.1 for health checks. Use the example in deploy/production.env.example. POSTGRES_PASSWORD must remain the generated hex string: changing it after PostgreSQL initializes does not update the database role password. Do not paste this file into chat or GitHub.

Default access for a private preview is an SSH tunnel from your Mac:

```sh
ssh -p 10335 -L 8080:127.0.0.1:8080 root@sandra335.mikrus.xyz
```

Then browse http://localhost:8080 after the first deployment. Public exposure requires your assigned Mikrus web routing or your own HTTPS reverse proxy. Keep PostgreSQL unexposed. If a local reverse proxy connects through Docker's host gateway, inspect the app network gateway and configure that exact address as TRUSTED_PROXY_IP; the app trusts no arbitrary forwarded headers. Configure the proxy to overwrite forwarding headers and send X-Forwarded-Proto=https. Set STRAVA_REDIRECT_URI to your final HTTPS callback and configure that domain in Strava. Do not enable OAuth over public HTTP.

## 5. GitHub configuration

After the first successful image build, open your GitHub profile → Packages → activitytracker → Package settings. Make the container package **public** if you want the VPS to pull without credentials. A public repository can initially produce a private GHCR package. The source is already public. Alternatively keep the package private and log in once AS THE DEPLOY USER using a dedicated classic PAT with only `read:packages` through `docker login ghcr.io --username YOUR_LOGIN --password-stdin`; enter the token privately on the VPS. Do not use a PAT for CI publishing: the workflow uses GITHUB_TOKEN automatically.

In repository Settings → Environments, create `production`. Limit deployment branches to main. Add environment secrets:

| Secret | Value |
| --- | --- |
| SSH_PRIVATE_KEY | Entire contents of the dedicated Mac file ~/.ssh/activitytracker_actions, including BEGIN/END lines; paste directly into GitHub, never chat |
| SSH_KNOWN_HOSTS | Verified host-key line from step 3 |

In repository Settings → Secrets and variables → Actions → Variables, add:

| Repository variable | Value |
| --- | --- |
| SSH_HOST | sandra335.mikrus.xyz |
| SSH_PORT | 10335 |
| SSH_USER | deploy |
| DEPLOY_ENABLED | true, only after all setup is complete |

Enable Actions if prompted. If package publication fails, check repository Actions policies allow the workflow's `packages: write` permission. Do not grant broader default workflow permissions.

Open Actions → Build, test and deploy → Run workflow on main for the first deployment. Thereafter main pushes deploy automatically. Adding required reviewers to the production environment makes each deployment wait for approval; omit that if you want unattended deployment.

## 6. Verify, backups and rollback

```sh
cd /opt/activitytracker
APP_IMAGE=$(cat current/image) docker compose --env-file shared/.env -f current/compose.production.yml ps
curl --fail http://127.0.0.1:8080/health
```

Failed health checks restore the previous image/configuration when available. A first deployment has no rollback target. Automatic rollback never reverses database schema/data changes. This version includes a transactional, additive upgrade from the original starter schema: it preserves existing participants and activities, adds local credentials and per-board exclusions, and seeds a default board when none exists. Rollback to the older image is unsafe after creating email accounts because the older model requires every account to have a Strava ID; back up first and use a forward fix rather than automatically restoring an incompatible older image. Later schema changes should use reviewed EF migrations. Existing browser sessions must log in again because the authentication cookie now uses internal account IDs.

Back up PostgreSQL AND the protection_keys volume before upgrades. The latter contains the keys needed to decrypt OAuth tokens and login cookies. Keys are protected by server filesystem/volume access, not by an external key-management service. Encrypt backups and keep them off the VPS. Example database backup as deploy:

```sh
cd /opt/activitytracker
umask 077
mkdir -p backups
APP_IMAGE=$(cat current/image) docker compose --env-file shared/.env -f current/compose.production.yml exec -T db pg_dump -U tracker activitytracker > "backups/database-$(date +%Y%m%d-%H%M%S).sql"
```

Test restore to an isolated database. Never run `down --volumes` in production. Old release directories/images are retained for rollback; monitor disk space and remove obsolete releases deliberately.

Strava activity import, refresh, webhooks, and consent/revocation remain unfinished starter features. Stored tokens now use Data Protection; future import code must unprotect them using purpose StravaTokens.v1. Existing plaintext tokens from an older install need an explicit migration or reauthorization. This starter ZIP has no prior production database.

## Leaderboard administrator and test accounts

All boards are public and all signed-in users may create or edit board details. Only one server-configured administrator can change per-board participants. Sign in, open `/login`, and note your numeric account ID. Add `ADMIN_PARTICIPANT_ID=YOUR_NUMERIC_ID` to `/opt/activitytracker/shared/.env`. Default 0 grants nobody administrator permissions. Do not grant admin based on an unverified email address.

For email/password testing behind HTTPS, add `TEST_LOGIN_ENABLED=true` to the same server file and redeploy. Accounts require 12–128 character passwords, stored as salted Identity password hashes. Authentication forms validate antiforgery tokens and are rate limited. Emails are private; display names are public. These test accounts have no verification or recovery flow and do not automatically link to Strava accounts. Production HTTP requests cannot submit credentials; local Development supports HTTP testing. Never deploy `tests/compose.integration.yml`: its settings are for disposable CI only.

After changing server settings, rerun the workflow. Keep production HTTPS and the trusted proxy configured before testing authentication.
