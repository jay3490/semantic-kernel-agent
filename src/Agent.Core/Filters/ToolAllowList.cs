namespace Agent.Core.Filters;

/// <summary>
/// Decides which kernel functions may run. Entries are <c>plugin.function</c>, <c>plugin.*</c> or <c>*</c>,
/// compared case-insensitively. An empty list allows nothing.
/// </summary>
public sealed class ToolAllowList
{
    private readonly bool _allowAll;
    private readonly HashSet<string> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _functions = new(StringComparer.OrdinalIgnoreCase);

    public ToolAllowList(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry == "*")
            {
                _allowAll = true;
            }
            else if (entry.EndsWith(".*", StringComparison.Ordinal))
            {
                _plugins.Add(entry[..^2]);
            }
            else if (entry.Contains('.'))
            {
                _functions.Add(entry);
            }
            else if (entry.Length > 0)
            {
                throw new ArgumentException($"Allow-list entry '{entry}' must be 'plugin.function', 'plugin.*' or '*'.", nameof(entries));
            }
        }
    }

    public bool IsAllowed(string? pluginName, string functionName) =>
        _allowAll ||
        (pluginName is not null && (_plugins.Contains(pluginName) || _functions.Contains($"{pluginName}.{functionName}")));
}
