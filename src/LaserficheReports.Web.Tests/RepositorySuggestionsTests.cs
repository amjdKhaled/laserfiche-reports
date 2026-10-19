using Xunit;
using LaserficheReports.Web;

public sealed class RepositorySuggestionsTests
{
    [Fact]
    public void MissingFolderOffersRealFieldsWithoutChangingScope()
    {
        var text = RepositorySuggestions.MissingFolder("انتهاء العقد", ["تاريخ انتهاء العقد", "الميزانية"]);
        Assert.Contains("تاريخ انتهاء العقد", text);
        Assert.DoesNotContain("الميزانية", text);
        Assert.Contains("لم أغيّر نطاق البحث", text);
    }
    [Fact]
    public void UnrelatedLabelsAreNotInventedAsAlternatives()
    {
        Assert.Empty(RepositorySuggestions.Closest("xyz", ["تاريخ العقد"]));
        Assert.Contains("مجلدًا أم حقلًا", RepositorySuggestions.MissingFolder("xyz", ["تاريخ العقد"]));
    }
}
