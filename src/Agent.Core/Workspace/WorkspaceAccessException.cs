namespace Agent.Core.Workspace;

/// <summary>Raised when a tool asks for a file it is not allowed to read, or that cannot be read.</summary>
public sealed class WorkspaceAccessException(string message) : Exception(message);
