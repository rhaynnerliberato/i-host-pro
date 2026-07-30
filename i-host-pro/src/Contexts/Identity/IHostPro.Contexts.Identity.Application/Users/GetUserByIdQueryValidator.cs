using FluentValidation;

namespace IHostPro.Contexts.Identity.Application.Users;

public sealed class GetUserByIdQueryValidator : AbstractValidator<GetUserByIdQuery>
{
    public GetUserByIdQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode("user_id_required").WithMessage("user_id_required");
    }
}
