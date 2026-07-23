using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed class DynamoDbRepository(IAmazonDynamoDB client, string tableName)
{
    public async Task PutItemAsync(string pk, string sk, Dictionary<string, AttributeValue> attributes, CancellationToken cancellationToken = default)
    {
        attributes["PK"] = new AttributeValue(pk);
        attributes["SK"] = new AttributeValue(sk);

        await client.PutItemAsync(new PutItemRequest
        {
            TableName = tableName,
            Item = attributes
        }, cancellationToken);
    }

    public async Task BatchWriteAsync(IReadOnlyList<Dictionary<string, AttributeValue>> items, CancellationToken cancellationToken = default)
    {
        foreach (var batch in items.Chunk(25))
        {
            var request = new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [tableName] = batch.Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } }).ToList()
                }
            };

            await client.BatchWriteItemAsync(request, cancellationToken);
        }
    }

    public async Task<EvalStatusDto?> GetEvalStatusAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(partitionKey, "EVAL#latest", cancellationToken);
        if (item is null)
        {
            return null;
        }

        return new EvalStatusDto
        {
            Status = item.TryGetValue("status", out var status) ? status.S : "unknown",
            RunAt = ParseOptionalDateTime(item, "runAt"),
            Faithfulness = ParseOptionalDouble(item, "faithfulness"),
            QuestionCount = ParseOptionalInt(item, "questionCount"),
            Error = item.TryGetValue("error", out var error) ? error.S : null,
            TenantId = item.TryGetValue("tenantId", out var tenantId) ? tenantId.S : null
        };
    }

    public async Task<IngestStatusDto?> GetIngestStatusAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(partitionKey, "INGEST#latest", cancellationToken);
        if (item is null)
        {
            return null;
        }

        return new IngestStatusDto
        {
            Status = item.TryGetValue("status", out var status) ? status.S : "unknown",
            StartedAt = ParseOptionalDateTime(item, "startedAt"),
            CompletedAt = ParseOptionalDateTime(item, "completedAt"),
            PdfCount = ParseOptionalInt(item, "pdfCount"),
            ChunkCount = ParseOptionalInt(item, "chunkCount"),
            Error = item.TryGetValue("error", out var error) ? error.S : null,
            TenantId = item.TryGetValue("tenantId", out var tenantId) ? tenantId.S : null
        };
    }

    public async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string pk, string sk, CancellationToken cancellationToken = default)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new (pk),
                ["SK"] = new (sk)
            }
        }, cancellationToken);

        return response.Item?.Count > 0 ? response.Item : null;
    }

    public async Task<List<ChildChunkRecord>> GetChildChunksAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        var items = new List<ChildChunkRecord>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await client.QueryAsync(new Amazon.DynamoDBv2.Model.QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :sk)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new (partitionKey),
                    [":sk"] = new ("CHILD#")
                },
                ExclusiveStartKey = lastKey
            }, cancellationToken);

            foreach (var item in response.Items)
            {
                items.Add(new ChildChunkRecord
                {
                    ChildId = item["SK"].S.Replace("CHILD#", string.Empty),
                    ParentId = item.TryGetValue("parentId", out var parent) ? parent.S : string.Empty,
                    DocumentId = item.TryGetValue("documentId", out var doc) ? doc.S : string.Empty,
                    Text = item.TryGetValue("text", out var text) ? text.S : string.Empty
                });
            }

            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey is not null);

        return items;
    }

    public async Task<ParentChunkRecord?> GetParentChunkAsync(string partitionKey, string parentId, CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(partitionKey, $"PARENT#{parentId}", cancellationToken);
        if (item is null)
        {
            return null;
        }

        return new ParentChunkRecord
        {
            ParentId = parentId,
            DocumentId = item.TryGetValue("documentId", out var doc) ? doc.S : string.Empty,
            SectionTitle = item.TryGetValue("sectionTitle", out var title) ? title.S : "Retrieved Context",
            Text = item.TryGetValue("text", out var text) ? text.S : string.Empty,
            PageNumber = item.TryGetValue("pageNumber", out var page) && int.TryParse(page.N, out var parsedPage)
                ? parsedPage
                : 0
        };
    }

    public async Task DeleteTenantDataAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await client.QueryAsync(new Amazon.DynamoDBv2.Model.QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue(partitionKey)
                },
                ProjectionExpression = "PK, SK",
                ExclusiveStartKey = lastKey
            }, cancellationToken);

            if (response.Items.Count == 0)
            {
                break;
            }

            var deleteRequests = response.Items
                .Select(item => new WriteRequest
                {
                    DeleteRequest = new DeleteRequest
                    {
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = item["PK"],
                            ["SK"] = item["SK"]
                        }
                    }
                })
                .ToList();

            foreach (var batch in deleteRequests.Chunk(25))
            {
                await client.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [tableName] = batch.ToList()
                    }
                }, cancellationToken);
            }

            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey is not null);
    }

    private static DateTime? ParseOptionalDateTime(Dictionary<string, AttributeValue> item, string key) =>
        item.TryGetValue(key, out var value) && DateTime.TryParse(value.S, out var parsed)
            ? parsed
            : null;

    private static int? ParseOptionalInt(Dictionary<string, AttributeValue> item, string key) =>
        item.TryGetValue(key, out var value) && int.TryParse(value.N, out var parsed)
            ? parsed
            : null;

    private static double? ParseOptionalDouble(Dictionary<string, AttributeValue> item, string key) =>
        item.TryGetValue(key, out var value) && double.TryParse(value.N, out var parsed)
            ? parsed
            : null;
}
