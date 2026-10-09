#!/usr/bin/env bash
set -euo pipefail

IMAGE="${1:?usage: smoke.sh IMAGE PLATFORM}"
PLATFORM="${2:?usage: smoke.sh IMAGE PLATFORM}"
SUFFIX="${GITHUB_RUN_ID:-local}-${RANDOM}-${RANDOM}"
CONTAINER="tuvima-smoke-${SUFFIX}"
VOLUME_PREFIX="tuvima-smoke-${SUFFIX}"
VOLUMES=(config db models artwork backups transcode library)

cleanup() {
    local status=$?
    if [ "$status" -ne 0 ]; then
        echo "--- smoke failed (exit ${status}); container state and last log lines ---"
        docker inspect --format '{{json .State}}' "$CONTAINER" 2>&1 || true
        docker logs --tail 80 "$CONTAINER" 2>&1 || true
    fi
    docker rm --force "$CONTAINER" >/dev/null 2>&1 || true
    for volume in "${VOLUMES[@]}"; do
        docker volume rm "${VOLUME_PREFIX}-${volume}" >/dev/null 2>&1 || true
    done
}
trap cleanup EXIT

for volume in "${VOLUMES[@]}"; do
    docker volume create "${VOLUME_PREFIX}-${volume}" >/dev/null
done

docker run --detach \
    --name "$CONTAINER" \
    --platform "$PLATFORM" \
    --env TUVIMA_UID=10001 \
    --env TUVIMA_GID=10001 \
    --env TUVIMA_UMASK=0002 \
    --env TUVIMA_PROXY_PORT=5018 \
    --volume "${VOLUME_PREFIX}-config:/config" \
    --volume "${VOLUME_PREFIX}-db:/db" \
    --volume "${VOLUME_PREFIX}-models:/models" \
    --volume "${VOLUME_PREFIX}-artwork:/artwork-cache" \
    --volume "${VOLUME_PREFIX}-backups:/backups" \
    --volume "${VOLUME_PREFIX}-transcode:/transcode" \
    --volume "${VOLUME_PREFIX}-library:/library" \
    "$IMAGE" >/dev/null

wait_for_health() {
    local deadline=$((SECONDS + 180))
    while [ "$SECONDS" -lt "$deadline" ]; do
        local status
        status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$CONTAINER")"
        if [ "$status" = "healthy" ]; then
            return 0
        fi
        if [ "$(docker inspect --format '{{.State.Running}}' "$CONTAINER")" != "true" ]; then
            docker logs "$CONTAINER"
            return 1
        fi
        sleep 2
    done
    docker inspect "$CONTAINER"
    docker logs "$CONTAINER"
    return 1
}

wait_for_health

# The runner reaches the container over Docker's bridge, so the Dashboard sees a private (home network) address.
container_ip="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$CONTAINER")"
test -n "$container_ip"
# Native app door is off by default: discovery and app actions answer "not found" before the Engine is contacted.
test "$(curl --silent --output /dev/null --write-out "%{http_code}" --max-time 30 --retry 2 --retry-connrefused "http://${container_ip}:5016/.well-known/tuvima")" = "404"
test "$(curl --silent --output /dev/null --write-out "%{http_code}" --max-time 30 --retry 2 --retry-connrefused "http://${container_ip}:5016/api/v1/playback/encode/jobs")" = "404"

docker exec "$CONTAINER" sh -exc '
    process_count=0
    for process in /proc/[0-9]*; do
        # The checking shell has the dll names in its own command line; skip it.
        test "$process" = "/proc/$$" && continue
        command="$(tr "\\000" " " < "$process/cmdline" 2>/dev/null || true)"
        case "$command" in
            *MediaEngine.Api.dll*|*MediaEngine.Web.dll*)
                process_uid="$(grep "^Uid:" "$process/status" | tr -s " \\t" " " | cut -d " " -f 2)"
                test "$process_uid" = "10001"
                process_count=$((process_count + 1))
                ;;
        esac
    done
    test "$process_count" = "2"
'

docker exec --user 10001:10001 "$CONTAINER" sh -exc '
    test "$(id -u)" = "10001"
    test "$(id -g)" = "10001"
    test -x /usr/bin/ffmpeg
    test -x /usr/bin/ffprobe
    ffmpeg -version >/dev/null
    ffprobe -version >/dev/null
    ffmpeg -hide_banner -encoders 2>&1 | grep -q "libx264"
    ffmpeg -hide_banner -encoders 2>&1 | grep -q " aac "
    ffmpeg -hide_banner -encoders 2>&1 | grep -q " webvtt "
    ffmpeg -hide_banner -muxers 2>&1 | grep -q " hls "
    test ! -e /config/backups/tuvima-backup-20260819-230724.zip
    test -z "$(find /config/secrets -type f -print -quit)"
    test -z "$(find /config -name "*.bak" -print -quit)"
    grep -q "schema_version.*6.0" /config/libraries.json
    grep -q "view_storage" /config/libraries.json
    test -z "$(grep "kind.*photos" /config/libraries.json || true)"
    grep -q "ffmpeg_binary_path.*/usr/bin/ffmpeg" /config/transcoding.json
    curl --fail --silent http://127.0.0.1:61495/health/live >/dev/null
    curl --fail --silent http://127.0.0.1:5016/health/live >/dev/null
    container_ip="$(hostname -i | cut -d " " -f 1)"
    # The Engine listens on loopback only (docker-entrypoint.sh); it must not answer on the container address.
    if curl --silent --max-time 5 "http://${container_ip}:61495/health/live" >/dev/null; then exit 1; fi
    # Readiness and playback diagnostics require an administrator sign-in, so the
    # smoke check confirms they are protected and that the native runtimes shipped.
    test "$(curl --silent --output /dev/null --write-out "%{http_code}" http://127.0.0.1:61495/health/ready)" = "401"
    test "$(curl --silent --output /dev/null --write-out "%{http_code}" http://127.0.0.1:61495/playback/diagnostics)" = "401"
    find /app/engine -iname "*skia*" -o -iname "*llama*" | head -20
    test -n "$(find /app/engine -iname "libSkiaSharp*" -print -quit)"
    test -n "$(find /app/engine -iname "libllama*" -print -quit)"
'

docker exec --user 10001:10001 "$CONTAINER" sh -exc '
    printf persisted > /models/container-smoke-marker
    printf persisted > /backups/container-smoke-marker
    ffmpeg -hide_banner -loglevel error -f lavfi -i color=c=purple:s=320x180:d=2 \
        -frames:v 1 /artwork-cache/container-smoke-thumbnail.jpg
'

# The proxy port counts every request as an internet visitor. With the default "who can connect"
# (home network), the front page is refused with 403 (ExposurePolicy, NotAvailableHere).
docker exec "$CONTAINER" sh -exc '
    test "$(curl --silent --output /dev/null --write-out "%{http_code}" --max-time 30 --retry 2 --retry-connrefused http://127.0.0.1:5018/)" = "403"
'

# First-run setup from another device needs the one-time code. Printing it proves the command works as root in the container.
# Not automated: a setup-begin refusal (setup_code_required) needs the Dashboard's live setup page, which the runner cannot drive over HTTP.
setup_code_output="$(docker exec "$CONTAINER" tuvima-admin setup code)"
printf '%s\n' "$setup_code_output"
printf '%s\n' "$setup_code_output" | grep -Eq '^Setup code: [A-Za-z0-9]{4}-[A-Za-z0-9]{4}$'

docker restart "$CONTAINER" >/dev/null
wait_for_health

docker exec "$CONTAINER" sh -exc '
    test -s /db/library.db
    test -s /artwork-cache/container-smoke-thumbnail.jpg
    test "$(cat /models/container-smoke-marker)" = persisted
    test "$(cat /backups/container-smoke-marker)" = persisted
    test "$(stat -c %u /db/library.db)" = 10001
    test "$(stat -c %g /db/library.db)" = 10001
'
