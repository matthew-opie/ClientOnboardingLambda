using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using ClientOnboardingLambda.Models;
using UglyToad.PdfPig;

namespace ClientOnboardingLambda.Services;

public sealed class DocumentIngestService(
    AppConfig config,
    IAmazonS3 s3,
    DynamoDbRepository dynamoDb,
    QdrantClient qdrant,
    OpenAiService openAi)
{
    public async Task<string> IngestTenantAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        config.ValidateForIngest();

        var prefix = $"{tenant.TenantId}/";
        var objects = await ListObjectsAsync(prefix, cancellationToken);
        if (objects.Count == 0)
        {
            throw new InvalidOperationException($"No objects found in s3://{config.SeedBucketName}/{prefix}");
        }

        await dynamoDb.DeleteTenantDataAsync(tenant.PartitionKey, cancellationToken);
        await dynamoDb.PutItemAsync(tenant.PartitionKey, "META", new Dictionary<string, AttributeValue>
        {
            ["tenantId"] = new AttributeValue(tenant.TenantId),
            ["name"] = new AttributeValue(tenant.Name),
            ["displayId"] = new AttributeValue(tenant.DisplayId),
            ["qdrantCollection"] = new AttributeValue(tenant.QdrantCollection)
        }, cancellationToken);

        var metadataKey = objects.FirstOrDefault(k => k.EndsWith("tenant_metadata.json", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(metadataKey))
        {
            var metadataJson = await DownloadTextAsync(metadataKey, cancellationToken);
            await SeedMetadataAsync(tenant, metadataJson, cancellationToken);
        }

        var pdfKeys = objects.Where(k => k.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList();
        var qdrantPoints = new List<QdrantPoint>();
        var totalChildren = 0;

        foreach (var pdfKey in pdfKeys)
        {
            var text = await ExtractPdfTextAsync(pdfKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var documentId = Path.GetFileNameWithoutExtension(pdfKey);
            var sectionTitle = InferSectionTitle(documentId);
            var parents = TextChunker.CreateParents(documentId, text);
            var children = TextChunker.CreateChildren(documentId, parents);
            totalChildren += children.Count;

            await dynamoDb.PutItemAsync(tenant.PartitionKey, $"DOC#{documentId}", new Dictionary<string, AttributeValue>
            {
                ["documentId"] = new (documentId),
                ["s3Key"] = new (pdfKey),
                ["sectionTitle"] = new (sectionTitle)
            }, cancellationToken);

            var ddbItems = new List<Dictionary<string, AttributeValue>>();

            foreach (var (parentId, parentText) in parents)
            {
                ddbItems.Add(new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new (tenant.PartitionKey),
                    ["SK"] = new ($"PARENT#{parentId}"),
                    ["parentId"] = new (parentId),
                    ["documentId"] = new (documentId),
                    ["sectionTitle"] = new (sectionTitle),
                    ["text"] = new (parentText)
                });
            }

            foreach (var (childId, parentId, childText) in children)
            {
                ddbItems.Add(new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new (tenant.PartitionKey),
                    ["SK"] = new ($"CHILD#{childId}"),
                    ["childId"] = new (childId),
                    ["parentId"] = new (parentId),
                    ["documentId"] = new (documentId),
                    ["text"] = new (childText)
                });
            }

            await dynamoDb.BatchWriteAsync(ddbItems, cancellationToken);

            var childTexts = children.Select(c => c.Text).ToList();
            var embeddings = await openAi.EmbedBatchAsync(childTexts, cancellationToken);

            for (var i = 0; i < children.Count; i++)
            {
                var (childId, parentId, _) = children[i];
                qdrantPoints.Add(new QdrantPoint
                {
                    Id = Guid.NewGuid(),
                    Vector = embeddings[i],
                    Payload = new Dictionary<string, object>
                    {
                        ["tenantId"] = tenant.TenantId,
                        ["childId"] = childId,
                        ["parentId"] = parentId,
                        ["documentId"] = documentId
                    }
                });
            }
        }

        await qdrant.UpsertPointsAsync(tenant.QdrantCollection, qdrantPoints, cancellationToken);

        return $"Ingested {pdfKeys.Count} PDF(s), {totalChildren} child chunks into {tenant.QdrantCollection}.";
    }

    private async Task SeedMetadataAsync(TenantInfo tenant, string metadataJson, CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.Deserialize<TenantMetadataJson>(metadataJson)
                       ?? throw new InvalidOperationException("Failed to parse tenant_metadata.json");

        await dynamoDb.PutItemAsync(tenant.PartitionKey, "KYC", new Dictionary<string, AttributeValue>
        {
            ["status"] = new ("VERIFIED"),
            ["complianceOfficer"] = new (metadata.ComplianceOfficer),
            ["lei"] = new (metadata.Lei),
            ["entityName"] = new (metadata.Name)
        }, cancellationToken);

        foreach (var tickerRow in metadata.ExcludedTickers)
        {
            if (tickerRow.Count == 0)
            {
                continue;
            }

            var ticker = tickerRow[0].ToUpperInvariant();
            await dynamoDb.PutItemAsync(tenant.PartitionKey, $"RESTRICTION#{ticker}", new Dictionary<string, AttributeValue>
            {
                ["ticker"] = new (ticker),
                ["company"] = new (tickerRow.Count > 1 ? tickerRow[1] : string.Empty),
                ["reason"] = new (tickerRow.Count > 2 ? tickerRow[2] : string.Empty)
            }, cancellationToken);
        }
    }

    private async Task<List<string>> ListObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? token = null;

        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = config.SeedBucketName,
                Prefix = prefix,
                ContinuationToken = token
            }, cancellationToken);

            keys.AddRange(response.S3Objects.Select(o => o.Key));
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);

        return keys;
    }

    private async Task<string> DownloadTextAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await s3.GetObjectAsync(config.SeedBucketName, key, cancellationToken);
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task<string> ExtractPdfTextAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await s3.GetObjectAsync(config.SeedBucketName, key, cancellationToken);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;

        using var document = PdfDocument.Open(ms);
        return string.Join(' ', document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
    }

    private static string InferSectionTitle(string documentId)
    {
        if (documentId.Contains("IMA", StringComparison.OrdinalIgnoreCase))
        {
            return "Investment Management Agreement";
        }

        if (documentId.Contains("KYC", StringComparison.OrdinalIgnoreCase))
        {
            return "KYC & AML Due Diligence";
        }

        if (documentId.Contains("Side_Letter", StringComparison.OrdinalIgnoreCase))
        {
            return "Side Letter — Restricted Securities";
        }

        return "Institutional Policy Document";
    }
}
