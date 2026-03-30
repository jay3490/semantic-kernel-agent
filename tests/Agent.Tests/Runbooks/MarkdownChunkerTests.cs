using Agent.Core.Runbooks;

namespace Agent.Tests.Runbooks;

public sealed class MarkdownChunkerTests
{
    [Fact]
    public void Splits_at_headings_and_keeps_the_heading_path_and_start_line()
    {
        var chunks = MarkdownChunker.Chunk("runbooks/crashloopbackoff.md", Samples.Read("runbooks/crashloopbackoff.md"));

        Assert.Equal(
            ["Pods in CrashLoopBackOff", "Pods in CrashLoopBackOff > Symptoms", "Pods in CrashLoopBackOff > Diagnosis", "Pods in CrashLoopBackOff > Mitigation"],
            chunks.Select(c => c.Heading));
        Assert.Equal([3, 7, 12, 24], chunks.Select(c => c.Line));
        Assert.StartsWith("1. Read the last crash", chunks[2].Text);
        Assert.All(chunks, c => Assert.Equal("runbooks/crashloopbackoff.md", c.File));
    }

    [Fact]
    public void Headings_inside_code_fences_do_not_split()
    {
        const string markdown = "# Title\n\n```bash\n# not a heading\necho hi\n```\n\n## Next\ntext\n";

        var chunks = MarkdownChunker.Chunk("x.md", markdown);

        Assert.Equal(["Title", "Title > Next"], chunks.Select(c => c.Heading));
        Assert.Contains("# not a heading", chunks[0].Text);
    }

    [Fact]
    public void A_deeper_heading_replaces_siblings_but_keeps_parents()
    {
        var chunks = MarkdownChunker.Chunk("x.md", "# A\na\n## B\nb\n### C\nc\n## D\nd\n");

        Assert.Equal(["A", "A > B", "A > B > C", "A > D"], chunks.Select(c => c.Heading));
    }

    [Fact]
    public void Long_sections_are_split_at_paragraph_breaks_with_their_own_lines()
    {
        var paragraphs = Enumerable.Range(1, 6).Select(i => $"paragraph {i} " + new string('x', 80));
        var markdown = "# Long\n\n" + string.Join("\n\n", paragraphs) + "\n";

        var chunks = MarkdownChunker.Chunk("x.md", markdown, maxChunkCharacters: 200);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200, $"chunk of {c.Text.Length} characters"));
        Assert.Equal(3, chunks[0].Line);
        Assert.StartsWith("paragraph 3", chunks[1].Text);
        Assert.Equal(7, chunks[1].Line);
    }

    [Fact]
    public void Empty_sections_produce_no_chunks()
    {
        Assert.Empty(MarkdownChunker.Chunk("x.md", "# Only a title\n\n"));
    }
}
