using Agent.Core;
using Agent.Core.Plugins;
using Agent.Core.Runbooks;
using Agent.Core.Workspace;
using Agent.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Runbooks;

public sealed class RunbookSearchServiceTests
{
    private static RunbookSearchService Create(
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
        string root = "",
        string directory = "runbooks",
        double minSimilarity = 0.3,
        int maxSnippet = 600) =>
        new(new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = root.Length == 0 ? Samples.Root : root })),
            Options.Create(new RunbookOptions { Directory = directory, MinSimilarity = minSimilarity, MaxSnippetCharacters = maxSnippet }),
            NullLogger<RunbookSearchService>.Instance,
            embeddings);

    [Fact]
    public async Task Without_an_embedding_model_ranks_by_keywords_and_cites_the_section()
    {
        var result = await Create().SearchAsync("payment gateway timeout", top: 2);

        Assert.Equal(RunbookSearchService.KeywordMode, result.Mode);
        Assert.Null(result.Note);
        var best = result.Hits[0];
        Assert.Equal("runbooks/high-error-rate.md:12 (High 5xx error rate > Payment gateway timeout)", best.Citation);
        Assert.Contains("circuit breaker", best.Snippet);
    }

    [Fact]
    public async Task With_an_embedding_model_ranks_by_similarity_and_embeds_chunks_once()
    {
        var embeddings = new HashingEmbeddingGenerator();
        var search = Create(embeddings);

        var first = await search.SearchAsync("pods keep restarting in CrashLoopBackOff, OOMKilled", top: 3);
        var embeddedAfterFirst = embeddings.Embedded.Count;
        var second = await search.SearchAsync("certificate renewal failed", top: 1);

        Assert.Equal(RunbookSearchService.SemanticMode, first.Mode);
        Assert.StartsWith("runbooks/crashloopbackoff.md", first.Hits[0].Citation);
        Assert.All(first.Hits, h => Assert.InRange(h.Score, 0.3, 1.0));
        Assert.Equal(RunbookSearchService.SemanticMode, second.Mode);
        Assert.StartsWith("runbooks/tls-certificate-expiry.md", second.Hits[0].Citation);

        // The second search embeds only its query.
        Assert.Equal(embeddedAfterFirst + 1, embeddings.Embedded.Count);
    }

    [Fact]
    public async Task Falls_back_to_keywords_when_the_embedding_model_fails()
    {
        var search = Create(new HashingEmbeddingGenerator { Failure = new HttpRequestException("connection refused") });

        var result = await search.SearchAsync("disk pressure evicted pods", top: 1);

        Assert.Equal(RunbookSearchService.KeywordMode, result.Mode);
        Assert.Contains("connection refused", result.Note);
        Assert.StartsWith("runbooks/disk-pressure.md", Assert.Single(result.Hits).Citation);
    }

    [Fact]
    public async Task Falls_back_to_keywords_when_nothing_is_similar_enough()
    {
        var result = await Create(new HashingEmbeddingGenerator(), minSimilarity: 0.99).SearchAsync("payment gateway timeout", top: 1);

        Assert.Equal(RunbookSearchService.KeywordMode, result.Mode);
        Assert.Contains("similarity of 0.99", result.Note);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task Snippets_are_whitespace_collapsed_and_truncated()
    {
        var result = await Create(maxSnippet: 40).SearchAsync("payment gateway timeout", top: 1);

        var snippet = Assert.Single(result.Hits).Snippet;
        Assert.True(snippet.Length <= 43);
        Assert.EndsWith("...", snippet);
        Assert.DoesNotContain("\n", snippet);
    }

    [Fact]
    public async Task Reindexes_when_a_runbook_changes()
    {
        var root = Directory.CreateTempSubdirectory("runbooks-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "books"));
            var file = Path.Combine(root, "books", "queue.md");
            await File.WriteAllTextAsync(file, "# Queue backlog\n\nScale the consumers.\n");
            var search = Create(root: root, directory: "books");

            var before = await search.SearchAsync("kafka lag", top: 1);
            await File.WriteAllTextAsync(file, "# Queue backlog\n\nKafka consumer lag: scale the consumers and check rebalancing.\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            var after = await search.SearchAsync("kafka lag", top: 1);

            Assert.Empty(before.Hits);
            Assert.Equal("books/queue.md:3 (Queue backlog)", Assert.Single(after.Hits).Citation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_empty_folder_returns_no_hits_with_a_note_and_a_missing_one_is_an_error()
    {
        var root = Directory.CreateTempSubdirectory("runbooks-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "empty"));

            var empty = await Create(root: root, directory: "empty").SearchAsync("anything", top: 3);

            Assert.Empty(empty.Hits);
            Assert.Contains("No markdown runbooks", empty.Note);
            await Assert.ThrowsAsync<WorkspaceAccessException>(() => Create(root: root, directory: "missing").SearchAsync("anything", top: 3));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Is_exposed_to_the_kernel_as_search_runbooks()
    {
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new RunbookPlugin(Create()), RunbookPlugin.PluginName);

        var result = await kernel.InvokeAsync(RunbookPlugin.PluginName, "search_runbooks",
            new KernelArguments { ["query"] = "certificate expiry", ["top"] = 1 });

        Assert.StartsWith("runbooks/tls-certificate-expiry.md", Assert.Single(result.GetValue<RunbookSearchResult>()!.Hits).Citation);
    }

    [Theory]
    [InlineData("text-embedding-3-small", "http://localhost:11434/v1", true)]
    [InlineData("", "http://localhost:11434/v1", false)]
    [InlineData("nomic-embed-text", "", false)]
    public void An_embedding_model_is_registered_only_when_named_and_reachable(string modelId, string chatEndpoint, bool registered)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workspace:Root"] = Samples.Root,
                ["ChatModel:Endpoint"] = chatEndpoint,
                ["Embeddings:ModelId"] = modelId,
            })
            .Build();

        var services = new ServiceCollection().AddLogging().AddDevOpsAgent(configuration);

        Assert.Equal(registered, services.Any(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>)));
    }
}
