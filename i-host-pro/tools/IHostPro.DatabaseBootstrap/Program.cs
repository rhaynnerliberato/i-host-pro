using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

// One-off bootstrap tool for a fresh RDS instance: creates/updates the
// ihostpro_migrator / ihostpro_app LOGIN roles idempotently, connecting only
// with the RDS-managed master credential (never used by any other runtime
// component). This mirrors docker/postgres/init/01-create-roles.sh's
// CREATE-if-absent/ALTER-if-present role logic exactly.
//
// Deliberately does NOT grant per-schema privileges (GRANT ... / ALTER
// DEFAULT PRIVILEGES) beyond the one database-level CREATE grant below -
// every Bounded Context's own EF InitialCreate migration and
// IHostPro.MigrationRunner's Wolverine outbox setup already issue those
// grants idempotently, every run, connected as ihostpro_migrator (confirmed
// by reading both - see e.g. Identity's InitialCreate migration and
// MigrationRunner/Program.cs's platform_messaging/identity_messaging/etc.
// blocks). Duplicating that logic here would be redundant and a second
// source of truth to keep in sync.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "IHostPro.DatabaseBootstrap")
    .WriteTo.Console()
    .CreateLogger();

try
{
    var rdsMasterSecretArn = RequireConfig(configuration, "DatabaseBootstrap:RdsMasterSecretArn");
    var appSecretArn = RequireConfig(configuration, "DatabaseBootstrap:AppSecretArn");
    var migratorSecretArn = RequireConfig(configuration, "DatabaseBootstrap:MigratorSecretArn");

    using var secretsClient = new AmazonSecretsManagerClient();

    Log.Information("Reading RDS master credential and target role connection strings from Secrets Manager.");

    var masterSecretJson = await GetSecretStringAsync(secretsClient, rdsMasterSecretArn);
    var master = JsonSerializer.Deserialize<RdsMasterSecret>(masterSecretJson)
        ?? throw new InvalidOperationException("RDS master secret did not deserialize to the expected shape.");

    var appConnectionString = await GetSecretStringAsync(secretsClient, appSecretArn);
    var migratorConnectionString = await GetSecretStringAsync(secretsClient, migratorSecretArn);

    // NpgsqlConnectionStringBuilder, not string splitting/regex - the
    // authoritative, injection-safe way to pull Username/Password back out
    // of a connection string this same codebase generated (RDS module's
    // secret_string_wo).
    var appRole = new NpgsqlConnectionStringBuilder(appConnectionString);
    var migratorRole = new NpgsqlConnectionStringBuilder(migratorConnectionString);

    var masterConnectionString = new NpgsqlConnectionStringBuilder
    {
        Host = master.Host,
        Port = master.Port,
        Database = master.Dbname,
        Username = master.Username,
        Password = master.Password,
        SslMode = SslMode.Require,
    }.ToString();

    await using var connection = new NpgsqlConnection(masterConnectionString);
    await connection.OpenAsync();

    Log.Information("Connected as RDS master credential. Reconciling roles.");

    await CreateOrUpdateLoginRoleAsync(connection, migratorRole.Username!, migratorRole.Password!);
    await CreateOrUpdateLoginRoleAsync(connection, appRole.Username!, appRole.Password!);

    // ihostpro_migrator needs CREATE on the database itself to run a Bounded
    // Context's first-ever migration (CREATE SCHEMA <context>) - same
    // requirement, same grant, as docker/postgres/init/01-create-roles.sh.
    // Idempotent: re-granting an already-held privilege is a no-op.
    await using (var grantCommand = connection.CreateCommand())
    {
        grantCommand.CommandText =
            $"GRANT CREATE ON DATABASE {QuoteIdentifier(master.Dbname)} TO {QuoteIdentifier(migratorRole.Username!)};";
        await grantCommand.ExecuteNonQueryAsync();
    }

    Log.Information("Database bootstrap completed successfully. Roles {MigratorRole} and {AppRole} are ready.",
        migratorRole.Username, appRole.Username);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Database bootstrap failed.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string RequireConfig(IConfiguration configuration, string key)
{
    var value = configuration[key];
    // appsettings.json ships with these keys present but empty (documenting
    // the expected shape without a real value baked into the image) - an
    // empty string must fail exactly like a missing key, not silently reach
    // the AWS SDK and fail there instead with a much less clear error.
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Missing required configuration value: {key}")
        : value;
}

static async Task<string> GetSecretStringAsync(IAmazonSecretsManager client, string secretArn)
{
    var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn });
    return response.SecretString
        ?? throw new InvalidOperationException($"Secret {secretArn} has no SecretString value.");
}

static async Task CreateOrUpdateLoginRoleAsync(NpgsqlConnection connection, string roleName, string password)
{
    // CREATE-if-absent / ALTER-if-present, same idempotency guarantee as
    // 01-create-roles.sh: never DROPs a role, never resets unrelated
    // attributes on an existing one (CREATEDB/CREATEROLE/BYPASSRLS/
    // membership stay whatever they already were).
    var sql = $"""
        DO $do$
        BEGIN
            IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = {QuoteLiteral(roleName)}) THEN
                CREATE ROLE {QuoteIdentifier(roleName)} LOGIN PASSWORD {QuoteLiteral(password)}
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
            ELSE
                ALTER ROLE {QuoteIdentifier(roleName)} WITH LOGIN PASSWORD {QuoteLiteral(password)};
            END IF;
        END
        $do$;
        """;

    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();

    Log.Information("Role {RoleName} reconciled (created or password updated).", roleName);
}

// PostgreSQL identifier/string-literal quoting (the same rules psql's
// format() %I/%L apply) - role names here are always our own fixed
// constants, and passwords are Terraform-generated with special=false
// (alphanumeric only), but both are still quoted defensively rather than
// concatenated raw, since this SQL can never be parameterized inside a DO
// block's dynamic role name/password.
static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";

internal sealed class RdsMasterSecret
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("password")]
    public required string Password { get; init; }

    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("dbname")]
    public required string Dbname { get; init; }
}
