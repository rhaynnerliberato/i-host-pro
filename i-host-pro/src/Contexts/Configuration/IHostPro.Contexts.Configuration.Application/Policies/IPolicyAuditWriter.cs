using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>Records a <see cref="PolicyAuditEntry"/> in the same transaction as the write it documents — mirrors <c>IReservationAuditWriter</c> exactly.</summary>
public interface IPolicyAuditWriter
{
    void Record(PolicyAuditEntry entry);
}
