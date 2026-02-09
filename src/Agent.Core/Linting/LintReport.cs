namespace Agent.Core.Linting;

/// <summary>Structured result of linting one input. Serialized as-is for the API and for tool results.</summary>
public sealed record LintReport
{
    public LintReport(string linter, IEnumerable<LintFinding> findings)
    {
        Linter = linter;
        Findings = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Line ?? int.MaxValue)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Which linter produced the report: <c>kubernetes</c> or <c>dockerfile</c>.</summary>
    public string Linter { get; }

    /// <summary>Findings ordered by severity (errors first), then by line.</summary>
    public IReadOnlyList<LintFinding> Findings { get; }

    public int Errors => Findings.Count(f => f.Severity == Severity.Error);

    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);

    public int Infos => Findings.Count(f => f.Severity == Severity.Info);

    /// <summary>True when the input has no errors and no warnings. Informational findings do not fail a report.</summary>
    public bool Passed => Errors == 0 && Warnings == 0;
}
