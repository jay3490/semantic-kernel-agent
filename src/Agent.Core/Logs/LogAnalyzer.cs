using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core.Logs;

/// <summary>
/// Parses plain-text, logfmt and JSON-lines logs, folds stack traces into the entry that produced them, and
/// groups ERROR/FATAL entries by a signature in which timestamps, ids, addresses, quoted values and numbers are masked.
/// </summary>
public sealed partial class LogAnalyzer
{
    public const int MaxTop = 50;
    private const int MaxSignatureLength = 200;
    private const int MaxExampleLength = 300;

    private static readonly string[] JsonLevelFields = ["level", "severity", "lvl", "@l", "log.level"];
    private static readonly string[] JsonMessageFields = ["message", "msg", "@m", "@mt"];

    public LogAnalysisReport Analyze(string text, int top = 5)
    {
        top = Math.Clamp(top, 1, MaxTop);

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var totalLines = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        var entries = 0;
        var errorEntries = 0;

        for (var i = 0; i < totalLines; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = ParseLine(line);
            if (entry is null && entries > 0)
            {
                // Stack frames, exception headers and wrapped text belong to the entry above.
                continue;
            }

            var (level, message) = entry ?? ("UNKNOWN", line);
            entries++;
            levels[level] = levels.GetValueOrDefault(level) + 1;

            if (level is "ERROR" or "FATAL")
            {
                errorEntries++;
                var signature = Signature(message);
                if (groups.TryGetValue(signature, out var group))
                {
                    group.Count++;
                    group.LastLine = i + 1;
                }
                else
                {
                    groups[signature] = new GroupBuilder(signature, i + 1, Truncate(line.Trim(), MaxExampleLength));
                }
            }
        }

        var topErrors = groups.Values
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.FirstLine)
            .Take(top)
            .Select(g => new ErrorGroup(g.Signature, g.Count, g.FirstLine, g.LastLine, g.Example))
            .ToArray();

        return new LogAnalysisReport(totalLines, entries, levels, errorEntries, groups.Count, topErrors);
    }

    /// <summary>Returns the level and message of a line that starts an entry, or null for a continuation line.</summary>
    private static (string Level, string Message)? ParseLine(string line)
    {
        if (char.IsWhiteSpace(line[0]))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('{') && TryParseJson(trimmed, out var json))
        {
            return json;
        }

        var match = LevelToken().Match(trimmed);
        if (!match.Success)
        {
            return LeadingTimestamp().IsMatch(trimmed) ? ("UNKNOWN", trimmed) : null;
        }

        var level = NormalizeLevel(match.Groups["level"].Value);
        var logfmtMessage = LogfmtMessage().Match(trimmed);
        var message = logfmtMessage.Success
            ? logfmtMessage.Groups["quoted"].Success ? logfmtMessage.Groups["quoted"].Value : logfmtMessage.Groups["bare"].Value
            : trimmed[(match.Index + match.Length)..].TrimStart(' ', '\t', ':', ']', '-', '|');

        return (level, message);
    }

    private static bool TryParseJson(string line, out (string Level, string Message) entry)
    {
        entry = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var level = FirstString(document.RootElement, JsonLevelFields);
            var message = FirstString(document.RootElement, JsonMessageFields) ?? line;
            entry = (level is null ? "UNKNOWN" : NormalizeLevel(level), message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstString(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string NormalizeLevel(string level) => level.ToUpperInvariant() switch
    {
        "TRACE" or "VERBOSE" => "TRACE",
        "DEBUG" or "DBG" => "DEBUG",
        "INFO" or "INFORMATION" or "NOTICE" => "INFO",
        "WARN" or "WARNING" => "WARN",
        "ERROR" or "ERR" => "ERROR",
        "FATAL" or "CRITICAL" or "CRIT" or "PANIC" or "ALERT" or "EMERG" => "FATAL",
        _ => "UNKNOWN",
    };

    /// <summary>Masks the parts of a message that vary between occurrences of the same problem.</summary>
    internal static string Signature(string message)
    {
        var signature = Timestamp().Replace(message, "<ts>");
        signature = GuidValue().Replace(signature, "<guid>");
        signature = IpAddress().Replace(signature, "<ip>");
        signature = HexValue().Replace(signature, "<hex>");
        signature = QuotedValue().Replace(signature, "<str>");
        signature = Number().Replace(signature, "<n>");
        signature = Whitespace().Replace(signature, " ").Trim();
        return Truncate(signature, MaxSignatureLength);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "...";

    private sealed class GroupBuilder(string signature, int firstLine, string example)
    {
        public string Signature { get; } = signature;
        public int FirstLine { get; } = firstLine;
        public string Example { get; } = example;
        public int Count { get; set; } = 1;
        public int LastLine { get; set; } = firstLine;
    }

    [GeneratedRegex(@"\blevel=""?(?<level>[A-Za-z]+)|\[(?<level>(?i:trace|debug|info|notice|warn|warning|error|err|fatal|crit|critical|alert|emerg|panic))\]|\b(?<level>TRACE|DEBUG|INFO|NOTICE|WARN|WARNING|ERROR|ERR|FATAL|CRITICAL|CRIT|PANIC|ALERT|EMERG)\b")]
    private static partial Regex LevelToken();

    [GeneratedRegex(@"\bmsg=(?:""(?<quoted>[^""]*)""|(?<bare>\S+))")]
    private static partial Regex LogfmtMessage();

    [GeneratedRegex(@"^\[?\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidValue();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b")]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b|\b(?=[0-9a-f]*\d)[0-9a-f]{12,}\b")]
    private static partial Regex HexValue();

    [GeneratedRegex(@"'[^']*'|""[^""]*""")]
    private static partial Regex QuotedValue();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?%|\b\d+(?:\.\d+)?(?:ms|us|ns|s|m|h|kb|mb|gb|b)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
