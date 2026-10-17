#!/bin/sh
# Build and replace only this project's container. Configuration and data stay on the host.
set -eu
umask 077

deployment_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd -P)
container_name=ergo-strava
image_name=ergo-strava:local
cd "$deployment_root"

if [ ! -f .env ]; then
    cp .env.example .env
    chmod 600 .env
fi
web_port=$(awk -F= '$1 == "WEB_PORT" { sub(/\r$/, "", $2); print $2; exit }' .env)
web_port=${web_port:-8080}
case "$web_port" in *[!0-9]*|'') printf 'Invalid WEB_PORT\n' >&2; exit 1;; esac
if [ "$web_port" -lt 1 ] || [ "$web_port" -gt 65535 ]; then
    printf 'WEB_PORT must be between 1 and 65535\n' >&2
    exit 1
fi
config_file=$(awk -F= '$1 == "CONFIG_FILE" { sub(/\r$/, "", $2); print $2; exit }' .env)
config_file=${config_file:-./ErgoStravaLogger/config.json}
case "$config_file" in /*) ;; *) config_file="$deployment_root/$config_file";; esac
if [ ! -f "$config_file" ]; then
    printf 'CONFIG_FILE does not point to an existing file.\n' >&2
    exit 1
fi
mkdir -p "$deployment_root/data"
previous_name="$container_name-update-backup"
if docker container inspect "$previous_name" >/dev/null 2>&1; then
    printf 'An update backup container already exists; inspect it before updating.\n' >&2
    exit 1
fi

if docker container inspect "$container_name" >/dev/null 2>&1; then
    existing_project=$(docker inspect --format '{{ index .Config.Labels "com.greener.ergo-strava.project" }}' "$container_name")
    if [ "$existing_project" != "$deployment_root" ]; then
        printf 'Container ergo-strava belongs to another deployment; nothing changed.\n' >&2
        exit 1
    fi
fi

# An unsuccessful build leaves the running container alone.
docker build -t "$image_name" .
if docker container inspect "$container_name" >/dev/null 2>&1; then
    docker stop "$container_name"
    docker rename "$container_name" "$previous_name"
fi
if docker run -d --name "$container_name" --restart unless-stopped --init --network host --stop-timeout 30 \
    --label "com.greener.ergo-strava.project=$deployment_root" \
    --env-file "$deployment_root/.env" \
    -e OUTPUT_DIRECTORY=/data -e "LISTEN_URL=http://0.0.0.0:$web_port" \
    --mount "type=bind,src=$config_file,dst=/config/config.json,readonly" \
    --mount "type=bind,src=$deployment_root/data,dst=/data" \
    --log-driver json-file --log-opt max-size=10m --log-opt max-file=3 \
    "$image_name"; then
    if docker container inspect "$previous_name" >/dev/null 2>&1; then
        docker rm "$previous_name"
    fi
else
    # Restore the previous container if Docker cannot launch its replacement.
    if docker container inspect "$container_name" >/dev/null 2>&1; then
        docker rm "$container_name"
    fi
    if docker container inspect "$previous_name" >/dev/null 2>&1; then
        docker rename "$previous_name" "$container_name"
        docker start "$container_name"
    fi
    exit 1
fi
