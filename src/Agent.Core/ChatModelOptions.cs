namespace Agent.Core;

/// <summary>
/// Which OpenAI-compatible chat model to use. Bound from the <c>ChatModel</c> configuration section.
/// Leave <see cref="Endpoint"/> empty for api.openai.com, or point it at any OpenAI-compatible server,
/// for example Ollama at <c>http://localhost:11434/v1</c>.
/// </summary>
public sealed class ChatModelOptions
{
    public const string SectionName = "ChatModel";

    public string ModelId { get; set; } = "gpt-4o-mini";

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>A model is usable when there is a key (OpenAI) or an endpoint (a local server that needs no key).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Endpoint);
}
