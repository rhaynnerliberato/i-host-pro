using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="IGuestStayOperationAuditWriter"/>
public sealed class GuestStayOperationAuditWriter : IGuestStayOperationAuditWriter
{
    private readonly GuestOperationsDbContext _dbContext;

    public GuestStayOperationAuditWriter(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public void Record(GuestStayOperationAuditEntry entry) => _dbContext.GuestStayOperationAuditLog.Add(entry);
}
