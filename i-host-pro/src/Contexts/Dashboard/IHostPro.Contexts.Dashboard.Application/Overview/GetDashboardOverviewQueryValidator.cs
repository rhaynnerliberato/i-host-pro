using FluentValidation;

namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <summary>
/// <see cref="GetDashboardOverviewQuery.From"/>/<see cref="GetDashboardOverviewQuery.To"/>
/// are always required (never an unbounded scan) and the window between them
/// is capped at <see cref="MaxWindow"/> — an explicit IMPLEMENTATION DECISION
/// (Checkpoint 2 mandate, §5: "DEC técnica do MVP para impedir consulta
/// ilimitada"), not a documented requirement — mirrors
/// <c>ListScheduleQueryValidator</c>'s own 100-day precedent exactly.
/// </summary>
public sealed class GetDashboardOverviewQueryValidator : AbstractValidator<GetDashboardOverviewQuery>
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(100);

    public GetDashboardOverviewQueryValidator()
    {
        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithErrorCode("dashboard_overview_invalid_interval")
            .WithMessage("dashboard_overview_invalid_interval");

        RuleFor(x => x)
            .Must(x => x.To - x.From <= MaxWindow)
            .WithErrorCode("dashboard_overview_window_too_large")
            .WithMessage("dashboard_overview_window_too_large");
    }
}
