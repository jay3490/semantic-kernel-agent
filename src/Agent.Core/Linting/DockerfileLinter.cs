using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core.Linting;

/// <summary>
/// Lints a Dockerfile for unpinned base images, a final stage that runs as root, ADD used for local files,
/// a missing HEALTHCHECK and apt package lists left in the image.
/// </summary>
public sealed partial class DockerfileLinter
{
    public const string Name = "dockerfile";

    private static readonly string[] ArchiveExtensions =
        [".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz", ".tar.zst"];

    public LintReport Lint(string dockerfile)
    {
        var instructions = DockerfileParser.Parse(dockerfile);
        var findings = new List<LintFinding>();
        var stages = SplitStages(instructions, out var globalArgs);

        if (stages.Count == 0)
        {
            findings.Add(new LintFinding("DF000", "no-from", Severity.Error,
                "No FROM instruction found; this is not a buildable Dockerfile.", 1));
            return new LintReport(Name, findings);
        }

        var stageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in stages)
        {
            CheckBaseImage(stage, globalArgs, stageNames, findings);
            if (stage.Alias is not null)
            {
                stageNames.Add(stage.Alias);
            }

            foreach (var instruction in stage.Instructions)
            {
                switch (instruction.Keyword)
                {
                    case "ADD":
                        CheckAdd(instruction, stage, findings);
                        break;
                    case "RUN":
                        CheckAptCache(instruction, stage, findings);
                        break;
                }
            }
        }

        var final = stages[^1];
        CheckUser(final, findings);
        CheckHealthcheck(final, findings);

        return new LintReport(Name, findings);
    }

    private static void CheckBaseImage(Stage stage, Dictionary<string, string> globalArgs, HashSet<string> earlierStages, List<LintFinding> findings)
    {
        var image = ArgReference().Replace(stage.Image, m =>
        {
            var name = m.Groups["braced"].Success ? m.Groups["braced"].Value : m.Groups["plain"].Value;
            return globalArgs.TryGetValue(name, out var value) ? value : m.Value;
        });

        // Unresolvable build args, scratch and references to earlier stages cannot be judged here.
        if (image.Length == 0 || image.Contains('$') || image.Equals("scratch", StringComparison.OrdinalIgnoreCase) || earlierStages.Contains(image))
        {
            return;
        }

        var reference = ImageReference.Parse(image);
        if (!reference.IsPinned)
        {
            findings.Add(new LintFinding("DF001", "unpinned-base-image", Severity.Warning,
                $"Base image {reference.DescribeUnpinned()}. Pin a version tag or digest so builds are reproducible.",
                stage.From.Line, stage.Description));
        }
    }

    private static void CheckUser(Stage final, List<LintFinding> findings)
    {
        var user = final.Instructions.LastOrDefault(i => i.Keyword == "USER");
        if (user is null)
        {
            findings.Add(new LintFinding("DF002", "runs-as-root", Severity.Warning,
                "The final stage has no USER instruction, so the container runs as root. Create an unprivileged user and switch to it.",
                final.From.Line, final.Description));
            return;
        }

        var name = user.Arguments.Split(':', 2)[0].Trim();
        if (name is "root" or "0")
        {
            findings.Add(new LintFinding("DF002", "runs-as-root", Severity.Warning,
                $"The last USER in the final stage is '{user.Arguments}', so the container runs as root. Switch to an unprivileged user.",
                user.Line, final.Description));
        }
    }

    private static void CheckAdd(DockerfileInstruction add, Stage stage, List<LintFinding> findings)
    {
        var operands = Operands(add.Arguments);
        if (operands.Count < 2)
        {
            return;
        }

        var sources = operands.Take(operands.Count - 1);
        if (sources.All(s => IsRemote(s) || IsArchive(s)))
        {
            return;
        }

        findings.Add(new LintFinding("DF003", "add-instead-of-copy", Severity.Warning,
            "ADD is used to copy local files. Use COPY, which does exactly that; keep ADD for URLs and archives that should be extracted.",
            add.Line, stage.Description));
    }

    private static void CheckAptCache(DockerfileInstruction run, Stage stage, List<LintFinding> findings)
    {
        if (!AptInstall().IsMatch(run.Arguments) || AptListsRemoved().IsMatch(run.Arguments) || AptCacheMount().IsMatch(run.Arguments))
        {
            return;
        }

        findings.Add(new LintFinding("DF005", "apt-cache-not-cleaned", Severity.Warning,
            "apt-get install runs without removing /var/lib/apt/lists in the same RUN, so the package index stays in the layer. End the RUN with '&& rm -rf /var/lib/apt/lists/*'.",
            run.Line, stage.Description));
    }

    private static void CheckHealthcheck(Stage final, List<LintFinding> findings)
    {
        // HEALTHCHECK NONE is an explicit decision, so any HEALTHCHECK instruction satisfies the rule.
        if (final.Instructions.Any(i => i.Keyword == "HEALTHCHECK"))
        {
            return;
        }

        findings.Add(new LintFinding("DF004", "missing-healthcheck", Severity.Info,
            "The final stage has no HEALTHCHECK, so Docker cannot tell a running container from a healthy one.",
            final.From.Line, final.Description));
    }

    private static List<Stage> SplitStages(IReadOnlyList<DockerfileInstruction> instructions, out Dictionary<string, string> globalArgs)
    {
        globalArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        var stages = new List<Stage>();

        foreach (var instruction in instructions)
        {
            if (instruction.Keyword == "FROM")
            {
                stages.Add(Stage.Create(instruction, stages.Count + 1));
            }
            else if (stages.Count > 0)
            {
                stages[^1].Instructions.Add(instruction);
            }
            else if (instruction.Keyword == "ARG")
            {
                var parts = instruction.Arguments.Split('=', 2);
                if (parts.Length == 2)
                {
                    globalArgs[parts[0].Trim()] = parts[1].Trim().Trim('"', '\'');
                }
            }
        }

        return stages;
    }

    /// <summary>Source and destination operands of ADD/COPY, in either shell or JSON (exec) form, without --flags.</summary>
    private static List<string> Operands(string arguments)
    {
        var tokens = arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(t => t.StartsWith("--", StringComparison.Ordinal))
            .ToList();

        var rest = string.Join(' ', tokens);
        if (rest.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(rest) ?? [];
            }
            catch (JsonException)
            {
                return tokens;
            }
        }

        return tokens;
    }

    private static bool IsRemote(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchive(string source) =>
        ArchiveExtensions.Any(ext => source.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\$(?:\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|(?<plain>[A-Za-z_][A-Za-z0-9_]*))")]
    private static partial Regex ArgReference();

    [GeneratedRegex(@"\bapt(?:-get)?\s+(?:-\S+\s+)*install\b")]
    private static partial Regex AptInstall();

    [GeneratedRegex(@"\brm\s+(?:-\S+\s+)*\S*/var/lib/apt/lists")]
    private static partial Regex AptListsRemoved();

    [GeneratedRegex(@"--mount=\S*type=cache\S*target=/var/lib/apt")]
    private static partial Regex AptCacheMount();

    private sealed record Stage(DockerfileInstruction From, string Image, string? Alias, int Number)
    {
        public List<DockerfileInstruction> Instructions { get; } = [];

        public string Description => Alias is null ? $"stage {Number}" : $"stage {Number} ({Alias})";

        public static Stage Create(DockerfileInstruction from, int number)
        {
            var tokens = from.Arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.StartsWith("--", StringComparison.Ordinal))
                .ToArray();

            var image = tokens.Length > 0 ? tokens[0] : string.Empty;
            var alias = tokens.Length >= 3 && tokens[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? tokens[2] : null;
            return new Stage(from, image, alias, number);
        }
    }
}
