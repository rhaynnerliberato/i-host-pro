using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="GenericResultFailureFactory"/> lives in BuildingBlocks.Application
/// (not Identity) and carries zero Identity-specific vocabulary — these tests
/// exercise it directly, independent of any Identity type, to demonstrate
/// that. It is covered here (in the only unit test project that currently
/// exists) rather than in a dedicated BuildingBlocks test project, which
/// would be disproportionate for one small, already fully-covered utility.
/// </summary>
public class GenericResultFailureFactoryTests
{
    private static readonly Error SampleError = new("Sample.Code", "Sample message");

    [Fact]
    public void Create_builds_a_failed_plain_Result_when_TResponse_is_Result()
    {
        var result = GenericResultFailureFactory.Create<Result>(SampleError);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Create_builds_a_failed_Result_of_T_when_TResponse_is_Result_of_T()
    {
        var result = GenericResultFailureFactory.Create<Result<Guid>>(SampleError);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Create_builds_a_failed_Result_of_a_reference_type_when_TResponse_is_Result_of_T()
    {
        var result = GenericResultFailureFactory.Create<Result<string>>(SampleError);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Create_throws_for_a_response_type_that_is_not_a_Result()
    {
        var act = () => GenericResultFailureFactory.Create<string>(SampleError);

        act.Should().Throw<InvalidOperationException>();
    }
}
