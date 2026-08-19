using FluentAssertions;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Application.Templates;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Templates;

public class TemplateCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider FixedTime = new(Now);

    [Fact]
    public async Task CreateTemplateCommandHandler_creates_a_new_template_when_key_is_free()
    {
        var repository = FakeTemplateRepository.WithExisting(null);
        var handler = new CreateTemplateCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new CreateTemplateCommand(TenantId, "RESERVATION_CONFIRMATION", "Olá"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Key.Should().Be("RESERVATION_CONFIRMATION");
        result.Value.IsActive.Should().BeTrue();
        repository.AddedTemplates.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateTemplateCommandHandler_fails_when_key_already_exists()
    {
        var existing = Template.Create(Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "original", Now);
        var repository = FakeTemplateRepository.WithExisting(existing);
        var handler = new CreateTemplateCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new CreateTemplateCommand(TenantId, "RESERVATION_CONFIRMATION", "new"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(TemplateErrorCodes.TemplateKeyAlreadyExists);
        repository.AddedTemplates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTemplateByKeyQueryHandler_returns_not_found_when_key_does_not_exist()
    {
        var repository = FakeTemplateRepository.WithExisting(null);
        var handler = new GetTemplateByKeyQueryHandler(repository);

        var result = await handler.Handle(new GetTemplateByKeyQuery("MISSING"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(TemplateErrorCodes.TemplateNotFound);
    }

    [Fact]
    public async Task GetTemplateByKeyQueryHandler_returns_the_existing_template()
    {
        var existing = Template.Create(Guid.NewGuid(), TenantId, "KEY", "content", Now);
        var repository = FakeTemplateRepository.WithExisting(existing);
        var handler = new GetTemplateByKeyQueryHandler(repository);

        var result = await handler.Handle(new GetTemplateByKeyQuery("KEY"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("content");
    }

    [Fact]
    public async Task UpdateTemplateContentCommandHandler_updates_an_existing_template()
    {
        var existing = Template.Create(Guid.NewGuid(), TenantId, "KEY", "original", Now);
        var repository = FakeTemplateRepository.WithExisting(existing);
        var handler = new UpdateTemplateContentCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new UpdateTemplateContentCommand("KEY", "updated"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("updated");
        repository.UpdatedTemplates.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateTemplateContentCommandHandler_fails_when_key_does_not_exist()
    {
        var repository = FakeTemplateRepository.WithExisting(null);
        var handler = new UpdateTemplateContentCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new UpdateTemplateContentCommand("MISSING", "updated"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(TemplateErrorCodes.TemplateNotFound);
    }

    [Fact]
    public async Task SetTemplateActiveCommandHandler_deactivates_and_reactivates()
    {
        var existing = Template.Create(Guid.NewGuid(), TenantId, "KEY", "content", Now);
        var repository = FakeTemplateRepository.WithExisting(existing);
        var handler = new SetTemplateActiveCommandHandler(repository, FixedTime);

        var deactivated = await handler.Handle(new SetTemplateActiveCommand("KEY", IsActive: false), CancellationToken.None);
        deactivated.IsSuccess.Should().BeTrue();
        deactivated.Value.IsActive.Should().BeFalse();

        var reactivated = await handler.Handle(new SetTemplateActiveCommand("KEY", IsActive: true), CancellationToken.None);
        reactivated.IsSuccess.Should().BeTrue();
        reactivated.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SetTemplateActiveCommandHandler_fails_when_key_does_not_exist()
    {
        var repository = FakeTemplateRepository.WithExisting(null);
        var handler = new SetTemplateActiveCommandHandler(repository, FixedTime);

        var result = await handler.Handle(new SetTemplateActiveCommand("MISSING", IsActive: true), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(TemplateErrorCodes.TemplateNotFound);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
