using Microsoft.Extensions.Options;

namespace Agent.Core.Workspace;

/// <summary>
/// Read-only access to files under a single root directory. The model chooses the paths the tools read,
/// so every path is resolved, checked to stay inside the root, and size-limited before it is opened.
/// </summary>
public sealed class WorkspaceFiles
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _root;
    private readonly long _maxBytes;

    public WorkspaceFiles(IOptions<WorkspaceOptions> options)
    {
        var value = options.Value;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Root));
        _maxBytes = value.MaxFileBytes;
    }

    /// <summary>Absolute path of the workspace root.</summary>
    public string Root => _root;

    /// <summary>Resolves <paramref name="relativePath"/> against the root and returns the full path if it stays inside it.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new WorkspaceAccessException("A file path is required.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new WorkspaceAccessException($"'{relativePath}' is absolute; give a path relative to the workspace root.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new WorkspaceAccessException($"'{relativePath}' resolves outside the workspace root.");
        }

        return fullPath;
    }

    /// <summary>Reads a text file under the root. Missing files, files that are themselves links, and files over the size limit are refused.</summary>
    public async Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Resolve(relativePath);
        var file = new FileInfo(fullPath);

        if (!file.Exists)
        {
            throw new WorkspaceAccessException($"'{relativePath}' does not exist in the workspace.");
        }

        if (file.LinkTarget is not null)
        {
            throw new WorkspaceAccessException($"'{relativePath}' is a link; links are not followed.");
        }

        if (file.Length > _maxBytes)
        {
            throw new WorkspaceAccessException($"'{relativePath}' is {file.Length} bytes; the limit is {_maxBytes} bytes.");
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }
}
