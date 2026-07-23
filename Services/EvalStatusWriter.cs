using Amazon.DynamoDBv2.Model;
using ClientOnboardingLambda.Models;

namespace ClientOnboardingLambda.Services;

public sealed class EvalStatusWriter(DynamoDbRepository dynamoDb)
{
    public Task WriteLatestAsync(
        TenantInfo tenant,
        string status,
        DateTime runAt,
        double? faithfulness = null,
        int? questionCount = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var attributes = new Dictionary<string, AttributeValue>
        {
            ["status"] = new (status),
            ["runAt"] = new (runAt.ToString("O")),
            ["tenantId"] = new (tenant.TenantId)
        };

        if (faithfulness.HasValue)
        {
            attributes["faithfulness"] = new AttributeValue { N = faithfulness.Value.ToString("F4") };
        }

        if (questionCount.HasValue)
        {
            attributes["questionCount"] = new AttributeValue { N = questionCount.Value.ToString() };
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            attributes["error"] = new (error);
        }

        return dynamoDb.PutItemAsync(tenant.PartitionKey, "EVAL#latest", attributes, cancellationToken);
    }
}
