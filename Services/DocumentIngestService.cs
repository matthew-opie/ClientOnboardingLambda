using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using ClientOnboardingLambda.Models;
using UglyToad.PdfPig;

namespace ClientOnboardingLambda.Services;

public sealed class DocumentIngestService
{
    private readonly AppConfig _config;
    private readonly IAmazonS3 _s3;
    private readonly DynamoDbRepository _dynamoDb;
    private readonly QdrantClient _qdrant;
    private readonly OpenAiService _openAi;

    public DocumentIngestService(
        AppConfig config,
        IAmazonS3 s3,
        DynamoDbRepository dynamoDb,
        QdrantClient qdrant,
        OpenAiService openAi)
    {
        _config = config;
        _s3 = s3;
        _dynamoDb = dynamoDb;
        _qdrant = qdrant;
        _openAi = openAi;
    }

    public async Task<string> IngestTenantAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        _config.ValidateForIngest();

        var prefix = $"{tenant.TenantId}/";
        var objects = await ListObjectsAsync(prefix, cancellationToken);
        if (objects.Count == 0)
        {
            throw new InvalidOperationException($"No objects found in s3://{_config.SeedBucketName}/{prefix}");
        }

        await _dynamoDb.DeleteTenantDataAsync(tenant.PartitionKey, cancellationToken);
        await _dynamoDb.PutItemAsync(tenant.PartitionKey, "META", new Dictionary<string, AttributeValue>
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

            await _dynamoDb.PutItemAsync(tenant.PartitionKey, $"DOC#{documentId}", new Dictionary<string, AttributeValue>
            {
                ["documentId"] = new AttributeValue(documentId),
                ["s3Key"] = new AttributeValue(pdfKey),
                ["sectionTitle"] = new AttributeValue(sectionTitle)
            }, cancellationToken);

            var ddbItems = new List<Dictionary<string, AttributeValue>>();

            foreach (var (parentId, parentText) in parents)
            {
                ddbItems.Add(new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue(tenant.PartitionKey),
                    ["SK"] = new AttributeValue($"PARENT#{parentId}"),
                    ["parentId"] = new AttributeValue(parentId),
                    ["documentId"] = new AttributeValue(documentId),
                    ["sectionTitle"] = new AttributeValue(sectionTitle),
                    ["text"] = new AttributeValue(parentText)
                });
            }

            foreach (var (childId, parentId, childText) in children)
            {
                ddbItems.Add(new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue(tenant.PartitionKey),
                    ["SK"] = new AttributeValue($"CHILD#{childId}"),
                    ["childId"] = new AttributeValue(childId),
                    ["parentId"] = new AttributeValue(parentId),
                    ["documentId"] = new AttributeValue(documentId),
                    ["text"] = new AttributeValue(childText)
                });
            }

            await _dynamoDb.BatchWriteAsync(ddbItems, cancellationToken);

            var childTexts = children.Select(c => c.Text).ToList();
            var embeddings = await _openAi.EmbedBatchAsync(childTexts, cancellationToken);

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

        await _qdrant.UpsertPointsAsync(tenant.QdrantCollection, qdrantPoints, cancellationToken);

        return $"Ingested {pdfKeys.Count} PDF(s), {totalChildren} child chunks into {tenant.QdrantCollection}.";
    }

    private async Task SeedMetadataAsync(TenantInfo tenant, string metadataJson, CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.Deserialize<TenantMetadataJson>(metadataJson)
                       ?? throw new InvalidOperationException("Failed to parse tenant_metadata.json");

        await _dynamoDb.PutItemAsync(tenant.PartitionKey, "KYC", new Dictionary<string, AttributeValue>
        {
            ["status"] = new AttributeValue("VERIFIED"),
            ["complianceOfficer"] = new AttributeValue(metadata.ComplianceOfficer),
            ["lei"] = new AttributeValue(metadata.Lei),
            ["entityName"] = new AttributeValue(metadata.Name)
        }, cancellationToken);

        foreach (var tickerRow in metadata.ExcludedTickers)
        {
            if (tickerRow.Count == 0)
            {
                continue;
            }

            var ticker = tickerRow[0].ToUpperInvariant();
            await _dynamoDb.PutItemAsync(tenant.PartitionKey, $"RESTRICTION#{ticker}", new Dictionary<string, AttributeValue>
            {
                ["ticker"] = new AttributeValue(ticker),
                ["company"] = new AttributeValue(tickerRow.Count > 1 ? tickerRow[1] : string.Empty),
                ["reason"] = new AttributeValue(tickerRow.Count > 2 ? tickerRow[2] : string.Empty)
            }, cancellationToken);
        }
    }

    private async Task<List<string>> ListObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? token = null;

        do
        {
            var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _config.SeedBucketName,
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
        using var response = await _s3.GetObjectAsync(_config.SeedBucketName, key, cancellationToken);
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task<string> ExtractPdfTextAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await _s3.GetObjectAsync(_config.SeedBucketName, key, cancellationToken);
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
