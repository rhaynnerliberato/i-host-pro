using FluentAssertions;
using IHostPro.Contexts.Dashboard.Application.Overview;

namespace IHostPro.Contexts.Dashboard.Tests.Unit.Application.Overview;

public class GetDashboardOverviewQueryValidatorTests
{
    private readonly GetDashboardOverviewQueryValidator _validator = new();

    [Fact]
    public void A_valid_window_passes()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new GetDashboardOverviewQuery(from, from.AddDays(30));

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void To_equal_to_From_fails_with_dashboard_overview_invalid_interval()
    {
        var moment = DateTimeOffset.UtcNow;
        var query = new GetDashboardOverviewQuery(moment, moment);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "dashboard_overview_invalid_interval");
    }

    [Fact]
    public void To_before_From_fails_with_dashboard_overview_invalid_interval()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new GetDashboardOverviewQuery(from, from.AddDays(-1));

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "dashboard_overview_invalid_interval");
    }

    [Fact]
    public void A_window_exactly_at_the_max_size_passes()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new GetDashboardOverviewQuery(from, from + GetDashboardOverviewQueryValidator.MaxWindow);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_window_larger_than_the_max_size_fails_with_dashboard_overview_window_too_large()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new GetDashboardOverviewQuery(
            from, from + GetDashboardOverviewQueryValidator.MaxWindow + TimeSpan.FromSeconds(1));

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "dashboard_overview_window_too_large");
    }

    /// <summary>
    /// Different UTC offsets can represent the exact same instant — the
    /// validator must compare instants, not local clock values (mandate §38).
    /// </summary>
    [Fact]
    public void A_valid_window_with_different_utc_offsets_representing_valid_instants_passes()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-3));
        var to = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.FromHours(9));

        var query = new GetDashboardOverviewQuery(from, to);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }
}
