using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Core.Linting;

/// <summary>One logical Dockerfile instruction after line continuations have been joined.</summary>
/// <param name="Keyword">Upper-cased instruction keyword, for example <c>RUN</c>.</param>
/// <param name="Arguments">Everything after the keyword, with continuations joined by spaces and heredoc bodies on their own lines.</param>
/// <param name="Line">1-based line the instruction starts on.</param>
public sealed record DockerfileInstruction(string Keyword, string Arguments, int Line);

/// <summary>
/// A small Dockerfile tokenizer: handles comments, the <c># escape=</c> parser directive, line continuations
/// and heredocs (<c>RUN &lt;&lt;EOF</c>). It does not evaluate anything.
/// </summary>
public static partial class DockerfileParser
{
    public static IReadOnlyList<DockerfileInstruction> Parse(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var escape = DetectEscapeCharacter(lines);
        var instructions = new List<DockerfileInstruction>();

        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            var startLine = index + 1;
            index++;

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var builder = new StringBuilder();
            while (EndsWithContinuation(line, escape) && index < lines.Length)
            {
                builder.Append(line.AsSpan(0, line.Length - 1).TrimEnd()).Append(' ');
                line = lines[index].Trim();
                index++;

                // Comment lines inside a continued instruction are dropped by Docker as well.
                while (line.StartsWith('#') && index < lines.Length)
                {
                    line = lines[index].Trim();
                    index++;
                }
            }

            builder.Append(EndsWithContinuation(line, escape) ? line[..^1].TrimEnd() : line);
            var logical = builder.ToString();

            var split = logical.IndexOfAny([' ', '\t']);
            var keyword = (split < 0 ? logical : logical[..split]).ToUpperInvariant();
            var arguments = split < 0 ? string.Empty : logical[(split + 1)..].Trim();

            foreach (Match heredoc in HeredocMarker().Matches(arguments))
            {
                var terminator = heredoc.Groups["word"].Value;
                var stripTabs = heredoc.Groups["dash"].Success;
                var body = new StringBuilder();
                while (index < lines.Length)
                {
                    var bodyLine = stripTabs ? lines[index].TrimStart('\t') : lines[index];
                    index++;
                    if (bodyLine.TrimEnd() == terminator)
                    {
                        break;
                    }

                    body.Append('\n').Append(bodyLine);
                }

                arguments += body.ToString();
            }

            instructions.Add(new DockerfileInstruction(keyword, arguments, startLine));
        }

        return instructions;
    }

    private static bool EndsWithContinuation(string line, char escape) => line.Length > 0 && line[^1] == escape;

    private static char DetectEscapeCharacter(string[] lines)
    {
        // Parser directives must appear before any other line, comment or instruction.
        foreach (var line in lines)
        {
            var match = EscapeDirective().Match(line);
            if (match.Success)
            {
                return match.Groups["char"].Value[0];
            }

            if (!DirectiveLine().IsMatch(line))
            {
                break;
            }
        }

        return '\\';
    }

    [GeneratedRegex(@"^\s*#\s*escape\s*=\s*(?<char>[\\`])\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EscapeDirective();

    [GeneratedRegex(@"^\s*#\s*[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveLine();

    [GeneratedRegex(@"<<(?<dash>-)?[""']?(?<word>[A-Za-z_][A-Za-z0-9_]*)[""']?")]
    private static partial Regex HeredocMarker();
}
