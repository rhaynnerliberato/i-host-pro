using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Domain;

public class CleaningTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Cleaning CreatePending(Guid? reservationId = null) =>
        Cleaning.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reservationId, Guid.NewGuid(), Now);

    private static Cleaning CreateAssigned()
    {
        var cleaning = CreatePending();
        cleaning.Assign(Guid.NewGuid(), Now.AddMinutes(1));
        return cleaning;
    }

    private static Cleaning CreateInTransit()
    {
        var cleaning = CreateAssigned();
        cleaning.MarkInTransit(Now.AddMinutes(2));
        return cleaning;
    }

    private static Cleaning CreateStarted()
    {
        var cleaning = CreateAssigned();
        cleaning.Start(Now.AddMinutes(2));
        return cleaning;
    }

    private static Cleaning CreateInInspection()
    {
        var cleaning = CreateStarted();
        cleaning.StartInspection(Now.AddMinutes(3));
        return cleaning;
    }

    private static Cleaning CreateCompleted()
    {
        var cleaning = CreateInInspection();
        cleaning.Complete(Now.AddMinutes(4));
        return cleaning;
    }

    // --- Create ---

    [Fact]
    public void Create_starts_as_Pending()
    {
        var cleaning = CreatePending();

        cleaning.Status.Should().Be(CleaningStatus.Pending);
        cleaning.CreatedAtUtc.Should().Be(Now);
        cleaning.AssignedHousekeeperUserId.Should().BeNull();
    }

    [Fact]
    public void Create_normalizes_the_creation_instant_to_UTC()
    {
        var localNow = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(-3));

        var cleaning = Cleaning.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), localNow);

        cleaning.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        cleaning.CreatedAtUtc.Should().Be(localNow.ToUniversalTime());
    }

    [Fact]
    public void Create_with_no_reservation_reference_leaves_ReservationId_null()
    {
        var cleaning = CreatePending(reservationId: null);

        cleaning.ReservationId.Should().BeNull();
    }

    [Fact]
    public void Create_with_a_reservation_reference_keeps_it()
    {
        var reservationId = Guid.NewGuid();

        var cleaning = CreatePending(reservationId);

        cleaning.ReservationId.Should().Be(reservationId);
    }

    // --- Assign ---

    [Fact]
    public void Assign_from_Pending_transitions_to_Assigned_and_stores_the_housekeeper()
    {
        var cleaning = CreatePending();
        var housekeeperId = Guid.NewGuid();

        cleaning.Assign(housekeeperId, Now.AddMinutes(1));

        cleaning.Status.Should().Be(CleaningStatus.Assigned);
        cleaning.AssignedHousekeeperUserId.Should().Be(housekeeperId);
    }

    [Fact]
    public void Assign_when_not_Pending_throws()
    {
        var cleaning = CreateAssigned();

        var act = () => cleaning.Assign(Guid.NewGuid(), Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    // --- Start ---

    [Fact]
    public void Start_from_Assigned_transitions_to_Started_and_stamps_StartedAtUtc()
    {
        var cleaning = CreateAssigned();

        cleaning.Start(Now.AddMinutes(2));

        cleaning.Status.Should().Be(CleaningStatus.Started);
        cleaning.StartedAtUtc.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void Start_from_Pending_throws()
    {
        var cleaning = CreatePending();

        var act = () => cleaning.Start(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Start_from_InTransit_transitions_to_Started()
    {
        var cleaning = CreateInTransit();

        cleaning.Start(Now.AddMinutes(3));

        cleaning.Status.Should().Be(CleaningStatus.Started);
    }

    // --- MarkInTransit (Fase 6, Incremento 2A) ---

    [Fact]
    public void MarkInTransit_from_Assigned_transitions_to_InTransit()
    {
        var cleaning = CreateAssigned();

        cleaning.MarkInTransit(Now.AddMinutes(2));

        cleaning.Status.Should().Be(CleaningStatus.InTransit);
    }

    [Fact]
    public void MarkInTransit_from_Pending_throws()
    {
        var cleaning = CreatePending();

        var act = () => cleaning.MarkInTransit(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkInTransit_from_Started_throws()
    {
        var cleaning = CreateStarted();

        var act = () => cleaning.MarkInTransit(Now.AddMinutes(3));

        act.Should().Throw<InvalidOperationException>();
    }

    // --- StartInspection ---

    [Fact]
    public void StartInspection_from_Started_transitions_to_InInspection_and_stamps_InspectionStartedAtUtc()
    {
        var cleaning = CreateStarted();

        cleaning.StartInspection(Now.AddMinutes(3));

        cleaning.Status.Should().Be(CleaningStatus.InInspection);
        cleaning.InspectionStartedAtUtc.Should().Be(Now.AddMinutes(3));
    }

    [Fact]
    public void StartInspection_from_Assigned_throws()
    {
        var cleaning = CreateAssigned();

        var act = () => cleaning.StartInspection(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    // --- Complete ---

    [Fact]
    public void Complete_from_InInspection_transitions_to_Completed_and_stamps_CompletedAtUtc()
    {
        var cleaning = CreateInInspection();

        cleaning.Complete(Now.AddMinutes(4));

        cleaning.Status.Should().Be(CleaningStatus.Completed);
        cleaning.CompletedAtUtc.Should().Be(Now.AddMinutes(4));
    }

    [Fact]
    public void Complete_from_Started_throws()
    {
        var cleaning = CreateStarted();

        var act = () => cleaning.Complete(Now.AddMinutes(3));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Completed_is_terminal_no_further_transition_succeeds()
    {
        var cleaning = CreateCompleted();

        var assign = () => cleaning.Assign(Guid.NewGuid(), Now.AddMinutes(5));
        var start = () => cleaning.Start(Now.AddMinutes(5));
        var startInspection = () => cleaning.StartInspection(Now.AddMinutes(5));
        var complete = () => cleaning.Complete(Now.AddMinutes(5));
        var cancel = () => cleaning.Cancel(Now.AddMinutes(5));
        var interrupt = () => cleaning.MarkInterrupted(Now.AddMinutes(5));

        assign.Should().Throw<InvalidOperationException>();
        start.Should().Throw<InvalidOperationException>();
        startInspection.Should().Throw<InvalidOperationException>();
        complete.Should().Throw<InvalidOperationException>();
        cancel.Should().Throw<InvalidOperationException>();
        interrupt.Should().Throw<InvalidOperationException>();
        cleaning.Status.Should().Be(CleaningStatus.Completed);
    }

    // --- Cancel ---

    [Fact]
    public void Cancel_from_Pending_transitions_to_Cancelled_and_stamps_CancelledAtUtc()
    {
        var cleaning = CreatePending();

        cleaning.Cancel(Now.AddMinutes(1));

        cleaning.Status.Should().Be(CleaningStatus.Cancelled);
        cleaning.CancelledAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Cancel_from_Assigned_transitions_to_Cancelled()
    {
        var cleaning = CreateAssigned();

        cleaning.Cancel(Now.AddMinutes(2));

        cleaning.Status.Should().Be(CleaningStatus.Cancelled);
    }

    [Fact]
    public void Cancel_from_Started_throws_no_documented_direct_cancel_path()
    {
        var cleaning = CreateStarted();

        var act = () => cleaning.Cancel(Now.AddMinutes(3));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_from_InInspection_throws()
    {
        var cleaning = CreateInInspection();

        var act = () => cleaning.Cancel(Now.AddMinutes(4));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancelled_is_terminal_no_further_transition_succeeds()
    {
        var cleaning = CreatePending();
        cleaning.Cancel(Now.AddMinutes(1));

        var assign = () => cleaning.Assign(Guid.NewGuid(), Now.AddMinutes(2));
        var cancelAgain = () => cleaning.Cancel(Now.AddMinutes(2));

        assign.Should().Throw<InvalidOperationException>();
        cancelAgain.Should().Throw<InvalidOperationException>();
        cleaning.Status.Should().Be(CleaningStatus.Cancelled);
    }

    // --- Side-states: MarkInterrupted / MarkWaitingMaterials / MarkWaitingHelp ---

    [Fact]
    public void MarkInterrupted_from_Started_transitions_to_Interrupted()
    {
        var cleaning = CreateStarted();

        cleaning.MarkInterrupted(Now.AddMinutes(3));

        cleaning.Status.Should().Be(CleaningStatus.Interrupted);
    }

    [Fact]
    public void MarkInterrupted_from_Assigned_throws()
    {
        var cleaning = CreateAssigned();

        var act = () => cleaning.MarkInterrupted(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkWaitingMaterials_from_Started_transitions_to_WaitingMaterials()
    {
        var cleaning = CreateStarted();

        cleaning.MarkWaitingMaterials(Now.AddMinutes(3));

        cleaning.Status.Should().Be(CleaningStatus.WaitingMaterials);
    }

    [Fact]
    public void MarkWaitingMaterials_from_Pending_throws()
    {
        var cleaning = CreatePending();

        var act = () => cleaning.MarkWaitingMaterials(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkWaitingHelp_from_Started_transitions_to_WaitingHelp()
    {
        var cleaning = CreateStarted();

        cleaning.MarkWaitingHelp(Now.AddMinutes(3));

        cleaning.Status.Should().Be(CleaningStatus.WaitingHelp);
    }

    [Fact]
    public void MarkWaitingHelp_from_InInspection_throws()
    {
        var cleaning = CreateInInspection();

        var act = () => cleaning.MarkWaitingHelp(Now.AddMinutes(4));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Interrupted_has_no_documented_way_out_this_increment()
    {
        var cleaning = CreateStarted();
        cleaning.MarkInterrupted(Now.AddMinutes(3));

        var start = () => cleaning.Start(Now.AddMinutes(4));
        var complete = () => cleaning.Complete(Now.AddMinutes(4));

        start.Should().Throw<InvalidOperationException>();
        complete.Should().Throw<InvalidOperationException>();
        cleaning.Status.Should().Be(CleaningStatus.Interrupted);
    }
}
