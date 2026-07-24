using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

internal static class ChildChunkCache
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ChildChunkRecord>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string partitionKey, out IReadOnlyList<ChildChunkRecord> chunks) =>
        Cache.TryGetValue(partitionKey, out chunks!);

    public static void Set(string partitionKey, IReadOnlyList<ChildChunkRecord> chunks) =>
        Cache[partitionKey] = chunks;

    public static void Invalidate(string partitionKey) =>
        Cache.TryRemove(partitionKey, out _);
}

public static class TextChunker
{
    private const int ParentChars = 4000;
    private const int ChildChars = 800;
    private const int ChildOverlap = 100;

    public static IReadOnlyList<(string ParentId, string Text)> CreateParents(string documentId, string text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return CreateParents(documentId, [(1, normalized)])
            .Select(p => (p.ParentId, p.Text))
            .ToList();
    }

    public static IReadOnlyList<(string ParentId, string Text, int PageNumber)> CreateParents(
        string documentId,
        IReadOnlyList<(int PageNumber, string Text)> pages)
    {
        var parents = new List<(string ParentId, string Text, int PageNumber)>();
        var buffer = string.Empty;
        var bufferStartPage = 1;
        var part = 0;

        foreach (var (pageNumber, pageText) in pages)
        {
            var normalized = Normalize(pageText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (string.IsNullOrEmpty(buffer))
            {
                bufferStartPage = pageNumber;
            }

            var combined = string.IsNullOrEmpty(buffer) ? normalized : $"{buffer} {normalized}";

            while (combined.Length >= ParentChars)
            {
                var slice = combined[..ParentChars].Trim();
                if (slice.Length > 0)
                {
                    parents.Add(($"{documentId}-p{part:D3}", slice, bufferStartPage));
                    part++;
                }

                combined = combined[ParentChars..].TrimStart();
                bufferStartPage = pageNumber;
            }

            buffer = combined;
        }

        if (!string.IsNullOrWhiteSpace(buffer))
        {
            parents.Add(($"{documentId}-p{part:D3}", buffer.Trim(), bufferStartPage));
        }

        return parents;
    }

    public static IReadOnlyList<(string ChildId, string ParentId, string Text)> CreateChildren(
        string documentId,
        IReadOnlyList<(string ParentId, string Text)> parents)
    {
        return CreateChildren(parents.Select(p => (p.ParentId, p.Text, 1)).ToList())
            .Select(c => (c.ChildId, c.ParentId, c.Text))
            .ToList();
    }

    public static IReadOnlyList<(string ChildId, string ParentId, string Text, int PageNumber)> CreateChildren(
        IReadOnlyList<(string ParentId, string Text, int PageNumber)> parents)
    {
        var children = new List<(string ChildId, string ParentId, string Text, int PageNumber)>();

        foreach (var (parentId, parentText, pageNumber) in parents)
        {
            var index = 0;
            var childIndex = 0;

            while (index < parentText.Length)
            {
                var length = Math.Min(ChildChars, parentText.Length - index);
                var slice = parentText.Substring(index, length).Trim();
                if (slice.Length > 0)
                {
                    children.Add(($"{parentId}-c{childIndex:D3}", parentId, slice, pageNumber));
                    childIndex++;
                }

                if (index + length >= parentText.Length)
                {
                    break;
                }

                index += Math.Max(1, length - ChildOverlap);
            }
        }

        return children;
    }

    public static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();
}

public static class Bm25Scorer
{
    public static List<(ChildChunkRecord Chunk, double Score)> Score(IReadOnlyList<ChildChunkRecord> corpus, string query, int topK = 8)
    {
        if (corpus.Count == 0)
        {
            return [];
        }

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
        {
            return [];
        }

        var docTermFreqs = corpus
            .Select(doc => (doc, freqs: TermFrequencies(Tokenize(doc.Text))))
            .ToList();

        var docLengths = docTermFreqs.ToDictionary(x => x.doc.ChildId, x => x.freqs.Values.Sum());
        var avgDocLength = docLengths.Values.DefaultIfEmpty(1).Average();
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in queryTerms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            docFreq[term] = docTermFreqs.Count(x => x.freqs.ContainsKey(term));
        }

        const double k1 = 1.2;
        const double b = 0.75;
        var scores = new List<(ChildChunkRecord, double)>();

        foreach (var (doc, freqs) in docTermFreqs)
        {
            var docLength = docLengths[doc.ChildId];
            double score = 0;

            foreach (var term in queryTerms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!freqs.TryGetValue(term, out var tf))
                {
                    continue;
                }

                var df = docFreq.GetValueOrDefault(term, 0);
                var idf = Math.Log(1 + (corpus.Count - df + 0.5) / (df + 0.5));
                var numerator = tf * (k1 + 1);
                var denominator = tf + k1 * (1 - b + b * (docLength / avgDocLength));
                score += idf * (numerator / denominator);
            }

            if (score > 0)
            {
                scores.Add((doc, score));
            }
        }

        return scores
            .OrderByDescending(x => x.Item2)
            .Take(topK)
            .ToList();
    }

    private static List<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 2)
            .ToList();

    private static Dictionary<string, int> TermFrequencies(IReadOnlyList<string> terms)
    {
        var freqs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            freqs[term] = freqs.GetValueOrDefault(term) + 1;
        }

        return freqs;
    }
}

public sealed class HybridRetrievalService(DynamoDbRepository dynamoDb, QdrantClient qdrant, OpenAiService openAi)
{
    public async Task<(IReadOnlyList<RetrievedChunkResult> Chunks, RetrievalTimings Timings, int RetrievedCount)> RetrieveAsync(
        TenantInfo tenant,
        string query,
        CancellationToken cancellationToken = default)
    {
        var embedTask = TimedEmbedAsync(query, cancellationToken);
        var childTask = LoadChildChunksAsync(tenant.PartitionKey, cancellationToken);

        await Task.WhenAll(embedTask, childTask);

        var (queryVector, embeddingMs) = await embedTask;
        var (childChunks, dynamoMs, childChunksCached) = await childTask;

        if (childChunks.Count == 0)
        {
            throw new InvalidOperationException($"No ingested chunks found for {tenant.TenantId}. Run /admin/ingest/{tenant.TenantId} first.");
        }

        var qdrantTask = TimedQdrantSearchAsync(tenant.QdrantCollection, queryVector, cancellationToken);
        var bm25Task = Task.Run(() => TimedBm25Score(childChunks, query), cancellationToken);

        await Task.WhenAll(qdrantTask, bm25Task);

        var (denseHits, qdrantMs) = await qdrantTask;
        var (bm25Hits, bm25Ms) = await bm25Task;

        var rerankSw = Stopwatch.StartNew();
        var fused = HybridRetrievalFusion.FuseResults(bm25Hits, denseHits);
        rerankSw.Stop();

        var parentHits = HybridRetrievalFusion.SelectParentHits(fused);

        var parentSw = Stopwatch.StartNew();
        var parentRecords = await dynamoDb.BatchGetParentChunksAsync(
            tenant.PartitionKey,
            parentHits.Select(hit => hit.ParentId).ToList(),
            cancellationToken);
        parentSw.Stop();

        var chunks = new List<RetrievedChunkResult>();

        foreach (var hit in parentHits)
        {
            if (!parentRecords.TryGetValue(hit.ParentId, out var parent))
            {
                throw new InvalidOperationException($"Parent chunk {hit.ParentId} not found.");
            }

            chunks.Add(new RetrievedChunkResult
            {
                DocumentId = parent.DocumentId,
                SectionTitle = parent.SectionTitle,
                Content = parent.Text,
                PrimaryMethod = hit.PrimaryMethod,
                HybridReranked = hit.HybridReranked,
                ParentChunkTokenSize = TextChunker.EstimateTokens(parent.Text),
                RelevanceScore = Math.Round(hit.Score, 2),
                PageNumber = parent.PageNumber
            });
        }

        var timings = new RetrievalTimings(
            embeddingMs,
            qdrantMs,
            dynamoMs,
            bm25Ms,
            rerankSw.Elapsed.TotalMilliseconds,
            parentSw.Elapsed.TotalMilliseconds,
            childChunksCached);

        return (chunks, timings, chunks.Count);
    }

    private async Task<(float[] Vector, double Ms)> TimedEmbedAsync(string query, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var vector = await openAi.EmbedAsync(query, cancellationToken);
        sw.Stop();
        return (vector, sw.Elapsed.TotalMilliseconds);
    }

    private async Task<(List<ChildChunkRecord> Chunks, double Ms, bool Cached)> LoadChildChunksAsync(
        string partitionKey,
        CancellationToken cancellationToken)
    {
        if (ChildChunkCache.TryGet(partitionKey, out var cached))
        {
            return (cached.ToList(), 0, true);
        }

        var sw = Stopwatch.StartNew();
        var chunks = await dynamoDb.GetChildChunksAsync(partitionKey, cancellationToken);
        sw.Stop();
        ChildChunkCache.Set(partitionKey, chunks);
        return (chunks, sw.Elapsed.TotalMilliseconds, false);
    }

    private async Task<(List<QdrantSearchHit> Hits, double Ms)> TimedQdrantSearchAsync(
        string collection,
        float[] queryVector,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var hits = await qdrant.SearchAsync(collection, queryVector, limit: 8, cancellationToken);
        sw.Stop();
        return (hits, sw.Elapsed.TotalMilliseconds);
    }

    private static (List<(ChildChunkRecord Chunk, double Score)> Hits, double Ms) TimedBm25Score(
        IReadOnlyList<ChildChunkRecord> childChunks,
        string query)
    {
        var sw = Stopwatch.StartNew();
        var hits = Bm25Scorer.Score(childChunks, query, topK: 8);
        sw.Stop();
        return (hits, sw.Elapsed.TotalMilliseconds);
    }
}

/// <summary>
/// Reciprocal rank fusion (RRF) and parent-chunk selection for hybrid retrieval.
/// Top 3 fused child hits are deduplicated to at most 2 distinct parent chunks for the LLM context window.
/// </summary>
internal static class HybridRetrievalFusion
{
    internal static List<FusedHit> SelectParentHits(IReadOnlyList<FusedHit> fused)
    {
        var parentHits = new List<FusedHit>();
        var seenParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in fused.Take(3))
        {
            if (seenParents.Add(hit.ParentId))
            {
                parentHits.Add(hit);
            }

            if (parentHits.Count >= 2)
            {
                break;
            }
        }

        return parentHits;
    }

    internal static List<FusedHit> FuseResults(
        IReadOnlyList<(ChildChunkRecord Chunk, double Score)> bm25Hits,
        IReadOnlyList<QdrantSearchHit> denseHits)
    {
        const double k = 60.0;
        var scores = new Dictionary<string, FusedHit>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < bm25Hits.Count; i++)
        {
            var hit = bm25Hits[i];
            var key = hit.Chunk.ChildId;
            var rrf = 1.0 / (k + i + 1);
            if (!scores.TryGetValue(key, out var fused))
            {
                fused = new FusedHit(hit.Chunk.ParentId, hit.Chunk.DocumentId, "Bm25", false, 0);
            }

            fused.Score += rrf;
            fused.HybridReranked = denseHits.Any(d => d.ChildId == key);
            scores[key] = fused;
        }

        for (var i = 0; i < denseHits.Count; i++)
        {
            var hit = denseHits[i];
            if (string.IsNullOrWhiteSpace(hit.ChildId))
            {
                continue;
            }

            var key = hit.ChildId;
            var rrf = 1.0 / (k + i + 1);
            if (!scores.TryGetValue(key, out var fused))
            {
                fused = new FusedHit(hit.ParentId, hit.DocumentId, "DenseVector", true, 0);
            }
            else if (fused.PrimaryMethod == "Bm25")
            {
                fused.HybridReranked = true;
            }
            else
            {
                fused.PrimaryMethod = "DenseVector";
            }

            fused.Score += rrf;
            scores[key] = fused;
        }

        return
        [
            .. scores.Values
                .OrderByDescending(v => v.Score)
        ];
    }
}

internal sealed class FusedHit(string parentId, string documentId, string primaryMethod, bool hybridReranked, double score)
{
    public string ParentId { get; } = parentId;
    public string DocumentId { get; } = documentId;
    public string PrimaryMethod { get; set; } = primaryMethod;
    public bool HybridReranked { get; set; } = hybridReranked;
    public double Score { get; set; } = score;
}
