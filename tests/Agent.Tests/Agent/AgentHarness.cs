using Agent.Core;
using Agent.Core.Filters;
using Agent.Core.History;
using Agent.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Agent;

/// <summary>Builds the agent exactly as the API does, with the scripted model in place of a real one.</summary>
internal sealed class AgentHarness
{
    public AgentHarness(IDictionary<string, string?>? settings = null, bool withModel = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:Root"] = Samples.Root })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILogger<ToolInvocationFilter>>(FilterLog);
        services.AddDevOpsAgent(configuration);
        if (withModel)
        {
            services.AddSingleton<IChatCompletionService>(Model);
        }

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public ScriptedChatCompletionService Model { get; } = new();

    public ListLogger<ToolInvocationFilter> FilterLog { get; } = new();

    public ServiceProvider Services { get; }

    public DevOpsAgent Agent => Services.GetRequiredService<DevOpsAgent>();

    public ChatHistory History(string sessionId) =>
        Services.GetRequiredService<IChatSessionStore>().GetOrCreate(sessionId, "unused").History;
}
