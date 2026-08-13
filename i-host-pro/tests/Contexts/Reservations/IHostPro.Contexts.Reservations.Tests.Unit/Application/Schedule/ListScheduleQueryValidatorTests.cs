using FluentAssertions;
using IHostPro.Contexts.Reservations.Application.Schedule;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Schedule;

public class ListScheduleQueryValidatorTests
{
    private readonly ListScheduleQueryValidator _validator = new();

    [Fact]
    public void A_valid_window_passes()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from.AddDays(30), null, null, null);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void To_equal_to_From_fails_with_schedule_invalid_interval()
    {
        var moment = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(moment, moment, null, null, null);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "schedule_invalid_interval");
    }

    [Fact]
    public void To_before_From_fails_with_schedule_invalid_interval()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from.AddDays(-1), null, null, null);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "schedule_invalid_interval");
    }

    [Fact]
    public void A_window_exactly_at_the_max_size_passes()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from + ListScheduleQueryValidator.MaxWindow, null, null, null);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_window_larger_than_the_max_size_fails_with_schedule_window_too_large()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from + ListScheduleQueryValidator.MaxWindow + TimeSpan.FromSeconds(1), null, null, null);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "schedule_window_too_large");
    }

    [Theory]
    [InlineData("Reservation")]
    [InlineData("Cleaning")]
    public void A_valid_eventType_passes(string eventType)
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from.AddDays(1), null, null, eventType);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_invalid_eventType_fails_with_schedule_event_type_invalid()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from.AddDays(1), null, null, "Maintenance");

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "schedule_event_type_invalid");
    }

    [Fact]
    public void A_null_eventType_is_not_validated_it_means_both_sources()
    {
        var from = DateTimeOffset.UtcNow;
        var query = new ListScheduleQuery(from, from.AddDays(1), null, null, null);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }
}
