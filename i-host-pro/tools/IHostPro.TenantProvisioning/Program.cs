using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.TenantProvisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

// One-off, explicitly-executed administrative tool (CP5.3D-C corrective
// Decision Gate) — provisions exactly one Tenant + one initial Admin user,
// idempotently, for an environment that has no self-service tenant creation
// and where DevelopmentIdentitySeeder is explicitly out of bounds (it only
// exists in the Development environment). See TenantProvisioner.cs for the
// real domain/persistence recipe this reuses.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "IHostPro.TenantProvisioning")
    .WriteTo.Console()
    .CreateLogger();

try
{
    var appSecretArn = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AppSecretArn");
    var adminPasswordSecretArn = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AdminPasswordSecretArn");
    var tenantSlugValue = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantSlug");
    var tenantName = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantName");
    var adminEmail = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AdminEmail");
    var adminFullName = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AdminFullName");

    using var secretsClient = new AmazonSecretsManagerClient();

    Log.Information("Reading the database/app connection string from Secrets Manager.");
    var appConnectionString = await GetSecretStringAsync(secretsClient, appSecretArn);

    // Generated in-process only — never accepted via config/args (CP5.3D-C
    // corrective Decision Gate item 14: must never pass through
    // stdout/logs/config/args/shell history).
    var adminPassword = SecurePasswordGenerator.Generate();

    var tenantContext = new TenantContext();
    var options = new DbContextOptionsBuilder<IdentityDbContext>()
        .UseNpgsql(appConnectionString, npgsqlOptions =>
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
        .Options;
    await using var dbContext = new IdentityDbContext(options, tenantContext);

    var provisioner = new TenantProvisioner(dbContext, tenantContext, TimeProvider.System);
    var request = new ProvisioningRequest(
        TenantSlug.Create(tenantSlugValue), tenantName, adminEmail, adminFullName, adminPassword);

    Log.Information("Provisioning tenant {TenantSlug}.", tenantSlugValue);
    var result = await provisioner.ProvisionAsync(request, CancellationToken.None);

    // Only the credential's DESTINATION secret is ever written to when a
    // genuinely new admin was created - reconciling an already-existing
    // admin (e.g. re-adding a lost role) never touches the password secret,
    // since no new password was generated for that admin.
    if (result.UserCreated)
    {
        Log.Information("New admin created — writing its initial password to Secrets Manager.");
        await secretsClient.PutSecretValueAsync(new PutSecretValueRequest
        {
            SecretId = adminPasswordSecretArn,
            SecretString = adminPassword,
        });
    }

    Log.Information(
        "Tenant provisioning completed. TenantId={TenantId} TenantCreated={TenantCreated} UserId={UserId} UserCreated={UserCreated} AdminRoleAssigned={AdminRoleAssigned}",
        result.TenantId, result.TenantCreated, result.UserId, result.UserCreated, result.AdminRoleAssigned);

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Tenant provisioning failed.");
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
