using System.Text.RegularExpressions;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Explicit, non-generic <c>{{Variable}}</c> interpolation (CP1 mandate
/// §19) — never reflection over arbitrary objects, never an expression
/// evaluator, never a scripting/templating library (Handlebars/Liquid/etc.).
/// Every token in <paramref name="content"/> must appear, case-sensitively,
/// in the caller-supplied <c>variables</c> allow-list — an unknown token
/// throws <see cref="UnsupportedTemplateVariableException"/> rather than
/// being rendered as a literal broken placeholder or silently dropped (CP1
/// mandate §17/§19).
/// </summary>
public static partial class TemplateRenderer
{
    public static string Render(string content, IReadOnlyDictionary<string, string> variables)
    {
        return TokenPattern().Replace(content, match =>
        {
            var name = match.Groups[1].Value;
            return variables.TryGetValue(name, out var value)
                ? value
                : throw new UnsupportedTemplateVariableException(name);
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex TokenPattern();
}
