namespace Agent.Core.Runbooks;

/// <summary>Where runbooks are and how they are searched. Bound from the <c>Runbooks</c> configuration section.</summary>
public sealed class RunbookOptions
{
    public const string SectionName = "Runbooks";

    /// <summary>Folder of markdown runbooks, relative to the workspace root. Searched recursively.</summary>
    public string Directory { get; set; } = "samples/runbooks";

    /// <summary>
    /// Lowest cosine similarity a chunk needs to count as a semantic match. When nothing reaches it, the search
    /// falls back to keyword ranking.
    /// </summary>
    public double MinSimilarity { get; set; } = 0.3;

    /// <summary>Longest snippet returned per hit, in characters.</summary>
    public int MaxSnippetCharacters { get; set; } = 600;
}

/// <summary>
/// An OpenAI-compatible embedding model for runbook search. Bound from the <c>Embeddings</c> section. Endpoint and
/// key default to the chat model's, so one Ollama or OpenAI configuration covers both.
/// </summary>
public sealed class EmbeddingModelOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>For example <c>text-embedding-3-small</c> or <c>nomic-embed-text</c>. Empty means keyword search only.</summary>
    public string? ModelId { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }
}
