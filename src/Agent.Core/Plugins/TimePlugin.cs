using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Clock functions, so the model does not have to guess the current time or do time-zone arithmetic.</summary>
public sealed class TimePlugin(TimeProvider clock)
{
    public const string PluginName = "time";

    [KernelFunction("utc_now")]
    [Description("Returns the current date and time in UTC, in ISO 8601 format.")]
    public string UtcNow() => clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    [KernelFunction("convert_time")]
    [Description("Converts a timestamp to another time zone. Returns ISO 8601 with the zone offset.")]
    public string ConvertTime(
        [Description("An ISO 8601 timestamp. Timestamps without an offset are treated as UTC.")] string timestamp,
        [Description("IANA time zone id such as Europe/Berlin or America/New_York, or UTC.")] string timeZone)
    {
        var instant = ParseTimestamp(timestamp);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            throw new ArgumentException($"Unknown time zone '{timeZone}'. Use an IANA id such as Europe/Berlin.", nameof(timeZone));
        }

        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    [KernelFunction("time_since")]
    [Description("Returns how long ago a timestamp was, relative to now. Negative when the timestamp is in the future.")]
    public ElapsedTime TimeSince(
        [Description("An ISO 8601 timestamp. Timestamps without an offset are treated as UTC.")] string timestamp)
    {
        var elapsed = clock.GetUtcNow() - ParseTimestamp(timestamp);
        return new ElapsedTime(Math.Round(elapsed.TotalSeconds), Humanize(elapsed));
    }

    private static DateTimeOffset ParseTimestamp(string timestamp)
    {
        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var value))
        {
            return value;
        }

        throw new ArgumentException($"'{timestamp}' is not a timestamp. Use ISO 8601, for example 2026-03-14T09:00:00Z.", nameof(timestamp));
    }

    private static string Humanize(TimeSpan elapsed)
    {
        var magnitude = elapsed.Duration();
        var parts = new List<string>(3);
        if (magnitude.Days > 0)
        {
            parts.Add($"{magnitude.Days}d");
        }

        if (magnitude.Hours > 0)
        {
            parts.Add($"{magnitude.Hours}h");
        }

        if (magnitude.Minutes > 0 && magnitude.Days == 0)
        {
            parts.Add($"{magnitude.Minutes}m");
        }

        if (parts.Count == 0)
        {
            parts.Add($"{magnitude.Seconds}s");
        }

        var text = string.Join(' ', parts);
        return elapsed < TimeSpan.Zero ? $"in {text}" : $"{text} ago";
    }
}

/// <summary>Elapsed time between a timestamp and now.</summary>
/// <param name="TotalSeconds">Whole seconds; negative for timestamps in the future.</param>
/// <param name="Human">Short description such as <c>2h 5m ago</c>.</param>
public sealed record ElapsedTime(double TotalSeconds, string Human);
