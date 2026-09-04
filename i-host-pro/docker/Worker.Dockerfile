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

# CP5.3C corrective Decision Gate: bake in the AWS RDS global trust bundle
# for SSL Mode=VerifyFull (never Trust Server Certificate=true) - downloaded
# only from the official AWS domain, validated as a non-empty, parseable
# certificate bundle before it ships in any image. curl/openssl are already
# present in this base image (confirmed) - no extra apt-get install needed.
RUN mkdir -p /app/rds-ca \
    && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /app/rds-ca/global-bundle.pem \
    && test -s /app/rds-ca/global-bundle.pem \
    && openssl x509 -in /app/rds-ca/global-bundle.pem -noout

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
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --from=build --chown=$APP_UID:$APP_UID /app/rds-ca /app/rds-ca

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# CP5.3D-A Decision Gate: same correction as Api.Dockerfile - the real
# bug was found in both images, not just Api's. /health/live (process
# alive), never /health/ready (Redis can legitimately be Degraded per
# RedisDownCorePolicyFlowWorks=true without Worker itself being
# unhealthy) - using /health/ready here would let ECS kill and replace a
# perfectly healthy Worker container over a transient Redis blip.
# /health/ready remains unchanged as a diagnostic endpoint.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:5141/health/live || exit 1

# Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — runs as the
# official image's own built-in non-root user (never root) from here on.
# Must come after apt-get (which needs root) and after the --chown'd COPY
# above, so the app user actually owns the files it runs.
USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.Worker.dll"]
