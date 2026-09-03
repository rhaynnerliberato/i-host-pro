# Fase 12, Checkpoint 5.3C (Database Bootstrap Tool) — packaging validation
# only, same convention as docker/{Api,Worker,MigrationRunner}.Dockerfile.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references resolve.
#
#   docker build -f docker/DatabaseBootstrap.Dockerfile -t ihostpro-database-bootstrap .
#
# Never pushed to any registry by this checkpoint — proves only that a real
# container image can be produced from the current source tree.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# CP5.3C corrective Decision Gate: bake in the AWS RDS global trust bundle
# for SSL Mode=VerifyFull (never Trust Server Certificate=true) - downloaded
# only from the official AWS domain, validated as a non-empty, parseable
# certificate bundle before it ships in any image. curl/openssl are already
# present in this base image (confirmed) - no extra apt-get install needed.
# This tool needs it too: it connects as the RDS master credential, which
# must never use a weaker TLS mode than the runtime app/migrator roles do.
RUN mkdir -p /app/rds-ca \
    && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /app/rds-ca/global-bundle.pem \
    && test -s /app/rds-ca/global-bundle.pem \
    && openssl x509 -in /app/rds-ca/global-bundle.pem -noout

COPY . .
RUN dotnet restore tools/IHostPro.DatabaseBootstrap/IHostPro.DatabaseBootstrap.csproj
RUN dotnet publish tools/IHostPro.DatabaseBootstrap/IHostPro.DatabaseBootstrap.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Unlike MigrationRunner, this tool has zero ProjectReferences into any
# Bounded Context (only AWSSDK.SecretsManager/Npgsql/Serilog directly) - its
# published runtimeconfig.json requires only Microsoft.NETCore.App, never
# Microsoft.AspNetCore.App (confirmed by inspecting the actual published
# runtimeconfig.json before choosing this base image), so the plain,
# smaller runtime image is correct here, not the aspnet one Api/Worker/
# MigrationRunner need.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --from=build --chown=$APP_UID:$APP_UID /app/rds-ca /app/rds-ca

# Runs as the official image's own built-in non-root user (never root),
# same as every other image in this project.
USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.DatabaseBootstrap.dll"]
