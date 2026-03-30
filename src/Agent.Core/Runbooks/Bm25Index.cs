using System.Text.RegularExpressions;

namespace Agent.Core.Runbooks;

/// <summary>
/// Okapi BM25 keyword ranking over a fixed set of documents. Used when no embedding model is configured, or when
/// it fails, so runbook search always works offline.
/// </summary>
public sealed partial class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "do", "does", "for", "from", "how", "i", "if", "in", "is",
        "it", "its", "my", "of", "on", "or", "our", "so", "that", "the", "then", "this", "to", "was", "we", "what",
        "when", "where", "which", "why", "with", "you", "your",
    };

    private readonly Dictionary<string, int>[] _termFrequencies;
    private readonly Dictionary<string, int> _documentFrequencies = new(StringComparer.Ordinal);
    private readonly int[] _lengths;
    private readonly double _averageLength;

    public Bm25Index(IReadOnlyList<string> documents)
    {
        _termFrequencies = new Dictionary<string, int>[documents.Count];
        _lengths = new int[documents.Count];

        for (var i = 0; i < documents.Count; i++)
        {
            var terms = Tokenize(documents[i]);
            _lengths[i] = terms.Count;
            _termFrequencies[i] = terms.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var term in _termFrequencies[i].Keys)
            {
                _documentFrequencies[term] = _documentFrequencies.GetValueOrDefault(term) + 1;
            }
        }

        _averageLength = documents.Count == 0 ? 0 : Math.Max(1, _lengths.Average());
    }

    public int Count => _lengths.Length;

    /// <summary>Documents that share at least one term with the query, best first.</summary>
    public IReadOnlyList<(int Document, double Score)> Search(string query, int top)
    {
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        var scores = new List<(int, double)>();

        for (var i = 0; i < _termFrequencies.Length; i++)
        {
            var score = 0.0;
            foreach (var term in terms)
            {
                if (!_termFrequencies[i].TryGetValue(term, out var frequency))
                {
                    continue;
                }

                var documents = _documentFrequencies[term];
                var idf = Math.Log(1 + ((Count - documents + 0.5) / (documents + 0.5)));
                score += idf * (frequency * (K1 + 1)) / (frequency + (K1 * (1 - B + (B * _lengths[i] / _averageLength))));
            }

            if (score > 0)
            {
                scores.Add((i, score));
            }
        }

        return scores.OrderByDescending(s => s.Item2).ThenBy(s => s.Item1).Take(top).ToArray();
    }

    /// <summary>Lower-case word and number tokens, without stop words and one-character tokens.</summary>
    public static List<string> Tokenize(string text) =>
        Word().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .ToList();

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();
}
