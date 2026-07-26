namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Marker interface implemented by every Bounded Context's DbContext. The
/// IHostPro.MigrationRunner discovers every implementation by assembly scanning
/// and applies its pending migrations independently, within its own PostgreSQL
/// schema and its own migrations-history table — adding a new module never
/// requires changing the MigrationRunner or any existing module
/// (Architecture Principles, Sections 10 and 16).
/// </summary>
public interface IModuleDbContext
{
    /// <summary>The PostgreSQL schema owned exclusively by this Bounded Context.</summary>
    string SchemaName { get; }
}
