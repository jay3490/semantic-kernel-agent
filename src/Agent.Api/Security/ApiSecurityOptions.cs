namespace Agent.Api.Security;

/// <summary>One client allowed to call the API.</summary>
public sealed class ApiKeyEntry
{
    /// <summary>Who the key belongs to; used in logs, metrics and as the rate-limit partition. Never the key itself.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The secret the client sends in the <c>X-Api-Key</c> header.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Overrides <see cref="RateLimitOptions.PermitsPerMinute"/> for this key.</summary>
    public int? PermitsPerMinute { get; set; }
}

/// <summary>API key authentication. Bound from the <c>Auth</c> configuration section.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Auth";

    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// The accepted keys. When the list is empty, authentication is off and every endpoint is open, which suits
    /// local development only; the API logs a warning at startup.
    /// </summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];

    public bool Enabled => ApiKeys.Count > 0;
}

/// <summary>Per-client request limits. Bound from the <c>RateLimiting</c> configuration section.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests a client may make per minute, refilled continuously. Zero turns rate limiting off.</summary>
    public int PermitsPerMinute { get; set; } = 60;

    /// <summary>How many requests a client may make at once before it has to wait for the bucket to refill.</summary>
    public int Burst { get; set; } = 20;
}
