using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed class DynamoDbRepository
{
    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;

    public DynamoDbRepository(IAmazonDynamoDB client, string tableName)
    {
        _client = client;
        _tableName = tableName;
    }

    public async Task PutItemAsync(string pk, string sk, Dictionary<string, AttributeValue> attributes, CancellationToken cancellationToken = default)
    {
        attributes["PK"] = new AttributeValue(pk);
        attributes["SK"] = new AttributeValue(sk);

        await _client.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
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
                    [_tableName] = batch.Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } }).ToList()
                }
            };

            await _client.BatchWriteItemAsync(request, cancellationToken);
        }
    }

    public async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string pk, string sk, CancellationToken cancellationToken = default)
    {
        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue(pk),
                ["SK"] = new AttributeValue(sk)
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
            var response = await _client.QueryAsync(new Amazon.DynamoDBv2.Model.QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :sk)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue(partitionKey),
                    [":sk"] = new AttributeValue("CHILD#")
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
            Text = item.TryGetValue("text", out var text) ? text.S : string.Empty
        };
    }

    public async Task DeleteTenantDataAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await _client.QueryAsync(new Amazon.DynamoDBv2.Model.QueryRequest
            {
                TableName = _tableName,
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

            await _client.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [_tableName] = deleteRequests
                }
            }, cancellationToken);

            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey is not null);
    }
}
