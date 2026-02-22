using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.History;

/// <summary>
/// Keeps a chat history inside a token budget by dropping the oldest whole turns.
/// </summary>
/// <remarks>
/// A turn starts at a user message and runs up to the next one, so an assistant tool call is never separated
/// from its tool results and the retained history always starts with a user message. Leading system messages
/// are always kept, and so is the latest turn, even if that alone is over budget.
/// </remarks>
public sealed class TokenBudgetHistoryTrimmer(ITokenCounter counter)
{
    /// <summary>Removes the oldest turns from <paramref name="history"/> until it fits <paramref name="tokenBudget"/>.</summary>
    /// <returns>The number of messages removed.</returns>
    public int Trim(ChatHistory history, int tokenBudget)
    {
        var systemCount = 0;
        while (systemCount < history.Count && history[systemCount].Role == AuthorRole.System)
        {
            systemCount++;
        }

        var turnStarts = new List<int>();
        for (var i = systemCount; i < history.Count; i++)
        {
            if (history[i].Role == AuthorRole.User)
            {
                turnStarts.Add(i);
            }
        }

        if (turnStarts.Count == 0)
        {
            return 0;
        }

        var costs = history.Select(counter.Count).ToArray();
        var systemCost = costs.Take(systemCount).Sum();

        // suffix[i] = cost of history[i..]
        var suffix = new int[history.Count + 1];
        for (var i = history.Count - 1; i >= 0; i--)
        {
            suffix[i] = suffix[i + 1] + costs[i];
        }

        if (systemCost + suffix[systemCount] <= tokenBudget)
        {
            return 0;
        }

        var cut = turnStarts.FirstOrDefault(start => start > systemCount && systemCost + suffix[start] <= tokenBudget, turnStarts[^1]);
        var removed = cut - systemCount;
        history.RemoveRange(systemCount, removed);
        return removed;
    }

    /// <summary>Estimated token cost of the whole history.</summary>
    public int Count(ChatHistory history) => history.Sum(counter.Count);
}
