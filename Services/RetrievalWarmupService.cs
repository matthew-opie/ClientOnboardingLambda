using System.Diagnostics;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed record WarmupResult(
    string TenantId,
    int ChildChunkCount,
    bool ChildChunksCached,
    double DynamoDbMs,
    double QdrantMs,
    double TotalMs);

/// <summary>
/// Primes retrieval dependencies without OpenAI calls — child chunk cache + Qdrant connection.
/// </summary>
public sealed class RetrievalWarmupService(DynamoDbRepository dynamoDb, QdrantClient qdrant)
{
    public async Task<WarmupResult> WarmAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        var childCached = ChildChunkCache.TryGet(tenant.PartitionKey, out var existingChunks);

        var childTask = LoadChildChunksAsync(tenant.PartitionKey, childCached, existingChunks, cancellationToken);
        var qdrantTask = qdrant.PingCollectionAsync(tenant.QdrantCollection, cancellationToken);

        await Task.WhenAll(childTask, qdrantTask);

        var (childCount, dynamoMs, wasCached) = await childTask;
        var qdrantMs = await qdrantTask;

        totalSw.Stop();

        return new WarmupResult(
            tenant.TenantId,
            childCount,
            wasCached,
            dynamoMs,
            qdrantMs,
            totalSw.Elapsed.TotalMilliseconds);
    }

    private async Task<(int Count, double Ms, bool Cached)> LoadChildChunksAsync(
        string partitionKey,
        bool alreadyCached,
        IReadOnlyList<ChildChunkRecord>? existingChunks,
        CancellationToken cancellationToken)
    {
        if (alreadyCached && existingChunks is not null)
        {
            return (existingChunks.Count, 0, true);
        }

        var sw = Stopwatch.StartNew();
        var chunks = await dynamoDb.GetChildChunksAsync(partitionKey, cancellationToken);
        sw.Stop();
        ChildChunkCache.Set(partitionKey, chunks);
        return (chunks.Count, sw.Elapsed.TotalMilliseconds, false);
    }
}
