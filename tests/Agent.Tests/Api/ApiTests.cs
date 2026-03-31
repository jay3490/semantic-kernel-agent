using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Api;

public sealed class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(ScriptedChatCompletionService? model = null, long? maxBytes = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("ChatModel:ApiKey", "");
            builder.UseSetting("ChatModel:Endpoint", "");
            if (maxBytes is not null)
            {
                builder.UseSetting("Workspace:MaxFileBytes", maxBytes.Value.ToString());
            }

            if (model is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<IChatCompletionService>(model));
            }
        }).CreateClient();

    [Fact]
    public async Task Health_reports_ok()
    {
        var response = await CreateClient().GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Lint_k8s_returns_a_structured_report()
    {
        var response = await CreateClient().PostAsync("/lint/k8s",
            new StringContent(Samples.Read("kubernetes/bad-deployment.yaml"), Encoding.UTF8, "application/yaml"));

        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = report.RootElement;
        Assert.Equal("kubernetes", root.GetProperty("linter").GetString());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(1, root.GetProperty("errors").GetInt32());

        var first = root.GetProperty("findings")[0];
        Assert.Equal("K8S006", first.GetProperty("ruleId").GetString());
        Assert.Equal("Error", first.GetProperty("severity").GetString());
        Assert.Equal(26, first.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Lint_dockerfile_passes_the_good_sample()
    {
        var response = await CreateClient().PostAsync("/lint/dockerfile",
            new StringContent(Samples.Read("docker/Dockerfile.good"), Encoding.UTF8, "text/plain"));

        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(report.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task Lint_terraform_returns_a_structured_report()
    {
        var response = await CreateClient().PostAsync("/lint/terraform",
            new StringContent(Samples.Read("terraform/bad/main.tf"), Encoding.UTF8, "text/plain"));

        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("terraform", report.RootElement.GetProperty("linter").GetString());
        Assert.Equal(3, report.RootElement.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task Lint_github_actions_returns_a_structured_report()
    {
        var response = await CreateClient().PostAsync("/lint/github-actions",
            new StringContent(Samples.Read("github-actions/bad-workflow.yml"), Encoding.UTF8, "application/yaml"));

        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("github-actions", report.RootElement.GetProperty("linter").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task Lint_rejects_empty_and_oversized_bodies()
    {
        var empty = await CreateClient().PostAsync("/lint/dockerfile", new StringContent(""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var tooLarge = await CreateClient(maxBytes: 64).PostAsync("/lint/k8s",
            new StringContent(Samples.Read("kubernetes/bad-deployment.yaml")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }

    [Fact]
    public async Task Chat_runs_tools_and_returns_the_reply()
    {
        var model = new ScriptedChatCompletionService()
            .ThenCallTool("dockerfile", "lint_dockerfile_file", new { path = "docker/Dockerfile.bad" })
            .ThenAnswer("Five warnings; start with USER root on line 16.");

        var response = await CreateClient(model).PostAsJsonAsync("/chat", new { sessionId = "api-1", message = "Check docker/Dockerfile.bad" });

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("api-1", body.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("Five warnings; start with USER root on line 16.", body.RootElement.GetProperty("reply").GetString());
        var call = body.RootElement.GetProperty("toolCalls")[0];
        Assert.Equal("dockerfile.lint_dockerfile_file", call.GetProperty("tool").GetString());
        Assert.True(call.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Chat_starts_a_new_session_when_none_is_given()
    {
        var model = new ScriptedChatCompletionService().ThenAnswer("hi");

        var response = await CreateClient(model).PostAsJsonAsync("/chat", new { message = "hello" });

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(32, body.RootElement.GetProperty("sessionId").GetString()!.Length);
    }

    [Fact]
    public async Task Chat_without_a_model_is_unavailable_but_lint_still_works()
    {
        var client = CreateClient();

        var chat = await client.PostAsJsonAsync("/chat", new { sessionId = "x", message = "hello" });
        var stream = await client.PostAsJsonAsync("/chat/stream", new { sessionId = "x", message = "hello" });
        var lint = await client.PostAsync("/lint/dockerfile", new StringContent("FROM alpine:3.20\n"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, chat.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stream.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lint.StatusCode);
    }

    [Fact]
    public async Task Chat_rejects_bad_session_ids_and_empty_messages()
    {
        var client = CreateClient(new ScriptedChatCompletionService());

        var badId = await client.PostAsJsonAsync("/chat", new { sessionId = "not valid!", message = "hello" });
        var empty = await client.PostAsJsonAsync("/chat", new { sessionId = "ok", message = " " });

        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Chat_stream_sends_server_sent_events()
    {
        var model = new ScriptedChatCompletionService()
            .ThenCallTool("time", "utc_now")
            .ThenAnswer("It is just past noon UTC.");

        var response = await CreateClient(model).PostAsJsonAsync("/chat/stream", new { sessionId = "sse-1", message = "time?" });

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var events = ParseEvents(await response.Content.ReadAsStringAsync());
        Assert.Equal(("tool_call", "{\"tool\":\"time.utc_now\"}"), events[0]);
        Assert.Equal("tool_result", events[1].Name);
        Assert.Equal("done", events[^1].Name);

        var text = string.Concat(events.Where(e => e.Name == "delta")
            .Select(e => JsonDocument.Parse(e.Data).RootElement.GetProperty("text").GetString()));
        Assert.Equal("It is just past noon UTC.", text);

        using var done = JsonDocument.Parse(events[^1].Data);
        Assert.Equal("It is just past noon UTC.", done.RootElement.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Model_failures_become_502_or_an_error_event()
    {
        var model = new ScriptedChatCompletionService()
            .Then(_ => throw new HttpRequestException("connection refused"))
            .Then(_ => throw new AggregateException(new HttpRequestException("connection refused")));
        var client = CreateClient(model);

        var chat = await client.PostAsJsonAsync("/chat", new { sessionId = "fail-1", message = "hello" });
        var stream = await client.PostAsJsonAsync("/chat/stream", new { sessionId = "fail-1", message = "hello" });

        Assert.Equal(HttpStatusCode.BadGateway, chat.StatusCode);
        Assert.Contains("connection refused", await chat.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, stream.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_session_forgets_it()
    {
        var model = new ScriptedChatCompletionService().ThenAnswer("one").ThenAnswer("two");
        var client = CreateClient(model);

        await client.PostAsJsonAsync("/chat", new { sessionId = "del-1", message = "first" });
        var deleted = await client.DeleteAsync("/chat/del-1");
        var again = await client.DeleteAsync("/chat/del-1");
        await client.PostAsJsonAsync("/chat", new { sessionId = "del-1", message = "fresh start" });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal([AuthorRole.System, AuthorRole.User], model.Requests[1].Select(m => m.Role));
    }

    [Fact]
    public async Task Reviewed_answers_carry_the_review_in_the_reply_and_the_stream()
    {
        var model = new ScriptedChatCompletionService()
            .ThenCallTool("dockerfile", "lint_dockerfile_file", new { path = "docker/Dockerfile.bad" })
            .ThenAnswer("All good.")
            .ThenAnswer("""{"verdict": "revise", "issues": ["six findings were left out"], "revised_answer": "Six findings; start with DF002."}""")
            .ThenCallTool("dockerfile", "lint_dockerfile_file", new { path = "docker/Dockerfile.bad" })
            .ThenAnswer("Six findings.")
            .ThenAnswer("""{"verdict": "approve", "issues": []}""");
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("Agent:ReviewLintAnswers", "true");
            builder.ConfigureTestServices(services => services.AddSingleton<IChatCompletionService>(model));
        }).CreateClient();

        var chat = await client.PostAsJsonAsync("/chat", new { sessionId = "rev-1", message = "lint docker/Dockerfile.bad" });
        var stream = await client.PostAsJsonAsync("/chat/stream", new { sessionId = "rev-2", message = "lint docker/Dockerfile.bad" });

        using var body = JsonDocument.Parse(await chat.Content.ReadAsStringAsync());
        Assert.Equal("Six findings; start with DF002.", body.RootElement.GetProperty("reply").GetString());
        Assert.Equal("revised", body.RootElement.GetProperty("review").GetProperty("verdict").GetString());

        var events = ParseEvents(await stream.Content.ReadAsStringAsync());
        Assert.Equal(["review", "done"], events.TakeLast(2).Select(e => e.Name));
        Assert.Contains("\"verdict\":\"approved\"", events[^2].Data);
    }

    private static List<(string Name, string Data)> ParseEvents(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n'))
            .Select(lines => (
                lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..],
                lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]))
            .ToList();
}
