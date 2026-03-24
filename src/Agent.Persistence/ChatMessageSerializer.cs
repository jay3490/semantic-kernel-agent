using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Persistence;

/// <summary>
/// Converts chat messages to and from a small JSON shape that keeps what the agent loop needs (role, text, tool
/// calls with their ids and arguments, tool results) and drops connector metadata, which holds SDK types that do
/// not round-trip and is never sent back to a model.
/// </summary>
public static class ChatMessageSerializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ChatMessageContent message)
    {
        var items = message.Items.Select(ToStored).OfType<StoredItem>().ToList();
        if (items.Count == 0 && !string.IsNullOrEmpty(message.Content))
        {
            items.Add(new StoredItem { Type = "text", Text = message.Content });
        }

        return JsonSerializer.Serialize(new StoredContent { Role = message.Role.Label, Name = message.AuthorName, Items = items }, Json);
    }

    public static ChatMessageContent Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredContent>(json, Json) ?? throw new JsonException("A stored message is empty.");
        var items = new ChatMessageContentItemCollection();
        foreach (var item in stored.Items)
        {
            items.Add(item.Type switch
            {
                "text" => new TextContent(item.Text),
                "call" => new FunctionCallContent(item.Function!, item.Plugin, item.CallId, ToArguments(item.Arguments)),
                "result" => new FunctionResultContent(item.Function, item.Plugin, item.CallId, item.Result),
                _ => throw new JsonException($"Unknown stored message item type '{item.Type}'."),
            });
        }

        return new ChatMessageContent(new AuthorRole(stored.Role), items) { AuthorName = stored.Name };
    }

    private static StoredItem? ToStored(KernelContent content) => content switch
    {
        TextContent text => new StoredItem { Type = "text", Text = text.Text },
        FunctionCallContent call => new StoredItem
        {
            Type = "call",
            CallId = call.Id,
            Plugin = call.PluginName,
            Function = call.FunctionName,
            Arguments = call.Arguments?.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value, Json)),
        },
        FunctionResultContent result => new StoredItem
        {
            Type = "result",
            CallId = result.CallId,
            Plugin = result.PluginName,
            Function = result.FunctionName,
            Result = result.Result switch
            {
                null => null,
                string text => text,
                var value => JsonSerializer.Serialize(value, Json),
            },
        },
        _ => null,
    };

    private static KernelArguments? ToArguments(Dictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var result = new KernelArguments();
        foreach (var (name, value) in arguments)
        {
            result[name] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => null,
                _ => value,
            };
        }

        return result;
    }

    private sealed class StoredContent
    {
        public required string Role { get; init; }

        public string? Name { get; init; }

        public List<StoredItem> Items { get; init; } = [];
    }

    private sealed class StoredItem
    {
        public required string Type { get; init; }

        public string? Text { get; init; }

        public string? CallId { get; init; }

        public string? Plugin { get; init; }

        public string? Function { get; init; }

        public Dictionary<string, JsonElement>? Arguments { get; init; }

        public string? Result { get; init; }
    }
}
