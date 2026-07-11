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
WORKDIR /app
COPY --from=backend /app/publish ./
# The backend serves the SPA from ./wwwroot (same origin) when present.
COPY --from=frontend /ui/dist ./wwwroot

# SQLite database lives on a volume so it survives container restarts.
RUN mkdir -p /data
VOLUME /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Database__ConnectionString="Data Source=/data/Knotarium.db"

# Secrets (credential-encryption + bundle-signing keys) are supplied at RUN time via env,
# never baked into the image — see docker-compose.yml / README.
# TODO(hardening): run as a non-root user once /data volume ownership is sorted.

EXPOSE 8080
ENTRYPOINT ["dotnet", "Knotarium.Api.dll"]
