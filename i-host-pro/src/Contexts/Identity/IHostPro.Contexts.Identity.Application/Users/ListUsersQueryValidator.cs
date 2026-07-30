using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Only validates a value the caller actually supplied — <c>null</c> is
/// always valid (it means "use the default") and is never checked against
/// the range rules below (Incremento 3, Checkpoint 5).
/// </summary>
public sealed class ListUsersQueryValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersQueryValidator(IUserListingSettingsProvider settings)
    {
        var maxPageSize = settings.MaxPageSize;

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .When(x => x.Page.HasValue)
            .WithErrorCode("page_must_be_positive")
            .WithMessage("page_must_be_positive");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, maxPageSize)
            .When(x => x.PageSize.HasValue)
            .WithErrorCode("page_size_out_of_range")
            .WithMessage("page_size_out_of_range");
    }
}
