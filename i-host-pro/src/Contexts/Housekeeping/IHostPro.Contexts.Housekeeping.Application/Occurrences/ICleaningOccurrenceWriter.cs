using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <summary>Stages a single append-only occurrence — mirrors <c>IHousekeepingAuditWriter</c> exactly.</summary>
public interface ICleaningOccurrenceWriter
{
    void Record(CleaningOccurrence occurrence);
}
