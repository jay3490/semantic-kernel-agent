using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using Agent.Core.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Runbooks;

/// <summary>A runbook section that matched a search.</summary>
/// <param name="Citation">Where the text comes from, as <c>file:line (heading)</c>, for the answer to cite.</param>
/// <param name="Score">Cosine similarity in semantic mode, BM25 score in keyword mode; only comparable within one result.</param>
public sealed record RunbookHit(string Citation, string File, int Line, string Heading, double Score, string Snippet);

/// <summary>The outcome of a runbook search.</summary>
/// <param name="Mode"><c>semantic</c> when ranked by embeddings, <c>keyword</c> when ranked by BM25.</param>
/// <param name="Note">Why keyword ranking was used although an embedding model is configured, if it was.</param>
public sealed record RunbookSearchResult(string Query, string Mode, IReadOnlyList<RunbookHit> Hits, string? Note = null);

/// <summary>
/// Searches the markdown runbooks under <see cref="RunbookOptions.Directory"/>. The runbooks are chunked by heading
/// and indexed on first use, and re-indexed when a file is added, removed or changed. With an embedding model the
/// chunks are ranked by cosine similarity to the query; without one, or when the model call fails or nothing is
/// similar enough, they are ranked with BM25.
/// </summary>
public sealed partial class RunbookSearchService(
    WorkspaceFiles files,
    IOptions<RunbookOptions> options,
    ILogger<RunbookSearchService> logger,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
{
    public const string SemanticMode = "semantic";
    public const string KeywordMode = "keyword";

    private const int EmbeddingBatchSize = 32;

    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private Index? _index;

    /// <summary>True when an embedding model is configured.</summary>
    public bool HasEmbeddings => embeddings is not null;

    public async Task<RunbookSearchResult> SearchAsync(string query, int top, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("The search query is empty.", nameof(query));
        }

        top = Math.Clamp(top, 1, 10);
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index.Chunks.Count == 0)
        {
            return new RunbookSearchResult(query, KeywordMode, [], $"No markdown runbooks were found in '{options.Value.Directory}'.");
        }

        string? note = null;
        if (embeddings is not null)
        {
            try
            {
                var semantic = await SemanticSearchAsync(index, query, top, cancellationToken).ConfigureAwait(false);
                if (semantic.Count > 0)
                {
                    return new RunbookSearchResult(query, SemanticMode, semantic);
                }

                note = $"No runbook section reached a similarity of {options.Value.MinSimilarity:0.##}; ranked by keywords instead.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEmbeddingFailed(ex);
                note = $"The embedding model failed ({ex.Message}); ranked by keywords instead.";
            }
        }

        var keyword = index.Keywords.Search(query, top)
            .Select(match => Hit(index.Chunks[match.Document], match.Score))
            .ToArray();
        return new RunbookSearchResult(query, KeywordMode, keyword, note);
    }

    private async Task<List<RunbookHit>> SemanticSearchAsync(Index index, string query, int top, CancellationToken cancellationToken)
    {
        var vectors = await EnsureEmbeddedAsync(index, cancellationToken).ConfigureAwait(false);
        var queryVector = (await embeddings!.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector;

        return vectors
            .Select((vector, i) => (Index: i, Score: (double)TensorPrimitives.CosineSimilarity(queryVector.Span, vector.Span)))
            .Where(s => s.Score >= options.Value.MinSimilarity)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Index)
            .Take(top)
            .Select(s => Hit(index.Chunks[s.Index], Math.Round(s.Score, 4)))
            .ToList();
    }

    /// <summary>Embeds the index's chunks the first time they are needed; a failure leaves them unembedded for the next try.</summary>
    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EnsureEmbeddedAsync(Index index, CancellationToken cancellationToken)
    {
        if (index.Vectors is { } ready)
        {
            return ready;
        }

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (index.Vectors is { } embedded)
            {
                return embedded;
            }

            var vectors = new List<ReadOnlyMemory<float>>(index.Chunks.Count);
            foreach (var batch in index.Chunks.Chunk(EmbeddingBatchSize))
            {
                var generated = await embeddings!.GenerateAsync(batch.Select(EmbeddingText), cancellationToken: cancellationToken).ConfigureAwait(false);
                vectors.AddRange(generated.Select(e => e.Vector));
            }

            index.Vectors = vectors;
            return vectors;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<Index> GetIndexAsync(CancellationToken cancellationToken)
    {
        var directory = options.Value.Directory;
        var paths = files.ListFiles(directory, ".md");
        var fingerprint = string.Join('\n', paths.Select(p => (p, files.Stat(p))).Select(f => $"{f.p}|{f.Item2.Length}|{f.Item2.LastWriteTimeUtc.Ticks}"));

        if (_index is { } current && current.Fingerprint == fingerprint)
        {
            return current;
        }

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is { } built && built.Fingerprint == fingerprint)
            {
                return built;
            }

            var chunks = new List<RunbookChunk>();
            foreach (var path in paths)
            {
                chunks.AddRange(MarkdownChunker.Chunk(path, await files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)));
            }

            var index = new Index(fingerprint, chunks, new Bm25Index(chunks.Select(c => $"{c.Heading}\n{c.Text}").ToArray()));
            _index = index;
            LogIndexed(chunks.Count, paths.Count, directory);
            return index;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static string EmbeddingText(RunbookChunk chunk) => chunk.Heading.Length == 0 ? chunk.Text : $"{chunk.Heading}\n\n{chunk.Text}";

    private RunbookHit Hit(RunbookChunk chunk, double score)
    {
        var snippet = Whitespace().Replace(chunk.Text, " ").Trim();
        var max = options.Value.MaxSnippetCharacters;
        if (snippet.Length > max)
        {
            snippet = snippet[..max].TrimEnd() + "...";
        }

        var citation = chunk.Heading.Length == 0 ? $"{chunk.File}:{chunk.Line}" : $"{chunk.File}:{chunk.Line} ({chunk.Heading})";
        return new RunbookHit(citation, chunk.File, chunk.Line, chunk.Heading, Math.Round(score, 4), snippet);
    }

    private sealed class Index(string fingerprint, IReadOnlyList<RunbookChunk> chunks, Bm25Index keywords)
    {
        public string Fingerprint { get; } = fingerprint;

        public IReadOnlyList<RunbookChunk> Chunks { get; } = chunks;

        public Bm25Index Keywords { get; } = keywords;

        public IReadOnlyList<ReadOnlyMemory<float>>? Vectors { get; set; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Indexed {Chunks} runbook sections from {Files} files in {Directory}")]
    private partial void LogIndexed(int chunks, int files, string directory);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "The embedding model failed; runbook search fell back to keyword ranking")]
    private partial void LogEmbeddingFailed(Exception exception);
}
