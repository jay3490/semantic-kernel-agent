using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agent.Tests.Api;

public sealed class OpenApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<JsonElement> DocumentAsync()
    {
        // Keys are configured: the document must still be readable without one.
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("Auth:ApiKeys:0:Name", "ci");
            builder.UseSetting("Auth:ApiKeys:0:Key", "ci-key-0123456789abcdef");
        }).CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Documents_every_chat_and_lint_operation()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");

        Assert.Equal(
            ["/chat", "/chat/stream", "/chat/{sessionId}", "/lint/dockerfile", "/lint/github-actions", "/lint/k8s", "/lint/terraform"],
            paths.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Chat", paths.GetProperty("/chat").GetProperty("post").GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task Declares_the_api_key_header_scheme()
    {
        var document = await DocumentAsync();

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal(("apiKey", "header", "X-Api-Key"),
            (scheme.GetProperty("type").GetString(), scheme.GetProperty("in").GetString(), scheme.GetProperty("name").GetString()));
        Assert.True(document.GetProperty("security")[0].TryGetProperty("ApiKey", out _));
    }

    [Fact]
    public async Task Chat_has_a_json_example_and_problem_responses()
    {
        var post = (await DocumentAsync()).GetProperty("paths").GetProperty("/chat").GetProperty("post");

        var example = post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");
        Assert.Equal("demo", example.GetProperty("sessionId").GetString());
        Assert.Contains("helm", example.GetProperty("message").GetString());

        var responses = post.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "401", "413", "429", "502", "503" })
        {
            Assert.True(responses.TryGetProperty(status, out _), $"missing response {status}");
        }

        Assert.True(responses.GetProperty("429").GetProperty("content").TryGetProperty("application/problem+json", out _));
    }

    [Fact]
    public async Task Lint_operations_take_a_raw_text_body_with_an_example()
    {
        var post = (await DocumentAsync()).GetProperty("paths").GetProperty("/lint/dockerfile").GetProperty("post");

        var body = post.GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        var text = body.GetProperty("content").GetProperty("text/plain");
        Assert.Equal("string", text.GetProperty("schema").GetProperty("type").GetString());
        Assert.StartsWith("FROM node:latest", text.GetProperty("example").GetString());
        Assert.True(post.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/json", out _));
    }
}
