using System.Text.Json.Serialization;
using Agent.Core.Review;

namespace Agent.Core;

/// <summary>One tool call made while answering a message.</summary>
/// <param name="Tool">Plugin and function, for example <c>kubernetes.lint_manifest_file</c>.</param>
/// <param name="Succeeded">False when the call was refused or threw.</param>
/// <param name="Error">The error passed back to the model, when the call did not succeed.</param>
public sealed record ToolCallRecord(string Tool, bool Succeeded, string? Error = null);

/// <summary>The agent's answer to one user message.</summary>
/// <param name="Review">How the reviewer pass judged the answer; null when no review ran.</param>
public sealed record AgentReply(
    string SessionId,
    string Reply,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnswerReview? Review = null);

/// <summary>Something that happened while a streamed reply was produced.</summary>
public abstract record AgentStreamEvent;

/// <summary>A piece of the assistant's text.</summary>
public sealed record TextDelta(string Text) : AgentStreamEvent;

/// <summary>The model asked for a tool; it is about to run.</summary>
public sealed record ToolCallStarted(string Tool) : AgentStreamEvent;

/// <summary>A tool call finished.</summary>
public sealed record ToolCallFinished(ToolCallRecord Call) : AgentStreamEvent;

/// <summary>The reviewer pass finished. Streamed text was the draft; <see cref="ReplyCompleted"/> carries the answer to keep.</summary>
public sealed record ReviewCompleted(AnswerReview Review) : AgentStreamEvent;

/// <summary>The turn is over; carries the full reply.</summary>
public sealed record ReplyCompleted(AgentReply Reply) : AgentStreamEvent;

/// <summary>Raised when a chat is attempted but no chat model is configured.</summary>
public sealed class ChatModelNotConfiguredException()
    : InvalidOperationException("No chat model is configured. Set ChatModel:ApiKey for OpenAI, or ChatModel:Endpoint for an OpenAI-compatible server such as Ollama.");
