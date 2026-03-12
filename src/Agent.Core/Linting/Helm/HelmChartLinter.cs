using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Agent.Core.Linting.Helm;

/// <summary>
/// Lints a Helm chart: checks <c>Chart.yaml</c> and <c>values.yaml</c>, renders every template against the chart's
/// default values with <see cref="HelmTemplateRenderer"/>, and runs the Kubernetes workload rules on the result.
/// Findings point at the template line that produced the offending manifest line, and say which value to change
/// when the template takes the setting from <c>.Values</c>.
/// </summary>
public sealed partial class HelmChartLinter(KubernetesManifestLinter manifests)
{
    public const string Name = "helm";

    /// <summary>Kubernetes rules that apply to rendered templates, and the Helm rule each one is reported as.</summary>
    private static readonly Dictionary<string, (string RuleId, string Rule, string? ValuesKey)> RenderedRules = new(StringComparer.Ordinal)
    {
        ["K8S005"] = ("HELM002", "unpinned-image-tag", "image.tag"),
        ["K8S002"] = ("HELM003", "missing-resource-limits", "resources"),
        ["K8S003"] = ("HELM004", "missing-liveness-probe", "livenessProbe"),
        ["K8S004"] = ("HELM005", "missing-readiness-probe", "readinessProbe"),
        ["K8S006"] = ("HELM006", "privileged-container", "securityContext"),
    };

    public LintReport Lint(HelmChartFiles chart)
    {
        var findings = new List<LintFinding>();

        var metadata = ReadChartYaml(chart.ChartYaml, findings);
        var values = ReadValues(chart.ValuesYaml, findings);
        if (metadata is null || values is null)
        {
            return new LintReport(Name, findings);
        }

        var renderer = new HelmTemplateRenderer(values, metadata);
        foreach (var template in chart.Templates)
        {
            LintTemplate(template, renderer, values, findings);
        }

        return new LintReport(Name, findings);
    }

    private void LintTemplate(HelmTemplateFile template, HelmTemplateRenderer renderer, YamlMappingNode values, List<LintFinding> findings)
    {
        RenderedTemplate rendered;
        try
        {
            rendered = renderer.Render(template.Text);
        }
        catch (HelmTemplateException ex)
        {
            findings.Add(new LintFinding("HELM001", "template-not-analyzable", Severity.Info,
                $"The template could not be rendered for analysis: {ex.Message}", ex.Line, File: template.Path));
            return;
        }

        if (string.IsNullOrWhiteSpace(rendered.Text))
        {
            return;
        }

        var templateLines = template.Text.Split('\n');
        foreach (var finding in manifests.Lint(rendered.Text).Findings)
        {
            var line = finding.Line is { } renderedLine ? rendered.SourceLineOf(renderedLine) : (int?)null;

            if (finding.RuleId == "K8S000")
            {
                findings.Add(new LintFinding("HELM001", "template-not-analyzable", Severity.Info,
                    $"The rendered template is not valid YAML, so it was not checked. It may use template features this linter does not render. {finding.Message}",
                    line, File: template.Path));
                continue;
            }

            if (!RenderedRules.TryGetValue(finding.RuleId, out var rule))
            {
                continue;
            }

            var message = finding.Message + ValuesHint(BlockAt(templateLines, line), rule.ValuesKey, values);
            findings.Add(new LintFinding(rule.RuleId, rule.Rule, finding.Severity, message, line, finding.Target, template.Path));
        }
    }

    /// <summary>
    /// The template text of the YAML block starting at <paramref name="line"/>: that line and the more deeply indented
    /// lines under it, such as one container of a pod spec.
    /// </summary>
    private static string BlockAt(string[] lines, int? line)
    {
        if (line is not { } start || start < 1 || start > lines.Length)
        {
            return string.Empty;
        }

        var indent = Indentation(lines[start - 1]);
        var end = start;
        while (end < lines.Length && (string.IsNullOrWhiteSpace(lines[end]) || Indentation(lines[end]) > indent))
        {
            end++;
        }

        return string.Join('\n', lines[(start - 1)..end]);

        static int Indentation(string text) => text.Length - text.TrimStart(' ').Length;
    }

    /// <summary>
    /// When the template block reads the setting from values (for example <c>toYaml .Values.resources</c>), names the
    /// value and its line in values.yaml, because that is where the fix goes.
    /// </summary>
    private static string ValuesHint(string block, string? key, YamlMappingNode values)
    {
        if (key is null)
        {
            return string.Empty;
        }

        var reference = ValuesReference().Matches(block)
            .Select(m => m.Groups["path"].Value)
            .FirstOrDefault(path => path == key || path.EndsWith("." + key, StringComparison.Ordinal) || path.StartsWith(key + ".", StringComparison.Ordinal));
        if (reference is null)
        {
            return string.Empty;
        }

        var node = FindValue(values, reference.Split('.'));
        return node is null
            ? $" The template reads it from .Values.{reference}, which values.yaml does not set."
            : $" The template reads it from .Values.{reference} (values.yaml line {node.Start.Line}); fix it there.";
    }

    private static YamlNode? FindValue(YamlMappingNode values, IEnumerable<string> path)
    {
        YamlNode? current = values;
        foreach (var name in path)
        {
            current = current is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(name), out var child) ? child : null;
        }

        return current;
    }

    private static HelmChartMetadata? ReadChartYaml(string? text, List<LintFinding> findings)
    {
        if (text is null)
        {
            findings.Add(new LintFinding("HELM000", "invalid-chart", Severity.Error,
                "Chart.yaml is missing, so this directory is not a Helm chart.", File: HelmChartFiles.ChartFile));
            return null;
        }

        if (Parse(text, HelmChartFiles.ChartFile, findings) is not { } root)
        {
            return null;
        }

        var missing = new[] { "apiVersion", "name", "version" }.Where(key => Scalar(root, key) is null).ToArray();
        if (missing.Length > 0)
        {
            findings.Add(new LintFinding("HELM000", "invalid-chart", Severity.Error,
                $"Chart.yaml does not set {string.Join(", ", missing)}; Helm refuses to package or install the chart without them.",
                1, File: HelmChartFiles.ChartFile));
            return null;
        }

        return new HelmChartMetadata(Scalar(root, "name")!, Scalar(root, "version")!, Scalar(root, "appVersion"));
    }

    private static YamlMappingNode? ReadValues(string? text, List<LintFinding> findings) =>
        string.IsNullOrWhiteSpace(text) ? new YamlMappingNode() : Parse(text, HelmChartFiles.ValuesFile, findings);

    private static YamlMappingNode? Parse(string text, string file, List<LintFinding> findings)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            findings.Add(new LintFinding("HELM000", "invalid-chart", Severity.Error,
                $"{file} is not valid YAML: {ex.Message}", (int)ex.Start.Line, File: file));
            return null;
        }

        if (stream.Documents.Count == 0)
        {
            return new YamlMappingNode();
        }

        if (stream.Documents[0].RootNode is YamlMappingNode mapping)
        {
            return mapping;
        }

        findings.Add(new LintFinding("HELM000", "invalid-chart", Severity.Error, $"{file} must be a YAML mapping.", 1, File: file));
        return null;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    [GeneratedRegex(@"\.Values\.(?<path>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")]
    private static partial Regex ValuesReference();
}
