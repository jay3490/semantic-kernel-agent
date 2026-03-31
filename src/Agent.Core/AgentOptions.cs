namespace Agent.Core;

/// <summary>Behaviour of the agent loop. Bound from the <c>Agent</c> configuration section.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public static readonly IReadOnlyList<string> DefaultAllowedTools = ["kubernetes.*", "dockerfile.*", "helm.*", "terraform.*", "github_actions.*", "logs.*", "runbooks.*", "time.*"];

    public string SystemPrompt { get; set; } =
        """
        You are a DevOps assistant. You can lint Kubernetes manifests, Dockerfiles, Helm charts, Terraform and
        GitHub Actions workflows, analyze application logs, search the team's runbooks, and look up the time, using
        the tools provided. File paths are relative to the workspace root.
        Base every statement about a file on a tool result: cite rule ids and line numbers, and say so when a tool
        fails. Do not invent findings. When you recommend a fix or an incident step, search the runbooks first and
        cite the runbook sections you use. Keep answers short and suggest concrete fixes.
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

    /// <summary>
    /// When true, an answer written after lint tools ran is checked by a second, tool-less reviewer pass against
    /// the raw lint results before it is returned. Costs one more model call on those turns.
    /// </summary>
    public bool ReviewLintAnswers { get; set; }

    /// <summary>System message for the reviewer pass.</summary>
    public string ReviewerPrompt { get; set; } =
        """
        You review a DevOps assistant's answer before it is sent. You get the user's question, the assistant's
        draft answer, and the raw JSON results of the lint tools it called. Check every statement about findings
        against the raw results: rule ids, severities, line numbers, files, counts, and whether the input passed.
        Flag findings the draft claims that are not in the results, details it misquotes, and errors it leaves out.
        Do not add advice the results do not support.
        Reply with only a JSON object:
        {"verdict": "approve" or "revise", "issues": ["..."], "revised_answer": "..."}
        Use "approve" with an empty issues list when the draft is accurate. With "revise", revised_answer is the
        corrected answer, written for the user in the same style as the draft.
        """;
}
