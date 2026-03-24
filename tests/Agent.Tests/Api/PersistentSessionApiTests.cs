using System.Net.Http.Json;
using Agent.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Api;

public sealed class PersistentSessionApiTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("api-sessions-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Each call starts a new application over the same database file: a restart.</summary>
    private WebApplicationFactory<Program> StartApi(ScriptedChatCompletionService model) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Workspace:Root", Samples.Root);
            builder.UseSetting("ChatModel:ApiKey", "");
            builder.UseSetting("ChatModel:Endpoint", "");
            builder.UseSetting("Sessions:Provider", "Sqlite");
            builder.UseSetting("Sessions:ConnectionString", $"Data Source={Path.Combine(_directory, "sessions.db")}");
            builder.ConfigureTestServices(services => services.AddSingleton<IChatCompletionService>(model));
        });

    [Fact]
    public async Task A_conversation_continues_after_a_restart()
    {
        var firstModel = new ScriptedChatCompletionService()
            .ThenCallTool("dockerfile", "lint_dockerfile_file", new { path = "docker/Dockerfile.bad" })
            .ThenAnswer("Six findings.");
        await using (var api = StartApi(firstModel))
        {
            var response = await api.CreateClient().PostAsJsonAsync("/chat", new { sessionId = "keep-me", message = "lint docker/Dockerfile.bad" });
            response.EnsureSuccessStatusCode();
        }

        var secondModel = new ScriptedChatCompletionService().ThenAnswer("The first one is DF001.");
        await using (var api = StartApi(secondModel))
        {
            var response = await api.CreateClient().PostAsJsonAsync("/chat", new { sessionId = "keep-me", message = "which first?" });
            response.EnsureSuccessStatusCode();
        }

        var history = secondModel.Requests.Single();
        Assert.Equal(
            [AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.Tool, AuthorRole.Assistant, AuthorRole.User],
            history.Select(m => m.Role));
        var toolResult = Assert.IsType<FunctionResultContent>(history[3].Items.Single());
        Assert.Contains("DF001", (string)toolResult.Result!);
    }
}
