using System.Text.Json;
using Agent.Core;
using Agent.Api.OpenApi;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Agent.Api;

/// <summary>Request body for the chat endpoints. Omit <see cref="SessionId"/> to start a new session.</summary>
public sealed record ChatRequest(string? SessionId, string Message);

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/chat").WithTags("Chat");

        chat.MapPost("/", async (ChatRequest request, DevOpsAgent agent, CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await agent.ChatAsync(SessionIdFor(request), request.Message, cancellationToken));
                }
                catch (Exception ex) when (ToProblem(ex) is { } problem)
                {
                    return problem;
                }
            })
            .WithName("Chat")
            .WithSummary("Answer a message")
            .WithDescription("Runs one agent turn: the model may call tools (listed in toolCalls) before it answers. Omit sessionId to start a new session; its id is returned. With Agent:ReviewLintAnswers on, answers based on lint results carry the reviewer's verdict in review.")
            .Produces<AgentReply>()
            .ProducesChatProblems()
            .WithJsonExample(Example);

        chat.MapPost("/stream", StreamAsync)
            .WithName("ChatStream")
            .WithSummary("Answer a message as server-sent events")
            .WithDescription("Same turn as POST /chat, streamed: tool_call and tool_result while tools run, delta for each piece of text, review when the reviewer pass ran, then done with the reply (or error).")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesChatProblems()
            .WithJsonExample(Example);

        chat.MapDelete("/{sessionId}", async (string sessionId, DevOpsAgent agent, CancellationToken cancellationToken) =>
                await agent.ResetAsync(sessionId, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("ResetSession")
            .WithSummary("Forget a session")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return chat;
    }

    /// <summary>
    /// Streams one turn as server-sent events: <c>tool_call</c> and <c>tool_result</c> while tools run,
    /// <c>delta</c> for each piece of text, <c>review</c> when the reviewer pass ran (the deltas were the draft),
    /// then <c>done</c> with the reply to keep (or <c>error</c>).
    /// </summary>
    private static async Task<IResult> StreamAsync(
        ChatRequest request,
        DevOpsAgent agent,
        HttpContext http,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var json = jsonOptions.Value.SerializerOptions;
        var events = agent.StreamChatAsync(SessionIdFor(request), request.Message, cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            // Advance once before any byte is written, so configuration and validation errors
            // can still be answered with a normal status code instead of an event.
            bool hasEvent;
            try
            {
                hasEvent = await events.MoveNextAsync();
            }
            catch (Exception ex) when (ToProblem(ex) is { } problem)
            {
                return problem;
            }

            var response = http.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                while (hasEvent)
                {
                    var (name, payload) = Describe(events.Current);
                    await WriteEventAsync(response, name, JsonSerializer.Serialize(payload, json), cancellationToken);
                    hasEvent = await events.MoveNextAsync();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The client went away; the agent rolls the unfinished turn back.
            }
            catch (Exception ex) when (ChatModelFailure.IsModelFailure(ex))
            {
                var payload = JsonSerializer.Serialize(new { message = $"The chat model request failed: {ChatModelFailure.Describe(ex)}" }, json);
                await WriteEventAsync(response, "error", payload, CancellationToken.None);
            }

            return Results.Empty;
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    private static (string Name, object Payload) Describe(AgentStreamEvent streamEvent) => streamEvent switch
    {
        TextDelta delta => ("delta", new { text = delta.Text }),
        ToolCallStarted started => ("tool_call", new { tool = started.Tool }),
        ToolCallFinished finished => ("tool_result", finished.Call),
        ReviewCompleted reviewed => ("review", reviewed.Review),
        ReplyCompleted completed => ("done", completed.Reply),
        _ => throw new InvalidOperationException($"Unknown stream event {streamEvent.GetType().Name}."),
    };

    private static async Task WriteEventAsync(HttpResponse response, string name, string data, CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {name}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static readonly ChatRequest Example = new("demo", "Lint samples/helm/bad-chart and tell me what to fix first.");

    private static RouteHandlerBuilder ProducesChatProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static string SessionIdFor(ChatRequest request) =>
        string.IsNullOrEmpty(request.SessionId) ? Guid.NewGuid().ToString("N") : request.SessionId;

    private static IResult? ToProblem(Exception exception) => exception switch
    {
        ChatModelNotConfiguredException => Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
        ArgumentException => Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest),
        _ when ChatModelFailure.IsModelFailure(exception) => Results.Problem(
            $"The chat model request failed: {ChatModelFailure.Describe(exception)}", statusCode: StatusCodes.Status502BadGateway),
        _ => null,
    };
}
