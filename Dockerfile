# ─────────────────────────────────────────────────────────────────────────────
# Tuvima Library — Docker multi-stage build
# Produces a single image containing the internal Engine and the user-facing
# Dashboard (port 5016).
#
# Build:   docker build -t tuvima/library:latest .
# Run:     docker compose up   (see docker-compose.yml)
# ─────────────────────────────────────────────────────────────────────────────

# ── Stage 1: Build ───────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Copy central package management and build props first (layer-cached separately
# from source so a code change doesn't bust the NuGet restore cache).
COPY Directory.Packages.props .
COPY Directory.Build.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

# Copy every .csproj in the correct relative position for restore.
COPY src/MediaEngine.Contracts/MediaEngine.Contracts.csproj       src/MediaEngine.Contracts/
COPY src/MediaEngine.Application/MediaEngine.Application.csproj   src/MediaEngine.Application/
COPY src/MediaEngine.Domain/MediaEngine.Domain.csproj             src/MediaEngine.Domain/
COPY src/MediaEngine.Storage/MediaEngine.Storage.csproj           src/MediaEngine.Storage/
COPY src/MediaEngine.Intelligence/MediaEngine.Intelligence.csproj src/MediaEngine.Intelligence/
COPY src/MediaEngine.Processors/MediaEngine.Processors.csproj     src/MediaEngine.Processors/
COPY src/MediaEngine.Providers/MediaEngine.Providers.csproj       src/MediaEngine.Providers/
COPY src/MediaEngine.Ingestion/MediaEngine.Ingestion.csproj       src/MediaEngine.Ingestion/
COPY src/MediaEngine.Identity/MediaEngine.Identity.csproj         src/MediaEngine.Identity/
COPY src/MediaEngine.Admin/MediaEngine.Admin.csproj               src/MediaEngine.Admin/
COPY src/MediaEngine.AI/MediaEngine.AI.csproj                     src/MediaEngine.AI/
COPY src/MediaEngine.Plugins/MediaEngine.Plugins.csproj           src/MediaEngine.Plugins/
COPY src/MediaEngine.Plugin.CommercialSkip/MediaEngine.Plugin.CommercialSkip.csproj src/MediaEngine.Plugin.CommercialSkip/
COPY src/MediaEngine.Plugin.FandomLore/MediaEngine.Plugin.FandomLore.csproj         src/MediaEngine.Plugin.FandomLore/
COPY src/MediaEngine.Plugin.MediaSegments/MediaEngine.Plugin.MediaSegments.csproj   src/MediaEngine.Plugin.MediaSegments/
COPY src/MediaEngine.Api/MediaEngine.Api.csproj                   src/MediaEngine.Api/
COPY src/MediaEngine.Web/MediaEngine.Web.csproj                   src/MediaEngine.Web/
COPY src/MediaEngine.Web/Build/                                 src/MediaEngine.Web/Build/

# Restore (cached until any .csproj changes).
RUN dotnet restore src/MediaEngine.Api/MediaEngine.Api.csproj -a $TARGETARCH -p:TuvimaContainerBuild=true
RUN dotnet restore src/MediaEngine.Web/MediaEngine.Web.csproj -a $TARGETARCH
RUN dotnet restore src/MediaEngine.Admin/MediaEngine.Admin.csproj -a $TARGETARCH

# Copy remaining source and config, then publish both projects.
COPY src/ src/
COPY config/ config/
# Shared brand images are static web assets of the Dashboard (see MediaEngine.Web.csproj).
COPY assets/images/ assets/images/

RUN dotnet publish src/MediaEngine.Api/MediaEngine.Api.csproj \
    --configuration Release \
    --arch $TARGETARCH \
    --output /app/engine \
    -p:TuvimaKeepAllRuntimeAssets=true \
    -p:TuvimaContainerBuild=true \
    --no-restore

RUN dotnet publish src/MediaEngine.Web/MediaEngine.Web.csproj \
    --configuration Release \
    --arch $TARGETARCH \
    --output /app/dashboard \
    -p:TuvimaKeepAllRuntimeAssets=true \
    --no-restore

RUN dotnet publish src/MediaEngine.Admin/MediaEngine.Admin.csproj \
    --configuration Release \
    --arch $TARGETARCH \
    --output /app/admin \
    -p:TuvimaKeepAllRuntimeAssets=true \
    --no-restore

# Some native-package build targets copy their complete RID catalog even during
# a targeted publish, which the runtime-output guard rejects. Publish with the
# guard relaxed (TuvimaKeepAllRuntimeAssets) and keep only the selected Linux
# runtime tree in each image here instead.
RUN case "$TARGETARCH" in \
      amd64) target_rid="linux-x64" ;; \
      arm64) target_rid="linux-arm64" ;; \
      *) echo "Unsupported container architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && for runtime_root in /app/engine/runtimes /app/dashboard/runtimes /app/admin/runtimes; do \
      if [ -d "$runtime_root" ]; then \
        find "$runtime_root" -mindepth 1 -maxdepth 1 -type d ! -name "$target_rid" -exec rm -rf -- {} +; \
      fi; \
    done

# ── Stage 2: Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG TARGETARCH
WORKDIR /app
ENV TUVIMA_AI_RUNTIME_DIR=bundled

# FFmpeg/FFprobe provide probing, thumbnails and transcodes. libfontconfig and
# libgomp are required by the bundled SkiaSharp and LLamaSharp CPU runtimes.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      gosu \
      libfontconfig1 \
      libgomp1 \
      xz-utils \
 && rm -rf /var/lib/apt/lists/*

# Static GPL build from the FFmpeg 9.0 release line, taken from BtbN's rolling
# "latest" release. Dated autobuild releases are pruned upstream, so a pinned URL
# eventually returns 404; the rolling tag keeps the container build working at
# the cost of an unpinned (non-reproducible) FFmpeg patch level.
RUN case "$TARGETARCH" in \
      amd64) ffmpeg_platform="linux64" ;; \
      arm64) ffmpeg_platform="linuxarm64" ;; \
      *) echo "Unsupported FFmpeg architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && ffmpeg_archive="ffmpeg-n9.0-latest-${ffmpeg_platform}-gpl-9.0.tar.xz" \
 && ffmpeg_url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${ffmpeg_archive}" \
 && curl --fail --location --retry 3 "$ffmpeg_url" --output /tmp/tuvima-ffmpeg.tar.xz \
 && mkdir -p /tmp/tuvima-ffmpeg-extract /usr/share/licenses/tuvima-ffmpeg \
 && tar -xJf /tmp/tuvima-ffmpeg.tar.xz -C /tmp/tuvima-ffmpeg-extract \
 && install -m 0755 "$(find /tmp/tuvima-ffmpeg-extract -type f -path '*/bin/ffmpeg' -print -quit)" /usr/bin/ffmpeg \
 && install -m 0755 "$(find /tmp/tuvima-ffmpeg-extract -type f -path '*/bin/ffprobe' -print -quit)" /usr/bin/ffprobe \
 && install -m 0644 "$(find /tmp/tuvima-ffmpeg-extract -type f -name 'LICENSE.txt' -print -quit)" /usr/share/licenses/tuvima-ffmpeg/LICENSE.txt \
 && rm -rf /tmp/tuvima-ffmpeg.tar.xz /tmp/tuvima-ffmpeg-extract

RUN ffmpeg -version >/dev/null \
 && ffprobe -version >/dev/null \
 && ffmpeg -hide_banner -encoders 2>&1 | grep -q 'libx264' \
 && ffmpeg -hide_banner -encoders 2>&1 | grep -q ' aac ' \
 && ffmpeg -hide_banner -encoders 2>&1 | grep -q ' webvtt ' \
 && ffmpeg -hide_banner -muxers 2>&1 | grep -q ' hls '

# Copy published output from build stage.
COPY --from=build /app/engine    ./engine
COPY --from=build /app/dashboard ./dashboard
COPY --from=build /app/admin      ./admin

# Copy only the distributable defaults admitted by .dockerignore. The entrypoint
# seeds these into an empty /config volume and overlays container path defaults.
COPY --from=build /src/config/ ./default-config/
COPY docker/config/ ./docker-config/

# Create mount points. Ownership is assigned to the configured UID/GID at
# startup, before the application processes drop root privileges.
RUN mkdir -p \
      /library \
      /config \
      /db \
      /models \
      /artwork-cache \
      /backups \
      /transcode

# Startup script that launches both processes.
COPY docker-entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# ── Ports ─────────────────────────────────────────────────────────────────────
# Only the Dashboard is a supported host-facing port. The Engine remains
# reachable inside the container for the Dashboard and health probes.
EXPOSE 5016

VOLUME ["/config", "/db", "/models", "/artwork-cache", "/backups", "/transcode"]

HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=3 \
  CMD curl --fail --silent --show-error http://127.0.0.1:61495/health/live >/dev/null \
   && curl --fail --silent --show-error http://127.0.0.1:5016/health/live >/dev/null \
   || exit 1

ENTRYPOINT ["/entrypoint.sh"]
