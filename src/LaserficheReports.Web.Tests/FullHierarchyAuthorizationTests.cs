using Xunit;

namespace LaserficheReports.Web.Tests;

public class FullHierarchyAuthorizationTests
{
    private static Dictionary<int, HierarchyNode> Tree() => new() {
        [553] = new(553, 18, false, true), [18] = new(18, 12, true, true),
        [12] = new(12, 7, true, true), [7] = new(7, 1, true, true), [1] = new(1, 0, true, true) };
    [Fact]
    public void IndependentDocumentRightsStillRequireEveryAncestorAndRoot()
    {
        var nodes = Tree();
        var result = HierarchyAuthorization.Evaluate(553, 1, id => nodes[id]);
        Assert.Equal("Accessible", result.Status);
        Assert.Equal(new[] { 553, 18, 12, 7, 1 }, result.Chain);
    }
    [Theory]
    [InlineData(18)] [InlineData(12)] [InlineData(7)] [InlineData(1)]
    public void DenialAtAnyDepthDoesNotReturnProtectedChain(int denied)
    {
        var nodes = Tree(); var original = nodes[denied];
        nodes[denied] = new(denied, original.ParentId, true, false);
        var result = HierarchyAuthorization.Evaluate(553, 1, id => nodes[id]);
        Assert.Equal("AncestorAccessDenied", result.Status); Assert.Null(result.Chain);
    }
    [Fact]
    public void EntryDenialIsIndependentFromAncestorRights()
    {
        var nodes = Tree(); nodes[553] = new(553, 18, false, false);
        Assert.Equal("DocumentAccessDenied", HierarchyAuthorization.Evaluate(553, 1, id => nodes[id]).Status);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(18)] [InlineData(553)]
    public void BrokenOrCyclicChainCannotBeAccepted(int parent)
    {
        var nodes = Tree(); nodes[12] = new(12, parent, true, true);
        Assert.Equal("VerificationUnavailable", HierarchyAuthorization.Evaluate(553, 1, id => nodes[id]).Status);
    }
    [Fact]
    public void HittingDepthLimitNeverAuthorizes()
    {
        Assert.Equal("VerificationUnavailable", HierarchyAuthorization.Evaluate(1, 300,
            id => new(id, id + 1, true, true)).Status);
    }
    [Fact]
    public void MissingAncestorAndSdkFailureNeverProduceAbsenceOrSuccess()
    {
        Assert.Throws<InvalidOperationException>(() => HierarchyAuthorization.Evaluate(553, 1,
            id => id == 18 ? throw new InvalidOperationException() : Tree()[id]));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void MoveOrRevocationDuringVerificationInvalidatesDecision(bool move)
    {
        var nodes = Tree(); var reads = 0;
        var result = HierarchyAuthorization.Evaluate(553, 1, id => {
            if (++reads == 6) return new(id, move ? 123 : nodes[id].ParentId, false, move);
            return nodes[id];
        });
        Assert.Equal("VerificationUnavailable", result.Status);
    }
    [Fact]
    public void DirectRootDocumentAndRootFolderAreSupported()
    {
        var nodes = Tree(); nodes[553] = new(553, 1, false, true);
        Assert.Equal(new[] { 553, 1 }, HierarchyAuthorization.Evaluate(553, 1, id => nodes[id]).Chain);
        Assert.Equal(new[] { 1 }, HierarchyAuthorization.Evaluate(1, 1, id => nodes[id]).Chain);
    }
    [Fact]
    public void UserDecisionsAreNotShared()
    {
        var a = Tree(); var b = Tree(); b[7] = new(7, 1, true, false);
        Assert.Equal("Accessible", HierarchyAuthorization.Evaluate(553, 1, id => a[id]).Status);
        Assert.Equal("AncestorAccessDenied", HierarchyAuthorization.Evaluate(553, 1, id => b[id]).Status);
    }
    [Fact]
    public void ADocumentCannotMasqueradeAsAnAncestor()
    {
        var nodes = Tree(); nodes[18] = new(18, 12, false, true);
        Assert.Equal("VerificationUnavailable", HierarchyAuthorization.Evaluate(553, 1, id => nodes[id]).Status);
    }
}
