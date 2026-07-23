using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClientOnboardingLambda.Services;

public sealed class QdrantClient
{
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseUrl;
    private readonly string _apiKey;

    public QdrantClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task UpsertPointsAsync(string collection, IReadOnlyList<QdrantPoint> points, CancellationToken cancellationToken = default)
    {
        if (points.Count == 0)
        {
            return;
        }

        foreach (var batch in points.Chunk(64))
        {
            var payload = new { points = batch.Select(p => new
            {
                id = p.Id,
                vector = p.Vector,
                payload = p.Payload
            })};

            using var request = CreateRequest(HttpMethod.Put, $"/collections/{collection}/points?wait=true", payload);
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Qdrant upsert failed ({(int)response.StatusCode}): {body}");
            }
        }
    }

    public async Task<List<QdrantSearchHit>> SearchAsync(string collection, float[] vector, int limit, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            vector,
            limit,
            with_payload = true
        };

        using var request = CreateRequest(HttpMethod.Post, $"/collections/{collection}/points/search", payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Qdrant search failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<QdrantSearchResponse>(body, JsonOptions);
        return parsed?.Result?.Select(r => new QdrantSearchHit
        {
            Score = r.Score,
            ChildId = ReadPayloadString(r.Payload, "childId"),
            ParentId = ReadPayloadString(r.Payload, "parentId"),
            DocumentId = ReadPayloadString(r.Payload, "documentId")
        }).ToList() ?? [];
    }

    private static string ReadPayloadString(Dictionary<string, JsonElement>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    public async Task DeleteCollectionPointsAsync(string collection, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            filter = new
            {
                must = Array.Empty<object>()
            }
        };

        using var request = CreateRequest(HttpMethod.Post, $"/collections/{collection}/points/delete?wait=true", payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Qdrant delete failed ({(int)response.StatusCode}): {body}");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object payload)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}{path}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private sealed class QdrantSearchResponse
    {
        [JsonPropertyName("result")]
        public List<QdrantSearchResult>? Result { get; set; }
    }

    private sealed class QdrantSearchResult
    {
        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("payload")]
        public Dictionary<string, JsonElement>? Payload { get; set; }
    }
}

public sealed class QdrantPoint
{
    public required Guid Id { get; init; }
    public required float[] Vector { get; init; }
    public required Dictionary<string, object> Payload { get; init; }
}

public sealed class QdrantSearchHit
{
    public double Score { get; init; }
    public string ChildId { get; init; } = string.Empty;
    public string ParentId { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
}
