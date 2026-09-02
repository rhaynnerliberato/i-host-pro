# Fase 12, Checkpoint 1 (CI/CD Pipeline Hardening) — packaging validation only.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references across Bounded Contexts resolve.
#
#   docker build -f docker/Worker.Dockerfile -t ihostpro-worker .
#
# Never pushed to any registry by this checkpoint (CP1 mandate item 18) —
# proves only that a real container image can be produced from the current
# source tree.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Host/IHostPro.Worker/IHostPro.Worker.csproj
RUN dotnet publish src/Host/IHostPro.Worker/IHostPro.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Fase 12, Checkpoint 2 (Observability Finalization) — the aspnet image, not
# the plain runtime image used before this checkpoint: Worker now carries a
# minimal Kestrel listener (FrameworkReference Microsoft.AspNetCore.App,
# added this checkpoint, exclusively for /health/live and /health/ready) —
# the shared framework it depends on isn't present in dotnet/runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:5141/health/ready || exit 1

ENTRYPOINT ["dotnet", "IHostPro.Worker.dll"]
