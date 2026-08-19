namespace IHostPro.Contexts.Communication.Application;

/// <summary>Thrown by <see cref="TemplateRenderer"/> when a template references a <c>{{Variable}}</c> not present in the caller-supplied, explicit allow-list — never rendered as a literal broken placeholder, never silently dropped.</summary>
public sealed class UnsupportedTemplateVariableException : Exception
{
    public string VariableName { get; }

    public UnsupportedTemplateVariableException(string variableName)
        : base($"Template references unsupported variable '{{{{{variableName}}}}}'.") =>
        VariableName = variableName;
}
