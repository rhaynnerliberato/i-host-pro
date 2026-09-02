# Fase 12, Checkpoint 1 (CI/CD Pipeline Hardening) — packaging validation only.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references across Bounded Contexts resolve.
#
#   docker build -f docker/Api.Dockerfile -t ihostpro-api .
#
# Never pushed to any registry by this checkpoint (CP1 mandate item 18) —
# proves only that a real container image can be produced from the current
# source tree.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Host/IHostPro.Api/IHostPro.Api.csproj
RUN dotnet publish src/Host/IHostPro.Api/IHostPro.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

# Fase 12, Checkpoint 2 (Observability Finalization, Documento 21 §18) — the
# real dependency-aware readiness endpoint the Api now exposes. curl adds a
# small, standard layer for this alone; no other tool in this image needs it.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

# Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — runs as the
# official image's own built-in non-root user (never root) from here on.
# Must come after apt-get (which needs root) and after the --chown'd COPY
# above, so the app user actually owns the files it runs.
USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.Api.dll"]
