using FluentValidation;

namespace IHostPro.Contexts.Reservations.Application.Schedule;

/// <summary>
/// <see cref="ListScheduleQuery.From"/>/<see cref="ListScheduleQuery.To"/>
/// are always required (never an unbounded scan) and the window between
/// them is capped at <see cref="MaxWindow"/> — an explicit IMPLEMENTATION
/// DECISION (Checkpoint 1, item 29 of the approval: "propor um limite
/// técnico explícito... registrar como decisão de implementação"), not a
/// documented requirement — chosen to comfortably cover the month view
/// (Checkpoint 1 scope: day/week/month) plus adjacent-month navigation
/// buffer, while still bounding the query. Not yet backed by any caching
/// (Checkpoint 1, item 29: "não criar cache no Incremento 1 sem evidência de
/// necessidade").
/// </summary>
public sealed class ListScheduleQueryValidator : AbstractValidator<ListScheduleQuery>
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(100);

    public ListScheduleQueryValidator()
    {
        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithErrorCode("schedule_invalid_interval")
            .WithMessage("schedule_invalid_interval");

        RuleFor(x => x)
            .Must(x => x.To - x.From <= MaxWindow)
            .WithErrorCode("schedule_window_too_large")
            .WithMessage("schedule_window_too_large");

        RuleFor(x => x.EventType)
            .Must(type => type is "Reservation" or "Cleaning")
            .When(x => x.EventType is not null)
            .WithErrorCode("schedule_event_type_invalid")
            .WithMessage("schedule_event_type_invalid");
    }
}
