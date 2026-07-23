namespace ClientOnboardingLambda.Services;

public sealed class AppConfig
{
    public string OpenAiApiKey { get; init; } = string.Empty;
    public string DynamoDbTableName { get; init; } = string.Empty;
    public string SeedBucketName { get; init; } = string.Empty;
    public string QdrantUrl { get; init; } = string.Empty;
    public string QdrantApiKey { get; init; } = string.Empty;
    public string AdminApiKey { get; init; } = string.Empty;
    public string McpServerUrl { get; init; } = string.Empty;
    public string McpServerApiKey { get; init; } = string.Empty;
    public string CorsAllowedOrigins { get; init; } = "*";

    public static AppConfig Load()
    {
        return new AppConfig
        {
            OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty,
            DynamoDbTableName = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME") ?? "OnboardingPlatform",
            SeedBucketName = Environment.GetEnvironmentVariable("SEED_BUCKET_NAME") ?? string.Empty,
            QdrantUrl = (Environment.GetEnvironmentVariable("QDRANT_URL") ?? string.Empty).TrimEnd('/'),
            QdrantApiKey = Environment.GetEnvironmentVariable("QDRANT_API_KEY") ?? string.Empty,
            AdminApiKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY") ?? string.Empty,
            McpServerUrl = (Environment.GetEnvironmentVariable("MCP_SERVER_URL") ?? string.Empty).TrimEnd('/'),
            McpServerApiKey = Environment.GetEnvironmentVariable("MCP_SERVER_API_KEY") ?? string.Empty,
            CorsAllowedOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS") ?? "*"
        };
    }

    public void ValidateForQuery()
    {
        if (string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
        }

        if (string.IsNullOrWhiteSpace(QdrantUrl) || string.IsNullOrWhiteSpace(QdrantApiKey))
        {
            throw new InvalidOperationException("QDRANT_URL or QDRANT_API_KEY is not configured.");
        }
    }

    public void ValidateForIngest()
    {
        ValidateForQuery();

        if (string.IsNullOrWhiteSpace(SeedBucketName))
        {
            throw new InvalidOperationException("SEED_BUCKET_NAME is not configured.");
        }
    }
}
