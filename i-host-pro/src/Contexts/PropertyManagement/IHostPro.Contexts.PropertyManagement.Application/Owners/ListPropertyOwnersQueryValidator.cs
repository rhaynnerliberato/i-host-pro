using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>Mirrors <c>ListPropertiesQueryValidator</c> exactly.</summary>
public sealed class ListPropertyOwnersQueryValidator : AbstractValidator<ListPropertyOwnersQuery>
{
    public const int MaxPageSize = 100;

    public ListPropertyOwnersQueryValidator()
    {
        RuleFor(x => x.PropertyId).NotEmpty().WithErrorCode("property_id_required").WithMessage("property_id_required");

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
