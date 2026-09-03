# Fase 12, Checkpoint 5.3C (RabbitMQ Credential Rotation) — same convention
# as docker/{Api,Worker,MigrationRunner,DatabaseBootstrap}.Dockerfile.
# Build context MUST be the solution root (i-host-pro/), never this docker/
# folder itself, so project references resolve.
#
#   docker build -f docker/RabbitMqCredentialRotation.Dockerfile -t ihostpro-rabbitmq-credential-rotation .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore tools/IHostPro.RabbitMqCredentialRotation/IHostPro.RabbitMqCredentialRotation.csproj
RUN dotnet publish tools/IHostPro.RabbitMqCredentialRotation/IHostPro.RabbitMqCredentialRotation.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Same reasoning as DatabaseBootstrap.Dockerfile: zero ProjectReferences
# into any Bounded Context, only Microsoft.NETCore.App required - the plain
# runtime image is correct here, not aspnet.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "IHostPro.RabbitMqCredentialRotation.dll"]
