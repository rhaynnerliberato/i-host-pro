using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Templates;

public sealed class CreateTemplateCommandHandler : ICommandHandler<CreateTemplateCommand, TemplateResult>
{
    private static readonly Error TemplateKeyAlreadyExistsError = new(
        TemplateErrorCodes.TemplateKeyAlreadyExists, TemplateErrorCodes.TemplateKeyAlreadyExists);

    private readonly ITemplateRepository _repository;
    private readonly TimeProvider _timeProvider;

    public CreateTemplateCommandHandler(ITemplateRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<TemplateResult>> Handle(CreateTemplateCommand command, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetByKeyAsync(command.Key, cancellationToken);
        if (existing is not null)
            return Result.Failure<TemplateResult>(TemplateKeyAlreadyExistsError);

        var now = _timeProvider.GetUtcNow();
        var template = Template.Create(Guid.NewGuid(), command.TenantId, command.Key, command.Content, now);

        _repository.Add(template);

        return Result.Success(ToResult(template));
    }

    internal static TemplateResult ToResult(Template template) =>
        new(template.Id, template.Key, template.Content, template.IsActive, template.CreatedAtUtc, template.UpdatedAtUtc);
}
