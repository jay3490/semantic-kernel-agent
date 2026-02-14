using Agent.Core.Linting;

namespace Agent.Tests.Linting;

public sealed class DockerfileLinterTests
{
    private readonly DockerfileLinter _linter = new();

    [Fact]
    public void Good_sample_passes_with_no_findings()
    {
        var report = _linter.Lint(Samples.Read("docker/Dockerfile.good"));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Bad_sample_reports_each_problem_on_its_line()
    {
        var report = _linter.Lint(Samples.Read("docker/Dockerfile.bad"));

        var actual = report.Findings.Select(f => (f.RuleId, f.Line)).OrderBy(f => f.Line).ThenBy(f => f.RuleId);
        Assert.Equal(
            [("DF001", 1), ("DF001", 8), ("DF004", 8), ("DF005", 9), ("DF003", 11), ("DF002", 16)],
            actual);
        Assert.Equal(0, report.Errors);
        Assert.Equal(5, report.Warnings);
        Assert.Equal(1, report.Infos);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Findings_name_the_stage()
    {
        var report = _linter.Lint(Samples.Read("docker/Dockerfile.bad"));

        Assert.Equal("stage 1 (assets)", report.Findings.Single(f => f is { RuleId: "DF001", Line: 1 }).Target);
        Assert.Equal("stage 2", report.Findings.Single(f => f.RuleId == "DF002").Target);
    }

    [Theory]
    [InlineData("FROM alpine:3.20\nRUN adduser -D app\nUSER app\nHEALTHCHECK NONE\n", false)]
    [InlineData("FROM alpine:3.20\nUSER 0:0\nHEALTHCHECK NONE\n", true)]
    [InlineData("FROM alpine:3.20\nHEALTHCHECK NONE\n", true)]
    [InlineData("FROM alpine:3.20 AS build\nUSER app\nFROM alpine:3.20\nHEALTHCHECK NONE\n", true)]
    public void Only_the_final_stage_user_decides_whether_the_container_runs_as_root(string dockerfile, bool flagged)
    {
        Assert.Equal(flagged, _linter.Lint(dockerfile).Findings.Any(f => f.RuleId == "DF002"));
    }

    [Theory]
    [InlineData("FROM scratch", false)]
    [InlineData("FROM ubuntu:24.04", false)]
    [InlineData("FROM ubuntu@sha256:abcdef", false)]
    [InlineData("FROM --platform=linux/amd64 ubuntu", true)]
    [InlineData("FROM ubuntu:latest", true)]
    [InlineData("ARG TAG=22.04\nFROM ubuntu:${TAG}", false)]
    [InlineData("ARG TAG=latest\nFROM ubuntu:$TAG", true)]
    [InlineData("ARG BASE\nFROM ${BASE}", false)]
    [InlineData("FROM golang:1.23 AS build\nFROM build", false)]
    public void Base_images_must_be_pinned(string dockerfile, bool flagged)
    {
        Assert.Equal(flagged, _linter.Lint(dockerfile).Findings.Any(f => f.RuleId == "DF001"));
    }

    [Theory]
    [InlineData("ADD app.py /app/", true)]
    [InlineData("ADD --chown=app:app . /app", true)]
    [InlineData("ADD [\"config.json\", \"/etc/app/\"]", true)]
    [InlineData("ADD https://example.com/tool.bin /usr/local/bin/tool", false)]
    [InlineData("ADD rootfs.tar.gz /", false)]
    [InlineData("COPY app.py /app/", false)]
    public void ADD_is_only_accepted_for_urls_and_archives(string instruction, bool flagged)
    {
        var report = _linter.Lint($"FROM alpine:3.20\n{instruction}\n");

        Assert.Equal(flagged, report.Findings.Any(f => f.RuleId == "DF003"));
    }

    [Theory]
    [InlineData("RUN apt-get update && apt-get install -y curl", true)]
    [InlineData("RUN apt install -y curl", true)]
    [InlineData("RUN apt-get update && apt-get -y --no-install-recommends install curl", true)]
    [InlineData("RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*", false)]
    [InlineData("RUN apt-get update \\\n  && apt-get install -y curl \\\n  && rm -rf /var/lib/apt/lists/*", false)]
    [InlineData("RUN --mount=type=cache,target=/var/lib/apt apt-get update && apt-get install -y curl", false)]
    [InlineData("RUN apk add --no-cache curl", false)]
    public void Apt_installs_must_clean_the_package_lists_in_the_same_layer(string instruction, bool flagged)
    {
        var report = _linter.Lint($"FROM debian:12\n{instruction}\n");

        Assert.Equal(flagged, report.Findings.Any(f => f.RuleId == "DF005"));
    }

    [Fact]
    public void Input_without_FROM_is_an_error()
    {
        var finding = Assert.Single(_linter.Lint("RUN echo hi\n").Findings);

        Assert.Equal("DF000", finding.RuleId);
        Assert.Equal(Severity.Error, finding.Severity);
    }
}
