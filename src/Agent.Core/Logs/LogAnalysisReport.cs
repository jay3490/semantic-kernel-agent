namespace Agent.Core.Logs;

/// <summary>Error entries that share a signature once variable parts (ids, numbers, addresses) are masked.</summary>
/// <param name="Signature">The normalized message, for example <c>payment timeout after &lt;n&gt; for order &lt;n&gt;</c>.</param>
/// <param name="Count">How many entries have this signature.</param>
/// <param name="FirstLine">1-based line of the first occurrence.</param>
/// <param name="LastLine">1-based line of the last occurrence.</param>
/// <param name="Example">The first occurrence as it appeared in the log.</param>
public sealed record ErrorGroup(string Signature, int Count, int FirstLine, int LastLine, string Example);

/// <summary>Summary of a log: entry counts per level and the most frequent error signatures.</summary>
/// <param name="TotalLines">Physical lines in the input, including continuation lines such as stack frames.</param>
/// <param name="Entries">Log entries after continuation lines were folded into the entry they belong to.</param>
/// <param name="Levels">Entries per normalized level (TRACE, DEBUG, INFO, WARN, ERROR, FATAL, UNKNOWN).</param>
/// <param name="ErrorEntries">Entries at ERROR or FATAL.</param>
/// <param name="DistinctErrorSignatures">Number of different error signatures.</param>
/// <param name="TopErrors">The most frequent error signatures, most frequent first.</param>
public sealed record LogAnalysisReport(
    int TotalLines,
    int Entries,
    IReadOnlyDictionary<string, int> Levels,
    int ErrorEntries,
    int DistinctErrorSignatures,
    IReadOnlyList<ErrorGroup> TopErrors);
