using FluentAssertions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Tests.Unit.Domain;

public class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Provision_creates_an_active_tenant()
    {
        var tenant = Tenant.Provision(Guid.NewGuid(), TenantSlug.Create("acme-hospitality"), "Acme Hospitality", Now);

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ActivatedAt.Should().Be(Now);
    }

    [Fact]
    public void Provision_rejects_empty_name()
    {
        var act = () => Tenant.Provision(Guid.NewGuid(), TenantSlug.Create("acme"), "  ", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Suspend_then_Reactivate_round_trips_status()
    {
        var tenant = Tenant.Provision(Guid.NewGuid(), TenantSlug.Create("acme"), "Acme", Now);

        tenant.Suspend();
        tenant.Status.Should().Be(TenantStatus.Suspended);

        tenant.Reactivate();
        tenant.Status.Should().Be(TenantStatus.Active);
    }
}
