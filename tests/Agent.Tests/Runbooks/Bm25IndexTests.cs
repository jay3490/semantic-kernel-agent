using Agent.Core.Runbooks;

namespace Agent.Tests.Runbooks;

public sealed class Bm25IndexTests
{
    private static readonly string[] Documents =
    [
        "pods restart in crashloopbackoff after a deploy; check the previous logs",
        "payment gateway timeout errors; check the provider status page",
        "disk pressure evicts pods; prune images",
        "the the the pods pods pods pods pods pods pods pods",
    ];

    [Fact]
    public void Ranks_the_document_sharing_the_rarest_terms_first()
    {
        var hits = new Bm25Index(Documents).Search("payment timeout", top: 3);

        Assert.Equal(1, Assert.Single(hits).Document);
    }

    [Fact]
    public void Common_terms_count_for_less_and_repetition_saturates()
    {
        var hits = new Bm25Index(Documents).Search("pods crashloopbackoff", top: 4);

        Assert.Equal(0, hits[0].Document);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Queries_without_shared_terms_find_nothing()
    {
        Assert.Empty(new Bm25Index(Documents).Search("certificate expiry", top: 3));
        Assert.Empty(new Bm25Index([]).Search("anything", top: 3));
    }

    [Fact]
    public void Tokenizer_lowercases_and_drops_stop_words_and_single_characters()
    {
        Assert.Equal(["pods", "crashloopbackoff", "k8s002"], Bm25Index.Tokenize("Why are the Pods in CrashLoopBackOff? (K8S002) a"));
    }
}
