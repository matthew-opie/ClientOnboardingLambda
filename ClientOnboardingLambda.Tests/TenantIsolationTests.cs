using System.Text.Json;
using ClientOnboardingLambda.Models;
using ClientOnboardingLambda.Services;

namespace ClientOnboardingLambda.Tests;

public class TenantIsolationTests
{
    [Fact]
    public void TenantRegistry_assigns_unique_partition_and_collection_per_tenant()
    {
        var keys = TenantRegistry.All
            .Select(t => (t.PartitionKey, t.QdrantCollection))
            .ToList();

        Assert.Equal(10, keys.Count);
        Assert.Equal(10, keys.Select(k => k.PartitionKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(10, keys.Select(k => k.QdrantCollection).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(TenantRegistry.All, tenant =>
        {
            Assert.StartsWith("TENANT#", tenant.PartitionKey, StringComparison.Ordinal);
            Assert.StartsWith("tenant_", tenant.QdrantCollection, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task VerifyTenantIsolationAsync_reports_collection_for_requested_tenant()
    {
        var service = new ComplianceToolService(new DynamoDbRepository(new Amazon.DynamoDBv2.AmazonDynamoDBClient(), "OnboardingPlatform"));
        var tenant = TenantRegistry.Find("tenant_001")!;

        var json = await service.VerifyTenantIsolationAsync(tenant);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("isolated").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("cross_partition_reads").GetInt32());
        Assert.Equal("tenant_001", document.RootElement.GetProperty("collection").GetString());
    }
}
