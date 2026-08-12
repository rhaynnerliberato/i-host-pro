using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Occurrences;

internal sealed class FakeCleaningOccurrenceWriter : ICleaningOccurrenceWriter
{
    public List<CleaningOccurrence> RecordedOccurrences { get; } = [];

    public void Record(CleaningOccurrence occurrence) => RecordedOccurrences.Add(occurrence);
}
