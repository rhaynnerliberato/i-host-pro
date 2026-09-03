using Npgsql;
using Serilog;
using System.Text.Json.Serialization;

namespace IHostPro.DatabaseBootstrap;

// Extracted out of Program.cs (which keeps only AWS/orchestration
// plumbing) so this SQL logic can be exercised directly by
// IHostPro.DatabaseBootstrap.Tests.Integration against a real Postgres
// (Testcontainers) without needing a real AWS Secrets Manager call.
public static class DatabaseRoleReconciler
{
    public static async Task CreateOrUpdateLoginRoleAsync(
        NpgsqlConnection connection, string roleName, string password, CancellationToken cancellationToken = default)
    {
        // CREATE-if-absent / ALTER-if-present, same idempotency guarantee as
        // docker/postgres/init/01-create-roles.sh: never DROPs a role, never
        // resets unrelated attributes on an existing one (CREATEDB/
        // CREATEROLE/BYPASSRLS/membership stay whatever they already were).
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
        await command.ExecuteNonQueryAsync(cancellationToken);

        Log.Information("Role {RoleName} reconciled (created or password updated).", roleName);
    }

    // ihostpro_migrator needs CREATE on the database itself to run a Bounded
    // Context's first-ever migration (CREATE SCHEMA <context>) - same
    // requirement, same grant, as docker/postgres/init/01-create-roles.sh.
    // Idempotent: re-granting an already-held privilege is a no-op. This is
    // the ONLY grant this tool ever issues - per-schema GRANT/ALTER DEFAULT
    // PRIVILEGES belong to IHostPro.MigrationRunner and each Bounded
    // Context's own EF InitialCreate migration, which already do that,
    // idempotently, every run (confirmed by reading both).
    public static async Task GrantCreateOnDatabaseAsync(
        NpgsqlConnection connection, string databaseName, string roleName, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"GRANT CREATE ON DATABASE {QuoteIdentifier(databaseName)} TO {QuoteIdentifier(roleName)};";
        await command.ExecuteNonQueryAsync(cancellationToken);

        Log.Information("GRANT CREATE ON DATABASE applied to role {RoleName}.", roleName);
    }

    // PostgreSQL identifier/string-literal quoting (the same rules psql's
    // format() %I/%L apply) - role names here are always our own fixed
    // constants, and passwords are Terraform-generated with special=false
    // (alphanumeric only), but both are still quoted defensively rather than
    // concatenated raw, since this SQL can never be parameterized inside a
    // DO block's dynamic role name/password.
    public static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    public static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}

public sealed class RdsMasterSecret
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
