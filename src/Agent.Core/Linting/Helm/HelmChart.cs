namespace Agent.Core.Linting.Helm;

/// <summary>The fields of <c>Chart.yaml</c> that templates commonly read.</summary>
public sealed record HelmChartMetadata(string Name, string Version, string? AppVersion);

/// <summary>A template file of a chart. <see cref="Path"/> is relative to the chart directory, such as <c>templates/deployment.yaml</c>.</summary>
public sealed record HelmTemplateFile(string Path, string Text);

/// <summary>The files of a chart that the linter reads. Null text means the file does not exist.</summary>
public sealed record HelmChartFiles(string? ChartYaml, string? ValuesYaml, IReadOnlyList<HelmTemplateFile> Templates)
{
    public const string ChartFile = "Chart.yaml";
    public const string ValuesFile = "values.yaml";

    /// <summary>
    /// Builds the chart from a listing of its files (paths relative to the chart directory, <c>/</c> separated).
    /// Templates are the <c>.yaml</c>, <c>.yml</c> and <c>.tpl</c> files under <c>templates/</c>, except partials
    /// whose name starts with <c>_</c>; subcharts under <c>charts/</c> are not read.
    /// </summary>
    public static async Task<HelmChartFiles> LoadAsync(
        IEnumerable<string> chartRelativePaths,
        Func<string, CancellationToken, Task<string>> readFile,
        CancellationToken cancellationToken = default)
    {
        var paths = chartRelativePaths.ToHashSet(StringComparer.Ordinal);

        async Task<string?> ReadIfPresent(string path) =>
            paths.Contains(path) ? await readFile(path, cancellationToken).ConfigureAwait(false) : null;

        var templates = new List<HelmTemplateFile>();
        foreach (var path in paths.Where(IsTemplate).Order(StringComparer.Ordinal))
        {
            templates.Add(new HelmTemplateFile(path, await readFile(path, cancellationToken).ConfigureAwait(false)));
        }

        return new HelmChartFiles(await ReadIfPresent(ChartFile).ConfigureAwait(false), await ReadIfPresent(ValuesFile).ConfigureAwait(false), templates);
    }

    private static bool IsTemplate(string path) =>
        path.StartsWith("templates/", StringComparison.Ordinal) &&
        !System.IO.Path.GetFileName(path).StartsWith('_') &&
        (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".tpl", StringComparison.OrdinalIgnoreCase));
}
