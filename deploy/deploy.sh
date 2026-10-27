#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
# Fixed install root avoids accepting shell commands/paths from workflow inputs.
root=/opt/activitytracker
release=${1:?release id required}
image=${2:?image digest required}
[[ "$release" =~ ^[0-9]+-[0-9]+$ ]] || exit 2
[[ "$image" =~ ^ghcr\.io/revuimar/activitytracker@sha256:[a-f0-9]{64}$ ]] || exit 2
cd "$root"
exec 9>deploy.lock
flock -w 600 9
[[ -f shared/.env ]] || { echo 'Create shared/.env first; see DEPLOYMENT.md'; exit 1; }
chmod 600 shared/.env
candidate="$root/releases/$release"
[[ -f "$candidate/compose.production.yml" ]] || exit 1
export APP_IMAGE="$image"
compose=(docker compose --project-name activitytracker --env-file "$root/shared/.env" -f "$candidate/compose.production.yml")
"${compose[@]}" config --quiet
"${compose[@]}" pull
previous=$(readlink "$root/current" || true)
if ! "${compose[@]}" up -d --wait --wait-timeout 180; then
    echo 'Deployment unhealthy.'
    if [[ -n "$previous" && -f "$previous/image" ]]; then
        echo 'Restoring previous image and Compose configuration.'
        export APP_IMAGE; APP_IMAGE=$(cat "$previous/image")
        docker compose --project-name activitytracker --env-file "$root/shared/.env" \
            -f "$previous/compose.production.yml" up -d --wait --wait-timeout 180
    else
        echo 'First deployment failed; no previous release exists.'
    fi
    exit 1
fi
printf '%s\n' "$image" > "$candidate/image"
ln -sfn "$candidate" "$root/current.next"
mv -Tf "$root/current.next" "$root/current"
echo 'Deployment healthy.'
