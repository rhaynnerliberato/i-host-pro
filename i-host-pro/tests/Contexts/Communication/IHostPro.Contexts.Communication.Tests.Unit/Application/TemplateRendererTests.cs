using FluentAssertions;
using IHostPro.Contexts.Communication.Application;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

public class TemplateRendererTests
{
    [Fact]
    public void Render_interpolates_a_single_supported_variable()
    {
        var result = TemplateRenderer.Render("Olá, check-in em {{CheckInDate}}!", new Dictionary<string, string>
        {
            ["CheckInDate"] = "2026-08-20",
        });

        result.Should().Be("Olá, check-in em 2026-08-20!");
    }

    [Fact]
    public void Render_interpolates_multiple_occurrences_of_the_same_variable()
    {
        var result = TemplateRenderer.Render("{{CheckInDate}} até {{CheckInDate}}", new Dictionary<string, string>
        {
            ["CheckInDate"] = "2026-08-20",
        });

        result.Should().Be("2026-08-20 até 2026-08-20");
    }

    [Fact]
    public void Render_leaves_content_without_placeholders_unchanged()
    {
        var result = TemplateRenderer.Render("Texto fixo, sem variáveis.", new Dictionary<string, string>());

        result.Should().Be("Texto fixo, sem variáveis.");
    }

    [Fact]
    public void Render_throws_UnsupportedTemplateVariableException_for_a_token_not_in_the_allow_list()
    {
        var act = () => TemplateRenderer.Render("Olá {{GuestName}}", new Dictionary<string, string>
        {
            ["CheckInDate"] = "2026-08-20",
        });

        act.Should().Throw<UnsupportedTemplateVariableException>()
            .Which.VariableName.Should().Be("GuestName");
    }

    [Fact]
    public void Render_never_interpolates_a_variable_not_wrapped_in_double_braces()
    {
        var result = TemplateRenderer.Render("{CheckInDate} is not a placeholder", new Dictionary<string, string>
        {
            ["CheckInDate"] = "2026-08-20",
        });

        result.Should().Be("{CheckInDate} is not a placeholder");
    }

    [Fact]
    public void Render_is_case_sensitive()
    {
        var act = () => TemplateRenderer.Render("{{checkindate}}", new Dictionary<string, string>
        {
            ["CheckInDate"] = "2026-08-20",
        });

        act.Should().Throw<UnsupportedTemplateVariableException>()
            .Which.VariableName.Should().Be("checkindate");
    }
}
