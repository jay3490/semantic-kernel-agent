using System.ClientModel;
using Microsoft.SemanticKernel;

namespace Agent.Core;

/// <summary>
/// Recognises errors that mean the chat model could not be reached or refused the request, as opposed to bugs.
/// The OpenAI connector surfaces these differently depending on the call: <see cref="HttpOperationException"/>,
/// <see cref="ClientResultException"/>, <see cref="HttpRequestException"/>, or an <see cref="AggregateException"/>
/// of them once the SDK's retries are exhausted.
/// </summary>
public static class ChatModelFailure
{
    public static bool IsModelFailure(Exception exception) => exception switch
    {
        HttpOperationException or ClientResultException or HttpRequestException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsModelFailure),
        _ => false,
    };

    /// <summary>A one-line description, without the retry wrapper's repetition.</summary>
    public static string Describe(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? Describe(aggregate.InnerExceptions[0])
            : exception.Message;
}
