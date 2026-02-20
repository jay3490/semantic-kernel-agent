using Agent.Core.Plugins;

namespace Agent.Tests.Plugins;

public sealed class TimePluginTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TimePlugin _plugin = new(new FixedClock(Now));

    [Fact]
    public void Reports_utc_now()
    {
        Assert.Equal("2026-03-14T12:00:00Z", _plugin.UtcNow());
    }

    [Theory]
    [InlineData("2026-03-14T09:00:00Z", "Europe/Berlin", "2026-03-14T10:00:00+01:00")]
    [InlineData("2026-07-01T12:00:00Z", "America/New_York", "2026-07-01T08:00:00-04:00")]
    [InlineData("2026-03-14T09:00:00", "UTC", "2026-03-14T09:00:00+00:00")]
    public void Converts_between_time_zones(string timestamp, string zone, string expected)
    {
        Assert.Equal(expected, _plugin.ConvertTime(timestamp, zone));
    }

    [Fact]
    public void Rejects_unknown_time_zones()
    {
        var error = Assert.Throws<ArgumentException>(() => _plugin.ConvertTime("2026-03-14T09:00:00Z", "Mars/Olympus"));

        Assert.Contains("Unknown time zone", error.Message);
    }

    [Theory]
    [InlineData("2026-03-14T09:45:00Z", 8100, "2h 15m ago")]
    [InlineData("2026-03-14T11:59:30Z", 30, "30s ago")]
    [InlineData("2026-03-11T10:00:00Z", 266400, "3d 2h ago")]
    [InlineData("2026-03-14T12:30:00Z", -1800, "in 30m")]
    public void Describes_elapsed_time(string timestamp, double seconds, string human)
    {
        var elapsed = _plugin.TimeSince(timestamp);

        Assert.Equal(seconds, elapsed.TotalSeconds);
        Assert.Equal(human, elapsed.Human);
    }

    [Fact]
    public void Rejects_text_that_is_not_a_timestamp()
    {
        Assert.Throws<ArgumentException>(() => _plugin.TimeSince("yesterday-ish"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
