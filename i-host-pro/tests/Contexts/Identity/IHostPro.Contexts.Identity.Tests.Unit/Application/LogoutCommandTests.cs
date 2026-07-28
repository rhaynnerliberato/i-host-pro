using System.Reflection;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class LogoutCommandTests
{
    private static LogoutCommand Create() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Does_not_implement_IBootstrapRequest()
    {
        // Asserted via reflection, not a compile-time `is` check: the
        // compiler already proves LogoutCommand can never be
        // IBootstrapRequest (that is exactly the guarantee being tested),
        // which makes an `is` check here a build warning (CS0184) rather
        // than a meaningful runtime assertion.
        typeof(LogoutCommand).GetInterfaces().Should().NotContain(typeof(IBootstrapRequest));
    }

    [Fact]
    public void Implements_ICommand()
    {
        (Create() is ICommand).Should().BeTrue();
    }

    [Fact]
    public void Every_constructor_parameter_is_a_Guid_never_a_string_that_could_carry_a_token_or_free_form_id()
    {
        // Structural guarantee that Logout cannot be constructed from
        // arbitrary public-body-supplied identifiers or a refresh token
        // (Incremento 2 plan, Etapa 8): every primary-constructor parameter
        // must be a Guid (tenant/user/session, sourced exclusively from
        // authenticated claims by the future controller), and there must be
        // no string parameter of any kind on the type at all.
        var constructorParameters = typeof(LogoutCommand)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single()
            .GetParameters();

        constructorParameters.Should().OnlyContain(p => p.ParameterType == typeof(Guid));

        typeof(LogoutCommand).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(p => p.PropertyType == typeof(string));
    }

    [Fact]
    public void Has_no_property_named_or_shaped_like_a_refresh_token()
    {
        var propertyNames = typeof(LogoutCommand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        propertyNames.Should().NotContain(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }
}
