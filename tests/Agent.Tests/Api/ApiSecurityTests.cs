using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agent.Tests.Api;

public sealed class ApiSecurityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CiKey = "ci-key-0123456789abcdef";
    private const string OpsKey = "ops-key-0123456789abcdef";

    private HttpClient CreateClient(IDictionary<string, string>? settings = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("Auth:ApiKeys:0:Name", "ci");
            builder.UseSetting("Auth:ApiKeys:0:Key", CiKey);
            builder.UseSetting("Auth:ApiKeys:1:Name", "ops");
            builder.UseSetting("Auth:ApiKeys:1:Key", OpsKey);
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        }).CreateClient();

    private static HttpRequestMessage Lint(string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/lint/dockerfile") { Content = new StringContent("FROM alpine:3.20\n") };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return request;
    }

    [Fact]
    public async Task Requests_without_a_key_get_a_401_problem()
    {
        var response = await CreateClient().SendAsync(Lint(apiKey: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("ApiKey header=\"X-Api-Key\"", response.Headers.WwwAuthenticate.ToString());
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Requests_with_a_wrong_key_are_told_the_key_is_invalid()
    {
        var response = await CreateClient().SendAsync(Lint("ci-key-0123456789abcdeX"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("not valid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requests_with_a_valid_key_are_served_and_health_stays_open()
    {
        var client = CreateClient();

        var lint = await client.SendAsync(Lint(CiKey));
        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, lint.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Each_key_has_its_own_rate_limit_bucket()
    {
        var client = CreateClient(new Dictionary<string, string> { ["RateLimiting:PermitsPerMinute"] = "2", ["RateLimiting:Burst"] = "2" });

        var ciResponses = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            ciResponses.Add(await client.SendAsync(Lint(CiKey)));
        }

        var ops = await client.SendAsync(Lint(OpsKey));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], ciResponses.Select(r => r.StatusCode));
        Assert.True(int.Parse(ciResponses[2].Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal("application/problem+json", ciResponses[2].Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, ops.StatusCode);
    }

    [Fact]
    public async Task A_key_can_override_the_default_limit()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["RateLimiting:PermitsPerMinute"] = "1",
            ["RateLimiting:Burst"] = "1",
            ["Auth:ApiKeys:1:PermitsPerMinute"] = "0",
        });

        var ci = new[] { await client.SendAsync(Lint(CiKey)), await client.SendAsync(Lint(CiKey)) };
        var ops = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            ops.Add((await client.SendAsync(Lint(OpsKey))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, ci[1].StatusCode);
        Assert.All(ops, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    [Fact]
    public void Short_keys_stop_the_api_from_starting()
    {
        var error = Assert.ThrowsAny<Exception>(() => CreateClient(new Dictionary<string, string> { ["Auth:ApiKeys:0:Key"] = "short" }));

        Assert.Contains("at least 16 characters", error.ToString());
    }
}
