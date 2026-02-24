using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Fakes;

/// <summary>
/// A chat model stand-in that plays back scripted responses in order. Each step sees the history it was sent,
/// so a step can check that the previous tool result arrived before it answers. No network is involved.
/// </summary>
internal sealed class ScriptedChatCompletionService : IChatCompletionService
{
    private readonly Queue<Func<ChatHistory, ChatMessageContent>> _steps = new();

    /// <summary>Snapshot of the history sent with each request.</summary>
    public List<IReadOnlyList<ChatMessageContent>> Requests { get; } = [];

    /// <summary>Settings sent with each request.</summary>
    public List<PromptExecutionSettings?> Settings { get; } = [];

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public ScriptedChatCompletionService ThenCallTool(string plugin, string function, object? arguments = null, string? callId = null) =>
        Then(_ => ToolCall(plugin, function, arguments, callId ?? $"call-{_steps.Count + 1}"));

    public ScriptedChatCompletionService ThenAnswer(string text) => Then(_ => new ChatMessageContent(AuthorRole.Assistant, text));

    public ScriptedChatCompletionService Then(Func<ChatHistory, ChatMessageContent> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessageContent> response = [NextResponse(chatHistory, executionSettings)];
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = NextResponse(chatHistory, executionSettings);

        // Text arrives in small chunks, the way a real model streams it.
        var text = response.Content ?? string.Empty;
        for (var i = 0; i < text.Length; i += 8)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, text.Substring(i, Math.Min(8, text.Length - i)));
        }

        // Tool calls arrive as a name update followed by the arguments split across two updates.
        var index = 0;
        foreach (var call in response.Items.OfType<FunctionCallContent>())
        {
            var arguments = JsonSerializer.Serialize(call.Arguments?.ToDictionary(a => a.Key, a => a.Value) ?? []);
            var half = arguments.Length / 2;
            yield return Update(new StreamingFunctionCallUpdateContent(call.Id, $"{call.PluginName}-{call.FunctionName}", functionCallIndex: index));
            yield return Update(new StreamingFunctionCallUpdateContent(arguments: arguments[..half], functionCallIndex: index));
            yield return Update(new StreamingFunctionCallUpdateContent(arguments: arguments[half..], functionCallIndex: index));
            index++;
        }
    }

    private ChatMessageContent NextResponse(ChatHistory history, PromptExecutionSettings? settings)
    {
        Requests.Add(history.ToList());
        Settings.Add(settings);

        if (!_steps.TryDequeue(out var step))
        {
            throw new InvalidOperationException($"The script has no response left for request {Requests.Count}.");
        }

        return step(history);
    }

    private static ChatMessageContent ToolCall(string plugin, string function, object? arguments, string callId)
    {
        var kernelArguments = new KernelArguments();
        if (arguments is not null)
        {
            foreach (var property in arguments.GetType().GetProperties())
            {
                kernelArguments[property.Name] = property.GetValue(arguments);
            }
        }

        return new ChatMessageContent(AuthorRole.Assistant, [new FunctionCallContent(function, plugin, callId, kernelArguments)]);
    }

    private static StreamingChatMessageContent Update(StreamingFunctionCallUpdateContent content) =>
        new(AuthorRole.Assistant, content: null) { Items = [content] };
}
