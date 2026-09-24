using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.TenantProvisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using StackExchange.Redis;

// One-off, explicitly-executed administrative tool (CP5.3D-C corrective
// Decision Gate; extended by the Tenant Suspension/Reactivation Enforcement
// workstream) — provisions a Tenant + initial Admin user, or suspends/
// reactivates an existing one, for an environment that has no self-service
// tenant creation/administration and where DevelopmentIdentitySeeder is
// explicitly out of bounds (it only exists in the Development environment).
// See TenantProvisioner.cs for the real domain/persistence recipe this
// reuses.
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
    // Optional, defaults to "Provision" — every existing invocation of this
    // tool (ECS one-off tasks that never set this key at all) keeps behaving
    // exactly as before this workstream added Suspend/Reactivate.
    var operation = configuration["TenantProvisioning:Operation"];
    operation = string.IsNullOrWhiteSpace(operation) ? "Provision" : operation.Trim();

    var appSecretArn = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AppSecretArn");

    using var secretsClient = new AmazonSecretsManagerClient();

    Log.Information("Reading the database/app connection string from Secrets Manager.");
    var appConnectionString = await GetSecretStringAsync(secretsClient, appSecretArn);

    var tenantContext = new TenantContext();
    var options = new DbContextOptionsBuilder<IdentityDbContext>()
        .UseNpgsql(appConnectionString, npgsqlOptions =>
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
        .Options;
    await using var dbContext = new IdentityDbContext(options, tenantContext);

    switch (operation)
    {
        case "Provision":
            await RunProvisionAsync(configuration, secretsClient, dbContext, tenantContext);
            break;

        case "Suspend":
        case "Reactivate":
            await RunLifecycleOperationAsync(configuration, dbContext, tenantContext, operation);
            break;

        default:
            throw new InvalidOperationException(
                $"Unknown TenantProvisioning:Operation '{operation}' - expected 'Provision', 'Suspend' or 'Reactivate'.");
    }

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

static async Task RunProvisionAsync(
    IConfiguration configuration, IAmazonSecretsManager secretsClient, IdentityDbContext dbContext, ITenantContext tenantContext)
{
    var adminPasswordSecretArn = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AdminPasswordSecretArn");
    var tenantSlugValue = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantSlug");
    var tenantName = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantName");
    var adminEmail = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AdminEmail");
    var adminFullName = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:AdminFullName");

    // Generated in-process only — never accepted via config/args (CP5.3D-C
    // corrective Decision Gate item 14: must never pass through
    // stdout/logs/config/args/shell history).
    var adminPassword = SecurePasswordGenerator.Generate();

    // Suspend/Reactivate are never reachable from this operation, so a
    // real Redis connection is not required for a plain provisioning run —
    // preserves every existing invocation of this tool exactly as before.
    var provisioner = new TenantProvisioner(dbContext, tenantContext, new NullTenantAccessStateCache(), TimeProvider.System);
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
}

static async Task RunLifecycleOperationAsync(
    IConfiguration configuration, IdentityDbContext dbContext, ITenantContext tenantContext, string operation)
{
    var tenantSlugValue = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantSlug");
    var redisConnectionString = ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:RedisConnectionString");
    var tenantSlug = TenantSlug.Create(tenantSlugValue);

    // A real Redis connection is required here (unlike Provision above):
    // the cache write is what actually blocks/restores already-issued
    // credentials — see TenantProvisioner.SuspendAsync/ReactivateAsync's own
    // doc comments.
    await using var connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
    var accessStateCache = new RedisTenantAccessStateCache(connectionMultiplexer, NullLogger<RedisTenantAccessStateCache>.Instance);
    var provisioner = new TenantProvisioner(dbContext, tenantContext, accessStateCache, TimeProvider.System);

    Log.Information("{Operation} tenant {TenantSlug}.", operation, tenantSlugValue);
    var result = operation == "Suspend"
        ? await provisioner.SuspendAsync(tenantSlug, CancellationToken.None)
        : await provisioner.ReactivateAsync(tenantSlug, CancellationToken.None);

    Log.Information(
        "Tenant {Operation} completed. TenantId={TenantId} TenantSlug={TenantSlug} PreviousStatus={PreviousStatus} NewStatus={NewStatus}",
        operation, result.TenantId, result.TenantSlug, result.PreviousStatus, result.NewStatus);
}

static async Task<string> GetSecretStringAsync(IAmazonSecretsManager client, string secretArn)
{
    var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn });
    return response.SecretString
        ?? throw new InvalidOperationException($"Secret {secretArn} has no SecretString value.");
}
