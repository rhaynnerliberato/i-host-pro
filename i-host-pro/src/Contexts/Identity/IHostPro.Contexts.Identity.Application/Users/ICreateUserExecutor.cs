using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Runs the CreateUser transactional operation (Incremento 3, Checkpoint 5)
/// — wraps the shared <c>IIdentityTransactionExecutor</c>, additionally
/// translating a caught PostgreSQL unique-constraint violation on the
/// email-uniqueness index into <see cref="IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.EmailAlreadyInUse"/>.
/// The persisted index is the authoritative concurrency guarantee (Section
/// 4: "não implemente uma verificação prévia como única proteção") — a
/// pre-check the handler performs first only improves the common case's
/// error message, it is never relied on alone. No bounded retry: unlike
/// Logout/RefreshToken/RevokeOwnSession, CreateUser only ever INSERTs new
/// rows, so there is no existing-row optimistic-concurrency race to retry
/// (Section 10: "não introduzir retry genérico").
///
/// Declared here, in Application, so <c>CreateUserTenantAwareBehavior</c> and
/// <c>CreateUserCommandHandler</c>'s only caller depend on this abstraction —
/// never on the concrete Infrastructure class.
/// </summary>
public interface ICreateUserExecutor
{
    Task<Result<UserResult>> ExecuteAsync(Func<Task<Result<UserResult>>> operation, CancellationToken cancellationToken);
}
