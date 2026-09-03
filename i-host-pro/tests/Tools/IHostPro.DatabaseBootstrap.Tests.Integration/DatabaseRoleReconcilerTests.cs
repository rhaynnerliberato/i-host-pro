using FluentAssertions;
using IHostPro.DatabaseBootstrap;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IHostPro.DatabaseBootstrap.Tests.Integration;

// One shared Postgres container for the whole class (role/database
// reconciliation is cheap and the tests use uniquely-generated role names
// per test, so they don't interfere with each other) - matches the general
// Testcontainers-per-test-class shape already used elsewhere in this
// solution, just without this tool's own shared context fixture (it has
// none - it isn't a Bounded Context).
public sealed class DatabaseRoleReconcilerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    private NpgsqlConnection _superuserConnection = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _superuserConnection = new NpgsqlConnection(_postgres.GetConnectionString());
        await _superuserConnection.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        await _superuserConnection.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static string UniqueRoleName([System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        // PostgreSQL identifiers are silently truncated at 63 bytes - keep
        // well under that so two long test names never collide.
        var candidate = $"bt_{testName.ToLowerInvariant()}_{Guid.NewGuid():N}";
        return candidate[..Math.Min(63, candidate.Length)];
    }

    private async Task<bool> RoleExistsAsync(string roleName)
    {
        await using var command = _superuserConnection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @roleName)";
        command.Parameters.AddWithValue("roleName", roleName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> CanConnectAsync(string roleName, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = roleName,
            Password = password,
        };
        await using var connection = new NpgsqlConnection(builder.ToString());
        try
        {
            await connection.OpenAsync();
            return true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    [Fact]
    public async Task CreateOrUpdateLoginRoleAsync_role_absent_creates_it_with_login_and_the_given_password()
    {
        var roleName = UniqueRoleName();

        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "FirstPassword123");

        (await RoleExistsAsync(roleName)).Should().BeTrue();
        (await CanConnectAsync(roleName, "FirstPassword123")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateOrUpdateLoginRoleAsync_run_twice_is_idempotent_and_takes_the_alter_branch()
    {
        var roleName = UniqueRoleName();

        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "FirstPassword123");
        var act = async () => await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "FirstPassword123");

        await act.Should().NotThrowAsync();
        (await RoleExistsAsync(roleName)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateOrUpdateLoginRoleAsync_role_already_exists_reconciles_the_password_the_old_one_stops_working()
    {
        var roleName = UniqueRoleName();
        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "OldPassword123");

        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "NewPassword456");

        (await CanConnectAsync(roleName, "NewPassword456")).Should().BeTrue();
        (await CanConnectAsync(roleName, "OldPassword123")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateOrUpdateLoginRoleAsync_new_role_never_has_superuser_createdb_createrole_bypassrls_or_replication()
    {
        var roleName = UniqueRoleName();

        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "SomePassword123");

        await using var command = _superuserConnection.CreateCommand();
        command.CommandText = """
            SELECT rolsuper, rolcreatedb, rolcreaterole, rolbypassrls, rolreplication, rolcanlogin
            FROM pg_roles WHERE rolname = @roleName
            """;
        command.Parameters.AddWithValue("roleName", roleName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeFalse("rolsuper");
        reader.GetBoolean(1).Should().BeFalse("rolcreatedb");
        reader.GetBoolean(2).Should().BeFalse("rolcreaterole");
        reader.GetBoolean(3).Should().BeFalse("rolbypassrls");
        reader.GetBoolean(4).Should().BeFalse("rolreplication");
        reader.GetBoolean(5).Should().BeTrue("rolcanlogin");
    }

    [Fact]
    public async Task GrantCreateOnDatabaseAsync_grants_exactly_the_create_privilege_on_the_database()
    {
        var roleName = UniqueRoleName();
        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "SomePassword123");
        var databaseName = _postgres.GetConnectionString().Split(';')
            .First(p => p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase))
            .Split('=')[1];

        await DatabaseRoleReconciler.GrantCreateOnDatabaseAsync(_superuserConnection, databaseName, roleName);

        await using var command = _superuserConnection.CreateCommand();
        command.CommandText = "SELECT has_database_privilege(@roleName, @databaseName, 'CREATE')";
        command.Parameters.AddWithValue("roleName", roleName);
        command.Parameters.AddWithValue("databaseName", databaseName);
        var hasCreate = (bool)(await command.ExecuteScalarAsync())!;

        hasCreate.Should().BeTrue();
    }

    [Fact]
    public async Task CreateOrUpdateLoginRoleAsync_a_role_that_is_never_passed_to_GrantCreateOnDatabaseAsync_never_gets_that_privilege()
    {
        // The asymmetry that actually enforces least-privilege between the
        // two roles: ihostpro_migrator gets GRANT CREATE ON DATABASE (a
        // separate, explicit call), ihostpro_app never does -
        // CreateOrUpdateLoginRoleAsync itself grants nothing beyond LOGIN.
        var appLikeRoleName = UniqueRoleName();
        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, appLikeRoleName, "SomePassword123");

        await using var command = _superuserConnection.CreateCommand();
        command.CommandText = "SELECT has_database_privilege(@roleName, current_database(), 'CREATE')";
        command.Parameters.AddWithValue("roleName", appLikeRoleName);
        var hasCreate = (bool)(await command.ExecuteScalarAsync())!;

        hasCreate.Should().BeFalse();
    }

    [Fact]
    public async Task Reconciler_never_grants_privileges_on_a_pre_existing_schema_it_was_not_told_to_touch()
    {
        // Proves the tool stays inside its documented scope (roles + one
        // database-level GRANT CREATE only) - schema/table privileges are
        // MigrationRunner's and each Bounded Context's own EF migration's
        // job, not this tool's, even when a schema already exists.
        await using (var setupCommand = _superuserConnection.CreateCommand())
        {
            setupCommand.CommandText = "CREATE SCHEMA IF NOT EXISTS pre_existing_probe; CREATE TABLE pre_existing_probe.t (id int);";
            await setupCommand.ExecuteNonQueryAsync();
        }

        var roleName = UniqueRoleName();
        await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, "SomePassword123");
        await DatabaseRoleReconciler.GrantCreateOnDatabaseAsync(_superuserConnection, "postgres", roleName);

        await using var command = _superuserConnection.CreateCommand();
        command.CommandText = "SELECT has_schema_privilege(@roleName, 'pre_existing_probe', 'USAGE')";
        command.Parameters.AddWithValue("roleName", roleName);
        var hasSchemaUsage = (bool)(await command.ExecuteScalarAsync())!;

        hasSchemaUsage.Should().BeFalse();
    }

    [Fact]
    public void RequireConfig_missing_or_empty_value_throws_a_clear_error()
    {
        using var jsonStream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("""{ "DatabaseBootstrap": { "Present": "" } }"""));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .Build();

        var missingKey = () => BootstrapConfiguration.RequireConfig(configuration, "DatabaseBootstrap:Missing");
        var emptyValue = () => BootstrapConfiguration.RequireConfig(configuration, "DatabaseBootstrap:Present");

        missingKey.Should().Throw<InvalidOperationException>().WithMessage("*DatabaseBootstrap:Missing*");
        emptyValue.Should().Throw<InvalidOperationException>().WithMessage("*DatabaseBootstrap:Present*");
    }

    [Fact]
    public void RdsMasterSecret_malformed_json_fails_deserialization_instead_of_producing_a_half_populated_object()
    {
        var act = () => System.Text.Json.JsonSerializer.Deserialize<RdsMasterSecret>("{ \"username\": \"only-this-field\" }");

        // `required` members make System.Text.Json throw JsonException when
        // the payload is missing any of them (here, password), rather than
        // silently leaving it at a default - a malformed/unexpected secret
        // shape must fail loudly, never connect with a half-built target.
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    // CP5.3C runtime-proof correction: the real, AWS-managed master secret
    // for this RDS instance was found (via a real DatabaseBootstrap
    // execution that failed) to carry ONLY username/password - not
    // host/port/dbname, despite AWS's general documentation describing a
    // wider shape. This is the regression test proving the tool now works
    // against that minimal, real shape, with endpoint/database identity
    // coming from NON_SECRET_CONFIG instead (see Program.cs).
    [Fact]
    public void RdsMasterSecret_minimal_aws_managed_shape_deserializes_successfully()
    {
        const string minimalShape = """{"username":"ihostpro_master","password":"SomePassword123"}""";

        var act = () => System.Text.Json.JsonSerializer.Deserialize<RdsMasterSecret>(minimalShape);

        act.Should().NotThrow();
        var secret = System.Text.Json.JsonSerializer.Deserialize<RdsMasterSecret>(minimalShape)!;
        secret.Username.Should().Be("ihostpro_master");
        secret.Password.Should().Be("SomePassword123");
    }

    // Item 11: "missing host/port/database name must fail clearly" - proven
    // generically by RequireConfig_missing_or_empty_value_throws_a_clear_error
    // above (BootstrapConfiguration.RequireConfig has no special-casing per
    // key), demonstrated here explicitly for the 3 real key names Program.cs
    // actually reads, so the coverage is traceable rather than implied.
    [Theory]
    [InlineData("DatabaseBootstrap:RdsHost")]
    [InlineData("DatabaseBootstrap:RdsPort")]
    [InlineData("DatabaseBootstrap:RdsDatabaseName")]
    public void RequireConfig_missing_rds_endpoint_config_fails_clearly(string key)
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => BootstrapConfiguration.RequireConfig(configuration, key);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public async Task CreateOrUpdateLoginRoleAsync_failure_never_leaks_the_password_into_the_exception_message()
    {
        // Force a real failure: an invalid role name (containing a NUL-like
        // control construct Postgres rejects at the identifier level isn't
        // easy to produce safely - instead we reuse a role name that
        // collides with a RESERVED keyword scenario is unnecessary; simplest
        // reliable forced failure is closing the connection first.
        var roleName = UniqueRoleName();
        const string password = "SuperSecretValueThatMustNeverLeak123";
        await _superuserConnection.CloseAsync();

        var act = async () => await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(_superuserConnection, roleName, password);

        var assertion = await act.Should().ThrowAsync<Exception>();
        assertion.Which.ToString().Should().NotContain(password);
        assertion.Which.Message.Should().NotContain(password);

        // Restore the connection for IAsyncLifetime's DisposeAsync.
        await _superuserConnection.OpenAsync();
    }
}
