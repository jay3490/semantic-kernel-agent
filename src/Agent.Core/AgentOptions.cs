namespace Agent.Core;

/// <summary>Behaviour of the agent loop. Bound from the <c>Agent</c> configuration section.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public static readonly IReadOnlyList<string> DefaultAllowedTools = ["kubernetes.*", "dockerfile.*", "helm.*", "terraform.*", "logs.*", "time.*"];

    public string SystemPrompt { get; set; } =
        """
        You are a DevOps assistant. You can lint Kubernetes manifests, Dockerfiles, Helm charts and Terraform, analyze
        application logs, and look up the time, using the tools provided. File paths are relative to the workspace root.
        Base every statement about a file on a tool result: cite rule ids and line numbers, and say so when a tool
        fails. Do not invent findings. Keep answers short and suggest concrete fixes.
        """;

    /// <summary>Estimated tokens of history (system prompt included) sent with each request.</summary>
    public int HistoryTokenBudget { get; set; } = 8_000;

    /// <summary>How many rounds of tool calls the model may make before it has to answer.</summary>
    public int MaxToolRounds { get; set; } = 5;

    /// <summary>Sessions kept in memory before the least recently active one is evicted.</summary>
    public int MaxSessions { get; set; } = 500;

    /// <summary>Tools the model may call: <c>plugin.function</c>, <c>plugin.*</c> or <c>*</c>. Null means <see cref="DefaultAllowedTools"/>.</summary>
    public string[]? AllowedTools { get; set; }

    public IReadOnlyList<string> EffectiveAllowedTools => AllowedTools ?? DefaultAllowedTools;
}
