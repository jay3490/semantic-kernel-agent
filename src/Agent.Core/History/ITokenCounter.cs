using Microsoft.SemanticKernel;

namespace Agent.Core.History;

/// <summary>Estimates how many prompt tokens a chat message costs.</summary>
public interface ITokenCounter
{
    int Count(ChatMessageContent message);
}
