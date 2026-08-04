using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

public class ListPropertyOwnersQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();

    private static readonly Address SomeAddress = Address.Create(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static Property CreateProperty() =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create("STUDIO-1"), "Studio 1", 2, null, SomeAddress, Now);

    [Fact]
    public async Task A_nonexistent_property_fails_with_PropertyNotFound()
    {
        var propertyRepository = FakePropertyRepository.WithProperty(null);
        var ownerReader = FakePropertyOwnerReader.WithListResult(new PagedResult<PropertyOwnerResult>(1, 20, 0, []));
        var handler = new ListPropertyOwnersQueryHandler(propertyRepository, ownerReader);

        var result = await handler.Handle(new ListPropertyOwnersQuery(Guid.NewGuid(), null, null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
        ownerReader.LastRequestedPropertyId.Should().BeNull();
    }

    [Fact]
    public async Task An_existing_property_returns_its_paged_owners()
    {
        var property = CreateProperty();
        var expected = new PagedResult<PropertyOwnerResult>(
            1, 20, 1, [new PropertyOwnerResult(property.Id, Guid.NewGuid(), Now)]);
        var propertyRepository = FakePropertyRepository.WithProperty(property);
        var ownerReader = FakePropertyOwnerReader.WithListResult(expected);
        var handler = new ListPropertyOwnersQueryHandler(propertyRepository, ownerReader);

        var result = await handler.Handle(new ListPropertyOwnersQuery(property.Id, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expected);
        ownerReader.LastRequestedPropertyId.Should().Be(property.Id);
    }

    [Fact]
    public async Task Page_and_pageSize_default_when_not_provided()
    {
        var property = CreateProperty();
        var propertyRepository = FakePropertyRepository.WithProperty(property);
        var ownerReader = FakePropertyOwnerReader.WithListResult(new PagedResult<PropertyOwnerResult>(1, 20, 0, []));
        var handler = new ListPropertyOwnersQueryHandler(propertyRepository, ownerReader);

        await handler.Handle(new ListPropertyOwnersQuery(property.Id, null, null), CancellationToken.None);

        ownerReader.LastRequestedPage.Should().Be(1);
        ownerReader.LastRequestedPageSize.Should().Be(ListPropertyOwnersQueryHandler.DefaultPageSize);
    }

    [Fact]
    public async Task Explicit_page_and_pageSize_are_forwarded_unchanged()
    {
        var property = CreateProperty();
        var propertyRepository = FakePropertyRepository.WithProperty(property);
        var ownerReader = FakePropertyOwnerReader.WithListResult(new PagedResult<PropertyOwnerResult>(3, 50, 0, []));
        var handler = new ListPropertyOwnersQueryHandler(propertyRepository, ownerReader);

        await handler.Handle(new ListPropertyOwnersQuery(property.Id, 3, 50), CancellationToken.None);

        ownerReader.LastRequestedPage.Should().Be(3);
        ownerReader.LastRequestedPageSize.Should().Be(50);
    }
}
