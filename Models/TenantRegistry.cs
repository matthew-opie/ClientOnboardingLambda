namespace ClientOnboardingLambda.Models;

public sealed record TenantInfo(
    string TenantId,
    string DisplayId,
    string Name,
    string QdrantCollection,
    string PartitionKey);

public static class TenantRegistry
{
    public static readonly IReadOnlyList<TenantInfo> All =
    [
        Create(1, "Beacon Hill Endowment Fund"),
        Create(2, "Copley Sovereign Wealth Trust"),
        Create(3, "Charles River Retirement System"),
        Create(4, "Back Bay University Foundation"),
        Create(5, "Seaport Global Pension Trust"),
        Create(6, "Fenway Health & Sciences Endowment"),
        Create(7, "Kendall Square Tech Infrastructure Fund"),
        Create(8, "Prudential Center Private Wealth Mandate"),
        Create(9, "Newbury Real Assets & Clean Energy Trust"),
        Create(10, "Logan International Transport Workers Union Fund")
    ];

    public static TenantInfo? Find(string tenantId) =>
        All.FirstOrDefault(t => string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    private static TenantInfo Create(int number, string name)
    {
        var folderId = ToFolderId(number);
        return new TenantInfo(
            folderId,
            ToDisplayId(number),
            name,
            folderId,
            ToPartitionKey(number));
    }

    private static string ToFolderId(int number) => $"tenant_{number:D3}";

    private static string ToDisplayId(int number) => $"Tenant_{number:D3}";

    private static string ToPartitionKey(int number) => $"TENANT#{number:D3}";
}
