using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>Mirrors <c>ListPropertiesQueryValidator</c> exactly.</summary>
public sealed class ListMyPropertiesQueryValidator : AbstractValidator<ListMyPropertiesQuery>
{
    public const int MaxPageSize = 100;

    public ListMyPropertiesQueryValidator()
    {
        RuleFor(x => x.OwnerUserId).NotEmpty().WithErrorCode("owner_user_id_required").WithMessage("owner_user_id_required");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .When(x => x.Page.HasValue)
            .WithErrorCode("page_must_be_positive")
            .WithMessage("page_must_be_positive");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, MaxPageSize)
            .When(x => x.PageSize.HasValue)
            .WithErrorCode("page_size_out_of_range")
            .WithMessage("page_size_out_of_range");
    }
}
