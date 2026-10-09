#!/bin/bash
set -euo pipefail

readonly TUVIMA_UID_VALUE="${TUVIMA_UID:-1000}"
readonly TUVIMA_GID_VALUE="${TUVIMA_GID:-1000}"
readonly TUVIMA_UMASK_VALUE="${TUVIMA_UMASK:-0002}"

fail() {
    echo "[Tuvima] ERROR: $*" >&2
    exit 1
}

validate_runtime_identity() {
    [[ "$TUVIMA_UID_VALUE" =~ ^[0-9]+$ ]] || fail "TUVIMA_UID must be numeric."
    [[ "$TUVIMA_GID_VALUE" =~ ^[0-9]+$ ]] || fail "TUVIMA_GID must be numeric."
    [[ "$TUVIMA_UMASK_VALUE" =~ ^0?[0-7]{3}$ ]] || fail "TUVIMA_UMASK must be an octal mode such as 0002."
    [ "$TUVIMA_UID_VALUE" -ne 0 ] || fail "TUVIMA_UID must identify a non-root user."
    [ "$TUVIMA_GID_VALUE" -ne 0 ] || fail "TUVIMA_GID must identify a non-root group."
}

prepare_owned_directory() {
    local path="$1"
    mkdir -p "$path"
    chown "$TUVIMA_UID_VALUE:$TUVIMA_GID_VALUE" "$path"
    chmod 2775 "$path"
}

prepare_media_directory() {
    local path="$1"
    mkdir -p "$path"
    if [ -z "$(find "$path" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]; then
        chown "$TUVIMA_UID_VALUE:$TUVIMA_GID_VALUE" "$path"
        chmod 2775 "$path"
    fi
}

if [ "$(id -u)" -eq 0 ] && [ "${TUVIMA_PRIVILEGES_DROPPED:-0}" != "1" ]; then
    validate_runtime_identity
    for path in /config /db /models /artwork-cache /backups /transcode; do
        prepare_owned_directory "$path"
    done
    prepare_media_directory /library

    export TUVIMA_PRIVILEGES_DROPPED=1
    export HOME=/config/.home
    exec gosu "$TUVIMA_UID_VALUE:$TUVIMA_GID_VALUE" "$0" "$@"
fi

[ "$(id -u)" -ne 0 ] || fail "Application startup refused to continue as root."
umask "$TUVIMA_UMASK_VALUE"

if [ -z "$(find /config -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]; then
    echo "[Tuvima] /config is empty; seeding distributable defaults."
    cp -a /app/default-config/. /config/
    cp -a /app/docker-config/. /config/
    echo "[Tuvima] To finish setup from another device, run: docker exec -it <container> tuvima-admin setup code"
fi

for required_config in core.json libraries.json ai.json pipelines.json transcoding.json network.json; do
    [ -f "/config/$required_config" ] || fail "/config/$required_config is required. Empty the disposable pre-beta config volume to reseed it."
done

mkdir -p /config/secrets "$HOME" /artwork-cache/logs /transcode/variants

for writable_path in /config /db /models /artwork-cache /backups /transcode /library; do
    [ -w "$writable_path" ] || fail "$writable_path is not writable by UID $(id -u) and GID $(id -g)."
done
export TUVIMA_CONFIG_DIR="${TUVIMA_CONFIG_DIR:-/config}"
export TUVIMA_DB_PATH="${TUVIMA_DB_PATH:-/db/library.db}"
export TUVIMA_MODELS_DIR="${TUVIMA_MODELS_DIR:-/models}"
export TUVIMA_BACKUP_DIR="${TUVIMA_BACKUP_DIR:-/backups}"
export TUVIMA_DATA_PROTECTION_DIR="${TUVIMA_DATA_PROTECTION_DIR:-/config/.keys}"
export TUVIMA_LOG_DIR="${TUVIMA_LOG_DIR:-/artwork-cache/logs}"
export TUVIMA_REQUIRE_MEDIA_RUNTIME="${TUVIMA_REQUIRE_MEDIA_RUNTIME:-true}"
export TUVIMA_ENGINE_URL="${TUVIMA_ENGINE_URL:-http://127.0.0.1:61495}"
export TUVIMA_CORS_ORIGINS="${TUVIMA_CORS_ORIGINS:-http://localhost:5016}"

echo "[Tuvima] Starting as UID=$(id -u) GID=$(id -g) UMASK=$TUVIMA_UMASK_VALUE"
echo "[Tuvima] Dashboard=http://0.0.0.0:5016 Engine=http://127.0.0.1:61495 (loopback only)"

ENGINE_PID=""
DASHBOARD_PID=""
shutdown() {
    trap - TERM INT
    [ -z "$DASHBOARD_PID" ] || kill -TERM "$DASHBOARD_PID" 2>/dev/null || true
    [ -z "$ENGINE_PID" ] || kill -TERM "$ENGINE_PID" 2>/dev/null || true
    [ -z "$DASHBOARD_PID" ] || wait "$DASHBOARD_PID" 2>/dev/null || true
    [ -z "$ENGINE_PID" ] || wait "$ENGINE_PID" 2>/dev/null || true
}
trap shutdown TERM INT

ASPNETCORE_URLS="http://127.0.0.1:61495" \
ASPNETCORE_ENVIRONMENT="Production" \
dotnet /app/engine/MediaEngine.Api.dll &
ENGINE_PID=$!

echo "[Tuvima] Waiting for Engine liveness."
ENGINE_READY_TIMEOUT_SECONDS="${TUVIMA_ENGINE_READY_TIMEOUT_SECONDS:-120}"
ENGINE_READY_DEADLINE=$((SECONDS + ENGINE_READY_TIMEOUT_SECONDS))
while ! curl --fail --silent http://127.0.0.1:61495/health/live >/dev/null; do
    if ! kill -0 "$ENGINE_PID" 2>/dev/null; then
        wait "$ENGINE_PID" || ENGINE_EXIT_CODE=$?
        fail "Engine exited before becoming live. Exit code=${ENGINE_EXIT_CODE:-unknown}."
    fi
    if [ "$SECONDS" -ge "$ENGINE_READY_DEADLINE" ]; then
        shutdown
        fail "Engine did not become live within ${ENGINE_READY_TIMEOUT_SECONDS}s."
    fi
    sleep 1
done

ASPNETCORE_URLS="http://+:5016" \
ASPNETCORE_ENVIRONMENT="Production" \
Engine__BaseUrl="$TUVIMA_ENGINE_URL" \
dotnet /app/dashboard/MediaEngine.Web.dll &
DASHBOARD_PID=$!

set +e
wait -n "$ENGINE_PID" "$DASHBOARD_PID"
EXIT_CODE=$?
set -e
echo "[Tuvima] A service exited with code $EXIT_CODE; stopping the container."
shutdown
exit "$EXIT_CODE"
