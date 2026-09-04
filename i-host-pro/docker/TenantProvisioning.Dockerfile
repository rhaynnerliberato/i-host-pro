# CP5.3D-C corrective Decision Gate (Tenant + Admin provisioning tool) —
# packaging validation only, same convention as
# docker/{Api,Worker,MigrationRunner,DatabaseBootstrap}.Dockerfile.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references resolve.
#
#   docker build -f docker/TenantProvisioning.Dockerfile -t ihostpro-tenant-provisioning .
#
# Never pushed to any registry by this checkpoint — proves only that a real
# container image can be produced from the current source tree.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Same reasoning as DatabaseBootstrap.Dockerfile: this tool connects to RDS
# as ihostpro_app (the database/app secret's own connection string, already
# SSL Mode=VerifyFull) - never a weaker TLS mode than the runtime app role.
RUN mkdir -p /app/rds-ca \
    && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /app/rds-ca/global-bundle.pem \
    && test -s /app/rds-ca/global-bundle.pem \
    && openssl x509 -in /app/rds-ca/global-bundle.pem -noout

COPY . .
RUN dotnet restore tools/IHostPro.TenantProvisioning/IHostPro.TenantProvisioning.csproj
RUN dotnet publish tools/IHostPro.TenantProvisioning/IHostPro.TenantProvisioning.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Unlike DatabaseBootstrap (plain Npgsql only), this tool references
# IHostPro.Contexts.Identity.Infrastructure, which pulls in
# Microsoft.Extensions.Identity.Core - its published runtimeconfig.json
# requires Microsoft.AspNetCore.App (confirmed by inspecting the actual
# published runtimeconfig.json before choosing this base image, same
# diligence as every other Dockerfile here), so the aspnet runtime image is
# required, not the smaller plain runtime one DatabaseBootstrap uses.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --from=build --chown=$APP_UID:$APP_UID /app/rds-ca /app/rds-ca

# Runs as the official image's own built-in non-root user (never root),
# same as every other image in this project.
USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.TenantProvisioning.dll"]
