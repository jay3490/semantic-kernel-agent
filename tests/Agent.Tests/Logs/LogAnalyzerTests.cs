using Agent.Core.Logs;

namespace Agent.Tests.Logs;

public sealed class LogAnalyzerTests
{
    private readonly LogAnalyzer _analyzer = new();

    [Fact]
    public void Sample_log_is_counted_per_level_with_stack_traces_folded()
    {
        var report = _analyzer.Analyze(Samples.Read("logs/orders-api.log"));

        Assert.Equal(23, report.TotalLines);
        Assert.Equal(18, report.Entries);
        Assert.Equal(4, report.Levels["INFO"]);
        Assert.Equal(2, report.Levels["WARN"]);
        Assert.Equal(10, report.Levels["ERROR"]);
        Assert.Equal(1, report.Levels["FATAL"]);
        Assert.Equal(1, report.Levels["DEBUG"]);
        Assert.Equal(11, report.ErrorEntries);
        Assert.Equal(5, report.DistinctErrorSignatures);
    }

    [Fact]
    public void Errors_are_grouped_by_signature_and_ranked_by_count()
    {
        var report = _analyzer.Analyze(Samples.Read("logs/orders-api.log"), top: 3);

        Assert.Equal(3, report.TopErrors.Count);

        var first = report.TopErrors[0];
        Assert.Equal("payment gateway timeout after <n> for order <n>", first.Signature);
        Assert.Equal(4, first.Count);
        Assert.Equal(4, first.FirstLine);
        Assert.Equal(18, first.LastLine);
        Assert.StartsWith("2026-03-14T09:00:07.310Z ERROR payment gateway timeout", first.Example);

        // Ties are broken by first occurrence.
        Assert.Equal("unhandled exception for request <guid>", report.TopErrors[1].Signature);
        Assert.Equal("could not connect to redis at <ip>: connection refused", report.TopErrors[2].Signature);
    }

    [Theory]
    [InlineData("user 'alice' not found in tenant \"acme\"", "user <str> not found in tenant <str>")]
    [InlineData("job 0x7ffe1a2b failed at 2026-01-02T03:04:05Z", "job <hex> failed at <ts>")]
    [InlineData("commit 9f2c4e1a7b3d5f60 rejected", "commit <hex> rejected")]
    [InlineData("disk 93.5% full, 120MB left", "disk <n> full, <n> left")]
    [InlineData("  spaced    out  ", "spaced out")]
    public void Signatures_mask_variable_parts(string message, string expected)
    {
        Assert.Equal(expected, LogAnalyzer.Signature(message));
    }

    [Fact]
    public void Understands_json_lines_and_logfmt()
    {
        const string log = """
            {"ts":"2026-03-14T10:00:00Z","level":"error","msg":"queue full: dropped 12 events"}
            {"@t":"2026-03-14T10:00:01Z","@l":"Error","@m":"queue full: dropped 40 events"}
            {"level":"info","message":"ok"}
            ts=2026-03-14T10:00:02Z level=error msg="queue full: dropped 3 events" component=ingest
            ts=2026-03-14T10:00:03Z level=warn msg=backpressure
            """;

        var report = _analyzer.Analyze(log);

        Assert.Equal(5, report.Entries);
        Assert.Equal(3, report.ErrorEntries);
        var group = Assert.Single(report.TopErrors);
        Assert.Equal("queue full: dropped <n> events", group.Signature);
        Assert.Equal(3, group.Count);
        Assert.Equal(1, report.Levels["WARN"]);
    }

    [Fact]
    public void Bracketed_levels_are_recognised()
    {
        const string log = "2026/03/14 10:00:00 [error] 31#31: *1 connect() failed (111: Connection refused)\n";

        var report = _analyzer.Analyze(log);

        Assert.Equal(1, report.ErrorEntries);
    }

    [Fact]
    public void Lines_without_a_level_before_any_entry_are_counted_as_unknown()
    {
        var report = _analyzer.Analyze("plain output\nmore output\n");

        Assert.Equal(1, report.Entries);
        Assert.Equal(1, report.Levels["UNKNOWN"]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 5)]
    public void Top_is_clamped(int top, int expected)
    {
        var report = _analyzer.Analyze(Samples.Read("logs/orders-api.log"), top);

        Assert.Equal(expected, report.TopErrors.Count);
    }
}
