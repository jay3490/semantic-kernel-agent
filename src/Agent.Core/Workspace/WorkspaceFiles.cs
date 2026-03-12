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

    /// <summary>Most files <see cref="ListFiles"/> returns, so a huge tree cannot flood a tool result.</summary>
    public const int MaxListedFiles = 1_000;

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

    /// <summary>
    /// Lists the files under a directory of the workspace (<c>.</c> for the root itself), recursively, as paths
    /// relative to the root with <c>/</c> separators, in ordinal order. Links, and directories that are links, are
    /// skipped. At most <see cref="MaxListedFiles"/> files are returned.
    /// </summary>
    /// <param name="relativeDirectory">Directory relative to the root.</param>
    /// <param name="extensions">Extensions to keep, such as <c>.md</c>; none keeps every file.</param>
    public IReadOnlyList<string> ListFiles(string relativeDirectory, params string[] extensions)
    {
        var directory = new DirectoryInfo(ResolveDirectory(relativeDirectory));
        if (!directory.Exists)
        {
            throw new WorkspaceAccessException($"'{relativeDirectory}' is not a directory in the workspace.");
        }

        if (directory.LinkTarget is not null)
        {
            throw new WorkspaceAccessException($"'{relativeDirectory}' is a link; links are not followed.");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
        };

        return directory.EnumerateFiles("*", options)
            .Where(file => extensions.Length == 0 || extensions.Any(ext => file.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Select(file => Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .Take(MaxListedFiles)
            .ToArray();
    }

    /// <summary>True when <paramref name="relativeDirectory"/> is an existing directory inside the root.</summary>
    public bool DirectoryExists(string relativeDirectory) => Directory.Exists(ResolveDirectory(relativeDirectory));

    /// <summary>
    /// The canonical form of a directory path inside the root, as <see cref="ListFiles"/> prints it: relative,
    /// <c>/</c> separated, with a trailing <c>/</c>, or empty for the root itself.
    /// </summary>
    public string DirectoryPrefix(string relativeDirectory)
    {
        var relative = Path.GetRelativePath(_root, ResolveDirectory(relativeDirectory)).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? string.Empty : relative + "/";
    }

    /// <summary>Like <see cref="Resolve"/>, but the root itself is allowed.</summary>
    private string ResolveDirectory(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory) || Path.IsPathRooted(relativeDirectory))
        {
            return Resolve(relativeDirectory);
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, relativeDirectory)));
        return string.Equals(fullPath, _root, PathComparison) ? _root : Resolve(relativeDirectory);
    }
}
