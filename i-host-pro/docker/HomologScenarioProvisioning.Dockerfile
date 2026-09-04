# CP5.3D-D corrective Decision Gate (Homolog synthetic business fixture
# tool) — packaging validation only, same convention as
# docker/{Api,Worker,MigrationRunner,DatabaseBootstrap,TenantProvisioning}.Dockerfile.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references resolve.
#
#   docker build -f docker/HomologScenarioProvisioning.Dockerfile -t ihostpro-homolog-scenario-provisioning .
#
# HomologScenarioProvisioning=TEST_FIXTURE_ONLY - never part of the
# commercial/runtime image set, never pushed by any checkpoint but its own
# explicitly-authorized one.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Same reasoning as DatabaseBootstrap/TenantProvisioning.Dockerfile: this
# tool connects to RDS as ihostpro_app (the database/app secret's own
# connection string, already SSL Mode=VerifyFull).
RUN mkdir -p /app/rds-ca \
    && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /app/rds-ca/global-bundle.pem \
    && test -s /app/rds-ca/global-bundle.pem \
    && openssl x509 -in /app/rds-ca/global-bundle.pem -noout

COPY . .
RUN dotnet restore tools/IHostPro.HomologScenarioProvisioning/IHostPro.HomologScenarioProvisioning.csproj
RUN dotnet publish tools/IHostPro.HomologScenarioProvisioning/IHostPro.HomologScenarioProvisioning.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Unlike TenantProvisioning (which references Identity.Infrastructure and
# needs the aspnet image for Microsoft.Extensions.Identity.Core), this
# tool's dependencies (PropertyManagement/Reservations/ExternalIntegrations
# Infrastructure) require only Microsoft.NETCore.App - confirmed by
# inspecting the actual published runtimeconfig.json before choosing this
# base image, same diligence as every other Dockerfile here - so the
# smaller plain runtime image is correct, matching DatabaseBootstrap.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --from=build --chown=$APP_UID:$APP_UID /app/rds-ca /app/rds-ca

USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.HomologScenarioProvisioning.dll"]
