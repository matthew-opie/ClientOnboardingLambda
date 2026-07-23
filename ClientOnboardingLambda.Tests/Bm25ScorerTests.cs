using ClientOnboardingLambda.Models;
using ClientOnboardingLambda.Services;

namespace ClientOnboardingLambda.Tests;

public class Bm25ScorerTests
{
    [Fact]
    public void Score_ranks_matching_document_highest()
    {
        var corpus = new List<ChildChunkRecord>
        {
            new() { ChildId = "doc-p000-c000", ParentId = "doc-p000", DocumentId = "doc", Text = "position limit maximum security portfolio concentration rule" },
            new() { ChildId = "doc-p001-c000", ParentId = "doc-p001", DocumentId = "doc", Text = "maximum redemption liquidity notice unrelated terms" }
        };

        var hits = Bm25Scorer.Score(corpus, "maximum position size single security", topK: 2);

        Assert.NotEmpty(hits);
        Assert.Equal("doc-p000-c000", hits[0].Chunk.ChildId);
        if (hits.Count > 1)
        {
            Assert.True(hits[0].Score > hits[1].Score);
        }
    }

    [Fact]
    public void Score_returns_empty_for_empty_query_terms()
    {
        var corpus = new List<ChildChunkRecord>
        {
            new() { ChildId = "doc-p000-c000", ParentId = "doc-p000", DocumentId = "doc", Text = "alpha beta gamma delta" }
        };

        var hits = Bm25Scorer.Score(corpus, "a an", topK: 8);

        Assert.Empty(hits);
    }
}
