using System.Net;
using System.Net.Http.Json;
using Agent.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Api;

public sealed class MetricsEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Prometheus_endpoint_exposes_the_agent_metrics_without_an_api_key()
    {
        var model = new ScriptedChatCompletionService().ThenCallTool("time", "utc_now").ThenAnswer("noon");
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("Auth:ApiKeys:0:Name", "ci");
            builder.UseSetting("Auth:ApiKeys:0:Key", "ci-key-0123456789abcdef");
            builder.ConfigureTestServices(services => services.AddSingleton<IChatCompletionService>(model));
        }).CreateClient();

        var chat = new HttpRequestMessage(HttpMethod.Post, "/chat") { Content = JsonContent.Create(new { sessionId = "m1", message = "time?" }) };
        chat.Headers.Add("X-Api-Key", "ci-key-0123456789abcdef");
        (await client.SendAsync(chat)).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("agent_tool_calls_total{", text);
        Assert.Contains("tool=\"time.utc_now\"", text);
        Assert.Contains("agent_model_calls_total{", text);
        Assert.Contains("http_server_request_duration_seconds", text);
    }
}
