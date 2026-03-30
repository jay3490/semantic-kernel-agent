using System.ClientModel;
using Agent.Core.Filters;
using Agent.Core.History;
using Agent.Core.Linting;
using Agent.Core.Linting.Helm;
using Agent.Core.Linting.Terraform;
using Agent.Core.Logs;
using Agent.Core.Plugins;
using Agent.Core.Runbooks;
using Agent.Core.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenAI;

namespace Agent.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugins, the tool filter, session storage, the kernel and <see cref="DevOpsAgent"/>, plus an
    /// OpenAI-compatible chat model when the <c>ChatModel</c> section configures one.
    /// </summary>
    public static IServiceCollection AddDevOpsAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .Validate(o => o.HistoryTokenBudget > 0, "Agent:HistoryTokenBudget must be positive.")
            .Validate(o => o.MaxToolRounds >= 0, "Agent:MaxToolRounds cannot be negative.")
            .Validate(o => o.MaxSessions > 0, "Agent:MaxSessions must be positive.")
            .ValidateOnStart();
        services.AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName))
            .Validate(o => Directory.Exists(o.Root), "Workspace:Root must be an existing directory.")
            .Validate(o => o.MaxFileBytes > 0, "Workspace:MaxFileBytes must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WorkspaceFiles>();

        services.AddSingleton<KubernetesManifestLinter>();
        services.AddSingleton<DockerfileLinter>();
        services.AddSingleton<HelmChartLinter>();
        services.AddSingleton<TerraformLinter>();
        services.AddSingleton<GitHubActionsLinter>();
        services.AddSingleton<LogAnalyzer>();
        services.AddOptions<RunbookOptions>()
            .Bind(configuration.GetSection(RunbookOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Directory), "Runbooks:Directory is required.")
            .Validate(o => o.MinSimilarity is >= -1 and <= 1, "Runbooks:MinSimilarity must be between -1 and 1.")
            .Validate(o => o.MaxSnippetCharacters > 0, "Runbooks:MaxSnippetCharacters must be positive.")
            .ValidateOnStart();
        services.AddSingleton<RunbookSearchService>();

        services.AddSingleton<KubernetesManifestPlugin>();
        services.AddSingleton<DockerfilePlugin>();
        services.AddSingleton<HelmChartPlugin>();
        services.AddSingleton<TerraformPlugin>();
        services.AddSingleton<GitHubActionsPlugin>();
        services.AddSingleton<LogAnalysisPlugin>();
        services.AddSingleton<RunbookPlugin>();
        services.AddSingleton<TimePlugin>();
        AddPlugin<KubernetesManifestPlugin>(services, KubernetesManifestPlugin.PluginName);
        AddPlugin<DockerfilePlugin>(services, DockerfilePlugin.PluginName);
        AddPlugin<HelmChartPlugin>(services, HelmChartPlugin.PluginName);
        AddPlugin<TerraformPlugin>(services, TerraformPlugin.PluginName);
        AddPlugin<GitHubActionsPlugin>(services, GitHubActionsPlugin.PluginName);
        AddPlugin<LogAnalysisPlugin>(services, LogAnalysisPlugin.PluginName);
        AddPlugin<RunbookPlugin>(services, RunbookPlugin.PluginName);
        AddPlugin<TimePlugin>(services, TimePlugin.PluginName);

        services.AddSingleton(sp => new ToolAllowList(sp.GetRequiredService<IOptions<AgentOptions>>().Value.EffectiveAllowedTools));
        services.AddSingleton<IFunctionInvocationFilter, ToolInvocationFilter>();

        services.TryAddSingleton<ITokenCounter, ApproximateTokenCounter>();
        services.AddSingleton<TokenBudgetHistoryTrimmer>();
        services.TryAddSingleton<IChatSessionStore>(sp => new InMemoryChatSessionStore(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<AgentOptions>>().Value.MaxSessions));

        services.AddKernel();
        services.AddTransient<DevOpsAgent>();

        var model = configuration.GetSection(ChatModelOptions.SectionName).Get<ChatModelOptions>() ?? new ChatModelOptions();
        if (model.IsConfigured)
        {
            services.AddOpenAIChatCompletion(model.ModelId, CreateClient(model));
        }

        // Runbook search uses embeddings when a model is named; endpoint and key fall back to the chat model's.
        var embedding = configuration.GetSection(EmbeddingModelOptions.SectionName).Get<EmbeddingModelOptions>() ?? new EmbeddingModelOptions();
        var embeddingEndpoint = string.IsNullOrWhiteSpace(embedding.Endpoint) ? model.Endpoint : embedding.Endpoint;
        var embeddingKey = string.IsNullOrWhiteSpace(embedding.ApiKey) ? model.ApiKey : embedding.ApiKey;
        if (!string.IsNullOrWhiteSpace(embedding.ModelId) &&
            (!string.IsNullOrWhiteSpace(embeddingEndpoint) || !string.IsNullOrWhiteSpace(embeddingKey)))
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                CreateClient(embeddingEndpoint, embeddingKey).GetEmbeddingClient(embedding.ModelId).AsIEmbeddingGenerator());
        }

        return services;
    }

    /// <summary>
    /// Builds an OpenAI SDK client for api.openai.com or any OpenAI-compatible endpoint. Local servers such as
    /// Ollama ignore the key, but the SDK requires one, so a placeholder is used when none is configured.
    /// </summary>
    public static OpenAIClient CreateClient(ChatModelOptions model) => CreateClient(model.Endpoint, model.ApiKey);

    private static OpenAIClient CreateClient(string? endpoint, string? apiKey)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            clientOptions.Endpoint = new Uri(endpoint);
        }

        var key = string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey;
        return new OpenAIClient(new ApiKeyCredential(key), clientOptions);
    }

    private static void AddPlugin<TPlugin>(IServiceCollection services, string pluginName)
        where TPlugin : class =>
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(sp.GetRequiredService<TPlugin>(), pluginName));
}
