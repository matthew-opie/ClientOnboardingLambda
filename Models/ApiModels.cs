using System.Text.Json.Serialization;

namespace ClientOnboardingLambda.Models;

public sealed class PromptRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;
}

public sealed class QueryRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
}

public sealed class ApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("toolLogs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolLogDto>? ToolLogs { get; set; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContextDto? Context { get; set; }

    [JsonPropertyName("telemetry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TelemetryDto? Telemetry { get; set; }
}

public sealed class ToolLogDto
{
    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public string Parameters { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; }
}

public sealed class ContextDto
{
    [JsonPropertyName("documentId")]
    public string DocumentId { get; set; } = string.Empty;

    [JsonPropertyName("sectionTitle")]
    public string SectionTitle { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("primaryMethod")]
    public string PrimaryMethod { get; set; } = string.Empty;

    [JsonPropertyName("hybridReranked")]
    public bool HybridReranked { get; set; }

    [JsonPropertyName("parentChunkTokenSize")]
    public int ParentChunkTokenSize { get; set; }

    [JsonPropertyName("relevanceScore")]
    public double RelevanceScore { get; set; }
}

public sealed class TelemetryDto
{
    [JsonPropertyName("vectorSearchP95Ms")]
    public double VectorSearchP95Ms { get; set; }

    [JsonPropertyName("dynamoDbAssemblyMs")]
    public double DynamoDbAssemblyMs { get; set; }

    [JsonPropertyName("hybridRerankMs")]
    public double HybridRerankMs { get; set; }

    [JsonPropertyName("ragasFaithfulness")]
    public double RagasFaithfulness { get; set; }

    [JsonPropertyName("crossTenantLeakPercent")]
    public double CrossTenantLeakPercent { get; set; }

    [JsonPropertyName("retrievedChunks")]
    public int RetrievedChunks { get; set; }
}

public sealed class TenantListItemDto
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("displayId")]
    public string DisplayId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonPropertyName("vectorCollection")]
    public string VectorCollection { get; set; } = string.Empty;
}

public sealed class TenantMetadataJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("compliance_officer")]
    public string ComplianceOfficer { get; set; } = string.Empty;

    [JsonPropertyName("lei")]
    public string Lei { get; set; } = string.Empty;

    [JsonPropertyName("excluded_tickers")]
    public List<List<string>> ExcludedTickers { get; set; } = [];

    [JsonPropertyName("rules")]
    public List<List<string>> Rules { get; set; } = [];
}

public sealed class RetrievedChunkResult
{
    public required string DocumentId { get; init; }
    public required string SectionTitle { get; init; }
    public required string Content { get; init; }
    public required string PrimaryMethod { get; init; }
    public bool HybridReranked { get; init; }
    public int ParentChunkTokenSize { get; init; }
    public double RelevanceScore { get; init; }
}

public sealed class ChildChunkRecord
{
    public required string ChildId { get; init; }
    public required string ParentId { get; init; }
    public required string DocumentId { get; init; }
    public required string Text { get; init; }
}

public sealed class ParentChunkRecord
{
    public required string ParentId { get; init; }
    public required string DocumentId { get; init; }
    public required string SectionTitle { get; init; }
    public required string Text { get; init; }
}

public enum RetrievalMethodKind
{
    Bm25,
    DenseVector
}
