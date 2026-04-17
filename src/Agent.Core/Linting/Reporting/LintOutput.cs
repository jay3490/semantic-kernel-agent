using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Core.Linting.Reporting;

public enum LintOutputFormat
{
    Text,
    Json,
    Sarif,
}

/// <summary>Writes lint results as plain text, JSON, or SARIF 2.1.0 for code-scanning tools.</summary>
public static class LintOutput
{
    public const string ToolName = "devops-agent";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(TextWriter writer, LintOutputFormat format, IReadOnlyList<LintResult> results)
    {
        switch (format)
        {
            case LintOutputFormat.Json:
                writer.WriteLine(ToJson(results));
                break;
            case LintOutputFormat.Sarif:
                writer.WriteLine(ToSarif(results));
                break;
            default:
                WriteText(writer, results);
                break;
        }
    }

    public static void WriteText(TextWriter writer, IReadOnlyList<LintResult> results)
    {
        foreach (var result in results)
        {
            var report = result.Report;
            writer.WriteLine($"{result.Path} ({result.Kind.Name()}): {(report.Passed ? "passed" : "failed")}, " +
                             $"{report.Errors} error(s), {report.Warnings} warning(s), {report.Infos} info");
            foreach (var finding in report.Findings)
            {
                var location = Location(result, finding);
                var target = finding.Target is null ? string.Empty : $" [{finding.Target}]";
                writer.WriteLine($"  {location}: {finding.Severity.ToString().ToLowerInvariant()} {finding.RuleId} {finding.Rule}{target}");
                writer.WriteLine($"    {finding.Message}");
            }
        }

        var summary = Summarize(results);
        writer.WriteLine($"{results.Count} target(s): {summary.Errors} error(s), {summary.Warnings} warning(s), {summary.Infos} info");
    }

    public static string ToJson(IReadOnlyList<LintResult> results)
    {
        var summary = Summarize(results);
        return JsonSerializer.Serialize(new
        {
            results = results.Select(r => new { path = r.Path, type = r.Kind.Name(), report = r.Report }),
            summary = new { targets = results.Count, summary.Errors, summary.Warnings, summary.Infos, passed = summary.Errors == 0 && summary.Warnings == 0 },
        }, Json);
    }

    /// <summary>One SARIF run: every rule that fired is listed once in the driver, and every finding is a result.</summary>
    public static string ToSarif(IReadOnlyList<LintResult> results)
    {
        var rules = results
            .SelectMany(r => r.Report.Findings)
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var ruleIndex = rules.Select((rule, i) => (rule.RuleId, i)).ToDictionary(x => x.RuleId, x => x.i, StringComparer.Ordinal);

        var sarifRules = new JsonArray(rules.Select(rule => (JsonNode)new JsonObject
        {
            ["id"] = rule.RuleId,
            ["name"] = rule.Rule,
            ["shortDescription"] = new JsonObject { ["text"] = Describe(rule.Rule) },
            ["defaultConfiguration"] = new JsonObject { ["level"] = Level(rule.Severity) },
        }).ToArray());

        var sarifResults = new JsonArray();
        foreach (var result in results)
        {
            foreach (var finding in result.Report.Findings)
            {
                var physical = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = ArtifactUri(result, finding) } };
                if (finding.Line is >= 1)
                {
                    physical["region"] = new JsonObject { ["startLine"] = finding.Line.Value };
                }

                var message = finding.Target is null ? finding.Message : $"{finding.Target}: {finding.Message}";
                sarifResults.Add(new JsonObject
                {
                    ["ruleId"] = finding.RuleId,
                    ["ruleIndex"] = ruleIndex[finding.RuleId],
                    ["level"] = Level(finding.Severity),
                    ["message"] = new JsonObject { ["text"] = message },
                    ["locations"] = new JsonArray(new JsonObject { ["physicalLocation"] = physical }),
                });
            }
        }

        var log = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = ToolName,
                        ["version"] = ToolVersion(),
                        ["rules"] = sarifRules,
                    },
                },
                ["results"] = sarifResults,
            }),
        };

        return log.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static (int Errors, int Warnings, int Infos) Summarize(IEnumerable<LintResult> results) =>
        results.Aggregate((0, 0, 0), (sum, r) => (sum.Item1 + r.Report.Errors, sum.Item2 + r.Report.Warnings, sum.Item3 + r.Report.Infos));

    /// <summary>The file a finding is in: the target itself, or for charts and modules the file inside it.</summary>
    public static string ArtifactUri(LintResult result, LintFinding finding) =>
        finding.File is null || result.Kind is not (LintTargetKind.Helm or LintTargetKind.Terraform) || result.Path.EndsWith(".tf", StringComparison.OrdinalIgnoreCase)
            ? result.Path
            : $"{result.Path}/{finding.File}";

    private static string Location(LintResult result, LintFinding finding) =>
        finding.Line is { } line ? $"{ArtifactUri(result, finding)}:{line}" : ArtifactUri(result, finding);

    private static string Level(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        _ => "note",
    };

    /// <summary><c>missing-resource-limits</c> becomes <c>Missing resource limits</c>.</summary>
    private static string Describe(string rule)
    {
        var words = rule.Replace('-', ' ');
        return words.Length == 0 ? rule : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string ToolVersion() =>
        typeof(LintOutput).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(LintOutput).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
