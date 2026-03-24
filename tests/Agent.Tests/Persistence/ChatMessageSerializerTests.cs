using Agent.Persistence;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Persistence;

public sealed class ChatMessageSerializerTests
{
    private static ChatMessageContent RoundTrip(ChatMessageContent message) =>
        ChatMessageSerializer.Deserialize(ChatMessageSerializer.Serialize(message));

    [Fact]
    public void Text_messages_keep_role_text_and_author()
    {
        var copy = RoundTrip(new ChatMessageContent(AuthorRole.User, "lint my chart") { AuthorName = "jay" });

        Assert.Equal(AuthorRole.User, copy.Role);
        Assert.Equal("lint my chart", copy.Content);
        Assert.Equal("jay", copy.AuthorName);
    }

    [Fact]
    public void Tool_calls_keep_their_id_function_and_arguments()
    {
        var call = new FunctionCallContent("lint_helm_chart", "helm", "call-7", new KernelArguments { ["path"] = "samples/helm/bad-chart", ["top"] = 3 });

        var copy = Assert.IsType<FunctionCallContent>(Assert.Single(RoundTrip(new ChatMessageContent(AuthorRole.Assistant, [call])).Items));

        Assert.Equal(("lint_helm_chart", "helm", "call-7"), (copy.FunctionName, copy.PluginName, copy.Id));
        Assert.Equal("samples/helm/bad-chart", copy.Arguments!["path"]);
        Assert.Equal("3", copy.Arguments["top"]!.ToString());
    }

    [Fact]
    public void Tool_results_keep_the_call_id_and_text()
    {
        var call = new FunctionCallContent("utc_now", "time", "call-1");
        var message = new FunctionResultContent(call, "\"2026-03-14T12:00:00Z\"").ToChatMessage();

        var copy = RoundTrip(message);

        Assert.Equal(AuthorRole.Tool, copy.Role);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(copy.Items));
        Assert.Equal(("call-1", "utc_now", "time"), (result.CallId, result.FunctionName, result.PluginName));
        Assert.Equal("\"2026-03-14T12:00:00Z\"", result.Result);
    }

    [Fact]
    public void Connector_metadata_is_not_stored()
    {
        var message = new ChatMessageContent(AuthorRole.Assistant, "done", metadata: new Dictionary<string, object?> { ["Usage"] = new object() });

        Assert.DoesNotContain("Usage", ChatMessageSerializer.Serialize(message));
    }
}
