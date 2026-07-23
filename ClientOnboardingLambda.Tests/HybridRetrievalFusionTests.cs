using ClientOnboardingLambda.Models;
using ClientOnboardingLambda.Services;

namespace ClientOnboardingLambda.Tests;

public class HybridRetrievalFusionTests
{
    [Fact]
    public void FuseResults_boosts_chunks_present_in_both_lists()
    {
        var bm25Hits = new List<(ChildChunkRecord Chunk, double Score)>
        {
            (new ChildChunkRecord
            {
                ChildId = "doc-p000-c000",
                ParentId = "doc-p000",
                DocumentId = "doc",
                Text = "position limit"
            }, 3.2),
            (new ChildChunkRecord
            {
                ChildId = "doc-p001-c000",
                ParentId = "doc-p001",
                DocumentId = "doc",
                Text = "liquidity"
            }, 2.1)
        };

        var denseHits = new List<QdrantSearchHit>
        {
            new() { ChildId = "doc-p000-c000", ParentId = "doc-p000", DocumentId = "doc", Score = 0.91 },
            new() { ChildId = "doc-p002-c000", ParentId = "doc-p002", DocumentId = "doc", Score = 0.88 }
        };

        var fused = HybridRetrievalFusion.FuseResults(bm25Hits, denseHits);

        Assert.Equal(3, fused.Count);
        Assert.Equal("doc-p000", fused[0].ParentId);
        Assert.True(fused[0].HybridReranked);
        Assert.True(fused[0].Score > fused[^1].Score);
    }

    [Fact]
    public void SelectParentHits_deduplicates_to_two_parents()
    {
        var fused = new List<FusedHit>
        {
            new("doc-p000", "doc", "Bm25", true, 0.03),
            new("doc-p000", "doc", "Bm25", true, 0.025),
            new("doc-p001", "doc", "DenseVector", false, 0.02),
            new("doc-p002", "doc", "DenseVector", false, 0.015)
        };

        var parents = HybridRetrievalFusion.SelectParentHits(fused);

        Assert.Equal(2, parents.Count);
        Assert.Equal("doc-p000", parents[0].ParentId);
        Assert.Equal("doc-p001", parents[1].ParentId);
    }
}
