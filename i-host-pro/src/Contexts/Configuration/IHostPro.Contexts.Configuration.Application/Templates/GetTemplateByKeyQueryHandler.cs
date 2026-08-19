using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;

namespace IHostPro.Contexts.Configuration.Application.Templates;

public sealed class GetTemplateByKeyQueryHandler : IQueryHandler<GetTemplateByKeyQuery, TemplateResult>
{
    private static readonly Error TemplateNotFoundError = new(TemplateErrorCodes.TemplateNotFound, TemplateErrorCodes.TemplateNotFound);

    private readonly ITemplateRepository _repository;

    public GetTemplateByKeyQueryHandler(ITemplateRepository repository) => _repository = repository;

    public async ValueTask<Result<TemplateResult>> Handle(GetTemplateByKeyQuery query, CancellationToken cancellationToken)
    {
        var template = await _repository.GetByKeyAsync(query.Key, cancellationToken);
        return template is null
            ? Result.Failure<TemplateResult>(TemplateNotFoundError)
            : Result.Success(CreateTemplateCommandHandler.ToResult(template));
    }
}
