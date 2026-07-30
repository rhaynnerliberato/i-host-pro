using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserListingSettingsProvider : IUserListingSettingsProvider
{
    public FakeUserListingSettingsProvider(int defaultPageSize = 20, int maxPageSize = 100)
    {
        DefaultPageSize = defaultPageSize;
        MaxPageSize = maxPageSize;
    }

    public int DefaultPageSize { get; }
    public int MaxPageSize { get; }
}
