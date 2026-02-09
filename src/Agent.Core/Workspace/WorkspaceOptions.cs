namespace Agent.Core.Workspace;

/// <summary>Limits for the files the tools are allowed to read.</summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    /// <summary>Directory that every file path given to a tool is resolved against. Paths outside it are rejected.</summary>
    public string Root { get; set; } = ".";

    /// <summary>Largest file, in bytes, a tool will read. Also used as the request body limit for the lint endpoints.</summary>
    public long MaxFileBytes { get; set; } = 1_048_576;
}
