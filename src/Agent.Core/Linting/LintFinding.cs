namespace Agent.Core.Linting;

/// <summary>A single rule violation found by a linter.</summary>
/// <param name="RuleId">Stable rule identifier, for example <c>K8S002</c>.</param>
/// <param name="Rule">Short kebab-case rule name, for example <c>missing-resource-limits</c>.</param>
/// <param name="Severity">How serious the violation is.</param>
/// <param name="Message">Human readable explanation, including the fix.</param>
/// <param name="Line">1-based line in the input where the violation starts, when known.</param>
/// <param name="Target">The object the finding is about, such as <c>Deployment/web container=api</c> or <c>stage 2</c>.</param>
/// <param name="File">
/// The file the line refers to, relative to what was linted, for linters that read several files (a Helm chart,
/// a Terraform module). Null when the input was a single file or text.
/// </param>
public sealed record LintFinding(
    string RuleId,
    string Rule,
    Severity Severity,
    string Message,
    int? Line = null,
    string? Target = null,
    string? File = null);
