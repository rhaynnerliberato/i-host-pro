using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;

namespace IHostPro.Contexts.Configuration.Application.Templates;

public sealed class UpdateTemplateContentCommandHandler : ICommandHandler<UpdateTemplateContentCommand, TemplateResult>
{
    private static readonly Error TemplateNotFoundError = new(TemplateErrorCodes.TemplateNotFound, TemplateErrorCodes.TemplateNotFound);

    private readonly ITemplateRepository _repository;
    private readonly TimeProvider _timeProvider;

    public UpdateTemplateContentCommandHandler(ITemplateRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<TemplateResult>> Handle(UpdateTemplateContentCommand command, CancellationToken cancellationToken)
    {
        var template = await _repository.GetByKeyAsync(command.Key, cancellationToken);
        if (template is null)
            return Result.Failure<TemplateResult>(TemplateNotFoundError);

        template.UpdateContent(command.Content, _timeProvider.GetUtcNow());
        _repository.Update(template);

        return Result.Success(CreateTemplateCommandHandler.ToResult(template));
    }
}
