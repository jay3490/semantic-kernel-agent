using Microsoft.SemanticKernel;

namespace Agent.Core.Filters;

/// <summary>Raised by <see cref="ToolInvocationFilter"/> when a function outside the allow-list is invoked.</summary>
public sealed class ToolNotAllowedException(string toolName)
    : KernelException($"The tool '{toolName}' is not on the allow-list.")
{
    public string ToolName { get; } = toolName;
}
