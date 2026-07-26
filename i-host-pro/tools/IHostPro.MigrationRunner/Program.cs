using System.Reflection;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    using var host = builder.Build();

    var log = host.Services.GetRequiredService<ILogger<Program>>();

    log.LogInformation("IHostPro.MigrationRunner starting");

    // Discovers every Bounded Context's DbContext by scanning for implementations of
    // IModuleDbContext and applies its pending migrations independently, each within
    // its own PostgreSQL schema and its own migrations-history table
    // (Architecture Principles, Sections 10 and 16). This process is the ONLY place
    // that applies migrations — no DbContext ever calls Database.Migrate() at
    // application startup, avoiding races between multiple instances.
    //
    // Phase 0 note: no Bounded Context module exists yet, so this run is expected
    // to discover zero DbContext types. As each module is implemented (starting
    // with Identity & Access in Phase 1), its DbContext assembly is added to
    // moduleAssemblies below and will be picked up automatically — no other
    // module needs to change.
    var moduleAssemblies = new List<Assembly>
    {
        // typeof(IdentityDbContext).Assembly,     // added when Identity & Access exists
        // typeof(ReservationsDbContext).Assembly, // added when Reservation & Scheduling exists
    };

    var moduleDbContextTypes = moduleAssemblies
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => typeof(IModuleDbContext).IsAssignableFrom(type) && !type.IsAbstract)
        .ToList();

    if (moduleDbContextTypes.Count == 0)
    {
        log.LogWarning("No Bounded Context DbContext discovered. This is expected until the first module (Identity & Access, Phase 1) is implemented.");
    }

    foreach (var dbContextType in moduleDbContextTypes)
    {
        log.LogInformation("Applying migrations for {DbContext}", dbContextType.Name);

        if (Activator.CreateInstance(dbContextType) is DbContext dbContext)
        {
            await dbContext.Database.MigrateAsync();
        }
    }

    log.LogInformation("IHostPro.MigrationRunner finished");
}
catch (Exception ex)
{
    Log.Fatal(ex, "IHostPro.MigrationRunner terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
