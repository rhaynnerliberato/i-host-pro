using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.HomologScenarioProvisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

// CP5.3D-D corrective Decision Gate: one-off, explicitly-executed HOMOLOG
// TEST FIXTURE tool (HomologScenarioProvisioning=TEST_FIXTURE_ONLY - never
// a commercial onboarding mechanism). Idempotently reconciles the minimal
// real business-data chain (Property + Reservation + WhatsAppTenantRoute)
// needed for a real, signed webhook call (sent separately, not by this
// tool) to exercise the real inbound-message -> ConversationMessageReceived
// -> AI Agent -> Anthropic pipeline. See HomologScenarioProvisioner.cs for
// the full design rationale.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "IHostPro.HomologScenarioProvisioning")
    .WriteTo.Console()
    .CreateLogger();

try
{
    var appSecretArn = ScenarioConfiguration.RequireConfig(configuration, "HomologScenarioProvisioning:AppSecretArn");
    var tenantId = Guid.Parse(ScenarioConfiguration.RequireConfig(configuration, "HomologScenarioProvisioning:TenantId"));

    using var secretsClient = new AmazonSecretsManagerClient();

    Log.Information("Reading the database/app connection string from Secrets Manager.");
    var appConnectionString = await GetSecretStringAsync(secretsClient, appSecretArn);

    var tenantContext = new TenantContext();

    await using var propertyDbContext = new PropertyManagementDbContext(
        new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options,
        tenantContext);

    await using var reservationsDbContext = new ReservationsDbContext(
        new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options,
        tenantContext);

    await using var externalIntegrationsDbContext = new ExternalIntegrationsDbContext(
        new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql(appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
            .Options,
        tenantContext);

    var provisioner = new HomologScenarioProvisioner(
        propertyDbContext, reservationsDbContext, externalIntegrationsDbContext, tenantContext, TimeProvider.System);

    Log.Information("Provisioning Homolog test fixture for tenant {TenantId}.", tenantId);
    var result = await provisioner.ProvisionAsync(tenantId, CancellationToken.None);

    Log.Information(
        "Homolog scenario fixture completed. PropertyId={PropertyId} PropertyCreated={PropertyCreated} ReservationId={ReservationId} ReservationCreated={ReservationCreated} WhatsAppTenantRouteId={RouteId} RouteCreated={RouteCreated}",
        result.PropertyId, result.PropertyCreated, result.ReservationId, result.ReservationCreated, result.WhatsAppTenantRouteId, result.RouteCreated);

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Homolog scenario fixture provisioning failed.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<string> GetSecretStringAsync(IAmazonSecretsManager client, string secretArn)
{
    var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn });
    return response.SecretString
        ?? throw new InvalidOperationException($"Secret {secretArn} has no SecretString value.");
}
