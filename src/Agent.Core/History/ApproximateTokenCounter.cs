using System.Text.Json;
using Microsoft.SemanticKernel;

namespace Agent.Core.History;

/// <summary>
/// Model-independent estimate: roughly four characters per token for English text and JSON, plus a fixed
/// per-message overhead for role and framing. It does not match any tokenizer exactly; it only needs to be
/// consistent so the history stays well inside the model's context window.
/// </summary>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    public const int CharactersPerToken = 4;
    public const int PerMessageOverhead = 4;

    public int Count(ChatMessageContent message)
    {
        var characters = 0;

        if (message.Items.Count == 0)
        {
            characters += message.Content?.Length ?? 0;
        }

        foreach (var item in message.Items)
        {
            characters += item switch
            {
                TextContent text => text.Text?.Length ?? 0,
                FunctionCallContent call => call.FunctionName.Length + (call.PluginName?.Length ?? 0) +
                                            (call.Arguments is null ? 0 : JsonSerializer.Serialize(call.Arguments).Length),
                FunctionResultContent result => result.Result switch
                {
                    null => 0,
                    string s => s.Length,
                    var other => JsonSerializer.Serialize(other).Length,
                },
                _ => 0,
            };
        }

        return PerMessageOverhead + ((characters + CharactersPerToken - 1) / CharactersPerToken);
    }
}
