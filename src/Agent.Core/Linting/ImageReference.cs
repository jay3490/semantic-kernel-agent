namespace Agent.Core.Linting;

/// <summary>The parts of a container image reference that matter for pinning: tag and digest.</summary>
public readonly record struct ImageReference(string Name, string? Tag, string? Digest)
{
    /// <summary>True when the reference is pinned by digest, or has an explicit tag other than <c>latest</c>.</summary>
    public bool IsPinned => Digest is not null || (Tag is not null && !Tag.Equals("latest", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits <c>registry:port/repo/name:tag@sha256:...</c> into name, tag and digest. A colon only marks a tag
    /// when it comes after the last slash, so a registry port such as <c>localhost:5000/app</c> is not a tag.
    /// </summary>
    public static ImageReference Parse(string reference)
    {
        var value = reference.Trim();
        string? digest = null;

        var at = value.IndexOf('@');
        if (at >= 0)
        {
            digest = value[(at + 1)..];
            value = value[..at];
        }

        var lastSlash = value.LastIndexOf('/');
        var colon = value.IndexOf(':', lastSlash + 1);
        return colon >= 0
            ? new ImageReference(value[..colon], value[(colon + 1)..], digest)
            : new ImageReference(value, null, digest);
    }

    /// <summary>Explains why an unpinned reference is unpinned; only meaningful when <see cref="IsPinned"/> is false.</summary>
    public string DescribeUnpinned() => Tag is null
        ? $"'{Name}' has no tag, so it resolves to 'latest'"
        : $"'{Name}:{Tag}' uses the 'latest' tag";
}
