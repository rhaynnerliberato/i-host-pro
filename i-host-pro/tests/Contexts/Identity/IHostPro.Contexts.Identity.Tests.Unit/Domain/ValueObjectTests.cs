using FluentAssertions;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Tests.Unit.Domain;

public class EmailTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last@sub.example.com")]
    public void Create_accepts_valid_formats(string value)
    {
        var email = Email.Create(value);
        email.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public void Create_rejects_invalid_formats(string value)
    {
        var act = () => Email.Create(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NormalizedValue_is_lowercase_and_trimmed()
    {
        var email = Email.Create("  User@Example.COM  ");
        email.NormalizedValue.Should().Be("user@example.com");
    }

    [Fact]
    public void Equality_is_based_on_NormalizedValue()
    {
        var a = Email.Create("User@Example.com");
        var b = Email.Create("user@example.com");

        a.Should().Be(b);
    }
}

public class TenantSlugTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-hospitality")]
    [InlineData("abc")]
    public void Create_accepts_valid_slugs(string value)
    {
        var slug = TenantSlug.Create(value);
        slug.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("ab")] // too short
    [InlineData("has spaces")]
    [InlineData("has_underscore")]
    [InlineData("")]
    public void Create_rejects_invalid_slugs(string value)
    {
        var act = () => TenantSlug.Create(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_normalizes_case()
    {
        var slug = TenantSlug.Create("ACME-Hospitality");
        slug.Value.Should().Be("acme-hospitality");
    }
}

public class PasswordHashTests
{
    [Fact]
    public void FromEncoded_rejects_empty_value()
    {
        var act = () => PasswordHash.FromEncoded("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromEncoded_does_not_interpret_the_value()
    {
        // The Domain treats the hash as fully opaque — any non-empty string is
        // accepted; parsing/validating the PHC format belongs to Infrastructure.
        var hash = PasswordHash.FromEncoded("not-a-real-phc-string");
        hash.Value.Should().Be("not-a-real-phc-string");
    }
}
