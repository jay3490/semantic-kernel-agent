using Agent.Core.Linting.Helm;
using Agent.Core.Linting.Terraform;

namespace Agent.Core.Linting.Reporting;

/// <summary>What kind of input a lint target is.</summary>
public enum LintTargetKind
{
    Kubernetes,
    Dockerfile,
    Helm,
    Terraform,
    GitHubActions,
}

public static class LintTargetKinds
{
    private static readonly Dictionary<LintTargetKind, string> Names = new()
    {
        [LintTargetKind.Kubernetes] = "kubernetes",
        [LintTargetKind.Dockerfile] = "dockerfile",
        [LintTargetKind.Helm] = "helm",
        [LintTargetKind.Terraform] = "terraform",
        [LintTargetKind.GitHubActions] = "github-actions",
    };

    /// <summary>All names, as accepted by <see cref="TryParse"/>.</summary>
    public static IEnumerable<string> All => Names.Values;

    /// <summary>The name used on the command line and in JSON output, such as <c>github-actions</c>.</summary>
    public static string Name(this LintTargetKind kind) => Names[kind];

    /// <summary>Accepts the names above, case-insensitively, plus <c>k8s</c> for Kubernetes.</summary>
    public static bool TryParse(string? name, out LintTargetKind kind)
    {
        if (string.Equals(name, "k8s", StringComparison.OrdinalIgnoreCase))
        {
            kind = LintTargetKind.Kubernetes;
            return true;
        }

        foreach (var (candidate, candidateName) in Names)
        {
            if (string.Equals(name, candidateName, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>A file, or for Helm and Terraform a directory, to lint. <see cref="Path"/> uses <c>/</c> separators.</summary>
public sealed record LintTarget(string Path, LintTargetKind Kind);

/// <summary>The report for one target.</summary>
public sealed record LintResult(string Path, LintTargetKind Kind, LintReport Report);

/// <summary>
/// Finds and lints targets on the local file system, for the command-line <c>lint</c> command. Unlike the agent's
/// tools these paths come from the user, so they are not confined to a workspace.
/// </summary>
/// <remarks>
/// A directory with a <c>Chart.yaml</c> is one Helm chart and a directory with <c>.tf</c> files is one Terraform
/// module; other directories are searched for Dockerfiles, Terraform modules, Helm charts, workflow files and
/// Kubernetes YAML. YAML files are told apart by content: <c>jobs:</c> with <c>on:</c> is a workflow,
/// <c>apiVersion:</c> with <c>kind:</c> is a manifest, anything else is skipped.
/// </remarks>
public sealed class LintRunner(
    KubernetesManifestLinter kubernetes,
    DockerfileLinter dockerfiles,
    HelmChartLinter helm,
    TerraformLinter terraform,
    GitHubActionsLinter workflows)
{
    /// <summary>Files larger than this are reported as errors instead of being read.</summary>
    public const long MaxFileBytes = 1_048_576;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".terraform", "node_modules", "bin", "obj", "vendor", ".venv",
    };

    public LintRunner()
        : this(new KubernetesManifestLinter(), new DockerfileLinter(), new HelmChartLinter(new KubernetesManifestLinter()), new TerraformLinter(), new GitHubActionsLinter())
    {
    }

    /// <summary>Expands <paramref name="paths"/> into targets. Problems (missing paths, unrecognized files) go to <paramref name="errors"/>.</summary>
    /// <param name="kind">Lint every given file as this kind instead of detecting it.</param>
    public IReadOnlyList<LintTarget> Resolve(IEnumerable<string> paths, LintTargetKind? kind, List<string> errors)
    {
        var targets = new List<LintTarget>();
        foreach (var raw in paths)
        {
            var path = Normalize(raw);
            if (Directory.Exists(raw))
            {
                var found = kind is { } forced ? ExpandForced(raw, forced) : Expand(raw);
                if (found.Count == 0)
                {
                    errors.Add($"{path}: no files to lint were found.");
                }

                targets.AddRange(found);
            }
            else if (File.Exists(raw))
            {
                if ((kind ?? DetectFile(raw)) is { } detected)
                {
                    targets.Add(new LintTarget(path, detected));
                }
                else
                {
                    errors.Add($"{path}: cannot tell what kind of file this is; pass --type.");
                }
            }
            else
            {
                errors.Add($"{path}: no such file or directory.");
            }
        }

        return targets.DistinctBy(t => (t.Path, t.Kind)).ToList();
    }

    /// <summary>Lints each target. A target that cannot be read is reported in <paramref name="errors"/> and skipped.</summary>
    public async Task<IReadOnlyList<LintResult>> RunAsync(IEnumerable<LintTarget> targets, List<string> errors, CancellationToken cancellationToken = default)
    {
        var results = new List<LintResult>();
        foreach (var target in targets)
        {
            try
            {
                results.Add(new LintResult(target.Path, target.Kind, await LintAsync(target, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"{target.Path}: {ex.Message}");
            }
        }

        return results;
    }

    private async Task<LintReport> LintAsync(LintTarget target, CancellationToken cancellationToken) => target.Kind switch
    {
        LintTargetKind.Kubernetes => kubernetes.Lint(await ReadAsync(target.Path, cancellationToken).ConfigureAwait(false)),
        LintTargetKind.Dockerfile => dockerfiles.Lint(await ReadAsync(target.Path, cancellationToken).ConfigureAwait(false)),
        LintTargetKind.GitHubActions => workflows.Lint(await ReadAsync(target.Path, cancellationToken).ConfigureAwait(false)),
        LintTargetKind.Helm => helm.Lint(await LoadChartAsync(target.Path, cancellationToken).ConfigureAwait(false)),
        LintTargetKind.Terraform => terraform.Lint(await LoadModuleAsync(target.Path, cancellationToken).ConfigureAwait(false)),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static async Task<HelmChartFiles> LoadChartAsync(string directory, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Normalize(Path.GetRelativePath(directory, f)))
            .Where(f => !f.StartsWith("charts/", StringComparison.Ordinal));
        return await HelmChartFiles.LoadAsync(files, (relative, ct) => ReadAsync(Path.Combine(directory, relative), ct), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TerraformFile>> LoadModuleAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return [new TerraformFile(Path.GetFileName(path), await ReadAsync(path, cancellationToken).ConfigureAwait(false))];
        }

        var module = new List<TerraformFile>();
        foreach (var file in Directory.EnumerateFiles(path, "*.tf").Order(StringComparer.Ordinal))
        {
            module.Add(new TerraformFile(Path.GetFileName(file), await ReadAsync(file, cancellationToken).ConfigureAwait(false)));
        }

        return module;
    }

    private static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
        {
            throw new InvalidDataException($"{Normalize(path)} is {length} bytes; the limit is {MaxFileBytes} bytes.");
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static List<LintTarget> Expand(string directory)
    {
        var targets = new List<LintTarget>();
        if (File.Exists(Path.Combine(directory, HelmChartFiles.ChartFile)))
        {
            targets.Add(new LintTarget(Normalize(directory), LintTargetKind.Helm));
            return targets;
        }

        if (Directory.EnumerateFiles(directory, "*.tf").Any())
        {
            targets.Add(new LintTarget(Normalize(directory), LintTargetKind.Terraform));
        }

        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            if (!file.EndsWith(".tf", StringComparison.OrdinalIgnoreCase) && DetectFile(file) is { } kind)
            {
                targets.Add(new LintTarget(Normalize(file), kind));
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (!SkippedDirectories.Contains(name) && !new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                targets.AddRange(Expand(child));
            }
        }

        return targets;
    }

    private static List<LintTarget> ExpandForced(string directory, LintTargetKind kind)
    {
        if (kind is LintTargetKind.Helm or LintTargetKind.Terraform)
        {
            return [new LintTarget(Normalize(directory), kind)];
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => kind == LintTargetKind.Dockerfile ? IsDockerfile(Path.GetFileName(file)) : IsYaml(file))
            .Order(StringComparer.Ordinal)
            .Select(file => new LintTarget(Normalize(file), kind))
            .ToList();
    }

    /// <summary>Detects a single file's kind from its name and, for YAML, its content.</summary>
    public static LintTargetKind? DetectFile(string path)
    {
        var name = Path.GetFileName(path);
        if (IsDockerfile(name))
        {
            return LintTargetKind.Dockerfile;
        }

        if (name.EndsWith(".tf", StringComparison.OrdinalIgnoreCase))
        {
            return LintTargetKind.Terraform;
        }

        if (!IsYaml(path) || name is HelmChartFiles.ChartFile or HelmChartFiles.ValuesFile)
        {
            return null;
        }

        if (Normalize(path).Contains(".github/workflows/", StringComparison.Ordinal))
        {
            return LintTargetKind.GitHubActions;
        }

        var head = ReadHead(path);
        bool HasTopLevelKey(string key) => head.Any(line => line.StartsWith(key + ":", StringComparison.Ordinal) || line.StartsWith($"\"{key}\":", StringComparison.Ordinal));

        if (HasTopLevelKey("jobs") && (HasTopLevelKey("on") || HasTopLevelKey("true")))
        {
            return LintTargetKind.GitHubActions;
        }

        return HasTopLevelKey("apiVersion") && HasTopLevelKey("kind") ? LintTargetKind.Kubernetes : null;
    }

    private static string[] ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        var lines = new List<string>();
        for (var i = 0; i < 200 && reader.ReadLine() is { } line; i++)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private static bool IsDockerfile(string name) =>
        name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase);

    private static bool IsYaml(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
