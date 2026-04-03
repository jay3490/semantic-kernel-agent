using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Api;

public sealed class HardeningTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(IDictionary<string, string>? settings = null, ScriptedChatCompletionService? model = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("Runbooks:Directory", "runbooks");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            if (model is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<IChatCompletionService>(model));
            }
        }).CreateClient();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Chat_bodies_over_the_limit_are_refused_before_they_are_read()
    {
        var client = CreateClient(new Dictionary<string, string> { ["Api:MaxChatRequestBytes"] = "512" }, new ScriptedChatCompletionService());

        var response = await client.PostAsJsonAsync("/chat", new { sessionId = "big", message = new string('x', 2_000) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Contains("512-byte limit", problem.GetProperty("detail").GetString());
        Assert.Equal("POST /chat", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Messages_over_the_character_limit_are_a_bad_request()
    {
        var client = CreateClient(new Dictionary<string, string> { ["Agent:MaxMessageCharacters"] = "100" }, new ScriptedChatCompletionService());

        var response = await client.PostAsJsonAsync("/chat", new { sessionId = "long", message = new string('x', 101) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("the limit is 100", (await ProblemAsync(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Malformed_json_is_a_bad_request_problem()
    {
        var client = CreateClient(model: new ScriptedChatCompletionService());

        var response = await client.PostAsync("/chat", new StringContent("{\"message\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ProblemAsync(response)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Liveness_runs_no_checks_and_readiness_reports_each_one()
    {
        var client = CreateClient();

        using var live = JsonDocument.Parse(await client.GetStringAsync("/health/live"));
        var readyResponse = await client.GetAsync("/health/ready");
        using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());

        Assert.Equal("Healthy", live.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, live.RootElement.GetProperty("checks").GetArrayLength());

        // No chat model is configured, so the API is degraded but still ready to serve lint requests.
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        Assert.Equal("Degraded", ready.RootElement.GetProperty("status").GetString());
        var checks = ready.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("Healthy", checks["workspace"]);
        Assert.Equal("Degraded", checks["chat-model"]);
        Assert.Equal("Healthy", checks["runbooks"]);
        Assert.False(checks.ContainsKey("session-store"));
    }

    [Fact]
    public async Task Readiness_checks_the_sqlite_session_store_when_it_is_used()
    {
        var directory = Directory.CreateTempSubdirectory("health-").FullName;
        try
        {
            var client = CreateClient(new Dictionary<string, string>
            {
                ["Sessions:Provider"] = "Sqlite",
                ["Sessions:ConnectionString"] = $"Data Source={Path.Combine(directory, "sessions.db")}",
            });

            using var ready = JsonDocument.Parse(await client.GetStringAsync("/health/ready"));

            var store = ready.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "session-store");
            Assert.Equal("Healthy", store.GetProperty("status").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_workspace_makes_the_api_unhealthy()
    {
        var root = Directory.CreateTempSubdirectory("gone-").FullName;
        var client = CreateClient(new Dictionary<string, string> { ["Workspace:Root"] = root });
        await client.GetAsync("/health/ready");
        Directory.Delete(root);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
