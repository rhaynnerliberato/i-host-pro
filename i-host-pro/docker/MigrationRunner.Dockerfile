# Fase 12, Checkpoint 1 (CI/CD Pipeline Hardening) — packaging validation only.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references across Bounded Contexts resolve.
#
#   docker build -f docker/MigrationRunner.Dockerfile -t ihostpro-migrationrunner .
#
# Never pushed to any registry by this checkpoint (CP1 mandate item 18) —
# proves only that a real container image can be produced from the current
# source tree.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore tools/IHostPro.MigrationRunner/IHostPro.MigrationRunner.csproj
RUN dotnet publish tools/IHostPro.MigrationRunner/IHostPro.MigrationRunner.csproj \
    -c Release \
    -o /app/publish \
    --no-restore


# Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — the plain
# "runtime" image this final stage used before this checkpoint no longer
# boots at all: the published runtimeconfig.json requires
# Microsoft.AspNetCore.App (this project's own ProjectReferences pull it in
# transitively), which the plain runtime image does not carry, so
# `dotnet IHostPro.MigrationRunner.dll` fails at the native host-resolver
# stage before any managed code — including this checkpoint's own USER
# switch below — ever runs. Same aspnet image Api/Worker already use.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

# Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — runs as the
# official image's own built-in non-root user (never root).
USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.MigrationRunner.dll"]
