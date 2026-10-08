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

wait_for_search_result() {
    local encoded_query="$1"
    local expected="$2"
    local deadline=$((SECONDS + 180))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if docker exec "$CONTAINER" curl --fail --silent \
            "http://127.0.0.1:61495/api/v1/display/search?q=${encoded_query}" | grep --quiet "$expected"; then
            return 0
        fi
        sleep 2
    done
    docker exec "$CONTAINER" curl --silent http://127.0.0.1:61495/ingestion/operations || true
    docker logs "$CONTAINER"
    return 1
}

wait_for_health

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
    curl --fail --silent http://127.0.0.1:61495/health/ready > /tmp/ready.json
    grep -q "name.*media_runtime" /tmp/ready.json
    grep -q "skia.*true" /tmp/ready.json
    grep -q "llama_cpu.*true" /tmp/ready.json
    curl --fail --silent http://127.0.0.1:61495/playback/diagnostics > /tmp/playback.json
    grep -q "ffmpegAvailable.*true" /tmp/playback.json
    grep -q "adaptiveHlsReady.*true" /tmp/playback.json
'

docker exec --user 10001:10001 "$CONTAINER" sh -exc '
    mkdir -p /transcode/fixtures /library/Music /library/Movies
    ffmpeg -hide_banner -loglevel error -f lavfi -i sine=frequency=880:duration=2 \
        -metadata title="Container Audio" /transcode/fixtures/container-audio.mp3
    ffmpeg -hide_banner -loglevel error -f lavfi -i color=c=purple:s=320x180:d=2 \
        -f lavfi -i sine=frequency=440:duration=2 -shortest \
        -c:v libx264 -pix_fmt yuv420p -c:a aac /transcode/fixtures/container-video.mp4
    ffmpeg -hide_banner -loglevel error -ss 0.5 -i /transcode/fixtures/container-video.mp4 \
        -frames:v 1 /artwork-cache/container-smoke-thumbnail.jpg
    mv /transcode/fixtures/container-audio.mp3 "/library/Music/Container Audio.mp3"
    mv /transcode/fixtures/container-video.mp4 "/library/Movies/Container Video.mp4"
    printf persisted > /models/container-smoke-marker
    printf persisted > /backups/container-smoke-marker
'

wait_for_search_result "Container%20Audio" "Container Audio"
wait_for_search_result "Container%20Video" "Container Video"

docker restart "$CONTAINER" >/dev/null
wait_for_health
wait_for_search_result "Container%20Audio" "Container Audio"
wait_for_search_result "Container%20Video" "Container Video"

docker exec "$CONTAINER" sh -exc '
    test -s /db/library.db
    test -s /artwork-cache/container-smoke-thumbnail.jpg
    test "$(cat /models/container-smoke-marker)" = persisted
    test "$(cat /backups/container-smoke-marker)" = persisted
    test "$(stat -c %u /db/library.db)" = 10001
    test "$(stat -c %g /db/library.db)" = 10001
'
