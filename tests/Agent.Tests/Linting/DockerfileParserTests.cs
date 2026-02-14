using Agent.Core.Linting;

namespace Agent.Tests.Linting;

public sealed class DockerfileParserTests
{
    [Fact]
    public void Joins_continuations_and_skips_comments()
    {
        const string dockerfile = """
            # a comment
            FROM alpine:3.20
            RUN apk add --no-cache \
                # interleaved comment
                curl \
                jq
            """;

        var instructions = DockerfileParser.Parse(dockerfile);

        Assert.Equal(2, instructions.Count);
        Assert.Equal(new DockerfileInstruction("RUN", "apk add --no-cache curl jq", 3), instructions[1]);
    }

    [Fact]
    public void Honours_the_escape_directive()
    {
        const string dockerfile = "# escape=`\nFROM mcr.microsoft.com/windows/servercore:ltsc2022\nRUN dir `\n  C:\\\n";

        var instructions = DockerfileParser.Parse(dockerfile);

        Assert.Equal("dir C:\\", instructions[1].Arguments);
    }

    [Fact]
    public void Keeps_heredoc_bodies_with_their_instruction()
    {
        const string dockerfile = """
            FROM debian:12
            RUN <<EOF
            apt-get update
            apt-get install -y curl
            EOF
            USER nobody
            """;

        var instructions = DockerfileParser.Parse(dockerfile);

        Assert.Equal(["FROM", "RUN", "USER"], instructions.Select(i => i.Keyword));
        Assert.Contains("apt-get install -y curl", instructions[1].Arguments);
        Assert.Equal(6, instructions[2].Line);
    }
}
