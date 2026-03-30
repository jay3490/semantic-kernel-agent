using Agent.Core.Runbooks;
using Microsoft.Extensions.AI;

namespace Agent.Tests.Fakes;

/// <summary>
/// A deterministic stand-in for an embedding model: each word is hashed into one of a fixed number of dimensions
/// and the vector is normalized, so texts that share words are similar. No network is involved.
/// </summary>
internal sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 512;

    /// <summary>Every text embedded so far, in order.</summary>
    public List<string> Embedded { get; } = [];

    /// <summary>When set, every call throws it.</summary>
    public Exception? Failure { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(Failure);
        }

        var texts = values.ToList();
        Embedded.AddRange(texts);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(texts.Select(Embed)));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static Embedding<float> Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var token in Bm25Index.Tokenize(text))
        {
            vector[Hash(token) % Dimensions] += 1;
        }

        var length = MathF.Sqrt(vector.Sum(v => v * v));
        if (length > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= length;
            }
        }

        return new Embedding<float>(vector);
    }

    /// <summary>FNV-1a, which unlike string.GetHashCode is the same in every process.</summary>
    private static int Hash(string token)
    {
        var hash = 2166136261u;
        foreach (var c in token)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash & int.MaxValue);
    }
}
