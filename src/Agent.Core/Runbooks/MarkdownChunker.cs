using System.Text;

namespace Agent.Core.Runbooks;

/// <summary>A searchable piece of a runbook: one section, or part of a long one.</summary>
/// <param name="File">Path of the runbook, relative to the workspace root.</param>
/// <param name="Line">1-based line where the chunk starts.</param>
/// <param name="Heading">The heading path, such as <c>High 5xx rate &gt; Mitigation</c>; empty before the first heading.</param>
/// <param name="Text">The chunk's text without the heading line.</param>
public sealed record RunbookChunk(string File, int Line, string Heading, string Text);

/// <summary>
/// Splits markdown into chunks at headings, so a search hit points at the section that answers the question.
/// Headings inside fenced code blocks are ignored, and sections longer than the size limit are split at
/// paragraph breaks.
/// </summary>
public static class MarkdownChunker
{
    public const int DefaultMaxChunkCharacters = 1_200;

    public static IReadOnlyList<RunbookChunk> Chunk(string file, string markdown, int maxChunkCharacters = DefaultMaxChunkCharacters)
    {
        var chunks = new List<RunbookChunk>();
        var headings = new List<(int Level, string Title)>();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');

        var section = new StringBuilder();
        var sectionLine = 1;
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && HeadingLevel(line) is > 0 and var level)
            {
                Flush();
                headings.RemoveAll(h => h.Level >= level);
                headings.Add((level, line.TrimStart('#').Trim()));
                sectionLine = i + 2;
                continue;
            }

            if (section.Length == 0 && string.IsNullOrWhiteSpace(line))
            {
                sectionLine = i + 2;
                continue;
            }

            section.Append(line).Append('\n');
        }

        Flush();
        return chunks;

        void Flush()
        {
            var heading = string.Join(" > ", headings.Select(h => h.Title));
            foreach (var (text, offset) in SplitLongSection(section.ToString().TrimEnd(), maxChunkCharacters))
            {
                chunks.Add(new RunbookChunk(file, sectionLine + offset, heading, text));
            }

            section.Clear();
        }
    }

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    /// <summary>Yields pieces of at most <paramref name="max"/> characters, cut at blank lines where possible, with their line offset.</summary>
    private static IEnumerable<(string Text, int LineOffset)> SplitLongSection(string text, int max)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        if (text.Length <= max)
        {
            yield return (text, 0);
            yield break;
        }

        var paragraphs = text.Split("\n\n");
        var current = new StringBuilder();
        var currentOffset = 0;
        var offset = 0;

        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > max)
            {
                yield return (current.ToString(), currentOffset);
                current.Clear();
                currentOffset = offset;
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
            offset += paragraph.Count(c => c == '\n') + 2;
        }

        if (current.Length > 0)
        {
            yield return (current.ToString(), currentOffset);
        }
    }
}
