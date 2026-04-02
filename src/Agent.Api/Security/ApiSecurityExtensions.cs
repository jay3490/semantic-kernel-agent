using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Agent.Api.Security;

public static partial class ApiSecurityExtensions
{
    /// <summary>Policy on the chat and lint endpoints: a valid API key when keys are configured, anyone otherwise.</summary>
    public const string ClientPolicy = "api-client";

    /// <summary>Rate-limiter policy on the chat and lint endpoints: one token bucket per API key (or client IP without auth).</summary>
    public const string RateLimitPolicy = "per-client";

    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiKeyOptions>()
            .Bind(configuration.GetSection(ApiKeyOptions.SectionName))
            .Validate(o => o.ApiKeys.All(k => !string.IsNullOrWhiteSpace(k.Name) && k.Key.Length >= 16),
                "Every Auth:ApiKeys entry needs a Name and a Key of at least 16 characters.")
            .Validate(o => o.ApiKeys.Select(k => k.Name).Distinct(StringComparer.Ordinal).Count() == o.ApiKeys.Count,
                "Auth:ApiKeys names must be unique.")
            .ValidateOnStart();
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .Validate(o => o.PermitsPerMinute >= 0 && o.Burst > 0, "RateLimiting:PermitsPerMinute cannot be negative and Burst must be positive.")
            .ValidateOnStart();

        var keys = configuration.GetSection(ApiKeyOptions.SectionName).Get<ApiKeyOptions>() ?? new ApiKeyOptions();

        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorization(options => options.AddPolicy(ClientPolicy, policy =>
        {
            if (keys.Enabled)
            {
                policy.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser();
            }
            else
            {
                policy.RequireAssertion(_ => true);
            }
        }));

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitPolicy, http => Partition(http));
            options.OnRejected = WriteRejectionAsync;
        });

        return services;
    }

    /// <summary>Logs a warning when the API runs without authentication.</summary>
    public static WebApplication WarnIfUnauthenticated(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<ApiKeyOptions>>().Value.Enabled)
        {
            LogAuthDisabled(app.Logger);
        }

        return app;
    }

    private static RateLimitPartition<string> Partition(HttpContext http)
    {
        var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        var client = http.User.Identity?.IsAuthenticated == true
            ? $"key:{http.User.FindFirstValue(ClaimTypes.Name)}"
            : $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        var permits = limits.PermitsPerMinute;
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var name = http.User.FindFirstValue(ClaimTypes.Name);
            var entry = http.RequestServices.GetRequiredService<IOptions<ApiKeyOptions>>().Value.ApiKeys.FirstOrDefault(k => k.Name == name);
            permits = entry?.PermitsPerMinute ?? permits;
        }

        if (permits == 0)
        {
            return RateLimitPartition.GetNoLimiter(client);
        }

        return RateLimitPartition.GetTokenBucketLimiter(client, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(1, Math.Min(limits.Burst, permits)),
            TokensPerPeriod = permits,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var detail = "Too many requests for this client.";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            detail += $" Retry after {seconds} seconds.";
        }

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too Many Requests", Detail = detail },
        });
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning, Message = "No API keys are configured (Auth:ApiKeys), so the chat and lint endpoints accept anonymous requests")]
    private static partial void LogAuthDisabled(ILogger logger);
}
