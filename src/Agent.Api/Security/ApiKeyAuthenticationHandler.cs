using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agent.Api.Security;

/// <summary>
/// Authenticates requests by the <c>X-Api-Key</c> header. Keys are compared as SHA-256 hashes in constant time, so
/// neither the comparison time nor a log line reveals how much of a key was right. The authenticated name is the
/// key's <see cref="ApiKeyEntry.Name"/>.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<ApiKeyOptions> apiKeys,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";

    private readonly (string Name, byte[] Hash)[] _keys = apiKeys.Value.ApiKeys
        .Select(k => (k.Name, SHA256.HashData(Encoding.UTF8.GetBytes(k.Key))))
        .ToArray();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyOptions.HeaderName, out var values) || string.IsNullOrEmpty(values.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(values.ToString()));
        string? match = null;
        foreach (var (name, hash) in _keys)
        {
            // Every key is compared, so the time taken does not depend on which one matched.
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
            {
                match = name;
            }
        }

        if (match is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key is not valid."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, match)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{ApiKeyOptions.HeaderName}\"";

        var failure = (await HandleAuthenticateOnceSafeAsync()).Failure;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = failure?.Message ?? $"Send an API key in the {ApiKeyOptions.HeaderName} header.",
            },
        });
    }
}
