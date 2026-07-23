using System.Text.RegularExpressions;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

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

        var parents = new List<(string, string)>();
        var index = 0;
        var part = 0;

        while (index < normalized.Length)
        {
            var length = Math.Min(ParentChars, normalized.Length - index);
            var slice = normalized.Substring(index, length).Trim();
            if (slice.Length > 0)
            {
                parents.Add(($"{documentId}-p{part:D3}", slice));
                part++;
            }

            if (index + length >= normalized.Length)
            {
                break;
            }

            index += length;
        }

        return parents;
    }

    public static IReadOnlyList<(string ChildId, string ParentId, string Text)> CreateChildren(string documentId, IReadOnlyList<(string ParentId, string Text)> parents)
    {
        var children = new List<(string, string, string)>();

        foreach (var (parentId, parentText) in parents)
        {
            var index = 0;
            var childIndex = 0;

            while (index < parentText.Length)
            {
                var length = Math.Min(ChildChars, parentText.Length - index);
                var slice = parentText.Substring(index, length).Trim();
                if (slice.Length > 0)
                {
                    children.Add(($"{parentId}-c{childIndex:D3}", parentId, slice));
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
    public async Task<(RetrievedChunkResult Chunk, double VectorMs, double DynamoMs, double RerankMs, int RetrievedCount)> RetrieveAsync(
        TenantInfo tenant,
        string query,
        CancellationToken cancellationToken = default)
    {
        var dynamoSw = System.Diagnostics.Stopwatch.StartNew();
        var childChunks = await dynamoDb.GetChildChunksAsync(tenant.PartitionKey, cancellationToken);
        dynamoSw.Stop();

        if (childChunks.Count == 0)
        {
            throw new InvalidOperationException($"No ingested chunks found for {tenant.TenantId}. Run /admin/ingest/{tenant.TenantId} first.");
        }

        var vectorSw = System.Diagnostics.Stopwatch.StartNew();
        var queryVector = await openAi.EmbedAsync(query, cancellationToken);
        var denseHits = await qdrant.SearchAsync(tenant.QdrantCollection, queryVector, limit: 8, cancellationToken);
        vectorSw.Stop();

        var rerankSw = System.Diagnostics.Stopwatch.StartNew();
        var bm25Hits = Bm25Scorer.Score(childChunks, query, topK: 8);
        var fused = FuseResults(bm25Hits, denseHits);
        rerankSw.Stop();

        var top = fused.First();
        var parent = await dynamoDb.GetParentChunkAsync(tenant.PartitionKey, top.ParentId, cancellationToken)
                     ?? throw new InvalidOperationException($"Parent chunk {top.ParentId} not found.");

        var chunk = new RetrievedChunkResult
        {
            DocumentId = parent.DocumentId,
            SectionTitle = parent.SectionTitle,
            Content = parent.Text,
            PrimaryMethod = top.PrimaryMethod,
            HybridReranked = top.HybridReranked,
            ParentChunkTokenSize = TextChunker.EstimateTokens(parent.Text),
            RelevanceScore = Math.Round(top.Score, 2)
        };

        return (chunk, vectorSw.Elapsed.TotalMilliseconds, dynamoSw.Elapsed.TotalMilliseconds, rerankSw.Elapsed.TotalMilliseconds, fused.Count);
    }

    private static List<FusedHit> FuseResults(
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

        return scores.Values
            .OrderByDescending(v => v.Score)
            .Take(5)
            .ToList();
    }

    private sealed class FusedHit(string parentId, string documentId, string primaryMethod, bool hybridReranked, double score)
    {
        public string ParentId { get; } = parentId;
        public string DocumentId { get; } = documentId;
        public string PrimaryMethod { get; set; } = primaryMethod;
        public bool HybridReranked { get; set; } = hybridReranked;
        public double Score { get; set; } = score;
    }
}
