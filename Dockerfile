# syntax=docker/dockerfile:1
#
# Multi-stage build for Knotarium: build the React UI, publish the .NET backend, then
# serve both from a single ASP.NET runtime image (same origin — API + SPA).
#
# NOTE: the workflow engine compiles inline-code and dynamic custom nodes with Roslyn at
# runtime, so this is a framework-dependent, NON-trimmed, NON-AOT publish on purpose —
# trimming/AOT would remove the JIT + reflection metadata those features need.

# ---- 1. Build the frontend (Vite) -> /ui/dist ----
FROM node:22-slim AS frontend
WORKDIR /ui
COPY Frontend/package.json Frontend/package-lock.json ./
RUN npm ci
COPY Frontend/ ./
RUN npm run build

# ---- 2. Publish the backend (framework-dependent) -> /app/publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY Backend/ ./Backend/
RUN dotnet publish Backend/Knotarium.Api/Knotarium.Api.csproj \
      -c Release \
      -p:DebugType=none -p:DebugSymbols=false \
      -o /app/publish

# ---- 3. Runtime image ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.title="Knotarium" \
      org.opencontainers.image.description="Self-hosted, visual workflow automation" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.authors="Andre Kaufmann" \
      org.opencontainers.image.source="https://github.com/AndreKfm/Knotarium"
WORKDIR /app
COPY --from=backend /app/publish ./
# The backend serves the SPA from ./wwwroot (same origin) when present.
COPY --from=frontend /ui/dist ./wwwroot

# The SQLite database AND the auto-generated at-rest credential key live under this one
# directory, mounted as a volume so both survive container restarts/recreation. Storage__DataDirectory
# anchors both (CommonApplicationData — the default — is /usr/share on Linux, not persisted, so we
# point it at the volume explicitly).
#
# Run as the image's non-root "app" user (UID 1654, present since .NET 8). We create /data owned by
# that user *in the image*: a named volume (docker-compose) inherits the mount-point's image ownership
# on first initialization, so the app can write the DB + credential key without a root process. Bind
# mounts keep host ownership instead — chown the host directory to 1654 (or run the container with
# `--user`) when mounting one.
RUN mkdir -p /data && chown app:app /data
VOLUME /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:43120 \
    Storage__DataDirectory=/data

# The credential-encryption key auto-generates onto /data on first run (persisted with the DB), so
# credentials work out of the box. Supply Security__Credentials__EncryptionKeyBase64 at RUN time only
# to bring your own key (e.g. to share one across instances) — never bake secrets into the image; see
# docker-compose.yml / README. The bundle-signing key is env-only and optional.
#
# Port 43120 is unprivileged (>1024), so the non-root user binds it directly — no capabilities needed.
# Use the numeric $APP_UID (1654), not the name: Kubernetes' runAsNonRoot check requires a numeric UID.
USER $APP_UID

EXPOSE 43120
ENTRYPOINT ["dotnet", "Knotarium.Api.dll"]
