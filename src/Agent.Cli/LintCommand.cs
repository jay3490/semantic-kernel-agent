using System.Text;
using Agent.Core.Linting.Reporting;

namespace Agent.Cli;

/// <summary>
/// <c>devops-agent lint</c>: lints files and directories without a model, for CI pipelines. Exit codes: 0 when no
/// finding reaches the <c>--fail-on</c> severity, 1 when one does, 2 for usage errors and unreadable or
/// unrecognized paths.
/// </summary>
internal static class LintCommand
{
    public const int Passed = 0;
    public const int FindingsFound = 1;
    public const int UsageError = 2;

    public const string Usage = """
        Usage: devops-agent lint [options] <path>...

        Lints Kubernetes manifests, Dockerfiles, Helm charts, Terraform modules and GitHub Actions workflows.
        Directories are searched; the kind of each file is detected from its name and content.

        Options:
          -f, --format <text|json|sarif>   Output format (default: text).
          -o, --output <file>              Write the report to a file instead of standard output.
              --fail-on <error|warning|never>
                                           Lowest severity that makes the exit code 1 (default: error).
          -t, --type <kind>                Lint every given path as kubernetes, dockerfile, helm, terraform
                                           or github-actions instead of detecting it.
          -h, --help                       Show this help.

        Exit codes: 0 passed, 1 findings at or above --fail-on, 2 usage error or a path that could not be linted.
        """;

    private enum FailOn
    {
        Error,
        Warning,
        Never,
    }

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var options, out var problem))
        {
            if (problem is null)
            {
                await stdout.WriteLineAsync(Usage);
                return Passed;
            }

            await stderr.WriteLineAsync($"devops-agent lint: {problem}");
            await stderr.WriteLineAsync("Run 'devops-agent lint --help' for usage.");
            return UsageError;
        }

        var runner = new LintRunner();
        var errors = new List<string>();
        var targets = runner.Resolve(options.Paths, options.Kind, errors);
        var results = await runner.RunAsync(targets, errors, cancellationToken);

        if (options.OutputFile is null)
        {
            LintOutput.Write(stdout, options.Format, results);
        }
        else
        {
            await using var file = new StreamWriter(options.OutputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LintOutput.Write(file, options.Format, results);
        }

        foreach (var error in errors)
        {
            await stderr.WriteLineAsync($"devops-agent lint: {error}");
        }

        if (errors.Count > 0)
        {
            return UsageError;
        }

        var (errorCount, warningCount, _) = LintOutput.Summarize(results);
        var failed = options.FailOn switch
        {
            FailOn.Error => errorCount > 0,
            FailOn.Warning => errorCount + warningCount > 0,
            _ => false,
        };

        return failed ? FindingsFound : Passed;
    }

    private sealed record Options(List<string> Paths, LintOutputFormat Format, string? OutputFile, FailOn FailOn, LintTargetKind? Kind);

    /// <returns>False with a null <paramref name="problem"/> when help was asked for.</returns>
    private static bool TryParse(string[] args, out Options options, out string? problem)
    {
        options = new Options([], LintOutputFormat.Text, null, FailOn.Error, null);
        problem = null;
        var onlyPaths = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (onlyPaths || !arg.StartsWith('-') || arg == "-")
            {
                options.Paths.Add(arg);
                continue;
            }

            var (name, inlineValue) = arg.IndexOf('=') is > 0 and var eq ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
            string? Value()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                return i + 1 < args.Length ? args[++i] : null;
            }

            switch (name)
            {
                case "-h" or "--help":
                    return false;
                case "--":
                    onlyPaths = true;
                    break;
                case "-f" or "--format":
                    var format = Value();
                    if (!Enum.TryParse<LintOutputFormat>(format, ignoreCase: true, out var parsedFormat) || int.TryParse(format, out _))
                    {
                        problem = $"unknown format '{format}'; use text, json or sarif.";
                        return false;
                    }

                    options = options with { Format = parsedFormat };
                    break;
                case "-o" or "--output":
                    var output = Value();
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        problem = "--output needs a file name.";
                        return false;
                    }

                    options = options with { OutputFile = output };
                    break;
                case "--fail-on":
                    var failOn = Value();
                    if (!Enum.TryParse<FailOn>(failOn, ignoreCase: true, out var parsedFailOn) || int.TryParse(failOn, out _))
                    {
                        problem = $"unknown --fail-on value '{failOn}'; use error, warning or never.";
                        return false;
                    }

                    options = options with { FailOn = parsedFailOn };
                    break;
                case "-t" or "--type":
                    var type = Value();
                    if (!LintTargetKinds.TryParse(type, out var kind))
                    {
                        problem = $"unknown type '{type}'; use {string.Join(", ", LintTargetKinds.All)}.";
                        return false;
                    }

                    options = options with { Kind = kind };
                    break;
                default:
                    problem = $"unknown option '{name}'.";
                    return false;
            }
        }

        if (options.Paths.Count == 0)
        {
            problem = "give at least one file or directory to lint.";
            return false;
        }

        return true;
    }
}
